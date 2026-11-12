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

        var viewModel = new MainViewModel(_services);
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

        var startMinimized = e.Args.Contains("--minimized");
        if (!startMinimized)
        {
            window.Show();
        }

        viewModel.Start();
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
