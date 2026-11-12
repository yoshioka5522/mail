using System.Text;
using Hayabumi.Core.Accounts;
using Hayabumi.Core.Mime;
using MimeKit;
using MimeKit.Utils;

namespace Hayabumi.Core.Compose;

public sealed class AddressParseException(string field, string value)
    : Exception($"{field}のアドレスを読み取れません: {value}")
{
    public string Field { get; } = field;
    public string Value { get; } = value;
}

/// <summary>下書きから送信用の MIME メッセージを作る。</summary>
public static class MessageBuilder
{
    /// <summary>
    /// 添付ファイル名は RFC 2047 形式で付ける（RFC 2231 に対応していない古い日本のメールソフトでも読めるように）。
    /// </summary>
    public static FormatOptions FormatOptions { get; } = CreateFormatOptions();

    public static MimeMessage Build(Draft draft, Account account, DateTimeOffset? date = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(account);
        JapaneseEncodings.EnsureRegistered();

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(account.DisplayName == account.EmailAddress ? string.Empty : account.DisplayName, account.EmailAddress));
        message.To.AddRange(ParseAddresses("宛先", draft.To));
        message.Cc.AddRange(ParseAddresses("CC", draft.Cc));
        message.Bcc.AddRange(ParseAddresses("BCC", draft.Bcc));
        message.Date = date ?? DateTimeOffset.Now;
        message.MessageId = MimeUtils.GenerateMessageId(string.IsNullOrEmpty(account.Domain) ? "hayabumi.local" : account.Domain);

        var encoding = draft.Charset == BodyCharset.Iso2022Jp ? Encoding.GetEncoding(50220) : Encoding.UTF8;
        message.Headers.Replace(HeaderId.Subject, encoding, draft.Subject ?? string.Empty);

        if (draft.InReplyTo is not null)
        {
            message.InReplyTo = draft.InReplyTo;
        }

        foreach (var reference in draft.References)
        {
            message.References.Add(reference);
        }

        message.Headers.Add(HeaderId.XMailer, "Hayabumi");

        var text = new TextPart("plain");
        text.SetText(encoding, NormalizeNewLines(draft.Body ?? string.Empty));
        text.ContentTransferEncoding = draft.Charset == BodyCharset.Iso2022Jp ? ContentEncoding.SevenBit : ContentEncoding.Base64;
        if (draft.Charset == BodyCharset.Utf8 && IsAscii(draft.Body))
        {
            text.ContentTransferEncoding = ContentEncoding.SevenBit;
        }

        if (draft.Attachments.Count == 0)
        {
            message.Body = text;
            return message;
        }

        var mixed = new Multipart("mixed") { text };
        foreach (var path in draft.Attachments)
        {
            var attachment = new MimePart(MimeTypes.GetMimeType(path))
            {
                Content = new MimeContent(new MemoryStream(File.ReadAllBytes(path))),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                ContentTransferEncoding = ContentEncoding.Base64,
                FileName = Path.GetFileName(path),
            };
            // ヘッダーは値を設定した時点で文字列化されるため、パラメーターごとに形式を指定する
            foreach (var parameter in attachment.ContentDisposition.Parameters.Concat(attachment.ContentType.Parameters))
            {
                parameter.EncodingMethod = ParameterEncodingMethod.Rfc2047;
            }

            mixed.Add(attachment);
        }

        message.Body = mixed;
        return message;
    }

    /// <summary>宛先の文字列（カンマまたはセミコロン区切り）を解析する。</summary>
    public static IReadOnlyList<MailboxAddress> ParseAddresses(string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var normalized = value.Replace('；', ';').Replace('，', ',').Replace('、', ',').Replace(';', ',').Replace('＠', '@');
        if (!InternetAddressList.TryParse(MessageParser.Options, normalized, out var list))
        {
            throw new AddressParseException(field, value);
        }

        var result = new List<MailboxAddress>();
        foreach (var mailbox in list.Mailboxes)
        {
            if (!IsValidAddress(mailbox.Address))
            {
                throw new AddressParseException(field, mailbox.Address);
            }

            result.Add(mailbox);
        }

        return result;
    }

    private static bool IsValidAddress(string address)
    {
        var at = address.IndexOf('@', StringComparison.Ordinal);
        return at > 0 && at == address.LastIndexOf('@') && at < address.Length - 1 && address[(at + 1)..].Contains('.', StringComparison.Ordinal)
            && !address.Any(char.IsWhiteSpace);
    }

    private static string NormalizeNewLines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);

    private static bool IsAscii(string? text) => text is null || text.All(c => c < 0x80);

    private static FormatOptions CreateFormatOptions()
    {
        var options = FormatOptions.Default.Clone();
        options.ParameterEncodingMethod = ParameterEncodingMethod.Rfc2047;
        return options;
    }
}
