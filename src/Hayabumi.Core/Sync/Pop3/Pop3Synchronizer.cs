using Hayabumi.Core.Accounts;
using Hayabumi.Core.Mime;
using Hayabumi.Core.Security;
using Hayabumi.Core.Storage;
using Hayabumi.Core.Sync.Imap;
using MailKit.Net.Pop3;
using MailKit.Security;

namespace Hayabumi.Core.Sync.Pop3;

public sealed record Pop3Entry(int Index, string Uidl, long Size);

/// <summary>POP3 サーバーとの接続。テストでは偽の実装を使う。</summary>
public interface IPop3Session : IAsyncDisposable
{
    Task<IReadOnlyList<Pop3Entry>> ListAsync(CancellationToken cancellationToken);

    Task CopyMessageToAsync(int index, Stream destination, CancellationToken cancellationToken);

    /// <summary>削除の印を付ける（切断時に確定する）。</summary>
    Task DeleteAsync(IReadOnlyList<int> indexes, CancellationToken cancellationToken);
}

public interface IPop3SessionFactory
{
    Task<IPop3Session> ConnectAsync(Account account, CancellationToken cancellationToken);
}

/// <summary>
/// POP3 アカウントの受信。POP3 にはフォルダや既読の概念がないため、
/// 受信トレイ・送信済み・ゴミ箱はこの PC だけのフォルダとして持つ。
/// サーバーにはメールを残し（既定）、ローカルで完全に削除したものはサーバーからも削除する。
/// </summary>
public sealed class Pop3Synchronizer(
    IPop3SessionFactory sessions,
    FolderRepository folders,
    MessageRepository messages,
    PendingOperationRepository pending,
    MessageFileStore files)
{
    public static readonly (string FullName, string Name, FolderRole Role)[] LocalFolders =
    [
        ("INBOX", "受信トレイ", FolderRole.Inbox),
        ("Sent", "送信済み", FolderRole.Sent),
        ("Trash", "ゴミ箱", FolderRole.Trash),
    ];

    /// <summary>ローカルフォルダを作る（アカウント追加時と同期の前）。</summary>
    public static IReadOnlyList<Folder> EnsureFolders(FolderRepository folders, long accountId)
    {
        ArgumentNullException.ThrowIfNull(folders);
        return [.. LocalFolders.Select(f => folders.Upsert(accountId, f.FullName, f.Name, '/', f.Role, isLocal: true))];
    }

    public async Task<AccountSyncResult> SyncAsync(Account account, IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        var localFolders = EnsureFolders(folders, account.Id);
        var inbox = localFolders.Single(f => f.Role == FolderRole.Inbox);
        progress?.Report(new SyncProgress(0, string.Empty, 0, 0));

        var errors = new List<string>();
        var newUnread = new List<NewMessage>();
        var isInitial = messages.GetPop3Uidls(account.Id).Count == 0 && messages.Count(inbox.Id) == 0;
        var added = 0;

        await using (var session = await sessions.ConnectAsync(account, cancellationToken).ConfigureAwait(false))
        {
            var entries = await session.ListAsync(cancellationToken).ConfigureAwait(false);

            // 1. ローカルで削除したメールをサーバーから削除する
            var deleteOperations = pending.GetPending(account.Id).Where(o => o.Kind == PendingOperationKind.Pop3Delete).ToList();
            if (deleteOperations.Count > 0)
            {
                var targets = deleteOperations.SelectMany(o => o.RemoteIds).ToHashSet(StringComparer.Ordinal);
                var indexes = entries.Where(e => targets.Contains(e.Uidl)).Select(e => e.Index).ToList();
                try
                {
                    if (indexes.Count > 0)
                    {
                        await session.DeleteAsync(indexes, cancellationToken).ConfigureAwait(false);
                    }

                    foreach (var operation in deleteOperations)
                    {
                        pending.Complete(operation.Id);
                    }

                    entries = [.. entries.Where(e => !targets.Contains(e.Uidl))];
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add($"サーバーのメールを削除できませんでした: {ex.Message}");
                }
            }

            // 2. 新着を新しい順に取得する（POP3 は番号の大きいものほど新しい）
            var known = messages.GetPop3Uidls(account.Id);
            var newEntries = entries.Where(e => !known.Contains(e.Uidl)).OrderByDescending(e => e.Index).ToList();
            foreach (var entry in newEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var buffer = new MemoryStream();
                try
                {
                    await session.CopyMessageToAsync(entry.Index, buffer, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add($"メールを受信できませんでした（{entry.Uidl}）: {ex.Message}");
                    continue;
                }

                buffer.Position = 0;
                var parsed = MessageParser.Parse(buffer);
                buffer.Position = 0;
                var path = await files.SaveAsync(account.Id, buffer, cancellationToken).ConfigureAwait(false);
                var header = new NewMessage
                {
                    MessageId = parsed.MessageId,
                    InReplyTo = parsed.InReplyTo,
                    Subject = parsed.Subject,
                    FromName = parsed.FromName,
                    FromAddress = parsed.FromAddress,
                    ToText = parsed.ToText,
                    CcText = parsed.CcText,
                    Date = parsed.Date,
                    Size = buffer.Length,
                    HasAttachments = parsed.HasAttachments,
                    Preview = parsed.Preview,
                    Pop3Uidl = entry.Uidl,
                };
                var id = messages.InsertOne(account.Id, inbox.Id, header);
                messages.SetBody(id, path, parsed.PlainText, parsed.Preview, parsed.HasAttachments);
                added++;
                if (newUnread.Count < 5)
                {
                    newUnread.Add(header);
                }

                progress?.Report(new SyncProgress(inbox.Id, inbox.DisplayName, added, newEntries.Count));
            }
        }

        foreach (var folder in localFolders)
        {
            folders.RefreshCounts(folder.Id);
        }

        var result = new FolderSyncResult
        {
            FolderId = inbox.Id,
            Added = added,
            IsInitial = isInitial,
            Role = FolderRole.Inbox,
            NewUnread = newUnread,
        };
        return new AccountSyncResult(account.Id, [result], errors);
    }
}

public sealed class Pop3SessionFactory(IAccountSecretProvider secrets) : IPop3SessionFactory
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    public async Task<IPop3Session> ConnectAsync(Account account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        var client = new Pop3Client { Timeout = (int)Timeout.TotalMilliseconds };
        try
        {
            await client.ConnectAsync(account.Incoming.Host, account.Incoming.Port, ImapSessionFactory.ToSocketOptions(account.Incoming.Security), cancellationToken)
                .ConfigureAwait(false);
            var secret = await secrets.GetSecretAsync(account, cancellationToken).ConfigureAwait(false);
            if (account.AuthMethod == AuthMethod.Password)
            {
                await client.AuthenticateAsync(account.UserName, secret, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await client.AuthenticateAsync(new SaslMechanismOAuth2(account.UserName, secret), cancellationToken).ConfigureAwait(false);
            }

            if (!client.Capabilities.HasFlag(Pop3Capabilities.UIDL))
            {
                throw new NotSupportedException("この POP3 サーバーは UIDL に対応していないため、受信済みのメールを区別できません。");
            }

            return new Session(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private sealed class Session(Pop3Client client) : IPop3Session
    {
        public async Task<IReadOnlyList<Pop3Entry>> ListAsync(CancellationToken cancellationToken)
        {
            if (client.Count == 0)
            {
                return [];
            }

            var uids = await client.GetMessageUidsAsync(cancellationToken).ConfigureAwait(false);
            var sizes = await client.GetMessageSizesAsync(cancellationToken).ConfigureAwait(false);
            return [.. uids.Select((uid, i) => new Pop3Entry(i, uid, i < sizes.Count ? sizes[i] : 0))];
        }

        public async Task CopyMessageToAsync(int index, Stream destination, CancellationToken cancellationToken)
        {
            using var stream = await client.GetStreamAsync(index, false, cancellationToken).ConfigureAwait(false);
            await stream.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }

        public Task DeleteAsync(IReadOnlyList<int> indexes, CancellationToken cancellationToken)
            => client.DeleteMessagesAsync([.. indexes], cancellationToken);

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (client.IsConnected)
                {
                    // QUIT で削除が確定する
                    await client.DisconnectAsync(true).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or Pop3ProtocolException or Pop3CommandException)
            {
            }
            finally
            {
                client.Dispose();
            }
        }
    }
}
