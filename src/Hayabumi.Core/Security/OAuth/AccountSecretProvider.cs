using System.Text.Json;
using Hayabumi.Core.Accounts;

namespace Hayabumi.Core.Security.OAuth;

/// <summary>OAuth のクライアント ID などを返す（設定画面で入力した値）。</summary>
public interface IOAuthProviderSource
{
    /// <summary>設定されていなければ null。</summary>
    OAuthProvider? Find(AuthMethod method);
}

/// <summary>
/// 認証に使う秘密情報を返す。パスワード認証は保存したパスワード、
/// OAuth は保存したトークン（期限が近ければ更新して保存し直す）。
/// </summary>
public sealed class AccountSecretProvider(ICredentialStore store, IOAuthProviderSource providers, OAuthClient client) : IAccountSecretProvider, IDisposable
{
    /// <summary>期限がこれより近いアクセス トークンは、使う前に更新する。</summary>
    private static readonly TimeSpan s_refreshMargin = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public async Task<string> GetSecretAsync(Account account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.AuthMethod == AuthMethod.Password)
        {
            return await new StoredPasswordSecretProvider(store).GetSecretAsync(account, cancellationToken).ConfigureAwait(false);
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tokens = ReadTokens(store, account) ?? throw new OAuthException($"{account.EmailAddress} はサインインされていません。アカウントの設定から、もう一度サインインしてください。");
            if (tokens.ExpiresAt - DateTimeOffset.UtcNow > s_refreshMargin)
            {
                return tokens.AccessToken;
            }

            var provider = providers.Find(account.AuthMethod)
                ?? throw new OAuthException("OAuth のクライアント ID が設定されていません。［ツール］→［OAuth の設定］で設定してください。");
            var refreshed = await client.RefreshAsync(provider, tokens.RefreshToken, cancellationToken).ConfigureAwait(false);
            SaveTokens(store, account, refreshed);
            return refreshed.AccessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void Dispose() => _refreshLock.Dispose();

    public static void SaveTokens(ICredentialStore store, Account account, OAuthTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(account);
        store.Write(account.CredentialKey, account.UserName, JsonSerializer.Serialize(tokens));
    }

    public static OAuthTokens? ReadTokens(ICredentialStore store, Account account)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(account);
        var value = store.Read(account.CredentialKey);
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<OAuthTokens>(value);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
