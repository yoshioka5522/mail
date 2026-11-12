using Hayabumi.Core.Accounts;
using Hayabumi.Core.Security;
using Hayabumi.Core.Security.OAuth;
using Hayabumi.Core.Storage;

namespace Hayabumi.App.Services;

/// <summary>
/// OAuth のクライアント ID（Google Cloud / Microsoft Entra ID に登録したアプリの ID）。
/// 設定画面で入力した値を使い、なければビルド時に埋め込んだ既定値を使う。
/// クライアント ID は秘密ではないが、Google のクライアント シークレットは念のため資格情報マネージャーに保存する。
/// </summary>
public sealed class OAuthSettings(SettingsRepository settings, ICredentialStore credentials) : IOAuthProviderSource
{
    private const string GoogleClientIdKey = "oauth.google.client_id";
    private const string MicrosoftClientIdKey = "oauth.microsoft.client_id";
    private const string GoogleSecretKey = "Hayabumi/oauth/google-client-secret";

    public string GoogleClientId
    {
        get => settings.Get(GoogleClientIdKey) ?? BuildDefaults.GoogleClientId;
        set => settings.Set(GoogleClientIdKey, string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    public string GoogleClientSecret
    {
        get => credentials.Read(GoogleSecretKey) ?? BuildDefaults.GoogleClientSecret;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                credentials.Delete(GoogleSecretKey);
            }
            else
            {
                credentials.Write(GoogleSecretKey, "google", value.Trim());
            }
        }
    }

    public string MicrosoftClientId
    {
        get => settings.Get(MicrosoftClientIdKey) ?? BuildDefaults.MicrosoftClientId;
        set => settings.Set(MicrosoftClientIdKey, string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    public OAuthProvider? Find(AuthMethod method) => method switch
    {
        AuthMethod.OAuthGoogle when GoogleClientId.Length > 0 => OAuthProvider.Google(GoogleClientId, GoogleClientSecret),
        AuthMethod.OAuthMicrosoft when MicrosoftClientId.Length > 0 => OAuthProvider.Microsoft(MicrosoftClientId),
        _ => null,
    };

    /// <summary>ビルド時に埋め込む既定値（GitHub Actions の変数から設定する。未設定なら空）。</summary>
    private static class BuildDefaults
    {
        public static string GoogleClientId => Metadata("OAuthGoogleClientId");
        public static string GoogleClientSecret => Metadata("OAuthGoogleClientSecret");
        public static string MicrosoftClientId => Metadata("OAuthMicrosoftClientId");

        private static string Metadata(string key)
            => typeof(OAuthSettings).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
                .OfType<System.Reflection.AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == key)?.Value ?? string.Empty;
    }
}
