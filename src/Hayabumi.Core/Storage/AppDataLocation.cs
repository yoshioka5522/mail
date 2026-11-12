using System.Text.Json;

namespace Hayabumi.Core.Storage;

/// <summary>
/// データフォルダの場所を管理する。
/// 既定は %LOCALAPPDATA%\HayabumiData。アプリ本体のフォルダ（%LOCALAPPDATA%\Hayabumi）とは分けて、
/// アンインストールや更新でメールが消えないようにする。
/// 保存先を変更した場合は、既定の場所にある location.json に新しい場所を記録する。
/// </summary>
public sealed class AppDataLocation
{
    public const string DefaultFolderName = "HayabumiData";
    private const string PointerFileName = "location.json";

    public AppDataLocation(string? baseDirectory = null)
    {
        DefaultRoot = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DefaultFolderName);
        Root = ReadPointer() ?? DefaultRoot;
    }

    public string DefaultRoot { get; }

    public string Root { get; private set; }

    public string DatabasePath => Path.Combine(Root, "hayabumi.db");

    public string LogDirectory => Path.Combine(DefaultRoot, "logs");

    /// <summary>
    /// データフォルダの場所を変更する（ファイルの移動は呼び出し側で、アプリの終了中に行うこと）。
    /// </summary>
    public void ChangeRoot(string newRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(newRoot);
        Directory.CreateDirectory(DefaultRoot);
        var pointer = Path.Combine(DefaultRoot, PointerFileName);
        if (string.Equals(Path.GetFullPath(newRoot), Path.GetFullPath(DefaultRoot), StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(pointer);
        }
        else
        {
            File.WriteAllText(pointer, JsonSerializer.Serialize(new Pointer(newRoot)));
        }

        Root = newRoot;
    }

    private string? ReadPointer()
    {
        var pointer = Path.Combine(DefaultRoot, PointerFileName);
        if (!File.Exists(pointer))
        {
            return null;
        }

        try
        {
            var value = JsonSerializer.Deserialize<Pointer>(File.ReadAllText(pointer));
            return string.IsNullOrWhiteSpace(value?.Root) ? null : value.Root;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record Pointer(string Root);
}
