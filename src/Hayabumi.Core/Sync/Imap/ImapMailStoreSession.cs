using Hayabumi.Core.Accounts;
using Hayabumi.Core.Mime;
using Hayabumi.Core.Security;
using Hayabumi.Core.Storage;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using KitFlags = MailKit.MessageFlags;
using MessageFlags = Hayabumi.Core.Storage.MessageFlags;

namespace Hayabumi.Core.Sync.Imap;

public sealed class ImapSessionFactory(IAccountSecretProvider secrets) : IMailStoreSessionFactory
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    public async Task<IMailStoreSession> ConnectAsync(Account account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.IncomingProtocol != IncomingProtocol.Imap)
        {
            throw new NotSupportedException("IMAP 以外のアカウントには使えません。");
        }

        var client = new ImapClient { Timeout = (int)Timeout.TotalMilliseconds };
        try
        {
            await client.ConnectAsync(account.Incoming.Host, account.Incoming.Port, ToSocketOptions(account.Incoming.Security), cancellationToken)
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

            if (client.Capabilities.HasFlag(ImapCapabilities.UTF8Accept))
            {
                await client.EnableUTF8Async(cancellationToken).ConfigureAwait(false);
            }

            return new ImapMailStoreSession(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public static SecureSocketOptions ToSocketOptions(ConnectionSecurity security) => security switch
    {
        ConnectionSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        ConnectionSecurity.StartTls => SecureSocketOptions.StartTls,
        _ => SecureSocketOptions.None,
    };
}

public sealed class ImapMailStoreSession(ImapClient client) : IMailStoreSession
{
    public async Task<IReadOnlyList<RemoteFolderInfo>> GetFoldersAsync(CancellationToken cancellationToken)
    {
        var result = new List<RemoteFolderInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(IMailFolder folder)
        {
            if (!seen.Add(folder.FullName))
            {
                return;
            }

            var canSelect = !folder.Attributes.HasFlag(FolderAttributes.NoSelect) && !folder.Attributes.HasFlag(FolderAttributes.NonExistent);
            var role = folder == client.Inbox ? FolderRole.Inbox : RoleFromAttributes(folder.Attributes);
            if (role == FolderRole.None)
            {
                role = FolderRoleGuesser.Guess(folder.FullName, folder.DirectorySeparator == '\0' ? null : folder.DirectorySeparator);
            }

            var name = role == FolderRole.Inbox ? "受信トレイ" : folder.Name;
            result.Add(new RemoteFolderInfo(folder.FullName, name, folder.DirectorySeparator == '\0' ? null : folder.DirectorySeparator, role, canSelect));
        }

        Add(client.Inbox);
        var namespaces = client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces : [new FolderNamespace('/', string.Empty)];
        foreach (var ns in namespaces)
        {
            foreach (var folder in await client.GetFoldersAsync(ns, StatusItems.None, subscribedOnly: false, cancellationToken).ConfigureAwait(false))
            {
                Add(folder);
            }
        }

        return result;
    }

    public async Task<IRemoteFolder> OpenFolderAsync(string fullName, bool writable, CancellationToken cancellationToken)
    {
        var folder = string.Equals(fullName, client.Inbox.FullName, StringComparison.OrdinalIgnoreCase)
            ? client.Inbox
            : await client.GetFolderAsync(fullName, cancellationToken).ConfigureAwait(false);
        await folder.OpenAsync(writable ? FolderAccess.ReadWrite : FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
        return new ImapRemoteFolder(client, folder);
    }

    public async Task<bool?> WaitForChangesAsync(string folderFullName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!client.Capabilities.HasFlag(ImapCapabilities.Idle))
        {
            return null;
        }

        var folder = string.Equals(folderFullName, client.Inbox.FullName, StringComparison.OrdinalIgnoreCase)
            ? client.Inbox
            : await client.GetFolderAsync(folderFullName, cancellationToken).ConfigureAwait(false);
        if (!folder.IsOpen)
        {
            await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
        }

        var changed = false;
        using var done = new CancellationTokenSource(timeout);
        void OnChanged(object? sender, EventArgs e)
        {
            changed = true;
            done.Cancel();
        }

        folder.CountChanged += OnChanged;
        folder.MessageExpunged += OnChanged;
        folder.MessageFlagsChanged += OnChanged;
        try
        {
            await client.IdleAsync(done.Token, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            folder.CountChanged -= OnChanged;
            folder.MessageExpunged -= OnChanged;
            folder.MessageFlagsChanged -= OnChanged;
        }

        return changed;
    }

    public async Task<RemoteFolderInfo> CreateFolderAsync(string? parentFullName, string name, CancellationToken cancellationToken)
    {
        var parent = string.IsNullOrEmpty(parentFullName)
            ? client.GetFolder(client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces[0] : new FolderNamespace('/', string.Empty))
            : await client.GetFolderAsync(parentFullName, cancellationToken).ConfigureAwait(false);
        var created = await parent.CreateAsync(name, isMessageFolder: true, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("フォルダを作れませんでした。");
        await created.SubscribeAsync(cancellationToken).ConfigureAwait(false);
        var delimiter = created.DirectorySeparator == '\0' ? (char?)null : created.DirectorySeparator;
        return new RemoteFolderInfo(created.FullName, created.Name, delimiter, FolderRole.None, CanSelect: true);
    }

    public async Task<string> RenameFolderAsync(string fullName, string newName, CancellationToken cancellationToken)
    {
        var folder = await client.GetFolderAsync(fullName, cancellationToken).ConfigureAwait(false);
        var parent = folder.ParentFolder ?? client.GetFolder(client.PersonalNamespaces[0]);
        await folder.RenameAsync(parent, newName, cancellationToken).ConfigureAwait(false);
        return folder.FullName;
    }

    public async Task DeleteFolderAsync(string fullName, CancellationToken cancellationToken)
    {
        var folder = await client.GetFolderAsync(fullName, cancellationToken).ConfigureAwait(false);
        try
        {
            await folder.UnsubscribeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ImapCommandException)
        {
            // 購読していないフォルダ
        }

        await folder.DeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AppendAsync(string folderFullName, Stream message, MessageFlags flags, CancellationToken cancellationToken)
    {
        var folder = await client.GetFolderAsync(folderFullName, cancellationToken).ConfigureAwait(false);
        var mime = await MessageParser.LoadAsync(message, cancellationToken).ConfigureAwait(false);
        var request = new AppendRequest(mime, ImapRemoteFolder.ToKitFlags(flags)) { InternalDate = mime.Date };
        await folder.AppendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (client.IsConnected)
            {
                await client.DisconnectAsync(true).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ImapProtocolException or ImapCommandException)
        {
            // 切断時のエラーは無視する
        }
        finally
        {
            client.Dispose();
        }
    }

    private static FolderRole RoleFromAttributes(FolderAttributes attributes)
    {
        if (attributes.HasFlag(FolderAttributes.Inbox)) return FolderRole.Inbox;
        if (attributes.HasFlag(FolderAttributes.Sent)) return FolderRole.Sent;
        if (attributes.HasFlag(FolderAttributes.Drafts)) return FolderRole.Drafts;
        if (attributes.HasFlag(FolderAttributes.Trash)) return FolderRole.Trash;
        if (attributes.HasFlag(FolderAttributes.Junk)) return FolderRole.Junk;
        if (attributes.HasFlag(FolderAttributes.Archive)) return FolderRole.Archive;
        if (attributes.HasFlag(FolderAttributes.All)) return FolderRole.All;
        return FolderRole.None;
    }
}

internal sealed class ImapRemoteFolder(ImapClient client, IMailFolder folder) : IRemoteFolder
{
    private const string ForwardedKeyword = "$Forwarded";

    private static readonly HeaderSet s_summaryHeaders = new(
    [
        HeaderId.From, HeaderId.To, HeaderId.Cc, HeaderId.Subject, HeaderId.Date,
        HeaderId.MessageId, HeaderId.InReplyTo, HeaderId.References,
    ]);

    public string FullName => folder.FullName;

    public uint UidValidity => folder.UidValidity;

    public ulong? HighestModSeq => folder.Supports(FolderFeature.ModSequences) ? folder.HighestModSeq : null;

    public int Count => folder.Count;

    public async Task<IReadOnlyList<uint>> GetUidsAsync(uint minUid, CancellationToken cancellationToken)
    {
        var query = minUid <= 1
            ? SearchQuery.All
            : SearchQuery.Uids(new UniqueIdRange(new UniqueId(folder.UidValidity, minUid), UniqueId.MaxValue));
        var uids = await folder.SearchAsync(query, cancellationToken).ConfigureAwait(false);
        return [.. uids.Select(u => u.Id)];
    }

    public async Task<IReadOnlyList<NewMessage>> FetchHeadersAsync(IReadOnlyList<uint> uids, bool includePreview, CancellationToken cancellationToken)
    {
        // ENVELOPE は 8bit のヘッダー（charset 指定のない Shift_JIS の件名など）を正しく復号できないため、
        // 必要なヘッダーをそのまま取得して MessageParser で解析する
        var items = MessageSummaryItems.UniqueId | MessageSummaryItems.Flags
            | MessageSummaryItems.Size | MessageSummaryItems.BodyStructure | MessageSummaryItems.InternalDate;
        if (includePreview)
        {
            items |= MessageSummaryItems.PreviewText;
        }

        if (HighestModSeq is not null)
        {
            items |= MessageSummaryItems.ModSeq;
        }

        var request = new FetchRequest(items) { Headers = s_summaryHeaders };
        var summaries = await folder.FetchAsync(ToUniqueIds(uids), request, cancellationToken).ConfigureAwait(false);
        return [.. summaries.Select(ToNewMessage)];
    }

    public async Task<IReadOnlyList<RemoteFlags>> FetchFlagsAsync(IReadOnlyList<uint> uids, ulong? changedSince, CancellationToken cancellationToken)
    {
        var items = MessageSummaryItems.UniqueId | MessageSummaryItems.Flags;
        if (HighestModSeq is not null)
        {
            items |= MessageSummaryItems.ModSeq;
        }

        var request = new FetchRequest(items);
        if (changedSince is not null)
        {
            request.ChangedSince = changedSince;
        }

        var summaries = await folder.FetchAsync(ToUniqueIds(uids), request, cancellationToken).ConfigureAwait(false);
        return [.. summaries.Select(s => new RemoteFlags(s.UniqueId.Id, ToFlags(s.Flags, s.Keywords), [.. s.Keywords], s.ModSeq))];
    }

    public async Task CopyMessageToAsync(uint uid, Stream destination, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = await folder.GetStreamAsync(new UniqueId(folder.UidValidity, uid), cancellationToken).ConfigureAwait(false);
            await stream.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        catch (MessageNotFoundException ex)
        {
            throw new RemoteMessageNotFoundException($"UID {uid} のメールがサーバーにありません。", ex);
        }
    }

    public async Task SetFlagsAsync(IReadOnlyList<uint> uids, MessageFlags flags, bool add, CancellationToken cancellationToken)
    {
        var kitFlags = ToKitFlags(flags);
        var keywords = flags.HasFlag(MessageFlags.Forwarded) ? new HashSet<string> { ForwardedKeyword } : null;
        var request = keywords is null
            ? new StoreFlagsRequest(add ? StoreAction.Add : StoreAction.Remove, kitFlags) { Silent = true }
            : new StoreFlagsRequest(add ? StoreAction.Add : StoreAction.Remove, kitFlags, keywords) { Silent = true };
        await folder.StoreAsync(ToUniqueIds(uids), request, cancellationToken).ConfigureAwait(false);
    }

    public async Task MoveToAsync(IReadOnlyList<uint> uids, string destinationFullName, CancellationToken cancellationToken)
    {
        var destination = await client.GetFolderAsync(destinationFullName, cancellationToken).ConfigureAwait(false);
        await folder.MoveToAsync(ToUniqueIds(uids), destination, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeletePermanentlyAsync(IReadOnlyList<uint> uids, CancellationToken cancellationToken)
    {
        var ids = ToUniqueIds(uids);
        await folder.StoreAsync(ids, new StoreFlagsRequest(StoreAction.Add, KitFlags.Deleted) { Silent = true }, cancellationToken).ConfigureAwait(false);
        if (client.Capabilities.HasFlag(ImapCapabilities.UidPlus))
        {
            await folder.ExpungeAsync(ids, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await folder.ExpungeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (folder.IsOpen && client.IsConnected)
        {
            try
            {
                await folder.CloseAsync(false).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ImapProtocolException or ImapCommandException)
            {
            }
        }
    }

    private List<UniqueId> ToUniqueIds(IReadOnlyList<uint> uids) => [.. uids.Select(u => new UniqueId(folder.UidValidity, u))];

    private static NewMessage ToNewMessage(IMessageSummary s)
    {
        var headers = s.Headers is null ? null : MessageParser.ParseHeaders(s.Headers);
        var date = headers is not null && headers.Date != DateTimeOffset.UnixEpoch
            ? headers.Date
            : s.InternalDate ?? DateTimeOffset.UnixEpoch;
        return new NewMessage
        {
            Uid = s.UniqueId.Id,
            MessageId = headers?.MessageId,
            InReplyTo = headers?.InReplyTo,
            Subject = headers?.Subject ?? string.Empty,
            FromName = headers?.FromName ?? string.Empty,
            FromAddress = headers?.FromAddress ?? string.Empty,
            ToText = headers?.ToText ?? string.Empty,
            CcText = headers?.CcText ?? string.Empty,
            Date = date,
            Size = s.Size ?? 0,
            Flags = ToFlags(s.Flags, s.Keywords),
            Keywords = s.Keywords is null ? [] : [.. s.Keywords],
            ModSeq = s.ModSeq,
            HasAttachments = s.Body is not null && s.Attachments.Any(),
            Preview = s.PreviewText is null ? string.Empty : HtmlText.MakePreview(s.PreviewText),
        };
    }

    private static MessageFlags ToFlags(KitFlags? flags, IEnumerable<string>? keywords)
    {
        var result = MessageFlags.None;
        if (flags is { } f)
        {
            if (f.HasFlag(KitFlags.Seen)) result |= MessageFlags.Seen;
            if (f.HasFlag(KitFlags.Answered)) result |= MessageFlags.Answered;
            if (f.HasFlag(KitFlags.Flagged)) result |= MessageFlags.Flagged;
            if (f.HasFlag(KitFlags.Deleted)) result |= MessageFlags.Deleted;
            if (f.HasFlag(KitFlags.Draft)) result |= MessageFlags.Draft;
        }

        if (keywords is not null && keywords.Contains(ForwardedKeyword))
        {
            result |= MessageFlags.Forwarded;
        }

        return result;
    }

    internal static KitFlags ToKitFlags(MessageFlags flags)
    {
        var result = KitFlags.None;
        if (flags.HasFlag(MessageFlags.Seen)) result |= KitFlags.Seen;
        if (flags.HasFlag(MessageFlags.Answered)) result |= KitFlags.Answered;
        if (flags.HasFlag(MessageFlags.Flagged)) result |= KitFlags.Flagged;
        if (flags.HasFlag(MessageFlags.Deleted)) result |= KitFlags.Deleted;
        if (flags.HasFlag(MessageFlags.Draft)) result |= KitFlags.Draft;
        return result;
    }
}
