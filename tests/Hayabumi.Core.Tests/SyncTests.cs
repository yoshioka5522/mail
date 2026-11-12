using Hayabumi.Core.Security;
using Hayabumi.Core.Storage;
using Hayabumi.Core.Sync;

namespace Hayabumi.Core.Tests;

public sealed class SyncTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly FakeMailServer _server = new();

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(Folder Local, FolderSyncResult Result)> SyncInboxAsync(FakeFolder remoteFolder, Folder local, SyncOptions? options = null)
    {
        var account = _db.Accounts.Get(local.AccountId)!;
        await using var session = await _server.ConnectAsync(account, Ct);
        await using var remote = await session.OpenFolderAsync(remoteFolder.FullName, false, Ct);
        var result = await _db.CreateFolderSynchronizer(options).SyncAsync(remote, local, null, Ct);
        return (_db.Folders.Get(local.Id)!, result);
    }

    [Fact]
    public async Task Initial_sync_fetches_headers_newest_first_in_batches()
    {
        var account = _db.AddAccount();
        var inbox = _server.AddFolder("INBOX", FolderRole.Inbox);
        for (var i = 0; i < 5; i++)
        {
            inbox.Add(FakeMessage.Create($"メール{i}", minutesAgo: 10 - i));
        }

        var local = _db.Folders.Upsert(account.Id, "INBOX", "受信トレイ", '/', FolderRole.Inbox);
        var (updated, result) = await SyncInboxAsync(inbox, local, new SyncOptions { HeaderBatchSize = 2 });

        Assert.Equal(5, result.Added);
        Assert.Equal([[5u, 4u], [3u, 2u], [1u]], inbox.HeaderFetches);
        Assert.Equal(5, updated.TotalCount);
        Assert.Equal(5, updated.UnreadCount);
        Assert.Equal(1000u, updated.UidValidity);
        Assert.Equal("メール4", _db.Messages.GetPage(local.Id, 0, 1)[0].Subject);
    }

    [Fact]
    public async Task Incremental_sync_adds_new_updates_flags_and_removes_deleted()
    {
        var account = _db.AddAccount();
        var inbox = _server.AddFolder("INBOX", FolderRole.Inbox);
        var a = inbox.Add(FakeMessage.Create("A"));
        var b = inbox.Add(FakeMessage.Create("B"));
        inbox.Add(FakeMessage.Create("C"));
        var local = _db.Folders.Upsert(account.Id, "INBOX", "受信トレイ", '/', FolderRole.Inbox);
        (local, _) = await SyncInboxAsync(inbox, local);

        inbox.SetFlags(a, MessageFlags.Seen | MessageFlags.Flagged);
        inbox.Messages.Remove(b);
        inbox.Add(FakeMessage.Create("D"));
        inbox.HeaderFetches.Clear();

        var (updated, result) = await SyncInboxAsync(inbox, local);

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Removed);
        Assert.Equal([[4u]], inbox.HeaderFetches);
        Assert.Equal(["A", "C", "D"], _db.Messages.GetPage(local.Id, 0, 10).Select(m => m.Subject).Order());
        var itemA = _db.Messages.GetPage(local.Id, 0, 10).Single(m => m.Subject == "A");
        Assert.False(itemA.IsUnread);
        Assert.True(itemA.IsFlagged);
        Assert.Equal(2, updated.UnreadCount);
    }

    [Fact]
    public async Task Sync_without_changes_fetches_nothing_new()
    {
        var account = _db.AddAccount();
        var inbox = _server.AddFolder("INBOX", FolderRole.Inbox);
        inbox.Add(FakeMessage.Create("A"));
        var local = _db.Folders.Upsert(account.Id, "INBOX", "受信トレイ", '/', FolderRole.Inbox);
        (local, _) = await SyncInboxAsync(inbox, local);
        inbox.HeaderFetches.Clear();

        var (_, result) = await SyncInboxAsync(inbox, local);

        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Removed);
        Assert.Empty(inbox.HeaderFetches);
    }

    [Fact]
    public async Task Condstore_server_only_fetches_changed_flags()
    {
        var account = _db.AddAccount();
        var inbox = _server.AddFolder("INBOX", FolderRole.Inbox, supportsModSeq: true);
        var a = inbox.Add(FakeMessage.Create("A"));
        inbox.Add(FakeMessage.Create("B"));
        var local = _db.Folders.Upsert(account.Id, "INBOX", "受信トレイ", '/', FolderRole.Inbox);
        (local, _) = await SyncInboxAsync(inbox, local);
        var modSeqAfterFirstSync = local.HighestModSeq;

        var (_, unchanged) = await SyncInboxAsync(inbox, local);
        Assert.Equal(0, unchanged.FlagsUpdated);
        Assert.Empty(inbox.FlagFetches);

        inbox.SetFlags(a, MessageFlags.Seen);
        var (updated, changed) = await SyncInboxAsync(inbox, local);

        Assert.Equal(1, changed.FlagsUpdated);
        Assert.Equal([modSeqAfterFirstSync], inbox.FlagFetches);
        Assert.Equal(1, updated.UnreadCount);
    }

    [Fact]
    public async Task UidValidity_change_resets_folder()
    {
        var account = _db.AddAccount();
        var inbox = _server.AddFolder("INBOX", FolderRole.Inbox);
        inbox.Add(FakeMessage.Create("旧"));
        var local = _db.Folders.Upsert(account.Id, "INBOX", "受信トレイ", '/', FolderRole.Inbox);
        (local, _) = await SyncInboxAsync(inbox, local);

        inbox.Messages.Clear();
        inbox.UidValidity = 2000;
        inbox.NextUid = 1;
        inbox.Add(FakeMessage.Create("新"));

        var (updated, result) = await SyncInboxAsync(inbox, local);

        Assert.True(result.Reset);
        Assert.Equal(["新"], _db.Messages.GetPage(local.Id, 0, 10).Select(m => m.Subject));
        Assert.Equal(2000u, updated.UidValidity);
    }

    [Fact]
    public async Task Body_download_saves_eml_and_indexes_body()
    {
        var account = _db.AddAccount();
        var inbox = _server.AddFolder("INBOX", FolderRole.Inbox);
        inbox.Add(FakeMessage.Create("お知らせ", body: "来週の打ち合わせは会議室Bで行います。"));
        var local = _db.Folders.Upsert(account.Id, "INBOX", "受信トレイ", '/', FolderRole.Inbox);
        (local, _) = await SyncInboxAsync(inbox, local);

        await using var session = await _server.ConnectAsync(account, Ct);
        await using var remote = await session.OpenFolderAsync("INBOX", false, Ct);
        var count = await _db.CreateFolderSynchronizer().DownloadBodiesAsync(remote, local, Ct);

        Assert.Equal(1, count);
        var item = _db.Messages.GetPage(local.Id, 0, 1)[0];
        Assert.Equal(BodyState.Downloaded, item.BodyState);
        Assert.True(_db.Files.Exists(item.FilePath!));
        Assert.Equal("来週の打ち合わせは会議室Bで行います。", item.Preview);
        Assert.Single(_db.Messages.Search("会議室"));
        Assert.Empty(_db.Messages.GetMessagesWithoutBody(local.Id, 10));
    }

    [Fact]
    public async Task Account_sync_creates_folders_and_replays_pending_operations()
    {
        var account = _db.AddAccount();
        var inbox = _server.AddFolder("INBOX", FolderRole.Inbox);
        var trash = _server.AddFolder("Trash", FolderRole.Trash);
        var container = _server.AddFolder("[Gmail]");
        container.CanSelect = false;
        inbox.Add(FakeMessage.Create("残す"));
        inbox.Add(FakeMessage.Create("消す"));

        var synchronizer = new AccountSynchronizer(_server, _db.Folders, _db.Messages, _db.Pending, _db.Files, new SyncOptions());
        var first = await synchronizer.SyncAsync(account, null, Ct);
        Assert.Empty(first.Errors);
        Assert.Equal(["INBOX", "Trash"], _db.Folders.GetByAccount(account.Id).Select(f => f.FullName));

        // オフライン中に既読にして、もう 1 通を削除（ゴミ箱へ移動）した想定
        var actions = new MailActions(_db.Folders, _db.Messages, _db.Pending, _db.Files);
        var localInbox = _db.Folders.GetByAccount(account.Id).Single(f => f.Role == FolderRole.Inbox);
        var items = _db.Messages.GetPage(localInbox.Id, 0, 10);
        actions.MarkRead([items.Single(m => m.Subject == "残す")], read: true);
        actions.Delete([items.Single(m => m.Subject == "消す")]);

        Assert.Equal(["残す"], _db.Messages.GetPage(localInbox.Id, 0, 10).Select(m => m.Subject));
        Assert.Equal(2, _db.Pending.GetPending(account.Id).Count);

        var second = await synchronizer.SyncAsync(account, null, Ct);

        Assert.Empty(second.Errors);
        Assert.Empty(_db.Pending.GetPending(account.Id));
        Assert.Equal(MessageFlags.Seen, inbox.Messages.Values.Single().Flags);
        Assert.Equal("消す", trash.Messages.Values.Single().Header.Subject);
        var localTrash = _db.Folders.GetByAccount(account.Id).Single(f => f.Role == FolderRole.Trash);
        Assert.Equal(["消す"], _db.Messages.GetPage(localTrash.Id, 0, 10).Select(m => m.Subject));
        Assert.False(_db.Messages.GetPage(localInbox.Id, 0, 10).Single().IsUnread);
    }

    [Fact]
    public async Task Folder_removed_on_server_is_removed_locally()
    {
        var account = _db.AddAccount();
        _server.AddFolder("INBOX", FolderRole.Inbox);
        var old = _server.AddFolder("Old");
        old.Add(FakeMessage.Create("古いフォルダのメール"));
        var synchronizer = new AccountSynchronizer(_server, _db.Folders, _db.Messages, _db.Pending, _db.Files, new SyncOptions());
        await synchronizer.SyncAsync(account, null, Ct);

        _server.Folders.Remove("Old");
        await synchronizer.SyncAsync(account, null, Ct);

        Assert.Equal(["INBOX"], _db.Folders.GetByAccount(account.Id).Select(f => f.FullName));
        Assert.Empty(_db.Messages.Search("古い"));
    }

    [Fact]
    public async Task Stored_password_provider_reads_credential_store()
    {
        var store = new InMemoryCredentialStore();
        var account = _db.AddAccount();
        var provider = new StoredPasswordSecretProvider(store);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetSecretAsync(account, Ct));
        store.Write(account.CredentialKey, account.UserName, "secret");
        Assert.Equal("secret", await provider.GetSecretAsync(account, Ct));
    }

    [Theory]
    [InlineData("INBOX", '/', FolderRole.Inbox)]
    [InlineData("Sent", '/', FolderRole.Sent)]
    [InlineData("INBOX.Sent", '.', FolderRole.Sent)]
    [InlineData("送信済みアイテム", '/', FolderRole.Sent)]
    [InlineData("Deleted Items", '/', FolderRole.Trash)]
    [InlineData("迷惑メール", '/', FolderRole.Junk)]
    [InlineData("Projects/Sent", '/', FolderRole.None)]
    [InlineData("仕事", '/', FolderRole.None)]
    public void Folder_role_is_guessed_from_name(string fullName, char delimiter, FolderRole expected)
    {
        Assert.Equal(expected, FolderRoleGuesser.Guess(fullName, delimiter));
    }
}

