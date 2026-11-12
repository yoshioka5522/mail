namespace Hayabumi.Core.Storage;

[Flags]
public enum MessageFlags
{
    None = 0,
    Seen = 1,
    Answered = 2,
    Flagged = 4,
    Deleted = 8,
    Draft = 16,
    Forwarded = 32,
}

public enum FolderRole
{
    None,
    Inbox,
    Sent,
    Drafts,
    Trash,
    Junk,
    Archive,
    All,
    Outbox,
}

public enum BodyState
{
    HeadersOnly = 0,
    Downloaded = 1,
}

public sealed record Folder
{
    public long Id { get; init; }
    public long AccountId { get; init; }
    public required string FullName { get; init; }
    public required string DisplayName { get; init; }
    public char? Delimiter { get; init; }
    public FolderRole Role { get; init; }
    public uint? UidValidity { get; init; }
    public ulong? HighestModSeq { get; init; }
    public int UnreadCount { get; init; }
    public int TotalCount { get; init; }
}

/// <summary>サーバーから取得したヘッダー情報など、メッセージを登録するときの値。</summary>
public sealed record NewMessage
{
    public uint? Uid { get; init; }
    public string? MessageId { get; init; }
    public string? InReplyTo { get; init; }
    public string Subject { get; init; } = string.Empty;
    public string FromName { get; init; } = string.Empty;
    public string FromAddress { get; init; } = string.Empty;
    public string ToText { get; init; } = string.Empty;
    public string CcText { get; init; } = string.Empty;
    public DateTimeOffset Date { get; init; }
    public long Size { get; init; }
    public MessageFlags Flags { get; init; }
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public ulong? ModSeq { get; init; }
    public bool HasAttachments { get; init; }
    public string Preview { get; init; } = string.Empty;
}

/// <summary>メール一覧の1行分。</summary>
public sealed record MessageListItem
{
    public long Id { get; init; }
    public long AccountId { get; init; }
    public long FolderId { get; init; }
    public uint? Uid { get; init; }
    public string Subject { get; init; } = string.Empty;
    public string FromName { get; init; } = string.Empty;
    public string FromAddress { get; init; } = string.Empty;
    public string ToText { get; init; } = string.Empty;
    public DateTimeOffset Date { get; init; }
    public MessageFlags Flags { get; init; }
    public bool HasAttachments { get; init; }
    public string Preview { get; init; } = string.Empty;
    public BodyState BodyState { get; init; }
    public string? FilePath { get; init; }

    public bool IsUnread => (Flags & MessageFlags.Seen) == 0;
    public bool IsFlagged => (Flags & MessageFlags.Flagged) != 0;
    public string Sender => string.IsNullOrWhiteSpace(FromName) ? FromAddress : FromName;
}
