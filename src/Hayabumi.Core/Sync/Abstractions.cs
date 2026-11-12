using Hayabumi.Core.Accounts;
using Hayabumi.Core.Storage;

namespace Hayabumi.Core.Sync;

public sealed record RemoteFolderInfo(string FullName, string Name, char? Delimiter, FolderRole Role, bool CanSelect);

public sealed record RemoteFlags(uint Uid, MessageFlags Flags, IReadOnlyList<string> Keywords, ulong? ModSeq);

/// <summary>サーバー上でメッセージが見つからなかった（ほかの端末で削除されたなど）。</summary>
public sealed class RemoteMessageNotFoundException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// メールサーバーとの接続。IMAP の実装は <see cref="Imap.ImapMailStoreSession"/>。
/// 同期処理をサーバーなしでテストできるよう、必要な操作だけを抽象化している。
/// </summary>
public interface IMailStoreSession : IAsyncDisposable
{
    Task<IReadOnlyList<RemoteFolderInfo>> GetFoldersAsync(CancellationToken cancellationToken);

    Task<IRemoteFolder> OpenFolderAsync(string fullName, bool writable, CancellationToken cancellationToken);

    /// <summary>メッセージをフォルダに保存する（送信済み・下書きへの保存）。</summary>
    Task AppendAsync(string folderFullName, Stream message, MessageFlags flags, CancellationToken cancellationToken);
}

public interface IMailStoreSessionFactory
{
    Task<IMailStoreSession> ConnectAsync(Account account, CancellationToken cancellationToken);
}

/// <summary>開いているサーバー上のフォルダ。</summary>
public interface IRemoteFolder : IAsyncDisposable
{
    string FullName { get; }

    uint UidValidity { get; }

    /// <summary>CONDSTORE に対応していない場合は null。</summary>
    ulong? HighestModSeq { get; }

    int Count { get; }

    /// <summary>指定した UID 以上のメッセージの UID を返す（1 を指定するとすべて）。</summary>
    Task<IReadOnlyList<uint>> GetUidsAsync(uint minUid, CancellationToken cancellationToken);

    Task<IReadOnlyList<NewMessage>> FetchHeadersAsync(IReadOnlyList<uint> uids, bool includePreview, CancellationToken cancellationToken);

    /// <summary>フラグを取得する。<paramref name="changedSince"/> を指定すると、それ以降に変わったものだけを返す。</summary>
    Task<IReadOnlyList<RemoteFlags>> FetchFlagsAsync(IReadOnlyList<uint> uids, ulong? changedSince, CancellationToken cancellationToken);

    /// <summary>メッセージ全体（RFC 5322 形式の生データ）を書き出す。</summary>
    Task CopyMessageToAsync(uint uid, Stream destination, CancellationToken cancellationToken);

    Task SetFlagsAsync(IReadOnlyList<uint> uids, MessageFlags flags, bool add, CancellationToken cancellationToken);

    Task MoveToAsync(IReadOnlyList<uint> uids, string destinationFullName, CancellationToken cancellationToken);

    Task DeletePermanentlyAsync(IReadOnlyList<uint> uids, CancellationToken cancellationToken);
}
