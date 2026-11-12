using System.IO;
using Hayabumi.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Hayabumi.App.Services;

/// <summary>
/// データフォルダを別の場所へ移す。メールと設定をコピーしてから、次回の起動で新しい場所を使う。
/// 元のフォルダは安全のため残す（確認後に手で削除してもらう）。
/// </summary>
public static class DataFolderMover
{
    private static readonly string[] s_folders = ["messages", "drafts"];

    public static void Copy(MailDatabase database, AppDataLocation location, string newRoot)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(location);
        var source = Path.GetFullPath(location.Root);
        var target = Path.GetFullPath(newRoot);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("今と同じ場所です。");
        }

        if (target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("今のデータフォルダの中には移せません。");
        }

        Directory.CreateDirectory(target);
        if (File.Exists(Path.Combine(target, "hayabumi.db")))
        {
            throw new InvalidOperationException("移動先に既に Hayabumi のデータがあります。空のフォルダを選んでください。");
        }

        // 書き込み途中の内容をデータベース本体に反映してからコピーする
        using (var connection = database.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        File.Copy(Path.Combine(source, "hayabumi.db"), Path.Combine(target, "hayabumi.db"));
        foreach (var folder in s_folders)
        {
            var from = Path.Combine(source, folder);
            if (Directory.Exists(from))
            {
                CopyDirectory(from, Path.Combine(target, folder));
            }
        }

        location.ChangeRoot(target);
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: false);
        }

        foreach (var directory in Directory.EnumerateDirectories(from))
        {
            CopyDirectory(directory, Path.Combine(to, Path.GetFileName(directory)));
        }
    }
}
