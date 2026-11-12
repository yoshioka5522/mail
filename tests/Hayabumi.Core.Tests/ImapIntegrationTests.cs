using System.Text;
using Hayabumi.Core.Accounts;
using Hayabumi.Core.Security;
using Hayabumi.Core.Storage;
using Hayabumi.Core.Sync;
using Hayabumi.Core.Sync.Imap;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;
using MessageFlags = Hayabumi.Core.Storage.MessageFlags;

namespace Hayabumi.Core.Tests;

/// <summary>
/// 実際の IMAP サーバーを相手にした結合テスト。
/// 環境変数 HAYABUMI_IMAP_TEST（例: 127.0.0.1:10143:user:password、暗号化なし）を設定したときだけ実行する。
/// 指定したユーザーのメールボックスは中身を消して使うため、テスト専用のサーバーで実行すること。
/// </summary>
public sealed class ImapIntegrationTests : IDisposable
{
    private readonly TestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (string Host, int Port, string User, string Password)? Settings()
    {
        var value = Environment.GetEnvironmentVariable("HAYABUMI_IMAP_TEST");
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var parts = value.Split(':');
        return (parts[0], int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), parts[2], parts[3]);
    }

    [Fact]
    public async Task Full_sync_cycle_against_real_server()
    {
        var settings = Settings();
        Assert.SkipWhen(settings is null, "HAYABUMI_IMAP_TEST が設定されていません");
        var (host, port, user, password) = settings!.Value;

        using var admin = new ImapClient();
        await admin.ConnectAsync(host, port, SecureSocketOptions.None, Ct);
        await admin.AuthenticateAsync(user, password, Ct);
        await ResetMailboxAsync(admin);

        // ISO-2022-JP（NEC 特殊文字を含む）、Shift_JIS、添付付き UTF-8 の 3 通を用意する
        await admin.Inbox.AppendAsync(new AppendRequest(await MimeMessage.LoadAsync(new MemoryStream(BuildIso2022JpMail()), Ct)), Ct);
        await admin.Inbox.AppendAsync(new AppendRequest(await MimeMessage.LoadAsync(new MemoryStream(BuildShiftJisMail()), Ct)), Ct);
        await admin.Inbox.AppendAsync(new AppendRequest(BuildAttachmentMail()), Ct);

        var credentials = new InMemoryCredentialStore();
        var account = _db.Accounts.Add(new Account
        {
            DisplayName = "結合テスト",
            EmailAddress = user + "@localhost",
            IncomingProtocol = IncomingProtocol.Imap,
            Incoming = new ServerEndpoint(host, port, ConnectionSecurity.None),
            Outgoing = new ServerEndpoint(host, 25, ConnectionSecurity.None),
            UserName = user,
            AuthMethod = AuthMethod.Password,
        });
        credentials.Write(account.CredentialKey, user, password);

        var factory = new ImapSessionFactory(new StoredPasswordSecretProvider(credentials));
        var synchronizer = new AccountSynchronizer(factory, _db.Folders, _db.Messages, _db.Pending, _db.Files, new SyncOptions());

        // 1. 初回同期
        var first = await synchronizer.SyncAsync(account, null, Ct);
        Assert.Empty(first.Errors);
        Assert.Equal(3, first.Added);

        var folders = _db.Folders.GetByAccount(account.Id);
        var inbox = folders.Single(f => f.Role == FolderRole.Inbox);
        var trash = folders.Single(f => f.Role == FolderRole.Trash);
        Assert.Contains(folders, f => f.Role == FolderRole.Sent);
        Assert.NotNull(inbox.HighestModSeq);

        var items = _db.Messages.GetPage(inbox.Id, 0, 10);
        Assert.Equal(["あ①の件", "添付ファイル", "見積書の送付"], items.Select(i => i.Subject).Order(StringComparer.Ordinal));
        Assert.All(items, i => Assert.Equal(BodyState.Downloaded, i.BodyState));
        Assert.True(items.Single(i => i.Subject == "添付ファイル").HasAttachments);
        Assert.Single(_db.Messages.Search("見積"));
        Assert.Single(_db.Messages.Search("①"));
        Assert.Single(_db.Messages.Search("請求書"));

        // 2. ローカルで既読・フラグ・削除 → 同期でサーバーに反映
        var actions = new MailActions(_db.Folders, _db.Messages, _db.Pending, _db.Files);
        actions.MarkRead([items.Single(i => i.Subject == "あ①の件")], read: true);
        actions.SetFlags([items.Single(i => i.Subject == "見積書の送付")], MessageFlags.Flagged, add: true);
        actions.Delete([items.Single(i => i.Subject == "添付ファイル")]);

        var second = await synchronizer.SyncAsync(account, null, Ct);
        Assert.Empty(second.Errors);
        Assert.Empty(_db.Pending.GetPending(account.Id));

        await admin.Inbox.OpenAsync(FolderAccess.ReadOnly, Ct);
        var serverFlags = (await FetchWithSubjectsAsync(admin.Inbox))
            .ToDictionary(s => s.Subject, s => s.Summary.Flags!.Value);
        Assert.Equal(2, serverFlags.Count);
        Assert.True(serverFlags["あ①の件"].HasFlag(MailKit.MessageFlags.Seen));
        Assert.True(serverFlags["見積書の送付"].HasFlag(MailKit.MessageFlags.Flagged));
        Assert.Equal(["添付ファイル"], _db.Messages.GetPage(trash.Id, 0, 10).Select(i => i.Subject));

        // 3. ほかの端末での変更（未読に戻す・新着・削除）を取り込む
        await admin.Inbox.OpenAsync(FolderAccess.ReadWrite, Ct);
        var summaries = await FetchWithSubjectsAsync(admin.Inbox);
        var readUid = summaries.Single(s => s.Subject == "あ①の件").Summary.UniqueId;
        var flaggedUid = summaries.Single(s => s.Subject == "見積書の送付").Summary.UniqueId;
        await admin.Inbox.StoreAsync([readUid], new StoreFlagsRequest(StoreAction.Remove, MailKit.MessageFlags.Seen) { Silent = true }, Ct);
        await admin.Inbox.StoreAsync([flaggedUid], new StoreFlagsRequest(StoreAction.Add, MailKit.MessageFlags.Deleted) { Silent = true }, Ct);
        await admin.Inbox.ExpungeAsync(Ct);
        await admin.Inbox.AppendAsync(new AppendRequest(new MimeMessage(
            [new MailboxAddress("送信者", "sender@example.jp")], [new MailboxAddress("", user)], "新着メール", new TextPart("plain") { Text = "新しい本文です" })), Ct);
        await admin.Inbox.CloseAsync(false, Ct);

        var third = await synchronizer.SyncAsync(account, null, Ct);
        Assert.Empty(third.Errors);
        var after = _db.Messages.GetPage(inbox.Id, 0, 10);
        Assert.Equal(["あ①の件", "新着メール"], after.Select(i => i.Subject).Order(StringComparer.Ordinal));
        Assert.True(after.Single(i => i.Subject == "あ①の件").IsUnread);
        Assert.Single(_db.Messages.Search("新しい本文"));
        Assert.Empty(_db.Messages.Search("見積"));
        Assert.Equal(2, _db.Folders.Get(inbox.Id)!.UnreadCount);

        await admin.DisconnectAsync(true, Ct);
    }

    private static async Task<List<(string Subject, IMessageSummary Summary)>> FetchWithSubjectsAsync(IMailFolder folder)
    {
        var uids = await folder.SearchAsync(MailKit.Search.SearchQuery.All, Ct);
        var request = new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.Flags) { Headers = HeaderSet.All };
        var summaries = await folder.FetchAsync(uids, request, Ct);
        return [.. summaries.Select(s => (Hayabumi.Core.Mime.MessageParser.ParseHeaders(s.Headers!).Subject, s))];
    }

    private static async Task ResetMailboxAsync(ImapClient client)
    {
        foreach (var folder in await client.GetFoldersAsync(client.PersonalNamespaces[0], cancellationToken: Ct))
        {
            if (folder.Attributes.HasFlag(FolderAttributes.NoSelect))
            {
                continue;
            }

            await folder.OpenAsync(FolderAccess.ReadWrite, Ct);
            if (folder.Count > 0)
            {
                var all = await folder.SearchAsync(MailKit.Search.SearchQuery.All, Ct);
                await folder.StoreAsync(all, new StoreFlagsRequest(StoreAction.Add, MailKit.MessageFlags.Deleted) { Silent = true }, Ct);
                await folder.ExpungeAsync(Ct);
            }

            await folder.CloseAsync(false, Ct);
        }
    }

    private static byte[] BuildIso2022JpMail()
    {
        var subject = Convert.ToBase64String([.. MailBuilder.EscKanji, 0x24, 0x22, 0x2D, 0x21, 0x24, 0x4E, 0x37, 0x6F, .. MailBuilder.EscAscii]); // あ①の件
        return new MailBuilder()
            .Header("From: =?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes("山田")) + "?= <yamada@example.jp>")
            .Header("To: tester@localhost")
            .Header($"Subject: =?ISO-2022-JP?B?{subject}?=")
            .Header("Date: Mon, 09 Nov 2026 10:00:00 +0900")
            .Header("Message-ID: <iso@example.jp>")
            .Header("MIME-Version: 1.0")
            .Header("Content-Type: text/plain; charset=ISO-2022-JP")
            .EndHeaders()
            .Raw(MailBuilder.Iso2022JpA1())
            .Ascii("\r\n")
            .ToStream()
            .ToArray();
    }

    private static byte[] BuildShiftJisMail()
    {
        Hayabumi.Core.Mime.JapaneseEncodings.EnsureRegistered();
        var sjis = Encoding.GetEncoding(932);
        return new MailBuilder()
            .Header("From: sato@example.jp")
            .Header("To: tester@localhost")
            .Ascii("Subject: ").Raw(sjis.GetBytes("見積書の送付")).Ascii("\r\n")
            .Header("Date: Tue, 10 Nov 2026 10:00:00 +0900")
            .Header("MIME-Version: 1.0")
            .Header("Content-Type: text/plain; charset=Shift_JIS")
            .Header("Content-Transfer-Encoding: 8bit")
            .EndHeaders()
            .Raw(sjis.GetBytes("お世話になっております。見積書をお送りします。㈱サンプル"))
            .Ascii("\r\n")
            .ToStream()
            .ToArray();
    }

    private static MimeMessage BuildAttachmentMail()
    {
        var builder = new BodyBuilder { TextBody = "請求書を添付します。" };
        builder.Attachments.Add("請求書.pdf", "%PDF-1.4 test"u8.ToArray(), new ContentType("application", "pdf"));
        return new MimeMessage([new MailboxAddress("経理", "keiri@example.jp")], [new MailboxAddress("", "tester@localhost")], "添付ファイル", builder.ToMessageBody())
        {
            Date = new DateTimeOffset(2026, 11, 11, 10, 0, 0, TimeSpan.FromHours(9)),
        };
    }
}
