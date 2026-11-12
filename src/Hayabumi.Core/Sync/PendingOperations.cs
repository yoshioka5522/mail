using System.Text.Json;
using Hayabumi.Core.Storage;

namespace Hayabumi.Core.Sync;

public enum PendingOperationKind
{
    SetFlags = 1,
    Move = 2,
    DeletePermanently = 3,

    /// <summary>POP3 サーバーからの削除（UIDL で指定する）。</summary>
    Pop3Delete = 4,
}

/// <summary>サーバーへの反映を待っている操作。</summary>
public sealed record PendingOperation
{
    public long Id { get; init; }
    public long AccountId { get; init; }
    public PendingOperationKind Kind { get; init; }
    public required string FolderFullName { get; init; }
    public required IReadOnlyList<uint> Uids { get; init; }
    public MessageFlags Flags { get; init; }
    public bool Add { get; init; }
    public string? DestinationFullName { get; init; }

    /// <summary>UID 以外でメッセージを指定する場合（POP3 の UIDL）。</summary>
    public IReadOnlyList<string> RemoteIds { get; init; } = [];

    public int Attempts { get; init; }
}

/// <summary>
/// オフライン中や送信待ちの操作を保存しておき、接続時に順番にサーバーへ反映する。
/// </summary>
public sealed class PendingOperationRepository(MailDatabase database)
{
    private sealed record Payload(string Folder, uint[] Uids, int Flags, bool Add, string? Destination, string[]? RemoteIds = null);

    public long Enqueue(PendingOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var payload = JsonSerializer.Serialize(new Payload(
            operation.FolderFullName, [.. operation.Uids], (int)operation.Flags, operation.Add, operation.DestinationFullName, [.. operation.RemoteIds]));
        using var connection = database.Open();
        return connection.Scalar<long>(
            "INSERT INTO pending_operations (account_id, kind, payload, created_at) VALUES ($a, $k, $p, $c) RETURNING id;",
            ("$a", operation.AccountId), ("$k", (int)operation.Kind), ("$p", payload), ("$c", DateTimeOffset.UtcNow.ToUnixMs()));
    }

    public IReadOnlyList<PendingOperation> GetPending(long accountId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand(
            "SELECT id, account_id, kind, payload, attempts FROM pending_operations WHERE account_id = $a ORDER BY id;", ("$a", accountId));
        using var reader = command.ExecuteReader();
        var result = new List<PendingOperation>();
        while (reader.Read())
        {
            var payload = JsonSerializer.Deserialize<Payload>(reader.GetString(3))!;
            result.Add(new PendingOperation
            {
                Id = reader.GetInt64(0),
                AccountId = reader.GetInt64(1),
                Kind = (PendingOperationKind)reader.GetInt32(2),
                FolderFullName = payload.Folder,
                Uids = payload.Uids,
                Flags = (MessageFlags)payload.Flags,
                Add = payload.Add,
                DestinationFullName = payload.Destination,
                RemoteIds = payload.RemoteIds ?? [],
                Attempts = reader.GetInt32(4),
            });
        }

        return result;
    }

    public void Complete(long id)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM pending_operations WHERE id = $id;", ("$id", id));
    }

    public void MarkFailed(long id, string error)
    {
        using var connection = database.Open();
        connection.Execute("UPDATE pending_operations SET attempts = attempts + 1, last_error = $e WHERE id = $id;", ("$e", error), ("$id", id));
    }
}
