using System.Windows;
using System.Windows.Threading;
using Hayabumi.App.Services;
using Hayabumi.App.ViewModels;
using Hayabumi.App.Views;

namespace Hayabumi.App;

public partial class App : Application
{
    private AppServices? _services;

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
        window.Show();
        viewModel.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
