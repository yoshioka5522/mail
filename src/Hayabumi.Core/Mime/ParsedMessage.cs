namespace Hayabumi.Core.Mime;

public sealed record AttachmentInfo(string FileName, string MimeType, string? ContentId, bool IsInline);

/// <summary>
/// 解析済みのメール。一覧表示・検索インデックス・本文表示に必要な情報を持つ。
/// </summary>
public sealed record ParsedMessage
{
    public string? MessageId { get; init; }
    public string? InReplyTo { get; init; }
    public IReadOnlyList<string> References { get; init; } = [];
    public string Subject { get; init; } = string.Empty;
    public string FromName { get; init; } = string.Empty;
    public string FromAddress { get; init; } = string.Empty;
    public string ToText { get; init; } = string.Empty;
    public string CcText { get; init; } = string.Empty;
    public DateTimeOffset Date { get; init; }
    public string? TextBody { get; init; }
    public string? HtmlBody { get; init; }
    public IReadOnlyList<AttachmentInfo> Attachments { get; init; } = [];

    public bool HasAttachments => Attachments.Any(a => !a.IsInline);

    /// <summary>検索インデックス用の本文（テキストがなければ HTML から変換したもの）。</summary>
    public string PlainText => TextBody ?? (HtmlBody is null ? string.Empty : HtmlText.ToPlainText(HtmlBody));

    public string Preview => HtmlText.MakePreview(PlainText);
}
