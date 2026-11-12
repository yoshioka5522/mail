namespace Hayabumi.Core.Accounts;

public enum IncomingProtocol
{
    Imap,
    Pop3,
}

public enum ConnectionSecurity
{
    /// <summary>暗号化なし（明示的に許可した場合のみ）。</summary>
    None,

    /// <summary>接続直後から SSL/TLS（IMAPS 993、POP3S 995、SMTPS 465）。</summary>
    SslOnConnect,

    /// <summary>STARTTLS（IMAP 143、POP3 110、SMTP 587）。</summary>
    StartTls,
}

public enum AuthMethod
{
    /// <summary>パスワード（アプリパスワードを含む）。</summary>
    Password,
    OAuthGoogle,
    OAuthMicrosoft,
}

public sealed record ServerEndpoint(string Host, int Port, ConnectionSecurity Security);

public sealed record Account
{
    public long Id { get; init; }
    public required string DisplayName { get; init; }
    public required string EmailAddress { get; init; }
    public required IncomingProtocol IncomingProtocol { get; init; }
    public required ServerEndpoint Incoming { get; init; }
    public required ServerEndpoint Outgoing { get; init; }
    public required string UserName { get; init; }
    public required AuthMethod AuthMethod { get; init; }
    public int SortOrder { get; init; }

    /// <summary>資格情報マネージャーに保存するときのキー。</summary>
    public string CredentialKey => $"Hayabumi/account/{Id}";
}
