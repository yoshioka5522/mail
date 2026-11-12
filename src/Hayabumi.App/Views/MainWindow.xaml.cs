using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Hayabumi.App.Services;
using Hayabumi.App.ViewModels;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace Hayabumi.App.Views;

public partial class MainWindow : Window
{
    private const string ViewerHost = "viewer.hayabumi";

    private readonly MainViewModel _viewModel;
    private readonly AppServices _services;
    private bool _webViewReady;
    private MessageDetailViewModel? _pendingDetail;
    private bool _changingFolderSelection;
    private bool _exitRequested;
    private bool _trayNoticeShown;

    public MainWindow(MainViewModel viewModel, AppServices services)
    {
        _viewModel = viewModel;
        _services = services;
        DataContext = viewModel;
        InitializeComponent();

        viewModel.DetailChanged += (_, detail) => ShowDetail(detail);
        viewModel.SelectionProvider = () => [.. MessageList.SelectedItems.OfType<MessageItemViewModel>()];
        viewModel.AddAccountRequested += (_, _) => OpenAddAccount();
        viewModel.ComposeRequested += (_, draft) => OpenCompose(draft);
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedFolder))
            {
                Dispatcher.BeginInvoke(SyncFolderSelection, System.Windows.Threading.DispatcherPriority.Loaded);
            }
        };
        Closing += (_, e) =>
        {
            if (_exitRequested)
            {
                return;
            }

            // 閉じてもタスクトレイに常駐し、受信と通知を続ける
            e.Cancel = true;
            Hide();
            if (!_trayNoticeShown)
            {
                _trayNoticeShown = true;
                MinimizedToTray?.Invoke(this, EventArgs.Empty);
            }
        };
        TaskbarItemInfo = new System.Windows.Shell.TaskbarItemInfo();
        Loaded += async (_, _) =>
        {
            SyncFolderSelection();
            await InitializeWebViewAsync();
        };
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            Directory.CreateDirectory(_services.WebViewDataDirectory);
            Directory.CreateDirectory(_services.ViewerDirectory);
            var environment = await CoreWebView2Environment.CreateAsync(null, _services.WebViewDataDirectory);
            await BodyView.EnsureCoreWebView2Async(environment);

            var core = BodyView.CoreWebView2;
            var settings = core.Settings;
            settings.IsScriptEnabled = false;
            settings.IsWebMessageEnabled = false;
            settings.AreHostObjectsAllowed = false;
            settings.AreDevToolsEnabled = false;
            settings.IsStatusBarEnabled = true;
            settings.IsPasswordAutosaveEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsZoomControlEnabled = true;

            core.SetVirtualHostNameToFolderMapping(ViewerHost, _services.ViewerDirectory, CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += OnNewWindowRequested;

            _webViewReady = true;
            if (_pendingDetail is not null)
            {
                ShowDetail(_pendingDetail);
                _pendingDetail = null;
            }
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            Log.Error("WebView2 ランタイムが見つかりません", ex);
            MessageBox.Show(
                this,
                "メール本文の表示に必要な「Microsoft Edge WebView2 ランタイム」が見つかりません。\n" +
                "Microsoft のサイトからインストールしてから、もう一度起動してください。",
                "Hayabumi",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void ShowDetail(MessageDetailViewModel? detail)
    {
        if (!_webViewReady)
        {
            _pendingDetail = detail;
            return;
        }

        var target = detail is null
            ? "about:blank"
            : $"https://{ViewerHost}/{Path.GetFileName(detail.HtmlFilePath)}";
        BodyView.CoreWebView2.Navigate(target);
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (e.Uri.StartsWith($"https://{ViewerHost}/", StringComparison.OrdinalIgnoreCase) || e.Uri == "about:blank")
        {
            return;
        }

        // 本文内のリンクは、アプリ内ではなく既定のブラウザーで開く
        e.Cancel = true;
        OpenExternal(e.Uri);
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        OpenExternal(e.Uri);
    }

    private static void OpenExternal(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https" or "mailto"))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
    }

    private void OnFolderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingFolderSelection || sender is not ListBox list || list.SelectedItem is not FolderNodeViewModel folder)
        {
            return;
        }

        // アカウントごとに一覧が分かれているため、ほかのアカウントの選択を外す
        _changingFolderSelection = true;
        try
        {
            foreach (var other in FindVisualChildren<ListBox>(this).Where(l => l != list && l != MessageList))
            {
                other.SelectedItem = null;
            }
        }
        finally
        {
            _changingFolderSelection = false;
        }

        _viewModel.SelectedFolder = folder;
    }

    /// <summary>画面側で選んだのではないフォルダの選択（起動時や同期後）を、一覧の表示に反映する。</summary>
    private void SyncFolderSelection()
    {
        _changingFolderSelection = true;
        try
        {
            foreach (var list in FindVisualChildren<ListBox>(this).Where(l => l != MessageList))
            {
                var target = list.Items.OfType<FolderNodeViewModel>().FirstOrDefault(f => f.Id == _viewModel.SelectedFolder?.Id);
                list.SelectedItem = target;
            }
        }
        finally
        {
            _changingFolderSelection = false;
        }
    }

    private void OnMessageContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // 移動先は開くたびに作る（フォルダが増減するため）
        var moveMenu = (MenuItem)MessageList.ContextMenu.Items.OfType<MenuItem>().Single(m => m.Name == "MoveMenu");
        moveMenu.Items.Clear();
        foreach (var target in _viewModel.MoveTargets())
        {
            var item = new MenuItem { Header = target.Name, Tag = target };
            item.Click += (_, _) => _viewModel.MoveSelected(target.Folder);
            moveMenu.Items.Add(item);
        }

        moveMenu.IsEnabled = moveMenu.Items.Count > 0;
    }

    private FolderNodeViewModel? _contextFolder;
    private AccountNodeViewModel? _contextAccount;

    private void OnFolderContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var list = (ListBox)sender;
        _contextAccount = list.DataContext as AccountNodeViewModel;
        _contextFolder = (e.OriginalSource as FrameworkElement)?.DataContext as FolderNodeViewModel ?? list.SelectedItem as FolderNodeViewModel;
        var items = list.ContextMenu.Items.OfType<MenuItem>().ToList();
        items[1].IsEnabled = _contextFolder?.IsEditable == true;
        items[2].IsEnabled = _contextFolder?.IsEditable == true;
    }

    private async void OnNewFolderClick(object sender, RoutedEventArgs e)
    {
        if (_contextAccount is null)
        {
            return;
        }

        var parent = _contextFolder?.IsEditable == true ? _contextFolder : null;
        var prompt = parent is null ? "新しいフォルダの名前" : $"「{parent.Name}」の中に作るフォルダの名前";
        if (InputDialog.Ask(this, "新しいフォルダ", prompt) is { } name)
        {
            ShowError(await _viewModel.CreateFolderAsync(_contextAccount, parent, name));
        }
    }

    private async void OnRenameFolderClick(object sender, RoutedEventArgs e)
    {
        if (_contextFolder is { IsEditable: true } folder && InputDialog.Ask(this, "名前の変更", "新しい名前", folder.Name) is { } name)
        {
            ShowError(await _viewModel.RenameFolderAsync(folder, name));
        }
    }

    private async void OnDeleteFolderClick(object sender, RoutedEventArgs e)
    {
        if (_contextFolder is not { IsEditable: true } folder ||
            MessageBox.Show(this, $"フォルダ「{folder.Name}」と、中のメールをすべて削除しますか？\nこの操作は元に戻せません。", "フォルダの削除",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        ShowError(await _viewModel.DeleteFolderAsync(folder));
    }

    private void ShowError(string? message)
    {
        if (message is not null)
        {
            MessageBox.Show(this, message, "Hayabumi", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnFolderListMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // フォルダ一覧の ListBox がホイールを奪わないよう、外側のスクロールに渡す
        if (e.Handled)
        {
            return;
        }

        e.Handled = true;
        var args = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = sender };
        ((UIElement)((FrameworkElement)sender).Parent).RaiseEvent(args);
    }

    private void OnAttachmentClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AttachmentViewModel attachment)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            FileName = string.Join("_", attachment.FileName.Split(Path.GetInvalidFileNameChars())),
            Title = "添付ファイルの保存",
        };
        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.SaveAttachment(attachment, dialog.FileName);
            _viewModel.StatusText = $"{Path.GetFileName(dialog.FileName)} を保存しました";
        }
    }

    private void OpenAddAccount()
    {
        var dialog = new AddAccountWindow(_services) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _ = _viewModel.OnAccountAddedAsync();
        }
    }

    /// <summary>初めてタスクトレイに入ったとき（案内の通知を出すため）。</summary>
    public event EventHandler? MinimizedToTray;

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    /// <summary>アプリを終了する（トレイの［終了］、［ファイル］→［終了］）。</summary>
    public void ExitApplication()
    {
        _exitRequested = true;
        foreach (var window in Application.Current.Windows.OfType<ComposeWindow>().ToList())
        {
            window.Close();
            if (window.IsVisible)
            {
                // 下書きの保存確認でキャンセルされた
                _exitRequested = false;
                return;
            }
        }

        Close();
        Application.Current.Shutdown();
    }

    /// <summary>タスクバーのアイコンに未読数のバッジを重ねる。</summary>
    public void SetUnreadBadge(int count)
    {
        if (count <= 0)
        {
            TaskbarItemInfo.Overlay = null;
            return;
        }

        var text = count > 99 ? "99+" : count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawEllipse(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD9, 0x30, 0x25)), null, new Point(16, 16), 16, 16);
            var formatted = new System.Windows.Media.FormattedText(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new System.Windows.Media.Typeface("Segoe UI Semibold"),
                text.Length > 2 ? 12 : 17,
                System.Windows.Media.Brushes.White,
                1.0);
            dc.DrawText(formatted, new Point(16 - (formatted.Width / 2), 16 - (formatted.Height / 2)));
        }

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(32, 32, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);
        TaskbarItemInfo.Overlay = bitmap;
    }

    public void OpenCompose(Hayabumi.Core.Compose.Draft draft)
    {
        // メイン画面をトレイに隠しても作成中の画面が消えないよう、親ウィンドウを持たせない
        var window = new ComposeWindow(_services, draft);
        window.Queued += (_, item) => _viewModel.OnQueued(item);
        window.Show();
    }

    private void OnDraftsClick(object sender, RoutedEventArgs e)
    {
        var window = new DraftsWindow(_services) { Owner = this };
        window.OpenRequested += (_, draft) => OpenCompose(draft);
        window.ShowDialog();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.E && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocusWithin)
        {
            _viewModel.SearchText = string.Empty;
            MessageList.Focus();
            e.Handled = true;
        }
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => ExitApplication();

    private void OnUpdateTokenClick(object sender, RoutedEventArgs e)
    {
        var dialog = new UpdateTokenWindow(_services.Updates) { Owner = this };
        dialog.ShowDialog();
    }

    private void OnOAuthSettingsClick(object sender, RoutedEventArgs e)
        => new OAuthSettingsWindow(_services.OAuth) { Owner = this }.ShowDialog();

    private void OnOpenDataFolderClick(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_services.Location.Root}\"") { UseShellExecute = true });

    private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        _viewModel.StatusText = "更新を確認しています…";
        var message = await _viewModel.CheckUpdatesAsync();
        _viewModel.StatusText = message;
        if (_services.Updates.PendingVersion is not null)
        {
            if (MessageBox.Show(this, $"{message}\n今すぐ再起動して更新しますか？", "Hayabumi", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _services.Updates.ApplyAndRestart();
            }
        }
        else
        {
            MessageBox.Show(this, message, "Hayabumi", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        var version = _services.Updates.CurrentVersion
            ?? typeof(MainWindow).Assembly.GetName().Version?.ToString(3)
            ?? "-";
        MessageBox.Show(
            this,
            $"Hayabumi（はやぶみ）\nバージョン {version}\n\n広告のない、軽くて速いメールアプリ。\n\nデータフォルダ: {_services.Location.Root}",
            "バージョン情報",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
