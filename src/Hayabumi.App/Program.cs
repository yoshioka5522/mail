using System.Windows;
using Velopack;

namespace Hayabumi.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // インストール・更新・アンインストール時の処理。必ず最初に呼ぶ
        VelopackApp.Build().Run();

        // 同じデータベースを 2 つのプロセスで同期しないよう、二重起動を防ぐ
        using var mutex = new Mutex(initiallyOwned: true, @"Local\Hayabumi.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Hayabumi は既に起動しています。", "Hayabumi", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();
        GC.KeepAlive(mutex);
    }
}
