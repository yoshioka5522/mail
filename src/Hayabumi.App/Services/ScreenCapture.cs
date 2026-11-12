using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Hayabumi.App.Services;

/// <summary>画面の確認用に、画面全体を PNG に保存する（--screenshot で起動したとき）。</summary>
public static class ScreenCapture
{
    public static void Save(string path)
    {
        var bounds = Forms.Screen.PrimaryScreen?.Bounds ?? new Drawing.Rectangle(0, 0, 1280, 800);
        using var bitmap = new Drawing.Bitmap(bounds.Width, bounds.Height);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
        }

        bitmap.Save(path, Drawing.Imaging.ImageFormat.Png);
    }
}
