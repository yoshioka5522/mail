namespace Hayabumi.Core.Compose;

public enum ComposeMode
{
    New,
    Reply,
    ReplyAll,
    Forward,
}

public enum BodyCharset
{
    /// <summary>UTF-8（既定。絵文字や多言語を含めて送れる）。</summary>
    Utf8,

    /// <summary>ISO-2022-JP（古いメールソフトとのやり取り向け）。</summary>
    Iso2022Jp,
}

/// <summary>
/// 作成中のメール。下書きとして JSON で保存する。
/// 宛先は「名前 &lt;address&gt;, ...」形式の文字列で持つ（入力欄の内容そのまま）。
/// </summary>
public sealed record Draft
{
    public long Id { get; init; }
    public long AccountId { get; init; }
    public ComposeMode Mode { get; init; }
    public string To { get; init; } = string.Empty;
    public string Cc { get; init; } = string.Empty;
    public string Bcc { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;

    /// <summary>添付ファイルのフルパス（下書きフォルダにコピーしたもの）。</summary>
    public IReadOnlyList<string> Attachments { get; init; } = [];

    public string? InReplyTo { get; init; }
    public IReadOnlyList<string> References { get; init; } = [];

    /// <summary>返信・転送の元になったメール（送信後に「返信済み」「転送済み」の印を付けるため）。</summary>
    public long? SourceMessageId { get; init; }

    public BodyCharset Charset { get; init; } = BodyCharset.Utf8;

    public DateTimeOffset UpdatedAt { get; init; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(To) && string.IsNullOrWhiteSpace(Cc) && string.IsNullOrWhiteSpace(Bcc)
        && string.IsNullOrWhiteSpace(Subject) && string.IsNullOrWhiteSpace(Body) && Attachments.Count == 0;
}
