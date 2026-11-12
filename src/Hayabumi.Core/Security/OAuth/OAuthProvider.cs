using Hayabumi.Core.Accounts;

namespace Hayabumi.Core.Security.OAuth;

/// <summary>
/// OAuth 2.0 の提供元（Google / Microsoft）の設定。
/// クライアント ID は、各社の開発者向けサイトでアプリを登録して発行したものを使う。
/// </summary>
public sealed record OAuthProvider
{
    public required AuthMethod Method { get; init; }
    public required string Name { get; init; }
    public required string AuthorizationEndpoint { get; init; }
    public required string TokenEndpoint { get; init; }
    public required string Scope { get; init; }
    public required string ClientId { get; init; }

    /// <summary>Google のデスクトップ アプリでは必要（公開しても問題ない値とされている）。Microsoft では不要。</summary>
    public string? ClientSecret { get; init; }

    /// <summary>リダイレクト先のホスト名。Google は 127.0.0.1、Microsoft は localhost を登録する。</summary>
    public required string RedirectHost { get; init; }

    public static OAuthProvider Google(string clientId, string? clientSecret) => new()
    {
        Method = AuthMethod.OAuthGoogle,
        Name = "Google",
        AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth",
        TokenEndpoint = "https://oauth2.googleapis.com/token",
        Scope = "https://mail.google.com/ openid email",
        ClientId = clientId,
        ClientSecret = clientSecret,
        RedirectHost = "127.0.0.1",
    };

    public static OAuthProvider Microsoft(string clientId) => new()
    {
        Method = AuthMethod.OAuthMicrosoft,
        Name = "Microsoft",
        AuthorizationEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
        TokenEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/token",
        Scope = "https://outlook.office.com/IMAP.AccessAsUser.All https://outlook.office.com/POP.AccessAsUser.All https://outlook.office.com/SMTP.Send offline_access openid email",
        ClientId = clientId,
        RedirectHost = "localhost",
    };
}

/// <summary>保存するトークン。</summary>
public sealed record OAuthTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>サインインの結果。</summary>
public sealed record OAuthSignInResult(OAuthTokens Tokens, string? Email, string? Name);

public sealed class OAuthException(string message, Exception? inner = null) : Exception(message, inner);
