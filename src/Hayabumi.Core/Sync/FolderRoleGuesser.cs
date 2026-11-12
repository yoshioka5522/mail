using Hayabumi.Core.Storage;

namespace Hayabumi.Core.Sync;

/// <summary>
/// SPECIAL-USE 拡張に対応していないサーバーのために、フォルダ名から役割を推定する。
/// </summary>
public static class FolderRoleGuesser
{
    private static readonly (FolderRole Role, string[] Names)[] s_names =
    [
        (FolderRole.Sent, ["sent", "sent items", "sent messages", "sent mail", "送信済み", "送信済みアイテム", "送信済みメール", "送信箱", "送信トレイ"]),
        (FolderRole.Drafts, ["drafts", "draft", "下書き", "草稿"]),
        (FolderRole.Trash, ["trash", "deleted items", "deleted messages", "deleted", "bin", "ゴミ箱", "ごみ箱", "削除済みアイテム", "削除済みメール"]),
        (FolderRole.Junk, ["junk", "spam", "junk e-mail", "junk email", "bulk mail", "迷惑メール", "迷惑メール フォルダー"]),
        (FolderRole.Archive, ["archive", "archives", "アーカイブ"]),
    ];

    public static FolderRole Guess(string fullName, char? delimiter)
    {
        ArgumentNullException.ThrowIfNull(fullName);
        if (fullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase))
        {
            return FolderRole.Inbox;
        }

        var name = fullName;
        if (delimiter is { } d && name.LastIndexOf(d) is var i and >= 0)
        {
            var parent = name[..i];
            // 「INBOX.Sent」のように受信トレイの直下に作られている場合も対象にする
            if (!parent.Equals("INBOX", StringComparison.OrdinalIgnoreCase) && !parent.StartsWith('['))
            {
                return FolderRole.None;
            }

            name = name[(i + 1)..];
        }

        name = name.Trim().ToLowerInvariant();
        foreach (var (role, names) in s_names)
        {
            if (names.Contains(name))
            {
                return role;
            }
        }

        return FolderRole.None;
    }
}
