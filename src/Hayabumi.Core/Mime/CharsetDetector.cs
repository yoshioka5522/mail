using System.Text;

namespace Hayabumi.Core.Mime;

/// <summary>
/// charset が宣言されていない、または誤っている本文の文字コードを推定する。
/// </summary>
public static class CharsetDetector
{
    public static Encoding Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8;
        }

        var hasHighBit = false;
        foreach (var b in bytes)
        {
            if (b >= 0x80)
            {
                hasHighBit = true;
                break;
            }
        }

        if (!hasHighBit)
        {
            return ContainsIso2022JpEscape(bytes) ? JapaneseEncodings.Iso2022Jp : Encoding.UTF8;
        }

        if (IsValidUtf8(bytes))
        {
            return Encoding.UTF8;
        }

        return ScoreEucJp(bytes) > ScoreShiftJis(bytes) ? JapaneseEncodings.EucJp : JapaneseEncodings.Cp932;
    }

    public static bool ContainsIso2022JpEscape(ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i + 2 < bytes.Length; i++)
        {
            if (bytes[i] != 0x1B)
            {
                continue;
            }

            var a = bytes[i + 1];
            var b = bytes[i + 2];
            // ESC $ @, ESC $ B, ESC ( J, ESC ( I
            if ((a == (byte)'$' && (b == (byte)'@' || b == (byte)'B')) ||
                (a == (byte)'(' && (b == (byte)'J' || b == (byte)'I')))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsValidUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            JapaneseEncodings.StrictUtf8.GetCharCount(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static int ScoreShiftJis(ReadOnlySpan<byte> bytes)
    {
        var score = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            if (b < 0x80)
            {
                continue;
            }

            if (b >= 0xA1 && b <= 0xDF)
            {
                // 半角カナ。単独で現れることは少ないため加点は小さくする
                score += 1;
                continue;
            }

            if (((b >= 0x81 && b <= 0x9F) || (b >= 0xE0 && b <= 0xFC)) && i + 1 < bytes.Length)
            {
                var t = bytes[i + 1];
                if (t >= 0x40 && t <= 0xFC && t != 0x7F)
                {
                    // ひらがな（0x82 0x9F-0xF1）・カタカナ（0x83 0x40-0x96）はよく現れるため強く加点する
                    score += (b == 0x82 && t >= 0x9F) || b == 0x83 ? 3 : 2;
                    i++;
                    continue;
                }
            }

            score -= 5;
        }

        return score;
    }

    private static int ScoreEucJp(ReadOnlySpan<byte> bytes)
    {
        var score = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            if (b < 0x80)
            {
                continue;
            }

            if (b == 0x8E && i + 1 < bytes.Length && bytes[i + 1] >= 0xA1 && bytes[i + 1] <= 0xDF)
            {
                score += 1;
                i++;
                continue;
            }

            if (b >= 0xA1 && b <= 0xFE && i + 1 < bytes.Length && bytes[i + 1] >= 0xA1 && bytes[i + 1] <= 0xFE)
            {
                // ひらがな（0xA4）・カタカナ（0xA5）はよく現れるため強く加点する
                score += b == 0xA4 || b == 0xA5 ? 3 : 2;
                i++;
                continue;
            }

            score -= 5;
        }

        return score;
    }
}
