using System.Globalization;
using System.IO;
using System.Text;

namespace Hayabumi.App.Services;

/// <summary>
/// エラーログ。外部には送信せず、データフォルダの logs にだけ保存する（7 日分）。
/// </summary>
public static class Log
{
    private static readonly Lock s_lock = new();
    private static string? s_directory;

    public static void Initialize(string directory)
    {
        s_directory = directory;
        try
        {
            Directory.CreateDirectory(directory);
            foreach (var file in Directory.EnumerateFiles(directory, "*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-7))
                {
                    File.Delete(file);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Error(string message, Exception? exception) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        if (s_directory is null)
        {
            return;
        }

        var now = DateTime.Now;
        var line = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(' ')
            .Append(level).Append(' ').Append(message);
        if (exception is not null)
        {
            line.AppendLine().Append(exception);
        }

        line.AppendLine();
        try
        {
            lock (s_lock)
            {
                File.AppendAllText(Path.Combine(s_directory, $"hayabumi-{now:yyyyMMdd}.log"), line.ToString());
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
