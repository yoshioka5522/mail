using Hayabumi.Core.Storage;

namespace Hayabumi.Core.Tests;

public sealed class UnifiedViewTests : IDisposable
{
    private readonly TestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Unified_views_span_accounts_and_skip_trash_and_duplicates()
    {
        var a = _db.AddAccount("a@example.jp");
        var b = _db.AddAccount("b@example.jp");
        var inboxA = _db.Folders.Upsert(a.Id, "INBOX", "受信トレイ", '/', FolderRole.Inbox);
        var allA = _db.Folders.Upsert(a.Id, "[Gmail]/All", "すべて", '/', FolderRole.All);
        var labelA = _db.Folders.Upsert(a.Id, "仕事", "仕事", '/', FolderRole.None);
        var trashA = _db.Folders.Upsert(a.Id, "Trash", "ゴミ箱", '/', FolderRole.Trash);
        var inboxB = _db.Folders.Upsert(b.Id, "INBOX", "受信トレイ", '/', FolderRole.Inbox);
        var now = DateTimeOffset.UtcNow;

        NewMessage M(uint uid, string subject, int minutes, MessageFlags flags = MessageFlags.None, string? id = null)
            => new() { Uid = uid, Subject = subject, Date = now.AddMinutes(-minutes), Flags = flags, MessageId = id };

        _db.Messages.InsertMany(a.Id, inboxA.Id, [M(1, "A受信・未読", 5, id: "x@a"), M(2, "A受信・既読", 3, MessageFlags.Seen)]);
        _db.Messages.InsertMany(a.Id, allA.Id, [M(1, "A受信・未読", 5, id: "x@a")]);
        _db.Messages.InsertMany(a.Id, labelA.Id, [M(1, "A受信・未読", 5, id: "x@a"), M(2, "ラベル・フラグ", 8, MessageFlags.Seen | MessageFlags.Flagged)]);
        _db.Messages.InsertMany(a.Id, trashA.Id, [M(1, "ゴミ箱・未読", 1, MessageFlags.Flagged)]);
        _db.Messages.InsertMany(b.Id, inboxB.Id, [M(1, "B受信・未読", 4)]);

        Assert.Equal(["A受信・既読", "B受信・未読", "A受信・未読"], _db.Messages.GetUnifiedPage(UnifiedView.AllInboxes, 0, 10).Select(m => m.Subject));
        Assert.Equal(3, _db.Messages.CountUnified(UnifiedView.AllInboxes));

        Assert.Equal(["B受信・未読", "A受信・未読"], _db.Messages.GetUnifiedPage(UnifiedView.Unread, 0, 10).Select(m => m.Subject));
        Assert.Equal(2, _db.Messages.CountUnified(UnifiedView.Unread));

        Assert.Equal(["ラベル・フラグ"], _db.Messages.GetUnifiedPage(UnifiedView.Flagged, 0, 10).Select(m => m.Subject));
        Assert.Equal(["B受信・未読"], _db.Messages.GetUnifiedPage(UnifiedView.AllInboxes, 1, 1).Select(m => m.Subject));
    }
}
