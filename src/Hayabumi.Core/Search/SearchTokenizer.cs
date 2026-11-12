using System.Text;

namespace Hayabumi.Core.Search;

/// <summary>
/// 全文検索用の分かち書き。
/// 日本語などの CJK 文字は 2 文字ずつ（バイグラム）に分割し、それ以外は英数字の単語に分割する。
/// インデックス作成時と検索時に同じ処理を使うことで、日本語の部分一致検索を高速に行う。
/// </summary>
public static class SearchTokenizer
{
    /// <summary>
    /// インデックス用のトークン列（空白区切り）を作る。
    /// CJK の連続部分「会議室」は「会議 議室 室」となる（末尾の 1 文字は 1 文字検索用）。
    /// </summary>
    public static string ToIndexText(string? text, int maxLength = 64 * 1024)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (text.Length > maxLength)
        {
            text = text[..maxLength];
        }

        var sb = new StringBuilder(text.Length * 2);
        foreach (var token in Tokenize(text))
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(token);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 検索語を FTS5 の MATCH 式に変換する。空白区切りの語はすべて含む（AND）条件になる。
    /// 検索できる語がなければ null を返す。
    /// </summary>
    public static string? ToMatchExpression(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var phrases = new List<string>();
        foreach (var term in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = TokenizeQuery(term);
            if (tokens.Count == 0)
            {
                continue;
            }

            // 最後のトークンは前方一致にする（入力途中の語や、1 文字の CJK 検索に対応するため）
            phrases.Add("\"" + string.Join(' ', tokens) + "\" *");
        }

        return phrases.Count == 0 ? null : string.Join(" AND ", phrases);
    }

    internal static List<string> TokenizeQuery(string term)
    {
        var result = new List<string>();
        var segments = Segment(Normalize(term)).ToList();
        for (var s = 0; s < segments.Count; s++)
        {
            var segment = segments[s];
            if (!segment.IsCjk)
            {
                result.Add(segment.Text);
                continue;
            }

            var runes = segment.Runes;
            if (runes.Count == 1)
            {
                result.Add(runes[0].ToString());
                continue;
            }

            for (var i = 0; i + 1 < runes.Count; i++)
            {
                result.Add(string.Concat(runes[i].ToString(), runes[i + 1].ToString()));
            }

            // 末尾の 1 文字トークンは、後ろに英数字などが続く場合だけ付ける（インデックスと位置をそろえるため）。
            // 語の最後では付けない。文中の「会議室」に「会議」で一致させるため。
            if (s < segments.Count - 1)
            {
                result.Add(runes[^1].ToString());
            }
        }

        return result;
    }

    internal static IEnumerable<string> Tokenize(string text)
    {
        foreach (var segment in Segment(Normalize(text)))
        {
            if (!segment.IsCjk)
            {
                yield return segment.Text;
                continue;
            }

            var runes = segment.Runes;
            for (var i = 0; i + 1 < runes.Count; i++)
            {
                yield return string.Concat(runes[i].ToString(), runes[i + 1].ToString());
            }

            yield return runes[^1].ToString();
        }
    }

    /// <summary>全角英数を半角に、半角カナを全角にそろえ、小文字にする。</summary>
    internal static string Normalize(string text)
        => text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();

    private readonly record struct TextSegment(bool IsCjk, string Text, List<Rune> Runes);

    private static IEnumerable<TextSegment> Segment(string text)
    {
        var word = new StringBuilder();
        var cjk = new List<Rune>();

        foreach (var rune in text.EnumerateRunes())
        {
            if (IsCjk(rune))
            {
                if (word.Length > 0)
                {
                    yield return new TextSegment(false, word.ToString(), []);
                    word.Clear();
                }

                cjk.Add(rune);
            }
            else
            {
                if (cjk.Count > 0)
                {
                    yield return new TextSegment(true, string.Empty, [.. cjk]);
                    cjk.Clear();
                }

                if (Rune.IsLetterOrDigit(rune))
                {
                    word.Append(rune.ToString());
                }
                else if (word.Length > 0)
                {
                    yield return new TextSegment(false, word.ToString(), []);
                    word.Clear();
                }
            }
        }

        if (word.Length > 0)
        {
            yield return new TextSegment(false, word.ToString(), []);
        }

        if (cjk.Count > 0)
        {
            yield return new TextSegment(true, string.Empty, [.. cjk]);
        }
    }

    private static bool IsCjk(Rune rune)
    {
        var v = rune.Value;
        return v is (>= 0x3040 and <= 0x30FF)   // ひらがな・カタカナ（長音記号を含む）
            or (>= 0x31F0 and <= 0x31FF)       // カタカナ拡張
            or 0x3005 or 0x3006 or 0x3007      // 々 〆 〇
            or (>= 0x3400 and <= 0x4DBF)       // CJK 統合漢字拡張 A
            or (>= 0x4E00 and <= 0x9FFF)       // CJK 統合漢字
            or (>= 0xF900 and <= 0xFAFF)       // CJK 互換漢字
            or (>= 0xAC00 and <= 0xD7AF)       // ハングル
            or (>= 0x20000 and <= 0x3FFFF);    // CJK 統合漢字拡張 B 以降
    }
}
