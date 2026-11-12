using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Hayabumi.Core.Mime;

/// <summary>
/// HTML メールからプレビュー・検索用のプレーンテキストを取り出す（表示用ではない）。
/// </summary>
public static partial class HtmlText
{
    public static string ToPlainText(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        var text = InvisibleBlocks().Replace(html, " ");
        text = Comments().Replace(text, " ");
        text = BlockBreaks().Replace(text, "\n");
        text = Tags().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        return NormalizeLines(text);
    }

    /// <summary>行ごとに前後の空白を除き、連続する空行を1行にまとめる。</summary>
    public static string NormalizeLines(string text)
    {
        var sb = new StringBuilder(text.Length);
        var blank = false;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = HorizontalSpace().Replace(rawLine, " ").Trim();
            if (line.Length == 0)
            {
                blank = sb.Length > 0;
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append(blank ? "\n\n" : "\n");
            }

            sb.Append(line);
            blank = false;
        }

        return sb.ToString();
    }

    /// <summary>一覧に表示する1行の要約を作る。</summary>
    public static string MakePreview(string text, int maxLength = 200)
    {
        var oneLine = AnySpace().Replace(text, " ").Trim();
        return oneLine.Length <= maxLength ? oneLine : oneLine[..maxLength];
    }

    [GeneratedRegex(@"<(script|style|head|title)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex InvisibleBlocks();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"<\s*(br|/p|/div|/tr|/li|/h[1-6]|/blockquote|/table)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreaks();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.Singleline)]
    private static partial Regex Tags();

    [GeneratedRegex(@"[ \t\r\f\v 　]+")]
    private static partial Regex HorizontalSpace();

    [GeneratedRegex(@"\s+")]
    private static partial Regex AnySpace();
}
