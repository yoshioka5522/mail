using Hayabumi.Core.Accounts;
using Hayabumi.Core.Storage;
using Hayabumi.Core.Sync;

namespace Hayabumi.Core.Tests;

/// <summary>一時フォルダにデータベースとメール保存先を作る。</summary>
internal sealed class TestDatabase : IDisposable
{
    public TestDatabase()
    {
        Root = Path.Combine(Path.GetTempPath(), "hayabumi-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Database = new MailDatabase(Path.Combine(Root, "hayabumi.db"));
        Database.Initialize();
        Accounts = new AccountRepository(Database);
        Folders = new FolderRepository(Database);
        Messages = new MessageRepository(Database);
        Pending = new PendingOperationRepository(Database);
        Files = new MessageFileStore(Root);
    }

    public string Root { get; }
    public MailDatabase Database { get; }
    public AccountRepository Accounts { get; }
    public FolderRepository Folders { get; }
    public MessageRepository Messages { get; }
    public PendingOperationRepository Pending { get; }
    public MessageFileStore Files { get; }

    public Account AddAccount(string email = "user@example.jp") => Accounts.Add(new Account
    {
        DisplayName = "テスト",
        EmailAddress = email,
        IncomingProtocol = IncomingProtocol.Imap,
        Incoming = new ServerEndpoint("imap.example.jp", 993, ConnectionSecurity.SslOnConnect),
        Outgoing = new ServerEndpoint("smtp.example.jp", 587, ConnectionSecurity.StartTls),
        UserName = email,
        AuthMethod = AuthMethod.Password,
    });

    public FolderSynchronizer CreateFolderSynchronizer(SyncOptions? options = null)
        => new(Folders, Messages, Files, options ?? new SyncOptions());

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
