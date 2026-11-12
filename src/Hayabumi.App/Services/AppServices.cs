using System.IO;
using System.Net.Http;
using Hayabumi.Core.Accounts;
using Hayabumi.Core.Compose;
using Hayabumi.Core.Contacts;
using Hayabumi.Core.Security;
using Hayabumi.Core.Security.OAuth;
using Hayabumi.Core.Storage;
using Hayabumi.Core.Sync;
using Hayabumi.Core.Sync.Imap;
using Hayabumi.Core.Sync.Pop3;

namespace Hayabumi.App.Services;

/// <summary>アプリ全体で共有するサービス（起動時に 1 回だけ作る）。</summary>
public sealed class AppServices : IDisposable
{
    private AppServices(AppDataLocation location)
    {
        Location = location;
        Database = new MailDatabase(location.DatabasePath);
        Database.Initialize();
        Accounts = new AccountRepository(Database);
        Folders = new FolderRepository(Database);
        Messages = new MessageRepository(Database);
        Pending = new PendingOperationRepository(Database);
        Files = new MessageFileStore(location.Root);
        Credentials = new WindowsCredentialStore();
        Settings = new SettingsRepository(Database);
        OAuth = new OAuthSettings(Settings, Credentials);
        AppSettings = new AppSettings(Settings);
        OAuthClient = new OAuthClient(Http);
        var secrets = new AccountSecretProvider(Credentials, OAuth, OAuthClient);
        _secrets = secrets;
        Sessions = new ImapSessionFactory(secrets);
        Pop3Sessions = new Pop3SessionFactory(secrets);
        Actions = new MailActions(Folders, Messages, Pending, Files);
        FolderOperations = new FolderService(Sessions, Folders, Messages, Files);
        Updates = new UpdateService(Credentials);
        Drafts = new DraftRepository(Database, location.Root);
        Outbox = new OutboxRepository(Database);
        Contacts = new ContactRepository(Database);
        Send = new SendService(Accounts, Messages, Drafts, Outbox, Files, new SmtpMailSender(secrets), Sessions, Actions, Contacts)
        {
            UndoDelay = TimeSpan.FromSeconds(AppSettings.UndoSendSeconds),
        };
    }

    private readonly AccountSecretProvider _secrets;

    public static HttpClient Http { get; } = new() { Timeout = TimeSpan.FromSeconds(60) };

    public SettingsRepository Settings { get; }
    public OAuthSettings OAuth { get; }
    public AppSettings AppSettings { get; }
    public OAuthClient OAuthClient { get; }

    public AppDataLocation Location { get; }
    public MailDatabase Database { get; }
    public AccountRepository Accounts { get; }
    public FolderRepository Folders { get; }
    public MessageRepository Messages { get; }
    public PendingOperationRepository Pending { get; }
    public MessageFileStore Files { get; }
    public ICredentialStore Credentials { get; }
    public IMailStoreSessionFactory Sessions { get; }
    public IPop3SessionFactory Pop3Sessions { get; }
    public MailActions Actions { get; }
    public FolderService FolderOperations { get; }
    public UpdateService Updates { get; }
    public DraftRepository Drafts { get; }
    public OutboxRepository Outbox { get; }
    public ContactRepository Contacts { get; }
    public SendService Send { get; }
    public SyncOptions SyncOptions { get; } = new();

    /// <summary>同期は 1 つずつ行う（同じアカウントへの同時接続を避ける）。</summary>
    public SemaphoreSlim SyncLock { get; } = new(1, 1);

    public string WebViewDataDirectory => Path.Combine(Location.Root, "WebView2");

    public string ViewerDirectory => Path.Combine(Location.Root, "viewer");

    public static AppServices Create()
    {
        var location = new AppDataLocation();
        Log.Initialize(location.LogDirectory);
        Directory.CreateDirectory(location.Root);
        Log.Info($"起動 データフォルダ: {location.Root}");
        return new AppServices(location);
    }

    public AccountSynchronizer CreateAccountSynchronizer()
        => new(Sessions, Folders, Messages, Pending, Files, SyncOptions);

    /// <summary>アカウントの種類（IMAP / POP3）に応じて同期する。</summary>
    public Task<AccountSyncResult> SyncAccountAsync(Account account, IProgress<SyncProgress>? progress, Func<Folder, bool>? folderFilter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        return account.IncomingProtocol == IncomingProtocol.Pop3
            ? new Pop3Synchronizer(Pop3Sessions, Folders, Messages, Pending, Files).SyncAsync(account, progress, cancellationToken)
            : CreateAccountSynchronizer().SyncAsync(account, progress, folderFilter, cancellationToken);
    }

    public FolderSynchronizer CreateFolderSynchronizer()
        => new(Folders, Messages, Files, SyncOptions);

    /// <summary>入力されたパスワードで接続できるかを確かめる（保存前の確認用）。</summary>
    public static async Task TestConnectionAsync(Account account, string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.IncomingProtocol == IncomingProtocol.Pop3)
        {
            var pop3 = new Pop3SessionFactory(new FixedSecretProvider(password)) { Timeout = TimeSpan.FromSeconds(30) };
            await using var pop3Session = await pop3.ConnectAsync(account, cancellationToken).ConfigureAwait(false);
            return;
        }

        var factory = new ImapSessionFactory(new FixedSecretProvider(password)) { Timeout = TimeSpan.FromSeconds(30) };
        await using var session = await factory.ConnectAsync(account, cancellationToken).ConfigureAwait(false);
        await session.GetFoldersAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        SyncLock.Dispose();
        _secrets.Dispose();
        Send.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private sealed class FixedSecretProvider(string secret) : IAccountSecretProvider
    {
        public Task<string> GetSecretAsync(Account account, CancellationToken cancellationToken) => Task.FromResult(secret);
    }
}
