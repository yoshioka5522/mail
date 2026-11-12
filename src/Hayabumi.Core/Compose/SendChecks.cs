using System.Text.RegularExpressions;
using Hayabumi.Core.Accounts;
using MimeKit;

namespace Hayabumi.Core.Compose;

public enum SendWarningKind
{
    NoRecipients,
    EmptySubject,
    ExternalRecipients,
    ManyVisibleRecipients,
    MissingAttachment,
    LargeAttachments,
}

public sealed record SendWarning(SendWarningKind Kind, string Message, bool Blocking = false);

public sealed record RecipientCheck(string Field, MailboxAddress Address, bool IsExternal);

public sealed record SendCheckResult(IReadOnlyList<RecipientCheck> Recipients, IReadOnlyList<SendWarning> Warnings)
{
    public bool CanSend => !Warnings.Any(w => w.Blocking);
}

/// <summary>
/// 誤送信を防ぐための送信前チェック。
/// 送信前の確認画面で、宛先と添付ファイルを一つずつ確認してもらうための情報を作る。
/// </summary>
public static partial class SendChecks
{
    public const int ManyRecipientsThreshold = 10;
    public const long LargeAttachmentBytes = 20L * 1024 * 1024;

    public static SendCheckResult Analyze(Draft draft, Account account, IReadOnlyCollection<string>? internalDomains = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(account);

        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(account.Domain))
        {
            domains.Add(account.Domain);
        }

        foreach (var domain in internalDomains ?? [])
        {
            domains.Add(domain.Trim().TrimStart('@'));
        }

        var recipients = new List<RecipientCheck>();
        void Add(string field, string value)
        {
            foreach (var mailbox in MessageBuilder.ParseAddresses(field, value))
            {
                var domain = mailbox.Address[(mailbox.Address.LastIndexOf('@') + 1)..];
                // フリーメールのドメイン（gmail.com など）は、同じドメインでも別の人なので常に社外とみなす
                var isExternal = !domains.Contains(domain) || IsFreeMailDomain(domain);
                recipients.Add(new RecipientCheck(field, mailbox, isExternal));
            }
        }

        Add("宛先", draft.To);
        Add("CC", draft.Cc);
        Add("BCC", draft.Bcc);

        var warnings = new List<SendWarning>();
        if (recipients.Count == 0)
        {
            warnings.Add(new SendWarning(SendWarningKind.NoRecipients, "宛先がありません。", Blocking: true));
        }

        if (string.IsNullOrWhiteSpace(draft.Subject))
        {
            warnings.Add(new SendWarning(SendWarningKind.EmptySubject, "件名が空です。"));
        }

        var externalDomains = recipients.Where(r => r.IsExternal)
            .Select(r => r.Address.Address[(r.Address.Address.LastIndexOf('@') + 1)..].ToLowerInvariant())
            .Distinct()
            .ToList();
        if (externalDomains.Count > 0 && !IsFreeMailDomain(account.Domain))
        {
            warnings.Add(new SendWarning(SendWarningKind.ExternalRecipients, $"社外のドメインが含まれています: {string.Join(", ", externalDomains)}"));
        }

        var visible = recipients.Count(r => r.Field != "BCC");
        if (visible >= ManyRecipientsThreshold && externalDomains.Count > 1)
        {
            warnings.Add(new SendWarning(SendWarningKind.ManyVisibleRecipients,
                $"宛先・CC に {visible} 件のアドレスがあり、複数の社外ドメインが含まれています。互いにアドレスが見えてしまうため、BCC の利用を検討してください。"));
        }

        if (draft.Attachments.Count == 0 && MentionsAttachment().IsMatch(StripQuoted(draft.Body)))
        {
            warnings.Add(new SendWarning(SendWarningKind.MissingAttachment, "本文に「添付」とありますが、添付ファイルがありません。"));
        }

        var totalSize = draft.Attachments.Where(File.Exists).Sum(p => new FileInfo(p).Length);
        if (totalSize > LargeAttachmentBytes)
        {
            warnings.Add(new SendWarning(SendWarningKind.LargeAttachments,
                $"添付ファイルの合計が {totalSize / 1024 / 1024} MB あります。相手のサーバーで受け取れない場合があります。"));
        }

        return new SendCheckResult(recipients, warnings);
    }

    public static bool IsFreeMailDomain(string domain) => domain.ToLowerInvariant() is
        "gmail.com" or "googlemail.com" or "outlook.com" or "outlook.jp" or "hotmail.com" or "hotmail.co.jp"
        or "live.jp" or "live.com" or "yahoo.co.jp" or "ymail.ne.jp" or "icloud.com" or "me.com" or "mac.com"
        or "docomo.ne.jp" or "ezweb.ne.jp" or "au.com" or "softbank.ne.jp" or "i.softbank.jp";

    /// <summary>引用部分（&gt; で始まる行と、それより後ろ）を除いた本文。</summary>
    private static string StripQuoted(string body)
    {
        var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var own = lines.TakeWhile(l => !l.StartsWith('>') && !l.StartsWith("-------- 転送", StringComparison.Ordinal) && l != "-- ");
        return string.Join('\n', own);
    }

    [GeneratedRegex(@"添付|attach", RegexOptions.IgnoreCase)]
    private static partial Regex MentionsAttachment();
}
