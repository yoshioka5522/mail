namespace Hayabumi.Core.Accounts;

public sealed record ServerPreset(
    string Name,
    ServerEndpoint Incoming,
    ServerEndpoint Outgoing,
    AuthMethod RecommendedAuth,
    string? Note = null,
    ServerEndpoint? Pop3 = null);

/// <summary>
/// 主要なメールサービスのサーバー設定。メールアドレスのドメインから選ぶ。
/// 該当しない場合は一般的な設定（imap.ドメイン / smtp.ドメイン）を候補にする。
/// </summary>
public static class ServerPresets
{
    private static readonly ServerPreset s_gmail = new(
        "Gmail",
        new ServerEndpoint("imap.gmail.com", 993, ConnectionSecurity.SslOnConnect),
        new ServerEndpoint("smtp.gmail.com", 587, ConnectionSecurity.StartTls),
        AuthMethod.OAuthGoogle,
        "Google アカウントの「アプリ パスワード」を使うと、パスワード認証で接続できます（2 段階認証が必要）。",
        new ServerEndpoint("pop.gmail.com", 995, ConnectionSecurity.SslOnConnect));

    private static readonly ServerPreset s_microsoft = new(
        "Outlook.com / Microsoft 365",
        new ServerEndpoint("outlook.office365.com", 993, ConnectionSecurity.SslOnConnect),
        new ServerEndpoint("smtp.office365.com", 587, ConnectionSecurity.StartTls),
        AuthMethod.OAuthMicrosoft,
        "Microsoft のアカウントはパスワード認証が廃止されているため、［Microsoft でサインイン］を使ってください。",
        new ServerEndpoint("outlook.office365.com", 995, ConnectionSecurity.SslOnConnect));

    private static readonly ServerPreset s_yahooJapan = new(
        "Yahoo!メール",
        new ServerEndpoint("imap.mail.yahoo.co.jp", 993, ConnectionSecurity.SslOnConnect),
        new ServerEndpoint("smtp.mail.yahoo.co.jp", 465, ConnectionSecurity.SslOnConnect),
        AuthMethod.Password,
        "Yahoo!メールの設定で「IMAP/POP/SMTPアクセス」を有効にしてください。",
        new ServerEndpoint("pop.mail.yahoo.co.jp", 995, ConnectionSecurity.SslOnConnect));

    private static readonly ServerPreset s_icloud = new(
        "iCloud メール",
        new ServerEndpoint("imap.mail.me.com", 993, ConnectionSecurity.SslOnConnect),
        new ServerEndpoint("smtp.mail.me.com", 587, ConnectionSecurity.StartTls),
        AuthMethod.Password,
        "Apple アカウントで作成した「App 用パスワード」を入力してください。");

    private static readonly Dictionary<string, ServerPreset> s_byDomain = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gmail.com"] = s_gmail,
        ["googlemail.com"] = s_gmail,
        ["outlook.com"] = s_microsoft,
        ["outlook.jp"] = s_microsoft,
        ["hotmail.com"] = s_microsoft,
        ["hotmail.co.jp"] = s_microsoft,
        ["live.com"] = s_microsoft,
        ["live.jp"] = s_microsoft,
        ["msn.com"] = s_microsoft,
        ["yahoo.co.jp"] = s_yahooJapan,
        ["ymail.ne.jp"] = s_yahooJapan,
        ["icloud.com"] = s_icloud,
        ["me.com"] = s_icloud,
        ["mac.com"] = s_icloud,
    };

    /// <summary>メールアドレスから設定を推定する。ドメインが取れない場合は null。</summary>
    public static ServerPreset? Find(string emailAddress)
    {
        var at = emailAddress?.LastIndexOf('@') ?? -1;
        if (at <= 0 || at == emailAddress!.Length - 1)
        {
            return null;
        }

        var domain = emailAddress[(at + 1)..].Trim().ToLowerInvariant();
        if (s_byDomain.TryGetValue(domain, out var preset))
        {
            return preset;
        }

        return new ServerPreset(
            domain,
            new ServerEndpoint("imap." + domain, 993, ConnectionSecurity.SslOnConnect),
            new ServerEndpoint("smtp." + domain, 587, ConnectionSecurity.StartTls),
            AuthMethod.Password,
            "一般的な設定を入力しました。プロバイダーの案内と異なる場合は修正してください。",
            new ServerEndpoint("pop." + domain, 995, ConnectionSecurity.SslOnConnect));
    }
}
