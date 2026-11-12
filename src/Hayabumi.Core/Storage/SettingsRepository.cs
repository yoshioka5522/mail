using System.Globalization;

namespace Hayabumi.Core.Storage;

/// <summary>アプリの設定（キーと値）。秘密の値は保存しないこと（資格情報マネージャーを使う）。</summary>
public sealed class SettingsRepository(MailDatabase database)
{
    public string? Get(string key)
    {
        using var connection = database.Open();
        return connection.Scalar<string>("SELECT value FROM settings WHERE key = $k;", ("$k", key));
    }

    public string GetString(string key, string defaultValue) => Get(key) ?? defaultValue;

    public int GetInt(string key, int defaultValue)
        => int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : defaultValue;

    public bool GetBool(string key, bool defaultValue) => Get(key) is { } value ? value == "1" : defaultValue;

    public void Set(string key, string? value)
    {
        using var connection = database.Open();
        if (value is null)
        {
            connection.Execute("DELETE FROM settings WHERE key = $k;", ("$k", key));
        }
        else
        {
            connection.Execute("INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = $v;", ("$k", key), ("$v", value));
        }
    }

    public void Set(string key, int value) => Set(key, value.ToString(CultureInfo.InvariantCulture));

    public void Set(string key, bool value) => Set(key, value ? "1" : "0");
}
