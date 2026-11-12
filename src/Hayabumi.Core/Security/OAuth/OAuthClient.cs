using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hayabumi.Core.Security.OAuth;

/// <summary>
/// OAuth 2.0 の認可コード フロー（PKCE）。ブラウザーでサインインし、この PC の一時的なポートで結果を受け取る。
/// パスワードはアプリに渡らない。
/// </summary>
public sealed class OAuthClient(HttpClient http)
{
    public TimeSpan SignInTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// サインインする。<paramref name="openBrowser"/> には、既定のブラウザーで URL を開く処理を渡す。
    /// </summary>
    public async Task<OAuthSignInResult> SignInAsync(OAuthProvider provider, string? loginHint, Action<Uri> openBrowser, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(openBrowser);

        using var listener = new LoopbackListener();
        var redirectUri = $"http://{provider.RedirectHost}:{listener.Port}/";
        var state = RandomString(32);
        var verifier = RandomString(64);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        var query = new Dictionary<string, string>
        {
            ["client_id"] = provider.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["scope"] = provider.Scope,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["prompt"] = provider.Method == Accounts.AuthMethod.OAuthGoogle ? "consent" : "select_account",
        };
        if (provider.Method == Accounts.AuthMethod.OAuthGoogle)
        {
            // リフレッシュ トークンを確実に受け取るため
            query["access_type"] = "offline";
        }

        if (!string.IsNullOrWhiteSpace(loginHint))
        {
            query["login_hint"] = loginHint;
        }

        openBrowser(new Uri(provider.AuthorizationEndpoint + "?" + Encode(query)));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SignInTimeout);
        var parameters = await listener.WaitForRedirectAsync(timeout.Token).ConfigureAwait(false);

        if (parameters.TryGetValue("error", out var error))
        {
            throw new OAuthException(error == "access_denied" ? "サインインが取り消されました。" : $"サインインできませんでした（{error}）。");
        }

        if (!parameters.TryGetValue("state", out var returnedState) || returnedState != state)
        {
            throw new OAuthException("サインインの応答が正しくありません。もう一度お試しください。");
        }

        if (!parameters.TryGetValue("code", out var code))
        {
            throw new OAuthException("サインインの応答に認可コードがありません。");
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = provider.ClientId,
            ["code_verifier"] = verifier,
        };
        if (provider.ClientSecret is { Length: > 0 } secret)
        {
            form["client_secret"] = secret;
        }

