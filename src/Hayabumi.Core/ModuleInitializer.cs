using System.Runtime.CompilerServices;
using Hayabumi.Core.Mime;

namespace Hayabumi.Core;

internal static class ModuleInitializer
{
    /// <summary>
    /// Shift_JIS や ISO-2022-JP などの文字コードを、MimeKit が初めて使われるより前に登録する。
    /// MimeKit は最初に使われた時点で使える文字コードを調べるため、登録が遅れると ISO-2022-JP の件名などが化ける。
    /// </summary>
#pragma warning disable CA2255 // 文字コードの登録は、どの型よりも先に行う必要があるため
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize() => JapaneseEncodings.EnsureRegistered();
}
