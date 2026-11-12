using System.Globalization;
using Hayabumi.Core.Mime;

namespace Hayabumi.App.ViewModels;

public sealed record AttachmentViewModel(int Index, string FileName, string MimeType);

/// <summary>右側に表示している 1 通のメール。</summary>
public sealed class MessageDetailViewModel
{
    public required long MessageId { get; init; }
    public required string FilePath { get; init; }
    public required ParsedMessage Parsed { get; init; }
    public required string HtmlFilePath { get; init; }
    public bool HasRemoteContent { get; init; }
    public bool RemoteContentAllowed { get; init; }

    public string Subject => string.IsNullOrWhiteSpace(Parsed.Subject) ? "（件名なし）" : Parsed.Subject;

    public string From => string.IsNullOrWhiteSpace(Parsed.FromName)
        ? Parsed.FromAddress
        : $"{Parsed.FromName} <{Parsed.FromAddress}>";

    public string Initials => Avatar.Initials(string.IsNullOrWhiteSpace(Parsed.FromName) ? Parsed.FromAddress : Parsed.FromName);

    public System.Windows.Media.Brush AvatarBrush => Avatar.Brush(Parsed.FromAddress);

    public string To => Parsed.ToText;

    public string Cc => Parsed.CcText;

    public bool HasCc => !string.IsNullOrEmpty(Parsed.CcText);

    public string DateText => Parsed.Date.ToLocalTime().ToString("yyyy/MM/dd (ddd) HH:mm", CultureInfo.GetCultureInfo("ja-JP"));

    public IReadOnlyList<AttachmentViewModel> Attachments { get; init; } = [];

    public bool HasAttachments => Attachments.Count > 0;

    public bool ShowRemoteContentBar => HasRemoteContent && !RemoteContentAllowed;
}
