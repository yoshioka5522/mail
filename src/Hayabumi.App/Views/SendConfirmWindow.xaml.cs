using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hayabumi.Core.Compose;

namespace Hayabumi.App.Views;

/// <summary>
/// 誤送信防止の確認画面。宛先（社外は強調）と添付ファイルを一つずつチェックしないと送信できない。
/// </summary>
public partial class SendConfirmWindow : Window
{
    private readonly List<CheckBox> _checks = [];

    public SendConfirmWindow(SendCheckResult result, Draft draft)
    {
        InitializeComponent();

        AddHeader("宛先");
        foreach (var recipient in result.Recipients)
        {
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
            text.Inlines.Add(new System.Windows.Documents.Run($"{recipient.Field}: ") { Foreground = Brushes.Gray });
            text.Inlines.Add(new System.Windows.Documents.Run(DraftFactory.Format(recipient.Address)));
            if (recipient.IsExternal)
            {
                text.Inlines.Add(new System.Windows.Documents.Run("  社外") { Foreground = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)), FontWeight = FontWeights.Bold });
            }

            AddCheck(text);
        }

        if (draft.Attachments.Count > 0)
        {
            AddHeader("添付ファイル");
            foreach (var path in draft.Attachments)
            {
                AddCheck(new TextBlock { Text = Path.GetFileName(path), TextWrapping = TextWrapping.Wrap });
            }
        }

        AddHeader("件名");
        CheckList.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(draft.Subject) ? "（件名なし）" : draft.Subject,
            Margin = new Thickness(4, 0, 0, 4),
            TextWrapping = TextWrapping.Wrap,
        });

        WarningList.ItemsSource = result.Warnings;

        // 社外の宛先があるときは、まとめてチェックできないようにする
        CheckAllButton.Visibility = result.Recipients.Any(r => r.IsExternal) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AddHeader(string text)
        => CheckList.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4) });

    private void AddCheck(TextBlock content)
    {
        var check = new CheckBox { Content = content, Margin = new Thickness(4, 2, 0, 2), VerticalContentAlignment = VerticalAlignment.Center };
        check.Checked += (_, _) => Update();
        check.Unchecked += (_, _) => Update();
        _checks.Add(check);
        CheckList.Children.Add(check);
    }

    private void Update() => SendButton.IsEnabled = _checks.All(c => c.IsChecked == true);

    private void OnCheckAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var check in _checks)
        {
            check.IsChecked = true;
        }
    }

    private void OnSendClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
