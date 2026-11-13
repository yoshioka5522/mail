using Hayabumi.Core.Storage;

namespace Hayabumi.Core.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly TestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    private (long AccountId, long FolderId) CreateInbox()
    {
        var account = _db.AddAccount();
        var folder = _db.Folders.Upsert(account.Id, "INBOX", "受信トレイ", '/', FolderRole.Inbox);
        return (account.Id, folder.Id);
    }

    private static NewMessage Message(uint uid, string subject, string preview = "", int minutesAgo = 0, MessageFlags flags = MessageFlags.None) => new()
    {
        Uid = uid,
        Subject = subject,
        FromName = "山田 太郎",
        FromAddress = "taro@example.jp",
        ToText = "user@example.jp",
        Date = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
        Preview = preview,
        Flags = flags,
    };

    [Fact]
    public void Initialize_is_idempotent()
    {
        _db.Database.Initialize();
        _db.Database.Initialize();
        Assert.Empty(_db.Accounts.GetAll());
    }

    [Fact]
    public void Account_round_trip()
    {
        var account = _db.AddAccount("a@example.com");
        var loaded = _db.Accounts.Get(account.Id);
        Assert.Equal(account, loaded);
        Assert.Single(_db.Accounts.GetAll());
    }

    [Fact]
    public void New_accounts_go_last_and_order_can_be_changed()
    {
        var a = _db.AddAccount("a@example.jp");
        var b = _db.AddAccount("b@example.jp");
        var c = _db.AddAccount("c@example.jp");
        Assert.Equal(["a@example.jp", "b@example.jp", "c@example.jp"], _db.Accounts.GetAll().Select(x => x.EmailAddress));

        _db.Accounts.Update(c with { SortOrder = 0 });
        _db.Accounts.Update(a with { SortOrder = 1 });
        _db.Accounts.Update(b with { SortOrder = 2 });
        Assert.Equal(["c@example.jp", "a@example.jp", "b@example.jp"], _db.Accounts.GetAll().Select(x => x.EmailAddress));

        _db.AddAccount("d@example.jp");
        Assert.Equal("d@example.jp", _db.Accounts.GetAll()[^1].EmailAddress);
    }

    [Fact]
    public void Messages_are_listed_newest_first_and_duplicates_are_ignored()
    {
        var (accountId, folderId) = CreateInbox();
        var inserted = _db.Messages.InsertMany(accountId, folderId, [Message(1, "古い", minutesAgo: 10), Message(2, "新しい", minutesAgo: 1)]);
        var again = _db.Messages.InsertMany(accountId, folderId, [Message(2, "新しい")]);

        Assert.Equal(2, inserted);
        Assert.Equal(0, again);
        Assert.Equal(["新しい", "古い"], _db.Messages.GetPage(folderId, 0, 10).Select(m => m.Subject));
        Assert.Equal(["古い"], _db.Messages.GetPage(folderId, 1, 10).Select(m => m.Subject));
        Assert.Equal(2u, _db.Messages.GetMaxUid(folderId));
    }

    [Fact]
    public void Folder_counts_reflect_unread_messages()
    {
        var (accountId, folderId) = CreateInbox();
        _db.Messages.InsertMany(accountId, folderId, [Message(1, "a", flags: MessageFlags.Seen), Message(2, "b"), Message(3, "c")]);
        _db.Folders.RefreshCounts(folderId);

        var folder = _db.Folders.Get(folderId)!;
        Assert.Equal(3, folder.TotalCount);
        Assert.Equal(2, folder.UnreadCount);
    }

    [Theory]
    [InlineData("会議", true)]
    [InlineData("定例会議", true)]
    [InlineData("議", true)]
    [InlineData("会議室", true)]
    [InlineData("会室", false)]
    [InlineData("予約", true)]
    [InlineData("会議 予約", true)]
    [InlineData("会議 請求", false)]
    [InlineData("山田", true)]
    [InlineData("taro", true)]
    [InlineData("tar", true)]
    [InlineData("example", true)]
    [InlineData("ＴＡＲＯ", true)]
    [InlineData("山田太郎", false)]
    [InlineData("第3回", true)]
    [InlineData("第3", true)]
    [InlineData("3回", true)]
    [InlineData("第4回", false)]
    public void Japanese_search(string query, bool expected)
    {
        var (accountId, folderId) = CreateInbox();
        _db.Messages.InsertMany(accountId, folderId, [Message(1, "定例会議室の予約について（第3回）")]);

        Assert.Equal(expected, _db.Messages.Search(query).Count == 1);
    }

    [Fact]
    public void Search_body_after_download_and_delete_removes_from_index()
    {
        var (accountId, folderId) = CreateInbox();
        _db.Messages.InsertMany(accountId, folderId, [Message(1, "件名", preview: "冒頭の文章")]);
        var id = _db.Messages.GetPage(folderId, 0, 1)[0].Id;

        Assert.Single(_db.Messages.Search("冒頭"));
        _db.Messages.SetBody(id, "messages/1/aa/x.eml", "冒頭の文章\n添付の見積書をご確認ください。", "冒頭の文章", hasAttachments: true);
        Assert.Single(_db.Messages.Search("見積書"));
        Assert.True(_db.Messages.Get(id)!.HasAttachments);

        var files = _db.Messages.DeleteByUids(folderId, [1]);
        Assert.Equal(["messages/1/aa/x.eml"], files);
        Assert.Empty(_db.Messages.Search("見積書"));
        Assert.Empty(_db.Messages.Search("件名"));
    }

    [Fact]
    public void Search_can_be_limited_to_folder()
    {
        var account = _db.AddAccount();
        var inbox = _db.Folders.Upsert(account.Id, "INBOX", "受信トレイ", '/', FolderRole.Inbox);
        var sent = _db.Folders.Upsert(account.Id, "Sent", "送信済み", '/', FolderRole.Sent);
        _db.Messages.InsertMany(account.Id, inbox.Id, [Message(1, "打ち合わせ")]);
        _db.Messages.InsertMany(account.Id, sent.Id, [Message(1, "打ち合わせ")]);

        Assert.Equal(2, _db.Messages.Search("打ち合わせ").Count);
        Assert.Single(_db.Messages.Search("打ち合わせ", folderId: sent.Id));
    }

    [Fact]
    public void Deleting_account_removes_folders_messages_and_index()
    {
        var (accountId, folderId) = CreateInbox();
        _db.Messages.InsertMany(accountId, folderId, [Message(1, "削除テスト")]);
        _db.Accounts.Delete(accountId);

        Assert.Null(_db.Folders.Get(folderId));
        Assert.Empty(_db.Messages.Search("削除"));
    }

    [Fact]
    public async Task File_store_saves_and_reads_eml()
    {
        using var content = new MemoryStream("Subject: x\r\n\r\nbody"u8.ToArray());
        var path = await _db.Files.SaveAsync(1, content, TestContext.Current.CancellationToken);

        Assert.True(_db.Files.Exists(path));
        using (var reader = new StreamReader(_db.Files.OpenRead(path)))
        {
            Assert.Equal("Subject: x\r\n\r\nbody", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
        }

        _db.Files.Delete(path);
        Assert.False(_db.Files.Exists(path));
        Assert.Throws<ArgumentException>(() => _db.Files.GetFullPath("../outside.eml"));
    }

    [Fact]
    public void Data_location_can_be_changed_and_restored()
    {
        var location = new AppDataLocation(Path.Combine(_db.Root, "default"));
        Assert.Equal(location.DefaultRoot, location.Root);

        var custom = Path.Combine(_db.Root, "custom");
        location.ChangeRoot(custom);
        Assert.Equal(custom, new AppDataLocation(location.DefaultRoot).Root);

        location.ChangeRoot(location.DefaultRoot);
        Assert.Equal(location.DefaultRoot, new AppDataLocation(location.DefaultRoot).Root);
    }
}
