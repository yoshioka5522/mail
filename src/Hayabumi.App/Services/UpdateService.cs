using Hayabumi.Core.Security;
using Velopack;
using Velopack.Sources;

namespace Hayabumi.App.Services;

public enum UpdateCheckResult
{
    /// <summary>開発中の実行など、インストールされていない。</summary>
    NotInstalled,
    UpToDate,
    /// <summary>更新をダウンロードした。再起動すると適用される。</summary>
    Downloaded,
    /// <summary>非公開リポジトリのため、読み取り用トークンの設定が必要。</summary>
    TokenRequired,
}

/// <summary>
/// GitHub Releases から更新を確認・ダウンロードする。
/// 現在は非公開リポジトリのため、設定画面で入力した読み取り専用トークンを使う（exe には埋め込まない）。
/// </summary>
public sealed class UpdateService(ICredentialStore credentials)
{
    public const string RepositoryUrl = "https://github.com/yoshioka5522/mail";
    public const string TokenKey = "Hayabumi/update-token";

    private UpdateManager? _manager;
    private VelopackAsset? _pending;

    public string? CurrentVersion => CreateManager()?.CurrentVersion?.ToString();

    public bool HasToken => !string.IsNullOrEmpty(credentials.Read(TokenKey));

    public void SetToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            credentials.Delete(TokenKey);
        }
        else
        {
            credentials.Write(TokenKey, "github", token.Trim());
        }

        _manager = null;
    }

    public async Task<UpdateCheckResult> CheckAndDownloadAsync(CancellationToken cancellationToken = default)
    {
        if (!HasToken)
        {
            return CreateManager() is null ? UpdateCheckResult.NotInstalled : UpdateCheckResult.TokenRequired;
        }

        var manager = CreateManager();
        if (manager is null)
        {
            return UpdateCheckResult.NotInstalled;
        }

        var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (update is null)
        {
            return UpdateCheckResult.UpToDate;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await manager.DownloadUpdatesAsync(update, cancelToken: cancellationToken).ConfigureAwait(false);
        _pending = update.TargetFullRelease;
        return UpdateCheckResult.Downloaded;
    }

    public string? PendingVersion => _pending?.Version?.ToString();

    /// <summary>ダウンロード済みの更新を適用して再起動する。</summary>
    public void ApplyAndRestart()
    {
        if (_pending is not null && CreateManager() is { } manager)
        {
            manager.ApplyUpdatesAndRestart(_pending);
        }
    }

    private UpdateManager? CreateManager()
    {
        if (_manager is not null)
        {
            return _manager;
        }

        var token = credentials.Read(TokenKey);
        var manager = new UpdateManager(new GithubSource(RepositoryUrl, token, prerelease: false));
        if (!manager.IsInstalled)
        {
            return null;
        }

        _manager = manager;
        return manager;
    }
}
