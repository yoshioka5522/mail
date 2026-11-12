using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using Hayabumi.App.Services;

namespace Hayabumi.App.Views;

public partial class UpdateTokenWindow : Window
{
    private readonly UpdateService _updates;

    public UpdateTokenWindow(UpdateService updates)
    {
        _updates = updates;
        InitializeComponent();
        StatusText.Text = updates.HasToken ? "トークンは設定済みです（変更する場合は入力してください）。" : "トークンは未設定です。";
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (TokenBox.Password.Length > 0)
        {
            _updates.SetToken(TokenBox.Password);
        }

        DialogResult = true;
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        _updates.SetToken(null);
        DialogResult = true;
    }

    private void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
