using Microsoft.Data.Sqlite;

namespace Hayabumi.Core.Storage;

public sealed class FolderRepository(MailDatabase database)
{
    private const string SelectColumns = """
        SELECT id, account_id, full_name, display_name, delimiter, role, uid_validity, highest_modseq, unread_count, total_count, is_local
        FROM folders
        """;

    /// <summary>フォルダを登録する。既にあれば表示名と役割を更新する。</summary>
    public Folder Upsert(long accountId, string fullName, string displayName, char? delimiter, FolderRole role, bool isLocal = false)
    {
        using var connection = database.Open();
        var id = connection.Scalar<long>(
            """
            INSERT INTO folders (account_id, full_name, display_name, delimiter, role, is_local)
            VALUES ($a, $f, $d, $del, $r, $local)
            ON CONFLICT (account_id, full_name) DO UPDATE SET display_name = excluded.display_name, delimiter = excluded.delimiter, role = excluded.role
            RETURNING id;
            """,
            ("$a", accountId), ("$f", fullName), ("$d", displayName), ("$del", delimiter?.ToString()), ("$r", (int)role), ("$local", isLocal ? 1 : 0));
        return Get(id)!;
    }

    public Folder? Get(long id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand(SelectColumns + " WHERE id = $id;", ("$id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public IReadOnlyList<Folder> GetByAccount(long accountId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand(SelectColumns + " WHERE account_id = $a ORDER BY CASE role WHEN 1 THEN 0 ELSE 1 END, full_name;", ("$a", accountId));
        using var reader = command.ExecuteReader();
        var result = new List<Folder>();
        while (reader.Read())
        {
            result.Add(Read(reader));
        }

        return result;
    }

    public void UpdateSyncState(long folderId, uint? uidValidity, ulong? highestModSeq)
    {
        using var connection = database.Open();
        connection.Execute(
            "UPDATE folders SET uid_validity = $v, highest_modseq = $m WHERE id = $id;",
            ("$v", uidValidity is null ? null : (long)uidValidity.Value),
            ("$m", highestModSeq is null ? null : (long)highestModSeq.Value),
            ("$id", folderId));
    }

    /// <summary>未読数・総数をメッセージ表から数え直す。</summary>
    public void RefreshCounts(long folderId)
    {
        using var connection = database.Open();
        connection.Execute(
            """
            UPDATE folders SET
                total_count  = (SELECT COUNT(*) FROM messages WHERE folder_id = $id),
                unread_count = (SELECT COUNT(*) FROM messages WHERE folder_id = $id AND (flags & 1) = 0)
            WHERE id = $id;
            """,
            ("$id", folderId));
    }

    public void Delete(long folderId)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginWrite();
        connection.Execute("DELETE FROM messages_fts WHERE rowid IN (SELECT id FROM messages WHERE folder_id = $id);", ("$id", folderId));
        connection.Execute("DELETE FROM folders WHERE id = $id;", ("$id", folderId));
        transaction.Commit();
    }

    private static Folder Read(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        AccountId = r.GetInt64(1),
        FullName = r.GetString(2),
        DisplayName = r.GetString(3),
        Delimiter = r.GetNullableString(4) is { Length: > 0 } d ? d[0] : null,
        Role = (FolderRole)r.GetInt32(5),
        UidValidity = r.GetNullableInt64(6) is { } v ? (uint)v : null,
        HighestModSeq = r.GetNullableInt64(7) is { } m ? (ulong)m : null,
        UnreadCount = r.GetInt32(8),
        TotalCount = r.GetInt32(9),
        IsLocal = r.GetInt32(10) != 0,
    };
}
