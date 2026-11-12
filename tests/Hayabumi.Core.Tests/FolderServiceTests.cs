using Hayabumi.Core.Accounts;
using Hayabumi.Core.Storage;
using Hayabumi.Core.Sync;
using Hayabumi.Core.Sync.Pop3;

namespace Hayabumi.Core.Tests;

public sealed class FolderServiceTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly FakeMailServer _server = new();

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FolderService Service => new(_server, _db.Folders, _db.Messages, _db.Files);

    [Fact]
    public async Task Imap_folder_create_rename_delete_and_messages_follow_rename()
    {
        var account = _db.AddAccount();
        _server.AddFolder("INBOX", FolderRole.Inbox);
        var created = await Service.CreateAsync(account, null, "取引先", Ct);
        Assert.Contains("取引先", _server.Folders.Keys);

        _server.Folders["取引先"].Add(FakeMessage.Create("見積"));
        var synchronizer = new AccountSynchronizer(_server, _db.Folders, _db.Messages, _db.Pending, _db.Files, new SyncOptions());
        await synchronizer.SyncAsync(account, null, Ct);
        Assert.Single(_db.Messages.GetPage(created.Id, 0, 10));

        var renamed = await Service.RenameAsync(account, created, "取引先A", Ct);
        Assert.Equal("取引先A", renamed.FullName);
        Assert.Equal(created.Id, renamed.Id);
        var after = await synchronizer.SyncAsync(account, null, Ct);
        Assert.Empty(after.Errors);
        Assert.Equal(0, after.Added);
        Assert.Single(_db.Messages.GetPage(renamed.Id, 0, 10));

        await Service.DeleteAsync(account, renamed, Ct);
        Assert.DoesNotContain("取引先A", _server.Folders.Keys);
        Assert.Null(_db.Folders.Get(renamed.Id));
        Assert.Empty(_db.Messages.Search("見積"));
    }

    [Fact]
    public async Task Special_folders_cannot_be_renamed_and_names_are_validated()
    {
        var account = _db.AddAccount();
        var inbox = _db.Folders.Upsert(account.Id, "INBOX", "受信トレイ", '/', FolderRole.Inbox);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.RenameAsync(account, inbox, "x", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Service.CreateAsync(account, null, "a/b", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Service.CreateAsync(account, null, "  ", Ct));
    }

    [Fact]
    public async Task Pop3_folders_are_local_and_messages_can_be_moved_into_them()
    {
        var account = _db.Accounts.Add(new Account
        {
            DisplayName = "p",
            EmailAddress = "me@example.jp",
            IncomingProtocol = IncomingProtocol.Pop3,
            Incoming = new ServerEndpoint("pop.example.jp", 995, ConnectionSecurity.SslOnConnect),
            Outgoing = new ServerEndpoint("smtp.example.jp", 587, ConnectionSecurity.StartTls),
            UserName = "me",
            AuthMethod = AuthMethod.Password,
        });
        var inbox = Pop3Synchronizer.EnsureFolders(_db.Folders, account.Id).Single(f => f.Role == FolderRole.Inbox);
        _db.Messages.InsertMany(account.Id, inbox.Id, [new NewMessage { Subject = "整理するメール", Date = DateTimeOffset.UtcNow }]);

        var folder = await Service.CreateAsync(account, null, "保存", Ct);
        Assert.True(folder.IsLocal);
        Assert.Equal(0, _server.ConnectCount);

        var actions = new MailActions(_db.Folders, _db.Messages, _db.Pending, _db.Files);
        actions.Move(_db.Messages.GetPage(inbox.Id, 0, 10), folder);
        Assert.Single(_db.Messages.GetPage(folder.Id, 0, 10));
        Assert.Empty(_db.Pending.GetPending(account.Id));

        var renamed = await Service.RenameAsync(account, folder, "保存済み", Ct);
        Assert.Equal("保存済み", renamed.DisplayName);
        await Service.DeleteAsync(account, renamed, Ct);
        Assert.Empty(_db.Messages.Search("整理"));
    }
}
