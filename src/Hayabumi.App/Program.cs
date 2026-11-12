using System.Windows;
using Velopack;

namespace Hayabumi.App;

public static class Program
{
    /// <summary>2 つ目の起動から、既に動いている Hayabumi に画面の表示を頼むための合図。</summary>
    public const string ShowSignalName = @"Local\Hayabumi.Show";

    [STAThread]
    public static void Main(string[] args)
    {
        // インストール・更新・アンインストール時の処理。必ず最初に呼ぶ
        VelopackApp.Build().Run();

        // 日本語の文字コードを、MimeKit が使われるより前に登録する
        Hayabumi.Core.Mime.JapaneseEncodings.EnsureRegistered();

        // 同じデータベースを 2 つのプロセスで同期しないよう、二重起動を防ぐ
        using var mutex = new Mutex(initiallyOwned: true, @"Local\Hayabumi.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            // トレイに常駐している場合もあるため、既存の画面を前面に出してもらう
            if (EventWaitHandle.TryOpenExisting(ShowSignalName, out var signal))
            {
                using (signal)
                {
                    signal.Set();
                }
            }
            else
            {
                MessageBox.Show("Hayabumi は既に起動しています。", "Hayabumi", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            return;
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();
        GC.KeepAlive(mutex);
    }
}
