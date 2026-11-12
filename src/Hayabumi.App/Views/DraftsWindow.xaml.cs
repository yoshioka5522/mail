using System.Globalization;
using System.Windows;
using Hayabumi.App.Services;
using Hayabumi.Core.Compose;

namespace Hayabumi.App.Views;

public sealed record DraftRow(string State, string To, string Subject, string Date, Draft? Draft, OutboxItem? Outbox);

/// <summary>保存した下書きと、送信待ち・送信に失敗したメールの一覧。</summary>
public partial class DraftsWindow : Window
{
    private readonly AppServices _services;

    public DraftsWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        Reload();
    }

    public event EventHandler<Draft>? OpenRequested;

    private DraftRow? Selected => Items.SelectedItem as DraftRow;

    private void Reload()
    {
        var rows = new List<DraftRow>();
        foreach (var item in _services.Outbox.GetAll())
        {
            var state = item.Failed ? "送信失敗" : item.Attempts > 0 ? "再送待ち" : "送信待ち";
            rows.Add(new DraftRow(state, item.Draft.To, item.Draft.Subject, Format(item.SendAt), null, item));
        }

        foreach (var draft in _services.Drafts.GetAll())
        {
            rows.Add(new DraftRow("下書き", draft.To, draft.Subject, Format(draft.UpdatedAt), draft, null));
        }

        Items.ItemsSource = rows;
    }

    private static string Format(DateTimeOffset date) => date.ToLocalTime().ToString("M/d HH:mm", CultureInfo.InvariantCulture);

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        switch (Selected)
        {
            case { Draft: { } draft }:
                OpenRequested?.Invoke(this, draft);
                Close();
                break;
            case { Outbox: { } item }:
                // 送信待ちから外して下書きに戻す
                if (_services.Send.Cancel(item.Id) is { } restored)
                {
                    OpenRequested?.Invoke(this, restored);
                    Close();
                }
                else
                {
                    Reload();
                }

                break;
        }
    }

    private void OnRetryClick(object sender, RoutedEventArgs e)
    {
        if (Selected?.Outbox is { } item)
        {
            _services.Outbox.Retry(item.Id);
            Reload();
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (Selected is null ||
            MessageBox.Show(this, "選んだメールを削除しますか？", "削除", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        if (Selected.Draft is { } draft)
        {
            _services.Drafts.Delete(draft.Id);
        }
        else if (Selected.Outbox is { } item && _services.Send.Cancel(item.Id) is { } restored)
        {
            _services.Drafts.Delete(restored.Id);
        }

        Reload();
    }
}
