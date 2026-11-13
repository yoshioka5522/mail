using Hayabumi.Core.Storage;

namespace Hayabumi.Core.Sync;

public sealed record SyncOptions
{
    /// <summary>ヘッダーを一度に取得する件数。</summary>
    public int HeaderBatchSize { get; init; } = 500;

    /// <summary>フラグを一度に取得する件数（CONDSTORE 非対応サーバー向け）。</summary>
    public int FlagBatchSize { get; init; } = 5000;

    /// <summary>
    /// 1 回の同期で本文のプレビューも合わせて取得する件数（新しい順）。
    /// それより古いメールのプレビューは、本文の取得時に作る。
    /// </summary>
    public int PreviewLimit { get; init; } = 1000;

    /// <summary>自動で本文を取得するメールの最大サイズ（これより大きいものは開いたときに取得する）。</summary>
    public long MaxAutoDownloadSize { get; init; } = 10 * 1024 * 1024;

    /// <summary>1 回の先読みで本文を取得する最大件数（フォルダごと）。</summary>
    public int BodyDownloadBatch { get; init; } = 100;

    /// <summary>本文を前もって取得する範囲（フォルダごとに新しい順でこの件数まで）。それより古いメールは開いたときに取得する。</summary>
    public int BodyPrefetchWindow { get; init; } = 300;
}

public sealed record FolderSyncResult
{
    public long FolderId { get; init; }
    public int Added { get; init; }
    public int Removed { get; init; }
    public int FlagsUpdated { get; init; }

    /// <summary>UIDVALIDITY が変わり、フォルダを取り直した。</summary>
    public bool Reset { get; init; }

    /// <summary>初めての同期（または取り直し）。新着通知の対象にしない。</summary>
    public bool IsInitial { get; init; }

    public FolderRole Role { get; init; }

    /// <summary>今回追加したメールのうち、未読のもの（新着通知用。新しい順に最大 5 件）。</summary>
    public IReadOnlyList<NewMessage> NewUnread { get; init; } = [];
}

public sealed record SyncProgress(long FolderId, string FolderName, int Done, int Total);
