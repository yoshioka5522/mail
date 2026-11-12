using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Hayabumi.App.Services;
using Hayabumi.Core.Accounts;
using Hayabumi.Core.Compose;
using Microsoft.Win32;

namespace Hayabumi.App.Views;

public sealed record AttachmentItem(string Path, string Name, string SizeText);

public partial class ComposeWindow : Window
{
    public static readonly RoutedCommand SendCommand = new();
    public static readonly RoutedCommand SaveCommand = new();

    private readonly AppServices _services;
    private readonly DispatcherTimer _autoSave;
    private Draft _draft;
    private string _savedSnapshot;
    private bool _closingHandled;

    public ComposeWindow(AppServices services, Draft draft)
    {
        _services = services;
        _draft = draft;
        InitializeComponent();

        var accounts = services.Accounts.GetAll();
        AccountBox.ItemsSource = accounts;
        AccountBox.SelectedItem = accounts.FirstOrDefault(a => a.Id == draft.AccountId) ?? (accounts.Count > 0 ? accounts[0] : null);
        ToBox.Contacts = CcBox.Contacts = BccBox.Contacts = services.Contacts;

        ToBox.Text = draft.To;
        CcBox.Text = draft.Cc;
        BccBox.Text = draft.Bcc;
        SubjectBox.Text = draft.Subject;
        BodyBox.Text = draft.Body;
        CharsetBox.SelectedIndex = draft.Charset == BodyCharset.Iso2022Jp ? 1 : 0;
        RefreshAttachments();
        UpdateTitle();
        _savedSnapshot = Snapshot(CurrentDraft());

        _autoSave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _autoSave.Tick += (_, _) => SaveIfChanged();
        _autoSave.Start();

        Loaded += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(ToBox.Text))
            {
                ToBox.Focus();
            }
            else
            {
                BodyBox.Focus();
                BodyBox.CaretIndex = 0;
            }
        };
        Closing += OnClosing;
        Closed += (_, _) => _autoSave.Stop();
    }

    /// <summary>送信待ちに入れたときに通知する（メイン画面で「取り消し」を表示するため）。</summary>
    public event EventHandler<OutboxItem>? Queued;

    private Account? SelectedAccount => AccountBox.SelectedItem as Account;

    private Draft CurrentDraft() => _draft with
    {
        AccountId = SelectedAccount?.Id ?? _draft.AccountId,
        To = ToBox.Text.Trim().TrimEnd(','),
        Cc = CcBox.Text.Trim().TrimEnd(','),
        Bcc = BccBox.Text.Trim().TrimEnd(','),
        Subject = SubjectBox.Text,
        Body = BodyBox.Text,
        Charset = CharsetBox.SelectedIndex == 1 ? BodyCharset.Iso2022Jp : BodyCharset.Utf8,
    };

    private static string Snapshot(Draft d) => string.Join('\u0001', d.AccountId, d.To, d.Cc, d.Bcc, d.Subject, d.Body, d.Charset, string.Join('|', d.Attachments));

    private bool HasChanges => Snapshot(CurrentDraft()) != _savedSnapshot;

    private void SaveIfChanged()
    {
        var current = CurrentDraft();
        if (!HasChanges || (current.IsEmpty && current.Id == 0))
        {
            return;
        }

        Save();
    }

    private void Save()
    {
        _draft = _services.Drafts.Save(CurrentDraft());
        _savedSnapshot = Snapshot(_draft);
        SavedText.Text = $"{DateTime.Now:HH:mm} に下書きを保存しました";
    }

    private void OnSaveClick(object sender, RoutedEventArgs e) => Save();

    private void OnSaveExecuted(object sender, ExecutedRoutedEventArgs e) => Save();

    private async void OnSendExecuted(object sender, ExecutedRoutedEventArgs e) => await SendAsync();

    private async void OnSendClick(object sender, RoutedEventArgs e) => await SendAsync();

    private async Task SendAsync()
    {
        if (SelectedAccount is not { } account)
        {
            MessageBox.Show(this, "差出人のアカウントを選んでください。", "送信", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var draft = CurrentDraft();
        SendCheckResult checks;
        try
        {
            checks = SendChecks.Analyze(draft, account);
        }
        catch (AddressParseException ex)
        {
            MessageBox.Show(this, ex.Message + "\nアドレスはカンマ（,）で区切って入力してください。", "送信", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!checks.CanSend)
        {
            MessageBox.Show(this, string.Join("\n", checks.Warnings.Where(w => w.Blocking).Select(w => w.Message)), "送信", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = new SendConfirmWindow(checks, draft) { Owner = this };
        if (confirm.ShowDialog() != true)
        {
            return;
        }

        SendButton.IsEnabled = false;
        try
        {
            _draft = _services.Drafts.Save(draft);
            var item = await _services.Send.QueueAsync(_draft);
            _closingHandled = true;
            Queued?.Invoke(this, item);
            Close();
        }
        catch (Exception ex)
        {
            Log.Error("送信の準備に失敗しました", ex);
            MessageBox.Show(this, $"送信できませんでした。\n{ex.Message}", "送信", MessageBoxButton.OK, MessageBoxImage.Error);
            SendButton.IsEnabled = true;
        }
    }

    private void OnAttachClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = "添付するファイル" };
        if (dialog.ShowDialog(this) == true)
        {
            AddAttachments(dialog.FileNames);
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            AddAttachments(files.Where(File.Exists));
        }
    }

    private void AddAttachments(IEnumerable<string> paths)
    {
        var draft = CurrentDraft();
        foreach (var path in paths)
        {
            draft = _services.Drafts.AddAttachment(draft, path);
        }

        _draft = draft;
        _savedSnapshot = Snapshot(_draft);
        RefreshAttachments();
    }

    private void OnRemoveAttachmentClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string path)
        {
            _draft = _services.Drafts.RemoveAttachment(CurrentDraft(), path);
            _savedSnapshot = Snapshot(_draft);
            RefreshAttachments();
        }
    }

    private void RefreshAttachments()
    {
        AttachmentList.ItemsSource = _draft.Attachments
            .Select(p => new AttachmentItem(p, Path.GetFileName(p), File.Exists(p) ? FormatSize(new FileInfo(p).Length) : "見つかりません"))
            .ToList();
    }

    private void OnDiscardClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "この下書きを破棄しますか？", "破棄", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        if (_draft.Id != 0)
        {
            _services.Drafts.Delete(_draft.Id);
        }

        _closingHandled = true;
        Close();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closingHandled || !HasChanges || CurrentDraft().IsEmpty)
        {
            return;
        }

        var answer = MessageBox.Show(this, "下書きに保存しますか？", "Hayabumi", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel)
        {
            e.Cancel = true;
        }
        else if (answer == MessageBoxResult.Yes)
        {
            Save();
        }
        else if (_draft.Id != 0 && _draft.UpdatedAt == default)
        {
            _services.Drafts.Delete(_draft.Id);
        }
    }

    private void OnSubjectChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateTitle();

    private void UpdateTitle() => Title = string.IsNullOrWhiteSpace(SubjectBox.Text) ? "新規メッセージ" : SubjectBox.Text;

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0 / 1024.0:0.#} MB"),
    };
}
