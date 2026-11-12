using System.Text;

namespace Hayabumi.Core.Tests;

/// <summary>テスト用の生メールをバイト列で組み立てる（文字コードを厳密に制御するため）。</summary>
internal sealed class MailBuilder
{
    private readonly List<byte> _bytes = [];

    public MailBuilder Header(string line) => Ascii(line + "\r\n");

    public MailBuilder Ascii(string text)
    {
        _bytes.AddRange(Encoding.ASCII.GetBytes(text));
        return this;
    }

    public MailBuilder Raw(params byte[] bytes)
    {
        _bytes.AddRange(bytes);
        return this;
    }

    public MailBuilder EndHeaders() => Ascii("\r\n");

    public MemoryStream ToStream() => new([.. _bytes]);

    /// <summary>ISO-2022-JP のエスケープシーケンス。</summary>
    public static readonly byte[] EscKanji = [0x1B, (byte)'$', (byte)'B'];

    public static readonly byte[] EscAscii = [0x1B, (byte)'(', (byte)'B'];

    /// <summary>「あ①」を ISO-2022-JP（NEC 特殊文字を含む）で表したもの。</summary>
    public static byte[] Iso2022JpA1() => [.. EscKanji, 0x24, 0x22, 0x2D, 0x21, .. EscAscii];
}
