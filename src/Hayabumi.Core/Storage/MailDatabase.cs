using Microsoft.Data.Sqlite;

namespace Hayabumi.Core.Storage;

/// <summary>
/// メールのインデックスを保持する SQLite データベース。
/// メール本体は .eml ファイルとして <see cref="MessageFileStore"/> に保存する。
/// </summary>
public sealed class MailDatabase
{
    private readonly string _connectionString;

    public MailDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    public string DatabasePath { get; }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        connection.Execute("PRAGMA foreign_keys = ON; PRAGMA synchronous = NORMAL; PRAGMA temp_store = MEMORY;");
        return connection;
    }

    /// <summary>データベースを作成し、スキーマを最新にする。</summary>
    public void Initialize()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(DatabasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = Open();
        connection.Execute("PRAGMA journal_mode = WAL;");
        SchemaMigrations.Apply(connection);
    }

    /// <summary>未使用領域の解放と、検索インデックスの最適化を行う。</summary>
    public void Optimize()
    {
        using var connection = Open();
        connection.Execute("INSERT INTO messages_fts(messages_fts) VALUES('optimize'); PRAGMA optimize;");
    }
}
