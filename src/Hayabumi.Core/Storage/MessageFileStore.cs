namespace Hayabumi.Core.Storage;

/// <summary>
/// メール本体を 1 通ずつ .eml ファイルとして保存する。
/// データベースが壊れても本体は残り、エクスポートもファイルのコピーで済む。
/// </summary>
public sealed class MessageFileStore(string rootDirectory)
{
    public string RootDirectory { get; } = rootDirectory;

    /// <summary>メールを保存し、ルートからの相対パスを返す。</summary>
    public async Task<string> SaveAsync(long accountId, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var name = Guid.NewGuid().ToString("N");
        var relative = Path.Combine("messages", accountId.ToString(System.Globalization.CultureInfo.InvariantCulture), name[..2], name + ".eml");
        var fullPath = GetFullPath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        // 書き込み途中で異常終了しても壊れたファイルが残らないよう、一時ファイルに書いてから名前を変える
        var temp = fullPath + ".tmp";
        await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
        {
            await content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            await file.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temp, fullPath, overwrite: true);
        return relative;
    }

    public Stream OpenRead(string relativePath)
        => new FileStream(GetFullPath(relativePath), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

    public bool Exists(string relativePath) => File.Exists(GetFullPath(relativePath));

    public void Delete(string relativePath)
    {
        var path = GetFullPath(relativePath);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void DeleteMany(IEnumerable<string> relativePaths)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);
        foreach (var path in relativePaths)
        {
            try
            {
                Delete(path);
            }
            catch (IOException)
            {
                // 他のプロセスが開いているなどで消せなかったファイルは残す（次回の整理で削除する）
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public string GetFullPath(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(RootDirectory, relativePath));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootDirectory)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("データフォルダの外を指すパスは使えません。", nameof(relativePath));
        }

        return full;
    }
}
