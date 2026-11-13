using System.IO;
using Hayabumi.Core.Storage;
using Microsoft.Win32;

namespace Hayabumi.App.Services;

/// <summary>画面の設定。値はデータベースの settings 表に保存する（Windows の自動起動だけはレジストリ）。</summary>
public sealed class AppSettings(SettingsRepository settings)
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Hayabumi";

    /// <summary>定期的な受信の間隔（分）。IDLE に対応していないサーバー向け。</summary>
    public int SyncIntervalMinutes
    {
        get => Math.Clamp(settings.GetInt("sync.interval_minutes", 5), 1, 120);
        set => settings.Set("sync.interval_minutes", Math.Clamp(value, 1, 120));
    }

    /// <summary>送信を取り消せる秒数（0 なら取り消しなし）。</summary>
    public int UndoSendSeconds
    {
        get => Math.Clamp(settings.GetInt("send.undo_seconds", 5), 0, 60);
        set => settings.Set("send.undo_seconds", Math.Clamp(value, 0, 60));
    }

    /// <summary>ウィンドウを閉じたときにタスクトレイに常駐する。</summary>
    public bool CloseToTray
    {
        get => settings.GetBool("app.close_to_tray", true);
        set => settings.Set("app.close_to_tray", value);
    }

    /// <summary>メール本文の外部の画像を、確認せずに常に表示する（既定はプライバシー保護のため表示しない）。</summary>
    public bool AlwaysShowRemoteImages
    {
        get => settings.GetBool("display.always_show_remote_images", false);
        set => settings.Set("display.always_show_remote_images", value);
    }

    /// <summary>社内のドメイン（社外宛ての警告から除く）。改行またはカンマ区切り。</summary>
    public IReadOnlyList<string> InternalDomains
    {
        get => [.. settings.GetString("send.internal_domains", string.Empty)
            .Split(['\n', ',', ' ', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.TrimStart('@').ToLowerInvariant())];
        set => settings.Set("send.internal_domains", string.Join('\n', value));
    }

    /// <summary>Windows へのサインイン時に、タスクトレイで起動する。</summary>
    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is string;
        }

        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (value && Environment.ProcessPath is { } path)
            {
                // Velopack は更新しても current フォルダの exe のパスが変わらないため、そのまま登録できる
                key.SetValue(RunValueName, $"\"{path}\" --minimized");
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
    }

    public static bool IsInstalledCopy => Environment.ProcessPath is { } p && p.Contains(Path.DirectorySeparatorChar + "current" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
