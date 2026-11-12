using System.Text;
using Hayabumi.Core.Search;
using Microsoft.Data.Sqlite;

namespace Hayabumi.Core.Storage;

public sealed class MessageRepository(MailDatabase database)
{
    private const string ListColumns = """
        SELECT m.id, m.account_id, m.folder_id, m.uid, m.subject, m.from_name, m.from_address, m.to_text, m.date_utc,
               m.flags, m.has_attachments, m.preview, m.body_state, m.file_path
        FROM messages m
        """;

    /// <summary>
    /// メッセージをまとめて登録する。同じフォルダに同じ UID のメッセージがあれば無視する。
    /// </summary>
    /// <returns>新しく登録した件数。</returns>
    public int InsertMany(long accountId, long folderId, IEnumerable<NewMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        using var connection = database.Open();
        using var transaction = connection.BeginWrite();

        using var insert = connection.CreateCommand(
            """
            INSERT OR IGNORE INTO messages (account_id, folder_id, uid, message_id, in_reply_to, subject, from_name, from_address,
                                            to_text, cc_text, date_utc, size, flags, keywords, modseq, has_attachments, preview)
            VALUES ($a, $f, $uid, $mid, $irt, $s, $fn, $fa, $to, $cc, $d, $size, $flags, $kw, $ms, $att, $pv)
            RETURNING id;
            """);
        var p = AddParameters(insert, "$a", "$f", "$uid", "$mid", "$irt", "$s", "$fn", "$fa", "$to", "$cc", "$d", "$size", "$flags", "$kw", "$ms", "$att", "$pv");
        p["$a"].Value = accountId;
        p["$f"].Value = folderId;

        using var index = connection.CreateCommand(
            "INSERT INTO messages_fts (rowid, subject, sender, recipients, body) VALUES ($id, $s, $sender, $rcpt, $body);");
        var q = AddParameters(index, "$id", "$s", "$sender", "$rcpt", "$body");

        var inserted = 0;
        foreach (var m in messages)
        {
            p["$uid"].Value = m.Uid is null ? DBNull.Value : (long)m.Uid.Value;
            p["$mid"].Value = (object?)m.MessageId ?? DBNull.Value;
            p["$irt"].Value = (object?)m.InReplyTo ?? DBNull.Value;
            p["$s"].Value = m.Subject;
            p["$fn"].Value = m.FromName;
            p["$fa"].Value = m.FromAddress;
            p["$to"].Value = m.ToText;
            p["$cc"].Value = m.CcText;
            p["$d"].Value = m.Date.ToUnixMs();
            p["$size"].Value = m.Size;
            p["$flags"].Value = (int)m.Flags;
            p["$kw"].Value = string.Join(' ', m.Keywords);
            p["$ms"].Value = m.ModSeq is null ? DBNull.Value : (long)m.ModSeq.Value;
            p["$att"].Value = m.HasAttachments ? 1 : 0;
            p["$pv"].Value = m.Preview;

            var id = insert.ExecuteScalar();
            if (id is null or DBNull)
            {
                continue;
            }

            q["$id"].Value = id;
            q["$s"].Value = SearchTokenizer.ToIndexText(m.Subject);
            q["$sender"].Value = SearchTokenizer.ToIndexText(m.FromName + " " + m.FromAddress);
            q["$rcpt"].Value = SearchTokenizer.ToIndexText(m.ToText + " " + m.CcText);
            // 本文を取得するまでは、プレビュー（本文の冒頭）で検索できるようにしておく
            q["$body"].Value = SearchTokenizer.ToIndexText(m.Preview);
            index.ExecuteNonQuery();
            inserted++;
        }

        transaction.Commit();
        return inserted;
    }

