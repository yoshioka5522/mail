using System.Windows;
using Forms = System.Windows.Forms;

namespace Hayabumi.App.Services;

/// <summary>
/// タスクトレイのアイコン。ウィンドウを閉じても常駐し、新着を通知する
/// （Windows 10/11 ではバルーン通知がトースト通知として表示される）。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    public TrayIcon()
    {
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Hayabumi.ico")).Stream;
        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize),
            Text = "Hayabumi",
            Visible = true,
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("開く", null, (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("受信", null, (_, _) => SyncRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("新規作成", null, (_, _) => ComposeRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));
        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                OpenRequested?.Invoke(this, EventArgs.Empty);
            }
        };
        _icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? OpenRequested;
    public event EventHandler? SyncRequested;
    public event EventHandler? ComposeRequested;
    public event EventHandler? ExitRequested;

    public void SetUnreadCount(int count)
    {
        var text = count > 0 ? $"Hayabumi（未読 {count:N0} 件）" : "Hayabumi";
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    public void Notify(string title, string text)
        => _icon.ShowBalloonTip(5000, title, text.Length > 250 ? text[..250] : text, Forms.ToolTipIcon.None);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
