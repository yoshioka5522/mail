using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Hayabumi.Core.Storage;

namespace Hayabumi.App.ViewModels;

public sealed partial class MessageItemViewModel : ObservableObject
{
    public static readonly MessageItemViewModel Placeholder = new(new MessageListItem());

    [ObservableProperty]
    private bool _isUnread;

    [ObservableProperty]
    private bool _isFlagged;

    public MessageItemViewModel(MessageListItem item)
    {
        Item = item;
        _isUnread = item.IsUnread && item.Id != 0;
        _isFlagged = item.IsFlagged;
    }

    public MessageListItem Item { get; private set; }

    public long Id => Item.Id;

    public string Subject => string.IsNullOrWhiteSpace(Item.Subject) ? "（件名なし）" : Item.Subject;

    public string Sender => Item.Sender;

    public string Preview => Item.Preview;

    public bool HasAttachments => Item.HasAttachments;

    public string DateText => FormatDate(Item.Date);

    public string DateToolTip => Item.Date.ToLocalTime().ToString("yyyy/MM/dd (ddd) HH:mm:ss", CultureInfo.GetCultureInfo("ja-JP"));

    public void Update(MessageListItem item)
    {
        Item = item;
        IsUnread = item.IsUnread;
        IsFlagged = item.IsFlagged;
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(HasAttachments));
    }

    public static string FormatDate(DateTimeOffset date)
    {
        var local = date.ToLocalTime();
        var now = DateTimeOffset.Now;
        if (local.Date == now.Date)
        {
            return local.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        return local.Year == now.Year
            ? local.ToString("M/d HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
    }
}
