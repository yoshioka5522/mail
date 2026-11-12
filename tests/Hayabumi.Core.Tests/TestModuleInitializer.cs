using System.Runtime.CompilerServices;

namespace Hayabumi.Core.Tests;

internal static class TestModuleInitializer
{
    /// <summary>テストが MimeKit を直接使う場合にも、先に文字コードを登録しておく（アプリの起動時と同じ状態にする）。</summary>
#pragma warning disable CA2255 // 文字コードの登録は、どの型よりも先に行う必要があるため
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize() => Hayabumi.Core.Mime.JapaneseEncodings.EnsureRegistered();
}