        var response = await PostTokenAsync(provider, form, cancellationToken).ConfigureAwait(false);
        var tokens = ToTokens(response, previousRefreshToken: null);
        var (email, name) = ReadIdToken(response);
        return new OAuthSignInResult(tokens, email, name);
    }

    /// <summary>リフレッシュ トークンで新しいアクセス トークンを取得する。</summary>
    public async Task<OAuthTokens> RefreshAsync(OAuthProvider provider, string refreshToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = provider.ClientId,
        };
        if (provider.ClientSecret is { Length: > 0 } secret)
        {
            form["client_secret"] = secret;
        }

        if (provider.Method == Accounts.AuthMethod.OAuthMicrosoft)
        {
            form["scope"] = provider.Scope;
        }

        var response = await PostTokenAsync(provider, form, cancellationToken).ConfigureAwait(false);
        return ToTokens(response, refreshToken);
    }

    private async Task<JsonElement> PostTokenAsync(OAuthProvider provider, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await http.PostAsync(new Uri(provider.TokenEndpoint), content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        JsonElement json;
        try
        {
            json = JsonDocument.Parse(body).RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new OAuthException($"{provider.Name} から正しい応答がありませんでした（HTTP {(int)response.StatusCode}）。", ex);
        }

        if (!response.IsSuccessStatusCode || json.TryGetProperty("error", out _))
        {
            var error = json.TryGetProperty("error", out var e) ? e.GetString() : null;
            var description = json.TryGetProperty("error_description", out var d) ? d.GetString() : null;
            if (error == "invalid_grant")
            {
                throw new OAuthException($"{provider.Name} のサインインの有効期限が切れました。アカウントの設定から、もう一度サインインしてください。");
            }

            throw new OAuthException($"{provider.Name} でエラーが発生しました: {error} {description}".Trim());
        }

        return json;
    }

    private static OAuthTokens ToTokens(JsonElement json, string? previousRefreshToken)
    {
        var access = json.GetProperty("access_token").GetString() ?? throw new OAuthException("アクセス トークンがありません。");
        var refresh = json.TryGetProperty("refresh_token", out var r) ? r.GetString() : previousRefreshToken;
        if (string.IsNullOrEmpty(refresh))
        {
            throw new OAuthException("リフレッシュ トークンを受け取れませんでした。もう一度サインインしてください。");
        }

        var expiresIn = json.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var seconds) ? seconds : 3600;
        return new OAuthTokens(access, refresh, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
    }

    /// <summary>ID トークンからメールアドレスと名前を読む（署名はトークン エンドポイントから直接受け取るため検証しない）。</summary>
    private static (string? Email, string? Name) ReadIdToken(JsonElement json)
    {
        if (!json.TryGetProperty("id_token", out var idToken) || idToken.GetString() is not { } token)
        {
            return (null, null);
        }

        var parts = token.Split('.');
        if (parts.Length < 2)
        {
            return (null, null);
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            var root = document.RootElement;
            string? Read(string name) => root.TryGetProperty(name, out var v) ? v.GetString() : null;
            return (Read("email") ?? Read("preferred_username") ?? Read("upn"), Read("name"));
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return (null, null);
        }
    }

    internal static string Encode(Dictionary<string, string> values)
        => string.Join("&", values.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));

    private static string RandomString(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// ブラウザーからのリダイレクトを受け取る小さな HTTP サーバー（この PC の中からだけ接続できる）。
    /// HttpListener は URL の予約に管理者権限が要る場合があるため、TCP で直接受ける。
    /// </summary>
    private sealed class LoopbackListener : IDisposable
    {
        private readonly TcpListener _v4;
        private readonly TcpListener? _v6;

        public LoopbackListener()
        {
            _v4 = new TcpListener(IPAddress.Loopback, 0);
            _v4.Start();
            Port = ((IPEndPoint)_v4.LocalEndpoint).Port;
            try
            {
                // 「localhost」が IPv6 で解決される環境のため、同じポートで IPv6 でも待つ
                _v6 = new TcpListener(IPAddress.IPv6Loopback, Port);
                _v6.Start();
            }
            catch (SocketException)
            {
                _v6 = null;
            }
        }

        public int Port { get; }

        public async Task<Dictionary<string, string>> WaitForRedirectAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var accepts = new List<Task<TcpClient>> { _v4.AcceptTcpClientAsync(cancellationToken).AsTask() };
                if (_v6 is not null)
                {
                    accepts.Add(_v6.AcceptTcpClientAsync(cancellationToken).AsTask());
                }

                var accepted = await Task.WhenAny(accepts).ConfigureAwait(false);
                using var client = await accepted.ConfigureAwait(false);
                var result = await HandleAsync(client, cancellationToken).ConfigureAwait(false);
                if (result is not null)
                {
                    return result;
                }
            }
        }

        private static async Task<Dictionary<string, string>?> HandleAsync(TcpClient client, CancellationToken cancellationToken)
        {
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)))
            {
                // ヘッダーは読み捨てる
            }

            var parts = requestLine.Split(' ');
            var target = parts.Length >= 2 ? parts[1] : "/";
            var queryStart = target.IndexOf('?', StringComparison.Ordinal);
            if (!parts[0].Equals("GET", StringComparison.Ordinal) || queryStart < 0)
            {
                // favicon などの要求
                await WriteResponseAsync(stream, 404, "Not Found", cancellationToken).ConfigureAwait(false);
                return null;
            }

            var parameters = target[(queryStart + 1)..]
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2))
                .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : string.Empty);

            var message = parameters.ContainsKey("code")
                ? "サインインが完了しました。このページを閉じて Hayabumi に戻ってください。"
                : "サインインできませんでした。このページを閉じて Hayabumi に戻ってください。";
            await WriteResponseAsync(stream, 200,
                $"<!doctype html><html lang=\"ja\"><meta charset=\"utf-8\"><title>Hayabumi</title><body style=\"font-family:sans-serif;padding:40px\"><p>{message}</p></body></html>",
                cancellationToken).ConfigureAwait(false);
            return parameters;
        }

        private static async Task WriteResponseAsync(Stream stream, int status, string body, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            var header = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _v4.Stop();
            _v6?.Stop();
        }
    }
}
