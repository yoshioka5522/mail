using System.Text.Json;
using Hayabumi.Core.Storage;

namespace Hayabumi.Core.Compose;

/// <summary>
/// 下書きを保存する。添付ファイルは下書きごとのフォルダ（drafts\{id}）にコピーして持つ。
/// </summary>
public sealed class DraftRepository(MailDatabase database, string rootDirectory)
{
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = false };

    public string GetAttachmentDirectory(long draftId)
        => Path.Combine(rootDirectory, "drafts", draftId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public Draft Save(Draft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var saved = draft with { UpdatedAt = DateTimeOffset.UtcNow };
        using var connection = database.Open();
        if (saved.Id == 0)
        {
            var id = connection.Scalar<long>(
                "INSERT INTO drafts (account_id, content, updated_at) VALUES ($a, '{}', $u) RETURNING id;",
                ("$a", saved.AccountId), ("$u", saved.UpdatedAt.ToUnixMs()));
            saved = saved with { Id = id };
        }

        connection.Execute(
            "UPDATE drafts SET account_id = $a, content = $c, updated_at = $u WHERE id = $id;",
            ("$a", saved.AccountId), ("$c", JsonSerializer.Serialize(saved, s_json)), ("$u", saved.UpdatedAt.ToUnixMs()), ("$id", saved.Id));
        return saved;
    }

    /// <summary>ファイルを下書きのフォルダにコピーして添付する（元のファイルが消えても送れるように）。</summary>
    public Draft AddAttachment(Draft draft, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Id == 0)
        {
            draft = Save(draft);
        }

        var directory = GetAttachmentDirectory(draft.Id);
        Directory.CreateDirectory(directory);
        var destination = UniquePath(Path.Combine(directory, Path.GetFileName(sourcePath)));
        File.Copy(sourcePath, destination);
        return Save(draft with { Attachments = [.. draft.Attachments, destination] });
    }

    public Draft RemoveAttachment(Draft draft, string path)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (path.StartsWith(GetAttachmentDirectory(draft.Id), StringComparison.OrdinalIgnoreCase) && File.Exists(path))
        {
            File.Delete(path);
        }

        return Save(draft with { Attachments = [.. draft.Attachments.Where(a => a != path)] });
    }

    public Draft? Get(long id)
    {
        using var connection = database.Open();
        var content = connection.Scalar<string>("SELECT content FROM drafts WHERE id = $id;", ("$id", id));
        return content is null ? null : JsonSerializer.Deserialize<Draft>(content, s_json);
    }

    public IReadOnlyList<Draft> GetAll()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand("SELECT content FROM drafts ORDER BY updated_at DESC;");
        using var reader = command.ExecuteReader();
        var result = new List<Draft>();
        while (reader.Read())
        {
            result.Add(JsonSerializer.Deserialize<Draft>(reader.GetString(0), s_json)!);
        }

        return result;
    }

    /// <summary>下書きを削除する。<paramref name="keepAttachments"/> が true なら添付ファイルは残す（送信待ちで使うため）。</summary>
    public void Delete(long id, bool keepAttachments = false)
    {
        using (var connection = database.Open())
        {
            connection.Execute("DELETE FROM drafts WHERE id = $id;", ("$id", id));
        }

        var directory = GetAttachmentDirectory(id);
        if (!keepAttachments && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
