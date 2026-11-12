using Hayabumi.Core.Accounts;

namespace Hayabumi.Core.Security;

/// <summary>
/// サーバーへの認証に使う秘密情報（パスワード、または OAuth のアクセストークン）を返す。
/// </summary>
public interface IAccountSecretProvider
{
    Task<string> GetSecretAsync(Account account, CancellationToken cancellationToken);
}

/// <summary>資格情報マネージャーに保存したパスワードを返す。OAuth はフェーズ 1 で追加する。</summary>
public sealed class StoredPasswordSecretProvider(ICredentialStore store) : IAccountSecretProvider
{
    public Task<string> GetSecretAsync(Account account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.AuthMethod != AuthMethod.Password)
        {
            throw new NotSupportedException("OAuth による認証はまだ実装されていません。");
        }

        var secret = store.Read(account.CredentialKey)
            ?? throw new InvalidOperationException($"{account.EmailAddress} のパスワードが保存されていません。");
        return Task.FromResult(secret);
    }
}
