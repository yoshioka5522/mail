using System.Globalization;
using System.Text;
using Hayabumi.Core.Accounts;
using Hayabumi.Core.Mime;
using MimeKit;

namespace Hayabumi.Core.Compose;

/// <summary>新規・返信・全員に返信・転送の下書きを作る。</summary>
public static class DraftFactory
{
    private static readonly CultureInfo s_japanese = CultureInfo.GetCultureInfo("ja-JP");

    public static Draft CreateNew(Account account, string to = "")
    {
        ArgumentNullException.ThrowIfNull(account);
        return new Draft
        {
            AccountId = account.Id,
            Mode = ComposeMode.New,
            To = to,
            Body = WithSignature(string.Empty, account.Signature),
        };
    }

    public static Draft CreateReply(Account account, MimeMessage original, long? sourceMessageId, bool replyAll)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(original);
        var parsed = MessageParser.Parse(original);

        var self = account.EmailAddress;
        var replyTo = original.ReplyTo.Mailboxes.Any() ? original.ReplyTo.Mailboxes : original.From.Mailboxes;
        var to = new List<MailboxAddress>(replyTo);
        var cc = new List<MailboxAddress>();

        // 自分が送ったメールへの返信は、元の宛先に送る
        if (to.All(m => IsSelf(m, self)))
        {
            to = [.. original.To.Mailboxes];
        }

        if (replyAll)
        {
            to.AddRange(original.To.Mailboxes);
            cc.AddRange(original.Cc.Mailboxes);
        }

        to = Distinct(to.Where(m => !IsSelf(m, self) || to.Count == 1)).ToList();
        cc = Distinct(cc.Where(m => !IsSelf(m, self) && !to.Any(t => SameAddress(t, m)))).ToList();

        var references = original.References.ToList();
        if (original.MessageId is { } id && !references.Contains(id))
        {
            references.Add(id);
        }

        var header = $"{FormatDate(parsed.Date)}、{FormatSender(parsed)} さんは書きました:";
        var quoted = Quote(parsed.PlainText);

        return new Draft
        {
            AccountId = account.Id,
            Mode = replyAll ? ComposeMode.ReplyAll : ComposeMode.Reply,
            To = Format(to),
            Cc = Format(cc),
            Subject = AddPrefix("Re: ", parsed.Subject),
            Body = WithSignature(string.Empty, account.Signature) + "\n\n" + header + "\n" + quoted,
            InReplyTo = original.MessageId,
            References = references,
            SourceMessageId = sourceMessageId,
            Charset = DetectCharset(original),
        };
    }

    /// <summary>転送の下書きを作る。元の添付ファイルは <paramref name="attachmentPaths"/> に保存済みのものを使う。</summary>
    public static Draft CreateForward(Account account, MimeMessage original, long? sourceMessageId, IReadOnlyList<string> attachmentPaths)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(original);
        var parsed = MessageParser.Parse(original);

        var block = new StringBuilder()
            .AppendLine("-------- 転送するメッセージ --------")
            .Append("件名: ").AppendLine(parsed.Subject)
            .Append("日時: ").AppendLine(FormatDate(parsed.Date))
            .Append("差出人: ").AppendLine(FormatSender(parsed))
            .Append("宛先: ").AppendLine(parsed.ToText);
        if (!string.IsNullOrEmpty(parsed.CcText))
        {
            block.Append("CC: ").AppendLine(parsed.CcText);
        }

        block.AppendLine().Append(parsed.PlainText);

        return new Draft
        {
            AccountId = account.Id,
            Mode = ComposeMode.Forward,
            Subject = AddPrefix("Fw: ", parsed.Subject),
            Body = WithSignature(string.Empty, account.Signature) + "\n\n" + block,
            Attachments = attachmentPaths,
            SourceMessageId = sourceMessageId,
            Charset = DetectCharset(original),
        };
    }

    /// <summary>「Re: Re: RE: 件名」のような重複を避けて接頭辞を付ける。</summary>
    public static string AddPrefix(string prefix, string subject)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        string[] same = prefix.StartsWith("Re", StringComparison.OrdinalIgnoreCase)
            ? ["re:", "re：", "返信:", "返信："]
            : ["fw:", "fwd:", "fw：", "fwd：", "転送:", "転送："];
        var rest = (subject ?? string.Empty).Trim();
        string? match;
        while ((match = same.FirstOrDefault(p => rest.StartsWith(p, StringComparison.OrdinalIgnoreCase))) is not null)
        {
            rest = rest[match.Length..].TrimStart();
        }

        return prefix + rest;
    }

    public static string Quote(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return string.Join("\n", lines.Select(l => l.StartsWith('>') ? ">" + l : "> " + l));
    }

    public static string WithSignature(string body, string signature)
        => string.IsNullOrWhiteSpace(signature) ? body : body + "\n\n-- \n" + signature.TrimEnd();

    private static string FormatDate(DateTimeOffset date) => date.ToLocalTime().ToString("yyyy/MM/dd (ddd) HH:mm", s_japanese);

    private static string FormatSender(ParsedMessage parsed)
        => string.IsNullOrWhiteSpace(parsed.FromName) ? parsed.FromAddress : $"{parsed.FromName} <{parsed.FromAddress}>";

    private static BodyCharset DetectCharset(MimeMessage original)
    {
        var (plain, html) = MessageParser.FindBodyParts(original);
        var charset = (plain ?? html)?.ContentType.Charset;
        return charset is not null && charset.StartsWith("iso-2022-jp", StringComparison.OrdinalIgnoreCase)
            ? BodyCharset.Iso2022Jp
            : BodyCharset.Utf8;
    }

    private static bool IsSelf(MailboxAddress mailbox, string self) => string.Equals(mailbox.Address, self, StringComparison.OrdinalIgnoreCase);

    private static bool SameAddress(MailboxAddress a, MailboxAddress b) => string.Equals(a.Address, b.Address, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<MailboxAddress> Distinct(IEnumerable<MailboxAddress> list)
        => list.DistinctBy(m => m.Address.ToLowerInvariant());

    /// <summary>入力欄にそのまま表示でき、再び解析できる形式（名前に記号があれば引用符で囲む）。</summary>
    public static string Format(IEnumerable<MailboxAddress> list)
        => string.Join(", ", list.Select(Format));

    public static string Format(MailboxAddress mailbox)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        if (string.IsNullOrWhiteSpace(mailbox.Name))
        {
            return mailbox.Address;
        }

        var name = mailbox.Name;
        if (name.IndexOfAny([',', ';', '<', '>', '"', '@', ':', '(', ')', '[', ']', '\\', '、', '；', '，']) >= 0)
        {
            name = "\"" + name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        }

        return $"{name} <{mailbox.Address}>";
    }
}
