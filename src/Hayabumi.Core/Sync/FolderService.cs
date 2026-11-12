using Hayabumi.Core.Accounts;
using Hayabumi.Core.Storage;

namespace Hayabumi.Core.Sync;

/// <summary>フォルダの作成・名前の変更・削除。IMAP ではサーバーに、POP3 ではこの PC だけに反映する。</summary>
public sealed class FolderService(IMailStoreSessionFactory sessions, FolderRepository folders, MessageRepository messages, MessageFileStore files)
{
    public async Task<Folder> CreateAsync(Account account, Folder? parent, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        name = ValidateName(name);

        if (account.IncomingProtocol == IncomingProtocol.Pop3)
        {
            var fullName = parent is null ? name : $"{parent.FullName}/{name}";
            return folders.Upsert(account.Id, fullName, name, '/', FolderRole.None, isLocal: true);
        }

        await using var session = await sessions.ConnectAsync(account, cancellationToken).ConfigureAwait(false);
        var created = await session.CreateFolderAsync(parent?.FullName, name, cancellationToken).ConfigureAwait(false);
        return folders.Upsert(account.Id, created.FullName, created.Name, created.Delimiter, FolderRole.None);
    }

    public async Task<Folder> RenameAsync(Account account, Folder folder, string newName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(folder);
        EnsureEditable(folder);
        newName = ValidateName(newName);

        string newFullName;
        if (folder.IsLocal)
        {
            var slash = folder.FullName.LastIndexOf('/');
            newFullName = slash >= 0 ? folder.FullName[..(slash + 1)] + newName : newName;
        }
        else
        {
            await using var session = await sessions.ConnectAsync(account, cancellationToken).ConfigureAwait(false);
            newFullName = await session.RenameFolderAsync(folder.FullName, newName, cancellationToken).ConfigureAwait(false);
        }

        // 名前を変えても UID は変わらないため、メールはそのまま新しいフォルダに付け替える
        folders.Rename(folder.Id, newFullName, newName);
        return folders.Get(folder.Id)!;
    }

    public async Task DeleteAsync(Account account, Folder folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(folder);
        EnsureEditable(folder);

        if (!folder.IsLocal)
        {
            await using var session = await sessions.ConnectAsync(account, cancellationToken).ConfigureAwait(false);
            await session.DeleteFolderAsync(folder.FullName, cancellationToken).ConfigureAwait(false);
        }

        files.DeleteMany(messages.DeleteAllInFolder(folder.Id));
        folders.Delete(folder.Id);
    }

    private static void EnsureEditable(Folder folder)
    {
        if (folder.Role != FolderRole.None)
        {
            throw new InvalidOperationException("受信トレイや送信済みなどの特別なフォルダは、名前の変更や削除ができません。");
        }
    }

    private static string ValidateName(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("フォルダ名を入力してください。", nameof(name));
        }

        if (trimmed.IndexOfAny(['/', '\\', '%', '*']) >= 0)
        {
            throw new ArgumentException("フォルダ名に「/ \\ % *」は使えません。", nameof(name));
        }

        return trimmed;
    }
}
