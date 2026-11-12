using Hayabumi.Core.Accounts;
using Hayabumi.Core.Storage;

namespace Hayabumi.Core.Sync;

public sealed record AccountSyncResult(long AccountId, IReadOnlyList<FolderSyncResult> Folders, IReadOnlyList<string> Errors)
{
    public int Added => Folders.Sum(f => f.Added);
}

/// <summary>
/// アカウント全体の同期。保留中の操作の反映 → フォルダ一覧の更新 → 各フォルダの同期 → 本文の取得、の順に行う。
/// </summary>
public sealed class AccountSynchronizer(
    IMailStoreSessionFactory sessionFactory,
    FolderRepository folders,
    MessageRepository messages,
    PendingOperationRepository pending,
    MessageFileStore files,
    SyncOptions options)
{
    /// <summary>この回数失敗した保留中の操作は諦める（エラーとして報告する）。</summary>
    public const int MaxOperationAttempts = 5;

    public async Task<AccountSyncResult> SyncAsync(Account account, IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        var errors = new List<string>();
        var results = new List<FolderSyncResult>();

        await using var session = await sessionFactory.ConnectAsync(account, cancellationToken).ConfigureAwait(false);

        await ReplayPendingAsync(session, account.Id, errors, cancellationToken).ConfigureAwait(false);

        var localFolders = await SyncFolderListAsync(session, account.Id, cancellationToken).ConfigureAwait(false);
        var synchronizer = new FolderSynchronizer(folders, messages, files, options);

        // 受信トレイを最初に同期する
        foreach (var folder in localFolders.OrderBy(f => f.Role == FolderRole.Inbox ? 0 : 1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var remote = await session.OpenFolderAsync(folder.FullName, writable: false, cancellationToken).ConfigureAwait(false);
                results.Add(await synchronizer.SyncAsync(remote, folder, progress, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{folder.DisplayName}: {ex.Message}");
            }
        }

        // 一覧がそろってから、新しいメールの本文を取得する
        foreach (var folder in localFolders.Where(f => f.Role is FolderRole.Inbox or FolderRole.Sent or FolderRole.None))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var current = folders.Get(folder.Id) ?? folder;
                await using var remote = await session.OpenFolderAsync(folder.FullName, writable: false, cancellationToken).ConfigureAwait(false);
                await synchronizer.DownloadBodiesAsync(remote, current, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{folder.DisplayName}: {ex.Message}");
            }
        }

        return new AccountSyncResult(account.Id, results, errors);
    }

    private async Task<IReadOnlyList<Folder>> SyncFolderListAsync(IMailStoreSession session, long accountId, CancellationToken cancellationToken)
    {
        var remoteFolders = await session.GetFoldersAsync(cancellationToken).ConfigureAwait(false);
        var selectable = remoteFolders.Where(f => f.CanSelect).ToList();
        var names = selectable.Select(f => f.FullName).ToHashSet(StringComparer.Ordinal);

        foreach (var stale in folders.GetByAccount(accountId).Where(f => !names.Contains(f.FullName)))
        {
            // サーバーで削除されたフォルダ
            files.DeleteMany(messages.DeleteAllInFolder(stale.Id));
            folders.Delete(stale.Id);
        }

        return [.. selectable.Select(f => folders.Upsert(accountId, f.FullName, f.Name, f.Delimiter, f.Role))];
    }

    private async Task ReplayPendingAsync(IMailStoreSession session, long accountId, List<string> errors, CancellationToken cancellationToken)
    {
        foreach (var group in pending.GetPending(accountId).GroupBy(o => o.FolderFullName))
        {
            IRemoteFolder? remote = null;
            try
            {
                remote = await session.OpenFolderAsync(group.Key, writable: true, cancellationToken).ConfigureAwait(false);
                foreach (var operation in group)
                {
                    try
                    {
                        await ApplyAsync(remote, operation, cancellationToken).ConfigureAwait(false);
                        pending.Complete(operation.Id);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        HandleFailure(operation, ex, errors);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                foreach (var operation in group)
                {
                    HandleFailure(operation, ex, errors);
                }
            }
            finally
            {
                if (remote is not null)
                {
                    await remote.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private void HandleFailure(PendingOperation operation, Exception ex, List<string> errors)
    {
        if (operation.Attempts + 1 >= MaxOperationAttempts)
        {
            pending.Complete(operation.Id);
            errors.Add($"サーバーに反映できなかった操作を取り消しました（{operation.FolderFullName}）: {ex.Message}");
        }
        else
        {
            pending.MarkFailed(operation.Id, ex.Message);
        }
    }

    private static Task ApplyAsync(IRemoteFolder remote, PendingOperation operation, CancellationToken cancellationToken) => operation.Kind switch
    {
        PendingOperationKind.SetFlags => remote.SetFlagsAsync(operation.Uids, operation.Flags, operation.Add, cancellationToken),
        PendingOperationKind.Move => remote.MoveToAsync(operation.Uids, operation.DestinationFullName!, cancellationToken),
        PendingOperationKind.DeletePermanently => remote.DeletePermanentlyAsync(operation.Uids, cancellationToken),
        _ => throw new NotSupportedException($"未対応の操作です: {operation.Kind}"),
    };
}
