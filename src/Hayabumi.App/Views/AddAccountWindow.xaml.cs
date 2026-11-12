using System.Globalization;
using System.Windows;
using Hayabumi.App.Services;
using Hayabumi.Core.Accounts;
using MailKit.Security;

namespace Hayabumi.App.Views;

public partial class AddAccountWindow : Window
{
    private readonly AppServices _services;
    private CancellationTokenSource? _connecting;

    public AddAccountWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        Loaded += (_, _) => EmailBox.Focus();
        Closed += (_, _) => _connecting?.Cancel();
    }

    private bool IsPop3 => Pop3Radio.IsChecked == true;

    private void OnProtocolChanged(object sender, RoutedEventArgs e)
    {
        if (Pop3Note is null)
        {
            return;
        }

        Pop3Note.Visibility = IsPop3 ? Visibility.Visible : Visibility.Collapsed;
        ApplyIncomingPreset(ServerPresets.Find(EmailBox.Text.Trim()));
    }

    private void ApplyIncomingPreset(ServerPreset? preset)
    {
        var endpoint = IsPop3
            ? preset?.Pop3 ?? new ServerEndpoint(ImapHostBox.Text, 995, ConnectionSecurity.SslOnConnect)
            : preset?.Incoming ?? new ServerEndpoint(ImapHostBox.Text, 993, ConnectionSecurity.SslOnConnect);
        ImapHostBox.Text = endpoint.Host;
        ImapPortBox.Text = endpoint.Port.ToString(CultureInfo.InvariantCulture);
        ImapSecurityBox.SelectedIndex = ToIndex(endpoint.Security);
    }

    private void OnEmailLostFocus(object sender, RoutedEventArgs e)
    {
        var email = EmailBox.Text.Trim();
        if (ServerPresets.Find(email) is not { } preset)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(UserBox.Text))
        {
            UserBox.Text = email;
        }

        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            NameBox.Text = email;
        }

        ApplyIncomingPreset(preset);
        SmtpHostBox.Text = preset.Outgoing.Host;
        SmtpPortBox.Text = preset.Outgoing.Port.ToString(CultureInfo.InvariantCulture);
        SmtpSecurityBox.SelectedIndex = ToIndex(preset.Outgoing.Security);

        PresetNote.Text = preset.Note ?? string.Empty;
        PresetNote.Visibility = string.IsNullOrEmpty(preset.Note) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnOkClick(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        var email = EmailBox.Text.Trim();
        if (!email.Contains('@', StringComparison.Ordinal))
        {
            ShowError("メールアドレスを入力してください。");
            return;
        }

        if (!int.TryParse(ImapPortBox.Text, out var imapPort) || !int.TryParse(SmtpPortBox.Text, out var smtpPort))
        {
            ShowError("ポートには数字を入力してください。");
            return;
        }

        if (string.IsNullOrWhiteSpace(ImapHostBox.Text) || string.IsNullOrWhiteSpace(SmtpHostBox.Text))
        {
            ShowError("サーバー名を入力してください。");
            return;
        }

        if (PasswordBox.Password.Length == 0)
        {
            ShowError("パスワードを入力してください。");
            return;
        }

        var account = new Account
        {
            DisplayName = string.IsNullOrWhiteSpace(NameBox.Text) ? email : NameBox.Text.Trim(),
            EmailAddress = email,
            IncomingProtocol = IsPop3 ? IncomingProtocol.Pop3 : IncomingProtocol.Imap,
            Incoming = new ServerEndpoint(ImapHostBox.Text.Trim(), imapPort, ToSecurity(ImapSecurityBox.SelectedIndex)),
            Outgoing = new ServerEndpoint(SmtpHostBox.Text.Trim(), smtpPort, ToSecurity(SmtpSecurityBox.SelectedIndex)),
            UserName = string.IsNullOrWhiteSpace(UserBox.Text) ? email : UserBox.Text.Trim(),
            AuthMethod = AuthMethod.Password,
        };

        if ((account.Incoming.Security == ConnectionSecurity.None || account.Outgoing.Security == ConnectionSecurity.None) &&
            MessageBox.Show(this, "暗号化しない接続では、パスワードやメールの内容が盗み見られるおそれがあります。続けますか？", "確認",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var password = PasswordBox.Password;
        SetBusy(true);
        _connecting = new CancellationTokenSource();
        try
        {
            await Task.Run(() => AppServices.TestConnectionAsync(account, password, _connecting.Token));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (AuthenticationException)
        {
            SetBusy(false);
            ShowError("ユーザー名またはパスワードが正しくありません。Gmail や iCloud では「アプリ パスワード」を使ってください。");
            return;
        }
        catch (Exception ex)
        {
            Log.Error($"接続の確認に失敗しました: {account.Incoming.Host}", ex);
            SetBusy(false);
            ShowError($"サーバーに接続できませんでした。設定を確認してください。\n（{ex.Message}）");
            return;
        }

        var saved = _services.Accounts.Add(account);
        _services.Credentials.Write(saved.CredentialKey, saved.UserName, password);
        if (saved.IncomingProtocol == IncomingProtocol.Pop3)
        {
            Hayabumi.Core.Sync.Pop3.Pop3Synchronizer.EnsureFolders(_services.Folders, saved.Id);
        }
        Log.Info($"アカウントを追加しました: {saved.EmailAddress}");
        DialogResult = true;
    }

    private void SetBusy(bool busy)
    {
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        OkButton.IsEnabled = !busy;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private static int ToIndex(ConnectionSecurity security) => security switch
    {
        ConnectionSecurity.SslOnConnect => 0,
        ConnectionSecurity.StartTls => 1,
        _ => 2,
    };

    private static ConnectionSecurity ToSecurity(int index) => index switch
    {
        0 => ConnectionSecurity.SslOnConnect,
        1 => ConnectionSecurity.StartTls,
        _ => ConnectionSecurity.None,
    };
}
