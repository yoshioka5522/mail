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

    /// <summary>1 回の同期で本文を取得する最大件数（フォルダごと）。</summary>
    public int BodyDownloadBatch { get; init; } = 200;
}

public sealed record FolderSyncResult
{
    public long FolderId { get; init; }
    public int Added { get; init; }
    public int Removed { get; init; }
    public int FlagsUpdated { get; init; }

    /// <summary>UIDVALIDITY が変わり、フォルダを取り直した。</summary>
    public bool Reset { get; init; }
}

public sealed record SyncProgress(string FolderName, int Done, int Total);
