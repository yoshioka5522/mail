using System.Diagnostics;
using System.Globalization;
using System.Windows;
using Hayabumi.App.Services;
using Hayabumi.Core.Accounts;
using Hayabumi.Core.Security.OAuth;
using Microsoft.Win32;

namespace Hayabumi.App.Views;

public partial class SettingsWindow : Window
{
    private readonly AppServices _services;
    private readonly Dictionary<long, (string Name, string Signature, string Password)> _edits = [];
    private Account? _current;

    public SettingsWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();

        var settings = services.AppSettings;
        IntervalBox.Text = settings.SyncIntervalMinutes.ToString(CultureInfo.InvariantCulture);
        UndoBox.Text = settings.UndoSendSeconds.ToString(CultureInfo.InvariantCulture);
        CloseToTrayBox.IsChecked = settings.CloseToTray;
        StartWithWindowsBox.IsChecked = AppSettings.StartWithWindows;
        StartWithWindowsBox.IsEnabled = AppSettings.IsInstalledCopy;
        DomainsBox.Text = string.Join(Environment.NewLine, settings.InternalDomains);
        DataFolderBox.Text = services.Location.Root;

        AccountList.ItemsSource = services.Accounts.GetAll();
        if (AccountList.Items.Count > 0)
        {
            AccountList.SelectedIndex = 0;
        }
    }

    /// <summary>アカウントを削除した、またはデータフォルダを移したなど、メイン画面で読み直しが必要か。</summary>
    public bool AccountsChanged { get; private set; }

    /// <summary>データフォルダを移したため、再起動が必要。</summary>
    public bool RestartRequired { get; private set; }

    private void OnAccountSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        KeepCurrentEdits();
        _current = AccountList.SelectedItem as Account;
        AccountPanel.IsEnabled = _current is not null;
        if (_current is null)
        {
            return;
        }

        var edit = _edits.TryGetValue(_current.Id, out var saved) ? saved : (_current.DisplayName, _current.Signature, string.Empty);
        DisplayNameBox.Text = edit.Item1;
        SignatureBox.Text = edit.Item2;
        PasswordBox.Password = edit.Item3;

        var isPassword = _current.AuthMethod == AuthMethod.Password;
        AuthText.Text = isPassword ? "パスワード（アプリ パスワード）" : $"{(_current.AuthMethod == AuthMethod.OAuthGoogle ? "Google" : "Microsoft")} でサインインしています";
        PasswordPanel.Visibility = isPassword ? Visibility.Visible : Visibility.Collapsed;
        ReSignInButton.Visibility = isPassword ? Visibility.Collapsed : Visibility.Visible;
        ServerText.Text = $"受信: {_current.IncomingProtocol.ToString().ToUpperInvariant()} {_current.Incoming.Host}:{_current.Incoming.Port}　送信: SMTP {_current.Outgoing.Host}:{_current.Outgoing.Port}";
    }

    private void KeepCurrentEdits()
    {
        if (_current is not null)
        {
            _edits[_current.Id] = (DisplayNameBox.Text.Trim(), SignatureBox.Text, PasswordBox.Password);
        }
    }

    private void OnDeleteAccountClick(object sender, RoutedEventArgs e)
    {
        if (_current is null ||
            MessageBox.Show(this, $"{_current.EmailAddress} を削除しますか？\nこの PC に保存したメールも削除されます（サーバーのメールは削除されません）。",
                "アカウントの削除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var account = _current;
        _current = null;
        _edits.Remove(account.Id);
        _services.Accounts.Delete(account.Id);
        _services.Credentials.Delete(account.CredentialKey);
        var directory = System.IO.Path.Combine(_services.Location.Root, "messages", account.Id.ToString(CultureInfo.InvariantCulture));
        try
        {
            if (System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
        }
        catch (System.IO.IOException ex)
        {
            Log.Error("メールのファイルを削除できませんでした", ex);
        }

        AccountsChanged = true;
        AccountList.ItemsSource = _services.Accounts.GetAll();
    }

    private async void OnReSignInClick(object sender, RoutedEventArgs e)
    {
        if (_current is null || _services.OAuth.Find(_current.AuthMethod) is not { } provider)
        {
            MessageBox.Show(this, "OAuth のクライアント ID が設定されていません。", "Hayabumi", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            ReSignInButton.IsEnabled = false;
            var result = await _services.OAuthClient.SignInAsync(provider, _current.EmailAddress,
                uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }), CancellationToken.None);
            AccountSecretProvider.SaveTokens(_services.Credentials, _current, result.Tokens);
            MessageBox.Show(this, "サインインしました。", "Hayabumi", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Hayabumi", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ReSignInButton.IsEnabled = true;
        }
    }

    private void OnOpenDataFolderClick(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_services.Location.Root}\"") { UseShellExecute = true });

    private void OnMoveDataFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "新しいデータフォルダ（空のフォルダ）を選んでください" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (MessageBox.Show(this, $"データを次の場所へコピーして、Hayabumi を再起動します。\n{dialog.FolderName}", "データフォルダの移動",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            if (!_services.SyncLock.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new InvalidOperationException("同期中のため移せませんでした。しばらくしてからもう一度お試しください。");
            }

            try
            {
                DataFolderMover.Copy(_services.Database, _services.Location, dialog.FolderName);
            }
            finally
            {
                _services.SyncLock.Release();
            }

            RestartRequired = true;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            Log.Error("データフォルダの移動に失敗しました", ex);
            MessageBox.Show(this, $"データフォルダを移せませんでした。\n{ex.Message}", "Hayabumi", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(IntervalBox.Text, out var interval) || !int.TryParse(UndoBox.Text, out var undo))
        {
            MessageBox.Show(this, "数字を入力してください。", "設定", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var settings = _services.AppSettings;
        settings.SyncIntervalMinutes = interval;
        settings.UndoSendSeconds = undo;
        settings.CloseToTray = CloseToTrayBox.IsChecked == true;
        settings.InternalDomains = [.. DomainsBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        if (StartWithWindowsBox.IsEnabled)
        {
            AppSettings.StartWithWindows = StartWithWindowsBox.IsChecked == true;
        }

        KeepCurrentEdits();
        foreach (var account in _services.Accounts.GetAll())
        {
            if (!_edits.TryGetValue(account.Id, out var edit))
            {
                continue;
            }

            _services.Accounts.Update(account with
            {
                DisplayName = string.IsNullOrWhiteSpace(edit.Name) ? account.EmailAddress : edit.Name,
                Signature = edit.Signature.TrimEnd(),
            });
            if (edit.Password.Length > 0 && account.AuthMethod == AuthMethod.Password)
            {
                _services.Credentials.Write(account.CredentialKey, account.UserName, edit.Password);
            }

            AccountsChanged = true;
        }

        DialogResult = true;
    }
}
