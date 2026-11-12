using System.Text;
using System.Text.RegularExpressions;
using MimeKit;

namespace Hayabumi.Core.Mime;

/// <summary>
/// MIME メッセージを解析する。日本語メール特有の文字コードの誤りに寛容に対応する。
/// </summary>
public static partial class MessageParser
{
    private static readonly Lazy<ParserOptions> s_options = new(() =>
    {
        JapaneseEncodings.EnsureRegistered();
        var options = ParserOptions.Default.Clone();
        // charset の宣言がない 8bit ヘッダーは、UTF-8 として解釈できなければ CP932 として扱う
        options.CharsetEncoding = JapaneseEncodings.Cp932;
        return options;
    });

    public static ParserOptions Options => s_options.Value;

    public static async Task<MimeMessage> LoadAsync(Stream stream, CancellationToken cancellationToken = default)
        => await MimeMessage.LoadAsync(Options, stream, cancellationToken).ConfigureAwait(false);

    public static MimeMessage Load(Stream stream) => MimeMessage.Load(Options, stream);

    public static ParsedMessage Parse(Stream stream) => Parse(Load(stream));

    public static ParsedMessage Parse(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var (plain, html) = FindBodyParts(message);
        var from = message.From.Mailboxes.FirstOrDefault();
        var date = message.Date == DateTimeOffset.MinValue ? DateTimeOffset.UnixEpoch : message.Date;

        return new ParsedMessage
        {
            MessageId = message.MessageId,
            InReplyTo = message.InReplyTo,
            References = [.. message.References],
            Subject = message.Subject ?? string.Empty,
            FromName = from?.Name ?? string.Empty,
            FromAddress = from?.Address ?? string.Empty,
            ToText = FormatAddresses(message.To),
            CcText = FormatAddresses(message.Cc),
            Date = date,
            TextBody = plain is null ? null : DecodeText(plain),
            HtmlBody = html is null ? null : DecodeText(html),
            Attachments = [.. EnumerateAttachments(message, plain, html).Select(a => a.Info)],
        };
    }

    public static string FormatAddresses(InternetAddressList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        return string.Join(", ", list.Mailboxes.Select(m =>
            string.IsNullOrWhiteSpace(m.Name) ? m.Address : $"{m.Name} <{m.Address}>"));
    }

    /// <summary>
    /// テキストパートを文字列に復号する。charset の宣言がない・誤っている場合は内容から推定する。
    /// </summary>
    public static string DecodeText(TextPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (part.Content is null)
        {
            return string.Empty;
        }

        using var buffer = new MemoryStream();
        part.Content.DecodeTo(buffer);
        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);

        var declared = JapaneseEncodings.FromCharsetName(part.ContentType.Charset);
        if (declared is null && part.IsHtml)
        {
            declared = JapaneseEncodings.FromCharsetName(FindHtmlMetaCharset(bytes));
        }

        return DecodeBytes(bytes, declared);
    }

    public static string DecodeBytes(ReadOnlySpan<byte> bytes, Encoding? declared)
    {
        var encoding = declared;
        if (encoding is null || !IsPlausible(bytes, encoding))
        {
            encoding = CharsetDetector.Detect(bytes);
        }

        var text = encoding.GetString(bytes);
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }

    /// <summary>宣言された文字コードが内容と矛盾していないかを確かめる。</summary>
    private static bool IsPlausible(ReadOnlySpan<byte> bytes, Encoding encoding)
    {
        if (encoding.CodePage is 50220 or 50221 or 50222)
        {
            // ISO-2022-JP は 7bit のはず。8bit が混ざっていれば宣言の誤り
            foreach (var b in bytes)
            {
                if (b >= 0x80)
                {
                    return false;
                }
            }

            return true;
        }

        if (encoding.CodePage == Encoding.UTF8.CodePage)
        {
            return CharsetDetector.IsValidUtf8(bytes);
        }

        // Shift_JIS などと宣言されていても、実は ISO-2022-JP や UTF-8 のことがある
        if (CharsetDetector.ContainsIso2022JpEscape(bytes))
        {
            return false;
        }

        // 日本語を含む Shift_JIS・EUC-JP の文章が、偶然 UTF-8 として正しい並びになることはまずない
        if (encoding.CodePage is 932 or 51932 or 20932 && HasHighBit(bytes) && CharsetDetector.IsValidUtf8(bytes))
        {
            return false;
        }

        return true;
    }

    private static bool HasHighBit(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            if (b >= 0x80)
            {
                return true;
            }
        }

        return false;
    }

    private static string? FindHtmlMetaCharset(ReadOnlySpan<byte> bytes)
    {
        var head = Encoding.ASCII.GetString(bytes[..Math.Min(bytes.Length, 4096)]);
        var match = MetaCharset().Match(head);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>本文として表示するテキストパートと HTML パートを探す。</summary>
    public static (TextPart? Plain, TextPart? Html) FindBodyParts(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        TextPart? plain = null;
        TextPart? html = null;
        foreach (var part in message.BodyParts.OfType<TextPart>())
        {
            if (part.IsAttachment)
            {
                continue;
            }

            if (plain is null && part.IsPlain)
            {
                plain = part;
            }
            else if (html is null && part.IsHtml)
            {
                html = part;
            }
        }

        return (plain, html);
    }

    /// <summary>添付ファイル（本文中の画像を含む）と、その MIME パートを返す。</summary>
    public static IReadOnlyList<(MimeEntity Entity, AttachmentInfo Info)> FindAttachments(MimeMessage message)
    {
        var (plain, html) = FindBodyParts(message);
        return [.. EnumerateAttachments(message, plain, html)];
    }

    private static IEnumerable<(MimeEntity Entity, AttachmentInfo Info)> EnumerateAttachments(MimeMessage message, TextPart? plain, TextPart? html)
    {
        foreach (var entity in message.BodyParts)
        {
            if (ReferenceEquals(entity, plain) || ReferenceEquals(entity, html))
            {
                continue;
            }

            if (entity is MimePart part)
            {
                var isInline = !part.IsAttachment && part.ContentId is not null;
                if (!part.IsAttachment && !isInline && part is TextPart)
                {
                    // 本文として使わなかった代替のテキストパート
                    continue;
                }

                yield return (part, new AttachmentInfo(
                    part.FileName ?? DefaultFileName(part.ContentType),
                    part.ContentType.MimeType,
                    part.ContentId,
                    isInline));
            }
            else if (entity is MessagePart rfc822)
            {
                var subject = rfc822.Message?.Subject;
                yield return (rfc822, new AttachmentInfo(
                    string.IsNullOrWhiteSpace(subject) ? "転送されたメール.eml" : subject + ".eml",
                    "message/rfc822",
                    null,
                    false));
            }
        }
    }

    private static string DefaultFileName(ContentType type)
        => type.MediaType.Equals("image", StringComparison.OrdinalIgnoreCase)
            ? "画像." + type.MediaSubtype
            : "添付ファイル";

    [GeneratedRegex(@"<meta[^>]+charset\s*=\s*[""']?\s*([A-Za-z0-9_\-:.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MetaCharset();
}
