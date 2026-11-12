using System.Text.Json;
using Hayabumi.Core.Storage;

namespace Hayabumi.Core.Compose;

public sealed record OutboxItem
{
    public long Id { get; init; }
    public long AccountId { get; init; }

    /// <summary>送信する .eml（データフォルダからの相対パス）。</summary>
    public required string FilePath { get; init; }

    /// <summary>送信を取り消したときに下書きへ戻すための内容。</summary>
    public required Draft Draft { get; init; }

    public DateTimeOffset SendAt { get; init; }
    public int Attempts { get; init; }
    public string? LastError { get; init; }
    public bool Failed { get; init; }
}

/// <summary>送信待ちのメール。送信の取り消し（指定秒数の間）、オフライン時の再送に使う。</summary>
public sealed class OutboxRepository(MailDatabase database)
{
    public OutboxItem Add(OutboxItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        using var connection = database.Open();
        var id = connection.Scalar<long>(
            "INSERT INTO outbox (account_id, file_path, draft, send_at) VALUES ($a, $p, $d, $s) RETURNING id;",
            ("$a", item.AccountId), ("$p", item.FilePath), ("$d", JsonSerializer.Serialize(item.Draft)), ("$s", item.SendAt.ToUnixMs()));
        return item with { Id = id };
    }

    public OutboxItem? Get(long id) => Query("WHERE id = $id", ("$id", id)).FirstOrDefault();

    public IReadOnlyList<OutboxItem> GetAll() => Query("ORDER BY send_at");

    /// <summary>送信時刻を過ぎた、失敗していないものを返す。</summary>
    public IReadOnlyList<OutboxItem> GetDue(DateTimeOffset now) => Query("WHERE failed = 0 AND send_at <= $now ORDER BY send_at", ("$now", now.ToUnixMs()));

    public DateTimeOffset? GetNextSendAt()
    {
        using var connection = database.Open();
        var value = connection.Scalar<long?>("SELECT MIN(send_at) FROM outbox WHERE failed = 0;");
        return value is null ? null : SqliteExtensions.FromUnixMs(value.Value);
    }

    public bool Remove(long id)
    {
        using var connection = database.Open();
        return connection.Execute("DELETE FROM outbox WHERE id = $id;", ("$id", id)) > 0;
    }

    public void RecordFailure(long id, string error, DateTimeOffset retryAt, bool giveUp)
    {
        using var connection = database.Open();
        connection.Execute(
            "UPDATE outbox SET attempts = attempts + 1, last_error = $e, send_at = $s, failed = $f WHERE id = $id;",
            ("$e", error), ("$s", retryAt.ToUnixMs()), ("$f", giveUp ? 1 : 0), ("$id", id));
    }

    public void Retry(long id)
    {
        using var connection = database.Open();
        connection.Execute("UPDATE outbox SET failed = 0, attempts = 0, send_at = $s WHERE id = $id;", ("$s", DateTimeOffset.UtcNow.ToUnixMs()), ("$id", id));
    }

    private List<OutboxItem> Query(string where, params (string, object?)[] parameters)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand(
            $"SELECT id, account_id, file_path, draft, send_at, attempts, last_error, failed FROM outbox {where};", parameters);
        using var reader = command.ExecuteReader();
        var result = new List<OutboxItem>();
        while (reader.Read())
        {
            result.Add(new OutboxItem
            {
                Id = reader.GetInt64(0),
                AccountId = reader.GetInt64(1),
                FilePath = reader.GetString(2),
                Draft = JsonSerializer.Deserialize<Draft>(reader.GetString(3))!,
                SendAt = SqliteExtensions.FromUnixMs(reader.GetInt64(4)),
                Attempts = reader.GetInt32(5),
                LastError = reader.GetNullableString(6),
                Failed = reader.GetInt32(7) != 0,
            });
        }

        return result;
    }
}
