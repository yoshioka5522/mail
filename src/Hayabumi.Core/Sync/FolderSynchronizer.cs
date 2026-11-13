using Hayabumi.Core.Mime;
using Hayabumi.Core.Storage;

namespace Hayabumi.Core.Sync;

/// <summary>
/// 1 つのフォルダをサーバーと同期する。
/// 新着ヘッダーを新しい順に取得 → フラグの変更を反映 → サーバーで削除されたメールを削除、の順に行う。
/// </summary>
public sealed class FolderSynchronizer(FolderRepository folders, MessageRepository messages, MessageFileStore files, SyncOptions options)
{
    public async Task<FolderSyncResult> SyncAsync(
        IRemoteFolder remote, Folder local, IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(local);

        var reset = false;
        if (local.UidValidity is { } knownValidity && knownValidity != remote.UidValidity)
        {
            // UID の振り直しが起きたため、ローカルの内容は使えない
            files.DeleteMany(messages.DeleteAllInFolder(local.Id));
            local = local with { UidValidity = null, HighestModSeq = null };
            reset = true;
        }

        var maxUid = messages.GetMaxUid(local.Id);

        // 1. 新着
        var added = 0;
        var newUnread = new List<NewMessage>();
        if (remote.Count > 0)
        {
            var newUids = (await remote.GetUidsAsync(maxUid + 1, cancellationToken).ConfigureAwait(false))
                .Where(uid => uid > maxUid)
                .OrderDescending()
                .ToList();

            foreach (var batch in newUids.Chunk(options.HeaderBatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var headers = await remote.FetchHeadersAsync(batch, added < options.PreviewLimit, cancellationToken).ConfigureAwait(false);
                added += messages.InsertMany(local.AccountId, local.Id, headers);
                if (newUnread.Count < 5)
                {
                    newUnread.AddRange(headers.Where(h => !h.Flags.HasFlag(MessageFlags.Seen)).OrderByDescending(h => h.Uid).Take(5 - newUnread.Count));
                }
                progress?.Report(new SyncProgress(local.Id, local.DisplayName, added, newUids.Count));
            }
        }

        // 2. フラグの変更（既に持っていたメールが対象）
        var flagsUpdated = 0;
        if (maxUid > 0)
        {
            flagsUpdated = await SyncFlagsAsync(remote, local, cancellationToken).ConfigureAwait(false);
        }

        // 3. サーバーで削除されたメール
        var removed = 0;
        if (maxUid > 0 && messages.Count(local.Id) != remote.Count)
        {
            var remoteUids = remote.Count == 0
                ? []
                : (await remote.GetUidsAsync(1, cancellationToken).ConfigureAwait(false)).ToHashSet();
            var vanished = messages.GetUids(local.Id).Where(uid => !remoteUids.Contains(uid)).ToList();
            if (vanished.Count > 0)
            {
                files.DeleteMany(messages.DeleteByUids(local.Id, vanished));
                removed = vanished.Count;
            }
        }

        folders.UpdateSyncState(local.Id, remote.UidValidity, remote.HighestModSeq);
        folders.RefreshCounts(local.Id);

        return new FolderSyncResult
        {
            FolderId = local.Id,
            Added = added,
            Removed = removed,
            FlagsUpdated = flagsUpdated,
            Reset = reset,
            IsInitial = maxUid == 0,
            Role = local.Role,
            NewUnread = newUnread,
        };
    }

    /// <summary>本文が未取得のメールを新しい順に取得して保存する。</summary>
    /// <returns>取得した件数。</returns>
    public async Task<int> DownloadBodiesAsync(IRemoteFolder remote, Folder local, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(local);

        var downloaded = 0;
        foreach (var (id, uid, _) in messages.GetMessagesWithoutBody(local.Id, options.BodyDownloadBatch, options.MaxAutoDownloadSize, options.BodyPrefetchWindow))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await DownloadBodyAsync(remote, local.AccountId, id, uid, cancellationToken).ConfigureAwait(false))
            {
                downloaded++;
            }
        }

        return downloaded;
    }

    /// <summary>1 通の本文を取得して保存する（メールを開いたときなど）。</summary>
    public async Task<bool> DownloadBodyAsync(IRemoteFolder remote, long accountId, long messageId, uint uid, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remote);
        using var buffer = new MemoryStream();
        try
        {
            await remote.CopyMessageToAsync(uid, buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (RemoteMessageNotFoundException)
        {
            // ほかの端末で削除された。次回の同期で一覧からも消える
            return false;
        }

        buffer.Position = 0;
        var parsed = MessageParser.Parse(buffer);
        buffer.Position = 0;
        var path = await files.SaveAsync(accountId, buffer, cancellationToken).ConfigureAwait(false);
        messages.SetBody(messageId, path, parsed.PlainText, parsed.Preview, parsed.HasAttachments);
        return true;
    }

    private async Task<int> SyncFlagsAsync(IRemoteFolder remote, Folder local, CancellationToken cancellationToken)
    {
        var localUids = messages.GetUids(local.Id);
        if (localUids.Count == 0)
        {
            return 0;
        }

        IReadOnlyList<RemoteFlags> changes;
        if (remote.HighestModSeq is { } remoteModSeq && local.HighestModSeq is { } localModSeq)
        {
            if (remoteModSeq == localModSeq)
            {
                return 0;
            }

            changes = await remote.FetchFlagsAsync(localUids, localModSeq, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var all = new List<RemoteFlags>(localUids.Count);
            foreach (var batch in localUids.Chunk(options.FlagBatchSize))
            {
                all.AddRange(await remote.FetchFlagsAsync(batch, null, cancellationToken).ConfigureAwait(false));
            }

            changes = all;
        }

        messages.UpdateFlags(local.Id, changes.Select(c => (c.Uid, c.Flags, c.Keywords, c.ModSeq)));
        return changes.Count;
    }
}
