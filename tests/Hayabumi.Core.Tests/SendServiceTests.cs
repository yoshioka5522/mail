using Hayabumi.Core.Accounts;
using Hayabumi.Core.Compose;
using Hayabumi.Core.Storage;
using Hayabumi.Core.Sync;
using MimeKit;

namespace Hayabumi.Core.Tests;

public sealed class SendServiceTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly FakeMailServer _server = new();
    private readonly FakeSender _sender = new();
    private readonly DraftRepository _drafts;
    private readonly OutboxRepository _outbox;
    private readonly SendService _service;

    public SendServiceTests()
    {
        _drafts = new DraftRepository(_db.Database, _db.Root);
        _outbox = new OutboxRepository(_db.Database);
        var actions = new MailActions(_db.Folders, _db.Messages, _db.Pending, _db.Files);
        _service = new SendService(_db.Accounts, _db.Messages, _drafts, _outbox, _db.Files, _sender, _server, actions) { UndoDelay = TimeSpan.Zero };
    }

    public void Dispose()
    {
        _service.Dispose();
        _db.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private (Account Account, Folder Sent) Setup()
    {
        var account = _db.AddAccount("me@example.co.jp");
        _server.AddFolder("INBOX", FolderRole.Inbox);
        _server.AddFolder("Sent", FolderRole.Sent);
        _db.Folders.Upsert(account.Id, "INBOX", "受信トレイ", '/', FolderRole.Inbox);
        var sent = _db.Folders.Upsert(account.Id, "Sent", "送信済み", '/', FolderRole.Sent);
        return (account, sent);
    }

    [Fact]
    public async Task Queued_message_is_sent_and_saved_to_sent_folder()
    {
        var (account, _) = Setup();
        var draft = _drafts.Save(new Draft { AccountId = account.Id, To = "a@partner.jp", Bcc = "b@partner.jp", Subject = "テスト送信", Body = "本文" });

        var item = await _service.QueueAsync(draft, Ct);
        Assert.Null(_drafts.Get(draft.Id));
        Assert.True(_db.Files.Exists(item.FilePath));

        var results = await _service.ProcessDueAsync(Ct);

        Assert.Equal([new SendResult(item.Id, true, null)], results);
        var sentMessage = Assert.Single(_sender.Sent);
        Assert.Equal("テスト送信", sentMessage.Subject);
        Assert.Equal("b@partner.jp", sentMessage.Bcc.Mailboxes.Single().Address);
        var copy = Assert.Single(_server.Folders["Sent"].Messages.Values);
        Assert.Equal("テスト送信", copy.Header.Subject);
        Assert.Equal(MessageFlags.Seen, copy.Flags);
        Assert.Empty(_outbox.GetAll());
        Assert.False(_db.Files.Exists(item.FilePath));
    }

    [Fact]
    public async Task Sending_can_be_cancelled_before_send_time()
    {
        var (account, _) = Setup();
        _service.UndoDelay = TimeSpan.FromMinutes(1);
        var item = await _service.QueueAsync(new Draft { AccountId = account.Id, To = "a@partner.jp", Subject = "取り消し" }, Ct);

        Assert.Empty(await _service.ProcessDueAsync(Ct));
        var restored = _service.Cancel(item.Id);

        Assert.NotNull(restored);
        Assert.Equal("取り消し", restored.Subject);
        Assert.NotNull(_drafts.Get(restored.Id));
        Assert.Empty(_outbox.GetAll());
        Assert.Null(_service.Cancel(item.Id));
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task Cancelling_a_saved_draft_restores_it_with_attachments()
    {
        var (account, _) = Setup();
        _service.UndoDelay = TimeSpan.FromMinutes(1);
        var file = Path.Combine(_db.Root, "a.txt");
        File.WriteAllText(file, "x");
        var draft = _drafts.AddAttachment(new Draft { AccountId = account.Id, To = "a@partner.jp", Subject = "保存済み" }, file);

        var item = await _service.QueueAsync(draft, Ct);
        Assert.Null(_drafts.Get(draft.Id));

        var restored = _service.Cancel(item.Id)!;
        Assert.Equal(draft.Id, restored.Id);
        Assert.Equal("保存済み", _drafts.Get(draft.Id)!.Subject);
        Assert.True(File.Exists(restored.Attachments.Single()));
    }

    [Fact]
    public async Task Failed_send_is_retried_later_and_gives_up_after_max_attempts()
    {
        var (account, _) = Setup();
        _sender.Fail = true;
        var item = await _service.QueueAsync(new Draft { AccountId = account.Id, To = "a@partner.jp", Subject = "再送" }, Ct);

        var result = Assert.Single(await _service.ProcessDueAsync(Ct));
        Assert.False(result.Sent);
        var stored = _outbox.Get(item.Id)!;
        Assert.Equal(1, stored.Attempts);
        Assert.False(stored.Failed);
        Assert.True(stored.SendAt > DateTimeOffset.UtcNow);
        Assert.Empty(await _service.ProcessDueAsync(Ct));

        for (var i = 1; i < SendService.MaxAttempts; i++)
        {
            _outbox.Retry(item.Id);
            using (var connection = _db.Database.Open())
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"UPDATE outbox SET attempts = {i} WHERE id = {item.Id};";
                command.ExecuteNonQuery();
            }

            await _service.ProcessDueAsync(Ct);
        }

        Assert.True(_outbox.Get(item.Id)!.Failed);

        _sender.Fail = false;
        _outbox.Retry(item.Id);
        Assert.True(Assert.Single(await _service.ProcessDueAsync(Ct)).Sent);
    }

    [Fact]
    public async Task Reply_marks_source_message_as_answered()
    {
        var (account, _) = Setup();
        var inbox = _db.Folders.GetByAccount(account.Id).Single(f => f.Role == FolderRole.Inbox);
        _db.Messages.InsertMany(account.Id, inbox.Id, [new NewMessage { Uid = 1, Subject = "質問", Date = DateTimeOffset.UtcNow }]);
        var source = _db.Messages.GetPage(inbox.Id, 0, 1)[0];

        await _service.QueueAsync(new Draft { AccountId = account.Id, To = "a@partner.jp", Subject = "Re: 質問", Mode = ComposeMode.Reply, SourceMessageId = source.Id }, Ct);
        await _service.ProcessDueAsync(Ct);

        Assert.True(_db.Messages.Get(source.Id)!.Flags.HasFlag(MessageFlags.Answered));
        Assert.Contains(_db.Pending.GetPending(account.Id), p => p.Kind == PendingOperationKind.SetFlags && p.Flags == MessageFlags.Answered);
    }

    [Fact]
    public async Task Gmail_does_not_get_a_duplicate_sent_copy()
    {
        _server.AddFolder("Sent", FolderRole.Sent);
        var account = _db.Accounts.Add(new Account
        {
            DisplayName = "g",
            EmailAddress = "me@gmail.com",
            IncomingProtocol = IncomingProtocol.Imap,
            Incoming = new ServerEndpoint("imap.gmail.com", 993, ConnectionSecurity.SslOnConnect),
            Outgoing = new ServerEndpoint("smtp.gmail.com", 587, ConnectionSecurity.StartTls),
            UserName = "me@gmail.com",
            AuthMethod = AuthMethod.Password,
        });
        _db.Folders.Upsert(account.Id, "Sent", "送信済み", '/', FolderRole.Sent);

        await _service.QueueAsync(new Draft { AccountId = account.Id, To = "a@partner.jp", Subject = "x" }, Ct);
        await _service.ProcessDueAsync(Ct);

        Assert.Single(_sender.Sent);
        Assert.Empty(_server.Folders["Sent"].Messages);
    }

    [Fact]
    public void Draft_attachments_are_copied_and_removed_with_draft()
    {
        var account = _db.AddAccount();
        var source = Path.Combine(_db.Root, "見積書.pdf");
        File.WriteAllBytes(source, [1, 2, 3]);

        var draft = _drafts.AddAttachment(new Draft { AccountId = account.Id }, source);
        draft = _drafts.AddAttachment(draft, source);

        Assert.Equal(["見積書.pdf", "見積書 (2).pdf"], draft.Attachments.Select(Path.GetFileName));
        Assert.All(draft.Attachments, p => Assert.True(File.Exists(p)));
        Assert.Equal(draft.Attachments, _drafts.Get(draft.Id)!.Attachments);

        draft = _drafts.RemoveAttachment(draft, draft.Attachments[1]);
        Assert.Single(draft.Attachments);

        _drafts.Delete(draft.Id);
        Assert.False(Directory.Exists(_drafts.GetAttachmentDirectory(draft.Id)));
        Assert.True(File.Exists(source));
    }

    private sealed class FakeSender : IMailSender
    {
        public List<MimeMessage> Sent { get; } = [];

        public bool Fail { get; set; }

        public Task SendAsync(Account account, MimeMessage message, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new IOException("SMTP サーバーに接続できません");
            }

            Sent.Add(message);
            return Task.CompletedTask;
        }
    }
}
