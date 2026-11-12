using Hayabumi.Core.Accounts;
using Hayabumi.Core.Storage;
using Hayabumi.Core.Sync;
using MimeKit;

namespace Hayabumi.Core.Compose;

/// <summary>SMTP でメールを送る。実装は <see cref="SmtpMailSender"/>。</summary>
public interface IMailSender
{
    Task SendAsync(Account account, MimeMessage message, CancellationToken cancellationToken);
}

public sealed record SendResult(long OutboxId, bool Sent, string? Error);

/// <summary>
/// 送信の流れを管理する。
/// 送信ボタン → 送信待ちに入れる（指定秒数の間は取り消せる）→ 時刻が来たら SMTP で送信 → 送信済みフォルダに保存。
/// 送信できなかった場合は時間をおいて再送する（オフライン時も同じ）。
/// </summary>
public sealed class SendService(
    AccountRepository accounts,
    MessageRepository messages,
    DraftRepository drafts,
    OutboxRepository outbox,
    MessageFileStore files,
    IMailSender sender,
    IMailStoreSessionFactory sessions,
    MailActions actions,
    Contacts.ContactRepository? contacts = null) : IDisposable
{
    public const int MaxAttempts = 5;

    private static readonly TimeSpan[] s_retryDelays =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30)];

    private readonly SemaphoreSlim _processing = new(1, 1);

    /// <summary>送信を取り消せる時間（既定 5 秒。0 にするとすぐ送る）。</summary>
    public TimeSpan UndoDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 下書きを送信待ちに入れる。下書きは削除する（添付ファイルは送信が済むまで残す）。
    /// </summary>
    public async Task<OutboxItem> QueueAsync(Draft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var account = accounts.Get(draft.AccountId) ?? throw new InvalidOperationException("送信元のアカウントが見つかりません。");
        var message = MessageBuilder.Build(draft, account);

        using var buffer = new MemoryStream();
        await message.WriteToAsync(MessageBuilder.FormatOptions, buffer, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;
        var path = await files.SaveAsync(account.Id, buffer, cancellationToken).ConfigureAwait(false);

        var item = outbox.Add(new OutboxItem
        {
            AccountId = account.Id,
            FilePath = path,
            Draft = draft,
            SendAt = DateTimeOffset.UtcNow + UndoDelay,
        });

        if (draft.Id != 0)
        {
            drafts.Delete(draft.Id, keepAttachments: true);
        }

        return item;
    }

    /// <summary>まだ送っていなければ送信を取り消し、下書きに戻す。</summary>
    public Draft? Cancel(long outboxId)
    {
        var item = outbox.Get(outboxId);
        if (item is null || !outbox.Remove(outboxId))
        {
            return null;
        }

        files.Delete(item.FilePath);
        return drafts.Save(item.Draft with { Id = item.Draft.Id });
    }

    /// <summary>送信時刻を過ぎたメールを送る。</summary>
    public async Task<IReadOnlyList<SendResult>> ProcessDueAsync(CancellationToken cancellationToken = default)
    {
        if (!await _processing.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        try
        {
            var results = new List<SendResult>();
            foreach (var item in outbox.GetDue(DateTimeOffset.UtcNow))
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(await SendOneAsync(item, cancellationToken).ConfigureAwait(false));
            }

            return results;
        }
        finally
        {
            _processing.Release();
        }
    }

    private async Task<SendResult> SendOneAsync(OutboxItem item, CancellationToken cancellationToken)
    {
        var account = accounts.Get(item.AccountId);
        if (account is null)
        {
            outbox.Remove(item.Id);
            return new SendResult(item.Id, false, "アカウントが削除されています。");
        }

        MimeMessage message;
        try
        {
            await using var stream = files.OpenRead(item.FilePath);
            message = await MimeMessage.LoadAsync(stream, cancellationToken).ConfigureAwait(false);
            await sender.SendAsync(account, message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var attempt = item.Attempts + 1;
            var giveUp = attempt >= MaxAttempts;
            var delay = s_retryDelays[Math.Min(attempt - 1, s_retryDelays.Length - 1)];
            outbox.RecordFailure(item.Id, ex.Message, DateTimeOffset.UtcNow + delay, giveUp);
            return new SendResult(item.Id, false, ex.Message);
        }

        // ここから先は送信済み。失敗しても再送はしない
        outbox.Remove(item.Id);
        contacts?.RecordUsage(message.To.Mailboxes.Concat(message.Cc.Mailboxes).Concat(message.Bcc.Mailboxes), sent: true);
        try
        {
            await SaveSentCopyAsync(account, message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SendResult(item.Id, true, $"送信しましたが、送信済みフォルダへの保存に失敗しました: {ex.Message}");
        }
        finally
        {
            MarkSource(item.Draft);
            files.Delete(item.FilePath);
            var attachmentDirectory = drafts.GetAttachmentDirectory(item.Draft.Id);
            if (item.Draft.Id != 0 && Directory.Exists(attachmentDirectory))
            {
                Directory.Delete(attachmentDirectory, recursive: true);
            }
        }

        return new SendResult(item.Id, true, null);
    }

    /// <summary>
    /// Gmail と Microsoft 365 は SMTP で送ったメールをサーバー側で送信済みに保存するため、二重に保存しない。
    /// </summary>
    public static bool ServerSavesSentCopy(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var host = account.Outgoing.Host.ToLowerInvariant();
        return host is "smtp.gmail.com" or "smtp.office365.com" or "smtp-mail.outlook.com";
    }

    private async Task SaveSentCopyAsync(Account account, MimeMessage message, CancellationToken cancellationToken)
    {
        if (account.IncomingProtocol == IncomingProtocol.Pop3)
        {
            await SaveLocalSentCopyAsync(account, message, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (ServerSavesSentCopy(account))
        {
            return;
        }

        var sent = messages.FindFolderNameByRole(account.Id, FolderRole.Sent);
        if (sent is null)
        {
            return;
        }

        using var buffer = new MemoryStream();
        await message.WriteToAsync(MessageBuilder.FormatOptions, buffer, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;
        await using var session = await sessions.ConnectAsync(account, cancellationToken).ConfigureAwait(false);
        await session.AppendAsync(sent, buffer, MessageFlags.Seen, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>POP3 のアカウントは、この PC の送信済みフォルダに保存する。</summary>
    private async Task SaveLocalSentCopyAsync(Account account, MimeMessage message, CancellationToken cancellationToken)
    {
        if (messages.FindFolderIdByRole(account.Id, FolderRole.Sent) is not { } folderId)
        {
            return;
        }

        using var buffer = new MemoryStream();
        await message.WriteToAsync(MessageBuilder.FormatOptions, buffer, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;
        var parsed = Mime.MessageParser.Parse(buffer);
        buffer.Position = 0;
        var path = await files.SaveAsync(account.Id, buffer, cancellationToken).ConfigureAwait(false);
        var id = messages.InsertOne(account.Id, folderId, new NewMessage
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
            Flags = MessageFlags.Seen,
            HasAttachments = parsed.HasAttachments,
            Preview = parsed.Preview,
        });
        messages.SetBody(id, path, parsed.PlainText, parsed.Preview, parsed.HasAttachments);
    }

    private void MarkSource(Draft draft)
    {
        if (draft.SourceMessageId is not { } id || messages.Get(id) is not { } source)
        {
            return;
        }

        var flag = draft.Mode == ComposeMode.Forward ? MessageFlags.Forwarded : MessageFlags.Answered;
        actions.SetFlags([source], flag, add: true);
    }

    public void Dispose() => _processing.Dispose();
}
