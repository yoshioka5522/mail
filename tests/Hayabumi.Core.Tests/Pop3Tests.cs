using Hayabumi.Core.Accounts;
using Hayabumi.Core.Compose;
using Hayabumi.Core.Security;
using Hayabumi.Core.Storage;
using Hayabumi.Core.Sync;
using Hayabumi.Core.Sync.Pop3;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;
using MessageFlags = Hayabumi.Core.Storage.MessageFlags;

namespace Hayabumi.Core.Tests;

[Collection(RealServerCollection.Name)]
public sealed class Pop3Tests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly FakePop3 _server = new();

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Account AddPop3Account(string host = "pop.example.jp", int port = 995, ConnectionSecurity security = ConnectionSecurity.SslOnConnect, string user = "me")
        => _db.Accounts.Add(new Account
        {
            DisplayName = "POP3",
            EmailAddress = "me@example.jp",
            IncomingProtocol = IncomingProtocol.Pop3,
            Incoming = new ServerEndpoint(host, port, security),
            Outgoing = new ServerEndpoint("smtp.example.jp", 587, ConnectionSecurity.StartTls),
            UserName = user,
            AuthMethod = AuthMethod.Password,
        });

    private Pop3Synchronizer CreateSynchronizer(IPop3SessionFactory? factory = null)
        => new(factory ?? _server, _db.Folders, _db.Messages, _db.Pending, _db.Files);

    [Fact]
    public async Task New_messages_are_downloaded_once_and_local_folders_created()
    {
        var account = AddPop3Account();
        _server.Add("u1", "古いメール", "本文1");
        _server.Add("u2", "新しいメール", "見積書を送ります");

        var first = await CreateSynchronizer().SyncAsync(account, null, Ct);

        Assert.Empty(first.Errors);
        Assert.Equal(2, first.Added);
        Assert.True(first.Folders.Single().IsInitial);
        var folders = _db.Folders.GetByAccount(account.Id);
        Assert.Equal([FolderRole.Inbox, FolderRole.Sent, FolderRole.Trash], folders.Select(f => f.Role).Order());
        Assert.All(folders, f => Assert.True(f.IsLocal));
        var inbox = folders.Single(f => f.Role == FolderRole.Inbox);
        Assert.All(_db.Messages.GetPage(inbox.Id, 0, 10), m => Assert.Equal(BodyState.Downloaded, m.BodyState));
        Assert.Single(_db.Messages.Search("見積書"));

        _server.Add("u3", "追加のメール", "本文3");
        var second = await CreateSynchronizer().SyncAsync(account, null, Ct);
        Assert.Equal(1, second.Added);
        Assert.False(second.Folders.Single().IsInitial);
        Assert.Equal("追加のメール", second.Folders.Single().NewUnread.Single().Subject);
        Assert.Equal(3, _db.Folders.Get(inbox.Id)!.UnreadCount);
        Assert.Equal(3, _server.Downloads);
    }

    [Fact]
    public async Task Local_actions_stay_local_and_permanent_delete_removes_from_server()
    {
        var account = AddPop3Account();
        _server.Add("u1", "残す", "a");
        _server.Add("u2", "消す", "b");
        await CreateSynchronizer().SyncAsync(account, null, Ct);
        var folders = _db.Folders.GetByAccount(account.Id);
        var inbox = folders.Single(f => f.Role == FolderRole.Inbox);
        var trash = folders.Single(f => f.Role == FolderRole.Trash);
        var actions = new MailActions(_db.Folders, _db.Messages, _db.Pending, _db.Files);
        var items = _db.Messages.GetPage(inbox.Id, 0, 10);

        actions.MarkRead(items, read: true);
        actions.Delete([items.Single(i => i.Subject == "消す")]);

        Assert.Empty(_db.Pending.GetPending(account.Id));
        Assert.Single(_db.Messages.GetPage(trash.Id, 0, 10));
        Assert.All(_db.Messages.GetPage(inbox.Id, 0, 10), m => Assert.False(m.IsUnread));

        // ゴミ箱から削除すると、次回の受信でサーバーからも削除する
        actions.Delete(_db.Messages.GetPage(trash.Id, 0, 10));
        Assert.Empty(_db.Messages.GetPage(trash.Id, 0, 10));
        Assert.Single(_db.Pending.GetPending(account.Id));

        var result = await CreateSynchronizer().SyncAsync(account, null, Ct);
        Assert.Empty(result.Errors);
        Assert.Equal(0, result.Added);
        Assert.Equal(["u1"], _server.Messages.Keys);
        Assert.Empty(_db.Pending.GetPending(account.Id));
    }

    [Fact]
    public async Task Real_pop3_server_round_trip()
    {
        var value = Environment.GetEnvironmentVariable("HAYABUMI_POP3_TEST");
        var imap = Environment.GetEnvironmentVariable("HAYABUMI_IMAP_TEST");
        Assert.SkipWhen(string.IsNullOrEmpty(value) || string.IsNullOrEmpty(imap), "HAYABUMI_POP3_TEST と HAYABUMI_IMAP_TEST が設定されていません");
        var pop = value!.Split(':');
        var imapParts = imap!.Split(':');

        // 同じメールボックスに IMAP で 2 通入れる
        using (var admin = new ImapClient())
        {
            await admin.ConnectAsync(imapParts[0], int.Parse(imapParts[1], System.Globalization.CultureInfo.InvariantCulture), SecureSocketOptions.None, Ct);
            await admin.AuthenticateAsync(imapParts[2], imapParts[3], Ct);
            await admin.Inbox.OpenAsync(FolderAccess.ReadWrite, Ct);
            var all = await admin.Inbox.SearchAsync(MailKit.Search.SearchQuery.All, Ct);
            if (all.Count > 0)
            {
                await admin.Inbox.StoreAsync(all, new StoreFlagsRequest(StoreAction.Add, MailKit.MessageFlags.Deleted) { Silent = true }, Ct);
                await admin.Inbox.ExpungeAsync(Ct);
            }

            foreach (var subject in new[] { "POP3 その1", "POP3 その2" })
            {
                await admin.Inbox.AppendAsync(new AppendRequest(new MimeMessage(
                    [new MailboxAddress("差出人", "from@example.jp")], [new MailboxAddress("", "me@example.jp")], subject, new TextPart("plain") { Text = subject + "の本文" })), Ct);
            }

            await admin.DisconnectAsync(true, Ct);
        }

        var credentials = new InMemoryCredentialStore();
        var account = AddPop3Account(pop[0], int.Parse(pop[1], System.Globalization.CultureInfo.InvariantCulture), ConnectionSecurity.None, pop[2]);
        credentials.Write(account.CredentialKey, pop[2], pop[3]);
        var factory = new Pop3SessionFactory(new StoredPasswordSecretProvider(credentials));

        var first = await CreateSynchronizer(factory).SyncAsync(account, null, Ct);
        Assert.Empty(first.Errors);
        Assert.Equal(2, first.Added);
        var again = await CreateSynchronizer(factory).SyncAsync(account, null, Ct);
        Assert.Equal(0, again.Added);

        var folders = _db.Folders.GetByAccount(account.Id);
        var inbox = folders.Single(f => f.Role == FolderRole.Inbox);
        var trash = folders.Single(f => f.Role == FolderRole.Trash);
        var actions = new MailActions(_db.Folders, _db.Messages, _db.Pending, _db.Files);
        actions.Delete([_db.Messages.GetPage(inbox.Id, 0, 10).Single(m => m.Subject == "POP3 その1")]);
        actions.Delete(_db.Messages.GetPage(trash.Id, 0, 10));

        var third = await CreateSynchronizer(factory).SyncAsync(account, null, Ct);
        Assert.Empty(third.Errors);

        await using var check = await factory.ConnectAsync(account, Ct);
        Assert.Single(await check.ListAsync(Ct));
    }

    [Fact]
    public async Task Sent_copy_of_pop3_account_is_saved_locally()
    {
        var account = AddPop3Account();
        Pop3Synchronizer.EnsureFolders(_db.Folders, account.Id);
        var drafts = new DraftRepository(_db.Database, _db.Root);
        var outbox = new OutboxRepository(_db.Database);
        using var service = new SendService(_db.Accounts, _db.Messages, drafts, outbox, _db.Files, new NullSender(), new FakeMailServer(),
            new MailActions(_db.Folders, _db.Messages, _db.Pending, _db.Files)) { UndoDelay = TimeSpan.Zero };

        await service.QueueAsync(new Draft { AccountId = account.Id, To = "a@partner.jp", Subject = "POP3 からの送信", Body = "本文" }, Ct);
        var result = Assert.Single(await service.ProcessDueAsync(Ct));

        Assert.True(result.Sent);
        Assert.Null(result.Error);
        var sent = _db.Folders.GetByAccount(account.Id).Single(f => f.Role == FolderRole.Sent);
        var copy = Assert.Single(_db.Messages.GetPage(sent.Id, 0, 10));
        Assert.Equal("POP3 からの送信", copy.Subject);
        Assert.False(copy.IsUnread);
        Assert.True(_db.Files.Exists(copy.FilePath!));
    }

    private sealed class NullSender : IMailSender
    {
        public Task SendAsync(Account account, MimeMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakePop3 : IPop3SessionFactory
    {
        public SortedDictionary<string, byte[]> Messages { get; } = new(StringComparer.Ordinal);

        public int Downloads { get; private set; }

        public void Add(string uidl, string subject, string body)
            => Messages[uidl] = FakeMessage.Create(subject, body).Raw;

        public Task<IPop3Session> ConnectAsync(Account account, CancellationToken cancellationToken)
            => Task.FromResult<IPop3Session>(new Session(this));

        private sealed class Session(FakePop3 server) : IPop3Session
        {
            private readonly List<string> _order = [.. server.Messages.Keys];
            private readonly HashSet<int> _deleted = [];

            public Task<IReadOnlyList<Pop3Entry>> ListAsync(CancellationToken cancellationToken)
                => Task.FromResult<IReadOnlyList<Pop3Entry>>([.. _order.Select((u, i) => new Pop3Entry(i, u, server.Messages[u].Length))]);

            public async Task CopyMessageToAsync(int index, Stream destination, CancellationToken cancellationToken)
            {
                server.Downloads++;
                await destination.WriteAsync(server.Messages[_order[index]], cancellationToken);
            }

            public Task DeleteAsync(IReadOnlyList<int> indexes, CancellationToken cancellationToken)
            {
                _deleted.UnionWith(indexes);
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                foreach (var index in _deleted)
                {
                    server.Messages.Remove(_order[index]);
                }

                return ValueTask.CompletedTask;
            }
        }
    }
}
