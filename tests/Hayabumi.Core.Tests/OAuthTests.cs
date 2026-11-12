using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hayabumi.Core.Accounts;
using Hayabumi.Core.Security;
using Hayabumi.Core.Security.OAuth;

namespace Hayabumi.Core.Tests;

public class OAuthTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly OAuthProvider s_google = OAuthProvider.Google("client-id", "client-secret");

    private static string IdToken(string email)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64("{\"alg\":\"none\"}")}.{B64($"{{\"email\":\"{email}\",\"name\":\"山田 太郎\"}}")}.sig";
    }

    /// <summary>ブラウザーの代わりに、認可画面の URL からリダイレクト先へ直接アクセスする。</summary>
    private static Action<Uri> Browser(Func<Dictionary<string, string>, string> buildRedirectQuery, List<Uri> opened)
        => uri =>
        {
            opened.Add(uri);
            var query = uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
                .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
            var redirect = query["redirect_uri"].Replace("localhost", "127.0.0.1", StringComparison.Ordinal) + "?" + buildRedirectQuery(query);
            _ = Task.Run(async () =>
            {
                using var browser = new HttpClient();
                await browser.GetAsync(new Uri(redirect.Replace("?", "favicon.ico?x", StringComparison.Ordinal).Split('?')[0]));
                await browser.GetAsync(new Uri(redirect));
            });
        };

    [Fact]
    public async Task Sign_in_exchanges_code_with_pkce_and_reads_email()
    {
        var handler = new TokenEndpoint(form =>
        {
            Assert.Equal("authorization_code", form["grant_type"]);
            Assert.Equal("the-code", form["code"]);
            Assert.Equal("client-secret", form["client_secret"]);
            return new { access_token = "access-1", refresh_token = "refresh-1", expires_in = 3600, id_token = IdToken("taro@gmail.com") };
        });
        var opened = new List<Uri>();
        string? challenge = null;
        var client = new OAuthClient(new HttpClient(handler));

        var result = await client.SignInAsync(s_google, "taro@gmail.com", Browser(q =>
        {
            challenge = q["code_challenge"];
            return $"code=the-code&state={q["state"]}&scope=x";
        }, opened), Ct);

        Assert.Equal("access-1", result.Tokens.AccessToken);
        Assert.Equal("refresh-1", result.Tokens.RefreshToken);
        Assert.Equal("taro@gmail.com", result.Email);
        Assert.Equal("山田 太郎", result.Name);
        var url = opened.Single().ToString();
        Assert.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?", url, StringComparison.Ordinal);
        Assert.Contains("access_type=offline", url, StringComparison.Ordinal);
        Assert.Contains("login_hint=taro%40gmail.com", url, StringComparison.Ordinal);
        Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A", url, StringComparison.Ordinal);

        // PKCE: トークン要求の code_verifier から challenge を再計算して一致すること
        var verifier = handler.Forms.Single()["code_verifier"];
        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(expected, challenge);
    }

    [Fact]
    public async Task Wrong_state_is_rejected()
    {
        var client = new OAuthClient(new HttpClient(new TokenEndpoint(_ => new { })));
        var ex = await Assert.ThrowsAsync<OAuthException>(() =>
            client.SignInAsync(s_google, null, Browser(_ => "code=x&state=forged", []), Ct));
        Assert.Contains("正しくありません", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Denied_consent_is_reported()
    {
        var client = new OAuthClient(new HttpClient(new TokenEndpoint(_ => new { })));
        var ex = await Assert.ThrowsAsync<OAuthException>(() =>
            client.SignInAsync(OAuthProvider.Microsoft("ms-client"), null, Browser(q => $"error=access_denied&state={q["state"]}", []), Ct));
        Assert.Equal("サインインが取り消されました。", ex.Message);
    }

    [Fact]
    public async Task Secret_provider_refreshes_expired_token_and_keeps_refresh_token()
    {
        var store = new InMemoryCredentialStore();
        var account = new Account
        {
            Id = 5,
            DisplayName = "g",
            EmailAddress = "taro@gmail.com",
            IncomingProtocol = IncomingProtocol.Imap,
            Incoming = new ServerEndpoint("imap.gmail.com", 993, ConnectionSecurity.SslOnConnect),
            Outgoing = new ServerEndpoint("smtp.gmail.com", 587, ConnectionSecurity.StartTls),
            UserName = "taro@gmail.com",
            AuthMethod = AuthMethod.OAuthGoogle,
        };
        var handler = new TokenEndpoint(form =>
        {
            Assert.Equal("refresh_token", form["grant_type"]);
            Assert.Equal("refresh-1", form["refresh_token"]);
            return new { access_token = "access-2", expires_in = 3600 };
        });
        using var provider = new AccountSecretProvider(store, new Providers(), new OAuthClient(new HttpClient(handler)));

        AccountSecretProvider.SaveTokens(store, account, new OAuthTokens("access-1", "refresh-1", DateTimeOffset.UtcNow.AddMinutes(30)));
        Assert.Equal("access-1", await provider.GetSecretAsync(account, Ct));
        Assert.Empty(handler.Forms);

        AccountSecretProvider.SaveTokens(store, account, new OAuthTokens("access-1", "refresh-1", DateTimeOffset.UtcNow.AddSeconds(30)));
        Assert.Equal("access-2", await provider.GetSecretAsync(account, Ct));
        var saved = AccountSecretProvider.ReadTokens(store, account)!;
        Assert.Equal("access-2", saved.AccessToken);
        Assert.Equal("refresh-1", saved.RefreshToken);
        Assert.True(saved.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(50));
    }

    [Fact]
    public async Task Revoked_refresh_token_gives_clear_message()
    {
        var handler = new TokenEndpoint(_ => new { error = "invalid_grant", error_description = "Token has been expired or revoked." }, HttpStatusCode.BadRequest);
        var client = new OAuthClient(new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<OAuthException>(() => client.RefreshAsync(s_google, "old", Ct));
        Assert.Contains("もう一度サインイン", ex.Message, StringComparison.Ordinal);
    }

    private sealed class Providers : IOAuthProviderSource
    {
        public OAuthProvider? Find(AuthMethod method) => method == AuthMethod.OAuthGoogle ? s_google : null;
    }

    private sealed class TokenEndpoint(Func<Dictionary<string, string>, object> respond, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<Dictionary<string, string>> Forms { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var form = body.Split('&').Select(p => p.Split('=', 2))
                .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
            Forms.Add(form);
            return new HttpResponseMessage(status) { Content = new StringContent(JsonSerializer.Serialize(respond(form))) };
        }
    }
}
