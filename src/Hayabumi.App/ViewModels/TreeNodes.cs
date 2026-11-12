using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Hayabumi.Core.Accounts;
using Hayabumi.Core.Storage;

namespace Hayabumi.App.ViewModels;

public sealed class AccountNodeViewModel(Account account)
{
    public Account Account { get; } = account;

    public string Name => string.IsNullOrWhiteSpace(Account.DisplayName) ? Account.EmailAddress : Account.DisplayName;

    public string Initials => Avatar.Initials(Name);

    public System.Windows.Media.Brush AvatarBrush => Avatar.Brush(Account.EmailAddress);

    public string EmailAddress => Account.EmailAddress;

    public ObservableCollection<FolderNodeViewModel> Folders { get; } = [];
}

public sealed partial class FolderNodeViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnreadText))]
    private int _unreadCount;

    public FolderNodeViewModel(Folder folder)
    {
        Folder = folder;
        _unreadCount = folder.UnreadCount;
    }

    /// <summary>すべてのアカウントをまとめた表示の項目を作る。</summary>
    public FolderNodeViewModel(UnifiedView view)
        : this(new Folder { Id = -1 - (int)view, FullName = "unified:" + view, DisplayName = view.ToString() })
    {
        Unified = view;
    }

    /// <summary>すべてのアカウントをまとめた表示なら、その種類。</summary>
    public UnifiedView? Unified { get; }

    public Folder Folder { get; private set; }

    public long Id => Folder.Id;

    public string Name => Unified switch
    {
        UnifiedView.AllInboxes => "すべての受信トレイ",
        UnifiedView.Unread => "未読",
        UnifiedView.Flagged => "フラグ付き",
        _ => FolderName,
    };

    private string FolderName => Folder.Role switch
    {
        FolderRole.Inbox => "受信トレイ",
        FolderRole.Sent => "送信済み",
        FolderRole.Drafts => "下書き",
        FolderRole.Trash => "ゴミ箱",
        FolderRole.Junk => "迷惑メール",
        FolderRole.Archive => "アーカイブ",
        FolderRole.All => "すべてのメール",
        _ => Folder.DisplayName,
    };

    /// <summary>階層の深さに応じた字下げ。</summary>
    public double Indent => Folder.Delimiter is { } d && Folder.Role == FolderRole.None
        ? Folder.FullName.Count(c => c == d) * 12
        : 0;

    public string Icon => Unified switch
    {
        UnifiedView.AllInboxes => "\uE8B7",
        UnifiedView.Unread => "\uE715",
        UnifiedView.Flagged => "\uE7C1",
        _ => FolderIcon,
    };

    private string FolderIcon => Folder.Role switch
    {
        FolderRole.Inbox => "",
        FolderRole.Sent => "",
        FolderRole.Drafts => "",
        FolderRole.Trash => "",
        FolderRole.Junk => "",
        FolderRole.Archive => "",
        _ => "",
    };

    public string UnreadText => UnreadCount > 0 ? UnreadCount.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;

    public void Update(Folder folder)
    {
        Folder = folder;
        UnreadCount = folder.UnreadCount;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Indent));
    }

    /// <summary>名前の変更や削除ができるフォルダか（受信トレイなどの特別なフォルダは不可）。</summary>
    public bool IsEditable => Unified is null && Folder.Role == FolderRole.None;
}