public class ServerPresetTests
{
    [Theory]
    [InlineData("taro@gmail.com", "imap.gmail.com")]
    [InlineData("taro@Yahoo.co.jp", "imap.mail.yahoo.co.jp")]
    [InlineData("taro@example.co.jp", "imap.example.co.jp")]
    public void Preset_is_found_by_domain(string email, string expectedHost)
    {
        Assert.Equal(expectedHost, Hayabumi.Core.Accounts.ServerPresets.Find(email)?.Incoming.Host);
    }

    [Theory]
    [InlineData("")]
    [InlineData("taro")]
    [InlineData("taro@")]
    public void Invalid_address_has_no_preset(string email)
    {
        Assert.Null(Hayabumi.Core.Accounts.ServerPresets.Find(email));
    }
}

public sealed class CopiesTests : IDisposable
{
    private readonly TestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Reading_in_inbox_marks_copies_in_other_folders_and_flush_pushes_to_server()
    {
        var server = new FakeMailServer();
        var account = _db.AddAccount();
        var inbox = server.AddFolder("INBOX", FolderRole.Inbox);
        var all = server.AddFolder("[Gmail]/すべてのメール", FolderRole.All);
        var unread = server.AddFolder("未読");
        var message = FakeMessage.Create("同じメール");
        foreach (var folder in new[] { inbox, all, unread })
        {
            folder.Add(new FakeMessage(message.Header with { MessageId = "same@example.jp" }, message.Raw));
        }

        var synchronizer = new AccountSynchronizer(server, _db.Folders, _db.Messages, _db.Pending, _db.Files, new SyncOptions());
        await synchronizer.SyncAsync(account, null, TestContext.Current.CancellationToken);
        var localInbox = _db.Folders.GetByAccount(account.Id).Single(f => f.Role == FolderRole.Inbox);
        var item = _db.Messages.GetPage(localInbox.Id, 0, 1)[0];

        var actions = new MailActions(_db.Folders, _db.Messages, _db.Pending, _db.Files);
        actions.MarkRead([item], read: true);

        foreach (var folder in _db.Folders.GetByAccount(account.Id))
        {
            Assert.False(_db.Messages.GetPage(folder.Id, 0, 1)[0].IsUnread);
            Assert.Equal(0, _db.Folders.Get(folder.Id)!.UnreadCount);
        }

        var errors = await synchronizer.FlushPendingAsync(account, TestContext.Current.CancellationToken);
        Assert.Empty(errors);
        Assert.Empty(_db.Pending.GetPending(account.Id));
        Assert.All(new[] { inbox, all, unread }, f => Assert.Equal(MessageFlags.Seen, f.Messages.Values.Single().Flags));
    }
}
