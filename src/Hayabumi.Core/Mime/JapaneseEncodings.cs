using System.Text;

namespace Hayabumi.Core.Mime;

/// <summary>
/// 日本語メールで使われる文字コードを扱う。
/// Shift_JIS は Windows の拡張文字（①、㈱、髙 など）を含む CP932 として、
/// ISO-2022-JP は NEC 特殊文字なども復号できる CP50222 として扱う。
/// </summary>
public static class JapaneseEncodings
{
    private static int s_registered;

    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref s_registered, 1) == 0)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
    }

    public static Encoding Cp932
    {
        get
        {
            EnsureRegistered();
            return Encoding.GetEncoding(932);
        }
    }

    public static Encoding Iso2022Jp
    {
        get
        {
            EnsureRegistered();
            return Encoding.GetEncoding(50222);
        }
    }

    public static Encoding EucJp
    {
        get
        {
            EnsureRegistered();
            return Encoding.GetEncoding(51932);
        }
    }

    public static Encoding StrictUtf8 { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// charset パラメーターの値から復号に使うエンコーディングを決める。不明な場合は null。
    /// </summary>
    public static Encoding? FromCharsetName(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
        {
            return null;
        }

        var name = charset.Trim().Trim('"').ToLowerInvariant();
        switch (name)
        {
            case "iso-2022-jp":
            case "iso-2022-jp-1":
            case "iso-2022-jp-2":
            case "iso-2022-jp-3":
            case "iso-2022-jp-2004":
            case "csiso2022jp":
            case "jis":
                return Iso2022Jp;
            case "shift_jis":
            case "shift-jis":
            case "sjis":
            case "x-sjis":
            case "ms_kanji":
            case "csshiftjis":
            case "windows-31j":
            case "cp932":
            case "ms932":
            case "x-ms-cp932":
            case "shift_jisx0213":
                return Cp932;
            case "euc-jp":
            case "x-euc-jp":
            case "eucjp":
            case "cseucpkdfmtjapanese":
                return EucJp;
            case "utf8":
            case "utf-8":
                return Encoding.UTF8;
            case "us-ascii":
            case "ascii":
                // ASCII と宣言されていても 8bit が混ざることが多いため、検出に任せる
                return null;
        }

        EnsureRegistered();
        try
        {
            return Encoding.GetEncoding(name);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