    public MessageListItem? Get(long id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand(ListColumns + " WHERE m.id = $id;", ("$id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadListItem(reader) : null;
    }

    /// <summary>役割（送信済みなど）からフォルダのフルネームを探す。</summary>
    public string? FindFolderNameByRole(long accountId, FolderRole role)
    {
        using var connection = database.Open();
        return connection.Scalar<string>(
            "SELECT full_name FROM folders WHERE account_id = $a AND role = $r ORDER BY id LIMIT 1;",
            ("$a", accountId), ("$r", (int)role));
    }

    public int Count(long folderId)
    {
        using var connection = database.Open();
        return connection.Scalar<int>("SELECT COUNT(*) FROM messages WHERE folder_id = $f;", ("$f", folderId));
    }

    /// <summary>新しい順に並べたメール一覧の一部を取得する（一覧の仮想化表示用）。</summary>
    public IReadOnlyList<MessageListItem> GetPage(long folderId, int offset, int limit)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand(
            ListColumns + " WHERE m.folder_id = $f ORDER BY m.date_utc DESC, m.id DESC LIMIT $limit OFFSET $offset;",
            ("$f", folderId), ("$limit", limit), ("$offset", offset));
        return ReadList(command);
    }

    public uint GetMaxUid(long folderId)
    {
        using var connection = database.Open();
        return (uint)connection.Scalar<long>("SELECT IFNULL(MAX(uid), 0) FROM messages WHERE folder_id = $f;", ("$f", folderId));
    }

    public IReadOnlyList<uint> GetUids(long folderId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand("SELECT uid FROM messages WHERE folder_id = $f AND uid IS NOT NULL ORDER BY uid;", ("$f", folderId));
        using var reader = command.ExecuteReader();
        var result = new List<uint>();
        while (reader.Read())
        {
            result.Add((uint)reader.GetInt64(0));
        }

        return result;
    }

    /// <summary>サーバー側のフラグの変更を反映する。</summary>
    public void UpdateFlags(long folderId, IEnumerable<(uint Uid, MessageFlags Flags, IReadOnlyList<string> Keywords, ulong? ModSeq)> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        using var connection = database.Open();
        using var transaction = connection.BeginWrite();
        using var command = connection.CreateCommand(
            "UPDATE messages SET flags = $flags, keywords = $kw, modseq = IFNULL($ms, modseq) WHERE folder_id = $f AND uid = $uid;");
        var p = AddParameters(command, "$flags", "$kw", "$ms", "$f", "$uid");
        p["$f"].Value = folderId;
        foreach (var (uid, flags, keywords, modSeq) in updates)
        {
            p["$flags"].Value = (int)flags;
            p["$kw"].Value = string.Join(' ', keywords);
            p["$ms"].Value = modSeq is null ? DBNull.Value : (long)modSeq.Value;
            p["$uid"].Value = (long)uid;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>ローカルでフラグを付け外しする（サーバーへの反映は保留中の操作として別に行う）。</summary>
    public void ChangeFlags(IReadOnlyCollection<long> messageIds, MessageFlags flags, bool add)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        using var connection = database.Open();
        using var transaction = connection.BeginWrite();
        using var command = connection.CreateCommand(
            add ? "UPDATE messages SET flags = flags | $fl WHERE id = $id;" : "UPDATE messages SET flags = flags & ~$fl WHERE id = $id;");
        var p = AddParameters(command, "$fl", "$id");
        p["$fl"].Value = (int)flags;
        foreach (var id in messageIds)
        {
            p["$id"].Value = id;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>指定した UID のメッセージを削除する。</summary>
    /// <returns>削除したメッセージの .eml ファイルの相対パス。</returns>
    public IReadOnlyList<string> DeleteByUids(long folderId, IEnumerable<uint> uids)
    {
        ArgumentNullException.ThrowIfNull(uids);
        using var connection = database.Open();
        using var transaction = connection.BeginWrite();
        var files = new List<string>();

        using var select = connection.CreateCommand("SELECT id, file_path FROM messages WHERE folder_id = $f AND uid = $uid;");
        var ps = AddParameters(select, "$f", "$uid");
        ps["$f"].Value = folderId;
        using var deleteFts = connection.CreateCommand("DELETE FROM messages_fts WHERE rowid = $id;");
        var pf = AddParameters(deleteFts, "$id");
        using var delete = connection.CreateCommand("DELETE FROM messages WHERE id = $id;");
        var pd = AddParameters(delete, "$id");

        foreach (var uid in uids)
        {
            ps["$uid"].Value = (long)uid;
            long? id = null;
            using (var reader = select.ExecuteReader())
            {
                if (reader.Read())
                {
                    id = reader.GetInt64(0);
                    if (reader.GetNullableString(1) is { } path)
                    {
                        files.Add(path);
                    }
                }
            }

            if (id is null)
            {
                continue;
            }

            pf["$id"].Value = id;
            deleteFts.ExecuteNonQuery();
            pd["$id"].Value = id;
            delete.ExecuteNonQuery();
        }

        transaction.Commit();
        return files;
    }

    /// <summary>フォルダ内のメッセージをすべて削除する（UIDVALIDITY が変わったときなど）。</summary>
    public IReadOnlyList<string> DeleteAllInFolder(long folderId)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginWrite();
        var files = new List<string>();
        using (var command = connection.CreateCommand("SELECT file_path FROM messages WHERE folder_id = $f AND file_path IS NOT NULL;", ("$f", folderId)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                files.Add(reader.GetString(0));
            }
        }

        connection.Execute("DELETE FROM messages_fts WHERE rowid IN (SELECT id FROM messages WHERE folder_id = $f);", ("$f", folderId));
        connection.Execute("DELETE FROM messages WHERE folder_id = $f;", ("$f", folderId));
        transaction.Commit();
        return files;
    }

    /// <summary>本文が未取得のメッセージを新しい順に取得する。</summary>
    public IReadOnlyList<(long Id, uint Uid, long Size)> GetMessagesWithoutBody(long folderId, int limit, long maxSize = long.MaxValue)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand(
            "SELECT id, uid, size FROM messages WHERE folder_id = $f AND body_state = 0 AND uid IS NOT NULL AND size <= $max ORDER BY date_utc DESC LIMIT $limit;",
            ("$f", folderId), ("$limit", limit), ("$max", maxSize));
        using var reader = command.ExecuteReader();
        var result = new List<(long, uint, long)>();
        while (reader.Read())
        {
            result.Add((reader.GetInt64(0), (uint)reader.GetInt64(1), reader.GetInt64(2)));
        }

        return result;
    }

    /// <summary>取得した本文を登録し、検索インデックスを本文で更新する。</summary>
    public void SetBody(long id, string filePath, string plainText, string preview, bool hasAttachments)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginWrite();
        string subject, sender, recipients;
        using (var command = connection.CreateCommand(
            "SELECT subject, from_name || ' ' || from_address, to_text || ' ' || cc_text FROM messages WHERE id = $id;", ("$id", id)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
            {
                return;
            }

            subject = reader.GetString(0);
            sender = reader.GetString(1);
            recipients = reader.GetString(2);
        }

        connection.Execute(
            "UPDATE messages SET file_path = $p, preview = $pv, has_attachments = $att, body_state = 1 WHERE id = $id;",
            ("$p", filePath), ("$pv", preview), ("$att", hasAttachments ? 1 : 0), ("$id", id));
        connection.Execute("DELETE FROM messages_fts WHERE rowid = $id;", ("$id", id));
        connection.Execute(
            "INSERT INTO messages_fts (rowid, subject, sender, recipients, body) VALUES ($id, $s, $sender, $rcpt, $body);",
            ("$id", id),
            ("$s", SearchTokenizer.ToIndexText(subject)),
            ("$sender", SearchTokenizer.ToIndexText(sender)),
            ("$rcpt", SearchTokenizer.ToIndexText(recipients)),
            ("$body", SearchTokenizer.ToIndexText(plainText)));
        transaction.Commit();
    }

    /// <summary>
    /// 全文検索を行う。空白で区切った語をすべて含むメールを、新しい順に返す。
    /// </summary>
    public IReadOnlyList<MessageListItem> Search(string query, long? accountId = null, long? folderId = null, int limit = 500)
    {
        var match = SearchTokenizer.ToMatchExpression(query);
        if (match is null)
        {
            return [];
        }

        var sql = new StringBuilder(ListColumns);
        sql.Append(" WHERE m.id IN (SELECT rowid FROM messages_fts WHERE messages_fts MATCH $q)");
        if (accountId is not null)
        {
            sql.Append(" AND m.account_id = $a");
        }

        if (folderId is not null)
        {
            sql.Append(" AND m.folder_id = $f");
        }

        sql.Append(" ORDER BY m.date_utc DESC, m.id DESC LIMIT $limit;");

        using var connection = database.Open();
        using var command = connection.CreateCommand(sql.ToString(), ("$q", match), ("$a", accountId), ("$f", folderId), ("$limit", limit));
        return ReadList(command);
    }

    private static Dictionary<string, SqliteParameter> AddParameters(SqliteCommand command, params string[] names)
    {
        var result = new Dictionary<string, SqliteParameter>(names.Length);
        foreach (var name in names)
        {
            result[name] = command.Parameters.Add(new SqliteParameter(name, DBNull.Value));
        }

        command.Prepare();
        return result;
    }

    private static List<MessageListItem> ReadList(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var result = new List<MessageListItem>();
        while (reader.Read())
        {
            result.Add(ReadListItem(reader));
        }

        return result;
    }

    private static MessageListItem ReadListItem(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        AccountId = r.GetInt64(1),
        FolderId = r.GetInt64(2),
        Uid = r.GetNullableInt64(3) is { } uid ? (uint)uid : null,
        Subject = r.GetString(4),
        FromName = r.GetString(5),
        FromAddress = r.GetString(6),
        ToText = r.GetString(7),
        Date = SqliteExtensions.FromUnixMs(r.GetInt64(8)),
        Flags = (MessageFlags)r.GetInt32(9),
        HasAttachments = r.GetInt32(10) != 0,
        Preview = r.GetString(11),
        BodyState = (BodyState)r.GetInt32(12),
        FilePath = r.GetNullableString(13),
    };
}
