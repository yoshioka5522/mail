namespace Hayabumi.Core.Security;

/// <summary>
/// パスワードやトークンの保管場所。Windows では資格情報マネージャー（DPAPI）を使う。
/// </summary>
public interface ICredentialStore
{
    string? Read(string key);

    void Write(string key, string userName, string secret);

    void Delete(string key);
}

/// <summary>テストやメモリ上だけで使う保管場所。</summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _secrets = [];

    public string? Read(string key) => _secrets.GetValueOrDefault(key);

    public void Write(string key, string userName, string secret) => _secrets[key] = secret;

    public void Delete(string key) => _secrets.Remove(key);
}
