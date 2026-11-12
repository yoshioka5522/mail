using Hayabumi.Core.Storage;

namespace Hayabumi.Core.Sync;

/// <summary>
/// 利用者の操作（既読・フラグ・移動・削除）を、まずローカルに反映してからサーバーへの反映を予約する。
/// オフラインでも操作でき、画面の反応も速い。
/// </summary>
public sealed class MailActions(FolderRepository folders, MessageRepository messages, PendingOperationRepository pending, MessageFileStore files)
{
    public void SetFlags(IReadOnlyCollection<MessageListItem> items, MessageFlags flags, bool add)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return;
        }

        // ほかのフォルダにある同じメールにも反映する（受信トレイで読んだら、ほかのフォルダでも既読になるように）
        items = [.. items.Concat(messages.FindCopies([.. items.Select(i => i.Id)])).DistinctBy(i => i.Id)];
        messages.ChangeFlags([.. items.Select(i => i.Id)], flags, add);
        foreach (var group in items.GroupBy(i => i.FolderId))
        {
            var folder = folders.Get(group.Key) ?? throw new InvalidOperationException("フォルダが見つかりません。");
            var uids = group.Where(i => i.Uid is not null).Select(i => i.Uid!.Value).ToList();
            if (uids.Count > 0 && !folder.IsLocal)
            {
                pending.Enqueue(new PendingOperation
                {
                    AccountId = folder.AccountId,
                    Kind = PendingOperationKind.SetFlags,
                    FolderFullName = folder.FullName,
                    Uids = uids,
                    Flags = flags,
                    Add = add,
                });
            }

            folders.RefreshCounts(folder.Id);
        }
    }

    public void MarkRead(IReadOnlyCollection<MessageListItem> items, bool read) => SetFlags(items, MessageFlags.Seen, read);

    /// <summary>
    /// 別のフォルダへ移動する。移動先では UID が変わるため、移動先のメールは次回の同期で取得し直す。
    /// </summary>
    public void Move(IReadOnlyCollection<MessageListItem> items, Folder destination)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(destination);
        foreach (var group in items.GroupBy(i => i.FolderId))
        {
            var folder = folders.Get(group.Key) ?? throw new InvalidOperationException("フォルダが見つかりません。");
            if (folder.Id == destination.Id)
            {
                continue;
            }

            if (folder.AccountId != destination.AccountId)
            {
                throw new NotSupportedException("別のアカウントへの移動にはまだ対応していません。");
            }

            if (folder.IsLocal || destination.IsLocal)
            {
                if (!folder.IsLocal || !destination.IsLocal)
                {
                    throw new NotSupportedException("サーバーのフォルダとローカルフォルダの間の移動にはまだ対応していません。");
                }

                messages.MoveLocal([.. group.Select(i => i.Id)], destination.Id);
                folders.RefreshCounts(folder.Id);
                folders.RefreshCounts(destination.Id);
                continue;
            }

            var uids = group.Where(i => i.Uid is not null).Select(i => i.Uid!.Value).ToList();
            files.DeleteMany(messages.DeleteByUids(folder.Id, uids));
            pending.Enqueue(new PendingOperation
            {
                AccountId = folder.AccountId,
                Kind = PendingOperationKind.Move,
                FolderFullName = folder.FullName,
                Uids = uids,
                DestinationFullName = destination.FullName,
            });
            folders.RefreshCounts(folder.Id);
        }
    }

    /// <summary>ゴミ箱へ移動する。ゴミ箱の中のメールや、ゴミ箱がないアカウントでは完全に削除する。</summary>
    public void Delete(IReadOnlyCollection<MessageListItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        foreach (var group in items.GroupBy(i => i.FolderId))
        {
            var folder = folders.Get(group.Key) ?? throw new InvalidOperationException("フォルダが見つかりません。");
            var trash = folders.GetByAccount(folder.AccountId).FirstOrDefault(f => f.Role == FolderRole.Trash);
            if (trash is not null && trash.Id != folder.Id)
            {
                Move([.. group], trash);
                continue;
            }

            if (folder.IsLocal)
            {
                // POP3 で受信したメールは、サーバーからも削除する（次回の受信時）
                var uidls = group.Select(i => messages.GetPop3Uidl(i.Id)).OfType<string>().ToList();
                files.DeleteMany(messages.DeleteByIds([.. group.Select(i => i.Id)]));
                if (uidls.Count > 0)
                {
                    pending.Enqueue(new PendingOperation
                    {
                        AccountId = folder.AccountId,
                        Kind = PendingOperationKind.Pop3Delete,
                        FolderFullName = folder.FullName,
                        Uids = [],
                        RemoteIds = uidls,
                    });
                }

                folders.RefreshCounts(folder.Id);
                continue;
            }

            var uids = group.Where(i => i.Uid is not null).Select(i => i.Uid!.Value).ToList();
            files.DeleteMany(messages.DeleteByUids(folder.Id, uids));
            pending.Enqueue(new PendingOperation
            {
                AccountId = folder.AccountId,
                Kind = PendingOperationKind.DeletePermanently,
                FolderFullName = folder.FullName,
                Uids = uids,
            });
            folders.RefreshCounts(folder.Id);
        }
    }
}
