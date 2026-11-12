using System.Windows;
using Hayabumi.App.Services;

namespace Hayabumi.App.Views;

public partial class OAuthSettingsWindow : Window
{
    private readonly OAuthSettings _settings;

    public OAuthSettingsWindow(OAuthSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        GoogleIdBox.Text = settings.GoogleClientId;
        GoogleSecretBox.Password = settings.GoogleClientSecret;
        MicrosoftIdBox.Text = settings.MicrosoftClientId;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        _settings.GoogleClientId = GoogleIdBox.Text;
        _settings.GoogleClientSecret = GoogleSecretBox.Password;
        _settings.MicrosoftClientId = MicrosoftIdBox.Text;
        DialogResult = true;
    }
}
