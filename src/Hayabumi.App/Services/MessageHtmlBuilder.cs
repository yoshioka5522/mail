using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Hayabumi.Core.Mime;
using MimeKit;

namespace Hayabumi.App.Services;

/// <summary>
/// 本文表示用の HTML を作る。
/// スクリプトは WebView2 側で無効にし、さらに CSP で外部の画像・フォントなどの読み込みを既定で止める
/// （開封の追跡やトラッキングを防ぐため）。
/// </summary>
public static partial class MessageHtmlBuilder
{
    private const long MaxInlineImageBytes = 10 * 1024 * 1024;

    public static (string Html, bool HasRemoteContent) Build(MimeMessage message, ParsedMessage parsed, bool allowRemoteContent)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(parsed);

        string body;
        if (parsed.HtmlBody is { } html)
        {
            body = ReplaceContentIds(html, message);
        }
        else
        {
            body = "<pre class=\"plain\">" + Linkify(WebUtility.HtmlEncode(parsed.TextBody ?? string.Empty)) + "</pre>";
        }

        var hasRemote = RemoteReference().IsMatch(body);
        var remote = allowRemoteContent ? " https: http:" : string.Empty;
        var head = new StringBuilder()
            .Append("<meta charset=\"utf-8\">")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data:")
            .Append(remote).Append("; style-src 'unsafe-inline'").Append(remote).Append("; font-src data:").Append(remote).Append(";\">")
            .Append("<style>")
            .Append("html{background:#fff;}")
            .Append("body{font-family:'Yu Gothic UI','Meiryo UI','Segoe UI',sans-serif;font-size:14px;line-height:1.6;color:#1f2328;margin:16px;overflow-wrap:anywhere;}")
            .Append("pre.plain{white-space:pre-wrap;font-family:inherit;margin:0;}")
            .Append("img{max-width:100%;height:auto;}")
            .Append("blockquote{border-left:3px solid #d0d7de;margin:0 0 0 4px;padding-left:10px;color:#57606a;}")
            .Append("</style>");

        return (head + body, hasRemote);
    }

    private static string ReplaceContentIds(string html, MimeMessage message)
    {
        var images = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var part in message.BodyParts.OfType<MimePart>())
        {
            if (part.ContentId is null || !part.ContentType.MediaType.Equals("image", StringComparison.OrdinalIgnoreCase) || part.Content is null)
            {
                continue;
            }

            using var buffer = new MemoryStream();
            part.Content.DecodeTo(buffer);
            total += buffer.Length;
            if (total > MaxInlineImageBytes)
            {
                break;
            }

            images[part.ContentId.Trim('<', '>')] = $"data:{part.ContentType.MimeType};base64,{Convert.ToBase64String(buffer.GetBuffer(), 0, (int)buffer.Length)}";
        }

        if (images.Count == 0)
        {
            return html;
        }

        return ContentIdReference().Replace(html, m =>
            images.TryGetValue(WebUtility.UrlDecode(m.Groups[1].Value), out var data) ? data : m.Value);
    }

    private static string Linkify(string encodedText)
        => Url().Replace(encodedText, m => $"<a href=\"{m.Value}\">{m.Value}</a>");

    [GeneratedRegex(@"cid:([^""'\s)>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ContentIdReference();

    [GeneratedRegex(@"(src|background|srcset)\s*=\s*[""']?\s*(https?:)?//|url\(\s*[""']?\s*(https?:)?//", RegexOptions.IgnoreCase)]
    private static partial Regex RemoteReference();

    [GeneratedRegex(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase)]
    private static partial Regex Url();
}
