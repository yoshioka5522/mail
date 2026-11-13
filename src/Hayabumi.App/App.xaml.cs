using System.Windows;
using System.Windows.Threading;
using Hayabumi.App.Services;
using Hayabumi.App.ViewModels;
using Hayabumi.App.Views;

namespace Hayabumi.App;

public partial class App : Application
{
    private AppServices? _services;
    private TrayIcon? _tray;
    private IdleWatcher? _idle;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("未処理の例外", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("未処理のタスク例外", args.Exception);
            args.SetObserved();
        };

        try
        {
            _services = AppServices.Create();
        }
        catch (Exception ex)
        {
            Log.Error("起動に失敗しました", ex);
            MessageBox.Show($"起動できませんでした。\n\n{ex.Message}", "Hayabumi", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var demo = e.Args.Contains("--demo");
        if (demo)
        {
            DemoData.Seed(_services);
        }

        var viewModel = new MainViewModel(_services) { OfflineDemo = demo };
        var window = new MainWindow(viewModel, _services);
        MainWindow = window;

        _tray = new TrayIcon();
        _tray.OpenRequested += (_, _) => window.ShowFromTray();
        _tray.SyncRequested += (_, _) => viewModel.SyncCommand.Execute(null);
        _tray.ComposeRequested += (_, _) =>
        {
            window.ShowFromTray();
            viewModel.NewMessageCommand.Execute(null);
        };
        _tray.ExitRequested += (_, _) => window.ExitApplication();
        viewModel.NewMailArrived += (_, mail) =>
        {
            if (!window.IsActive)
            {
                _tray.Notify(mail.Title, mail.Text);
            }
        };
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.UnreadInboxCount))
            {
                _tray.SetUnreadCount(viewModel.UnreadInboxCount);
                window.SetUnreadBadge(viewModel.UnreadInboxCount);
            }
        };
        window.MinimizedToTray += (_, _) => _tray.Notify("Hayabumi", "タスクトレイで動作しています。終了するには、トレイのアイコンを右クリックして［終了］を選んでください。");

        _idle = new IdleWatcher(_services.Sessions);
        _idle.Changed += (_, accountId) => Dispatcher.BeginInvoke(async () => await viewModel.SyncInboxAsync(accountId));
        viewModel.AccountsChanged += (_, _) => _idle.Watch(_services.Accounts.GetAll());

        // 2 つ目の起動から合図が来たら、画面を前面に出す
        var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowSignalName);
        var listener = new Thread(() =>
        {
            while (showSignal.WaitOne())
            {
                Dispatcher.BeginInvoke(window.ShowFromTray);
            }
        })
        { IsBackground = true, Name = "Hayabumi.ShowSignal" };
        listener.Start();

        if (e.Args.Contains("--screenshot"))
        {
            window.WindowState = WindowState.Maximized;
        }

        var startMinimized = e.Args.Contains("--minimized");
        if (!startMinimized)
        {
            window.Show();
        }

        viewModel.Start();

        // 画面の確認用: --screenshot <ファイル> で、表示が落ち着いてから画面を保存して終了する
        var shotIndex = Array.IndexOf(e.Args, "--screenshot");
        if (shotIndex >= 0 && shotIndex + 1 < e.Args.Length)
        {
            var shotPath = e.Args[shotIndex + 1];
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    window.Activate();
                    ScreenCapture.Save(shotPath);
                    var compose = new Views.ComposeWindow(_services, Hayabumi.Core.Compose.DraftFactory.CreateReply(
                        _services.Accounts.GetAll()[0],
                        new MimeKit.MimeMessage([new MimeKit.MailboxAddress("山田 太郎", "yamada@partner.example.jp")], [], "来週の定例会議の日程について",
                            new MimeKit.TextPart("plain") { Text = "来週の定例会議ですが、火曜日の 10 時からでいかがでしょうか。" }),
                        null, replyAll: false));
                    compose.Show();
                    var composeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                    composeTimer.Tick += (_, _) =>
                    {
                        composeTimer.Stop();
                        ScreenCapture.Save(System.IO.Path.ChangeExtension(shotPath, null) + "-compose.png");
                        compose.Close();
                        var settingsWindow = new Views.SettingsWindow(_services) { Owner = window };
                        settingsWindow.Tabs.SelectedIndex = 1;
                        settingsWindow.Show();
                        var settingsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                        settingsTimer.Tick += (_, _) =>
                        {
                            settingsTimer.Stop();
                            ScreenCapture.Save(System.IO.Path.ChangeExtension(shotPath, null) + "-settings.png");
                            settingsWindow.Close();
                            window.ExitApplication();
                        };
                        settingsTimer.Start();
                    };
                    composeTimer.Start();
                }
                catch (Exception ex)
                {
                    Log.Error("画面の保存に失敗しました", ex);
                    Shutdown(2);
                }
            };
            timer.Start();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _idle?.Dispose();
        _tray?.Dispose();
        _services?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("画面処理中の例外", e.Exception);
        MessageBox.Show($"エラーが発生しました。\n\n{e.Exception.Message}", "Hayabumi", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
