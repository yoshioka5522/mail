using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace Hayabumi.App.Services;

/// <summary>
/// ウィンドウが画面（タスクバーを除いた作業領域）からはみ出さないようにする。
/// フル HD で表示スケールが 150% などの場合、作業領域の高さは 700 前後しかないため、
/// 大きめのウィンドウを中央に表示するとタイトルバーが画面の外に出て、移動も閉じることもできなくなる。
/// </summary>
public static class WindowFit
{
    private const double Margin = 8;

    /// <summary>アプリのすべてのウィンドウに適用する（起動時に 1 回呼ぶ）。</summary>
    public static void RegisterForAllWindows()
        => EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is Window window)
            {
                Fit(window);
            }
        }));

    public static void Fit(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.WindowState != WindowState.Normal)
        {
            return;
        }

        var area = WorkAreaFor(window);
        var maxWidth = Math.Max(200, area.Width - (Margin * 2));
        var maxHeight = Math.Max(200, area.Height - (Margin * 2));

        // 大きさを作業領域に収める（内容に合わせて大きさが決まる画面は、上限を設ける）
        window.MaxWidth = Math.Min(window.MaxWidth, maxWidth);
        window.MaxHeight = Math.Min(window.MaxHeight, maxHeight);
        if (window.ActualWidth > maxWidth || double.IsNaN(window.Width) is false && window.Width > maxWidth)
        {
            window.Width = maxWidth;
        }

        if (window.ActualHeight > maxHeight || double.IsNaN(window.Height) is false && window.Height > maxHeight)
        {
            window.Height = maxHeight;
        }

        window.UpdateLayout();
        var width = Math.Min(window.ActualWidth, maxWidth);
        var height = Math.Min(window.ActualHeight, maxHeight);

        // 中央に表示する画面は、作業領域の中央に置き直す。そうでなくても、はみ出した分は内側に戻す
        double left = window.Left;
        double top = window.Top;
        if (window.WindowStartupLocation is WindowStartupLocation.CenterScreen
            || double.IsNaN(left) || double.IsNaN(top))
        {
            left = area.Left + ((area.Width - width) / 2);
            top = area.Top + ((area.Height - height) / 2);
        }

        left = Math.Clamp(left, area.Left + Margin, Math.Max(area.Left + Margin, area.Right - width - Margin));
        top = Math.Clamp(top, area.Top + Margin, Math.Max(area.Top + Margin, area.Bottom - height - Margin));
        window.Left = left;
        window.Top = top;
    }

    /// <summary>ウィンドウを表示するモニターの作業領域（WPF の座標）。</summary>
    private static Rect WorkAreaFor(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var owner = window.Owner ?? Application.Current?.MainWindow;
        var screen = handle != IntPtr.Zero
            ? Forms.Screen.FromHandle(handle)
            : owner is not null && new WindowInteropHelper(owner).Handle is var ownerHandle && ownerHandle != IntPtr.Zero
                ? Forms.Screen.FromHandle(ownerHandle)
                : Forms.Screen.PrimaryScreen;
        if (screen is null)
        {
            return SystemParameters.WorkArea;
        }

        var dpi = VisualTreeHelper.GetDpi(window);
        var working = screen.WorkingArea;
        return new Rect(
            working.Left / dpi.DpiScaleX,
            working.Top / dpi.DpiScaleY,
            working.Width / dpi.DpiScaleX,
            working.Height / dpi.DpiScaleY);
    }
}
