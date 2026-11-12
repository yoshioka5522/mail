using System.Text;
using Hayabumi.Core.Mime;

namespace Hayabumi.Core.Tests;

public class CharsetTests
{
    private static ParsedMessage Parse(MailBuilder builder) => MessageParser.Parse(builder.ToStream());

    [Fact]
    public void Iso2022Jp_body_with_NEC_special_characters_is_decoded()
    {
        var mail = new MailBuilder()
            .Header("Subject: test")
            .Header("Content-Type: text/plain; charset=ISO-2022-JP")
            .Header("Content-Transfer-Encoding: 7bit")
            .EndHeaders()
            .Raw(MailBuilder.Iso2022JpA1());

        Assert.Equal("あ①", Parse(mail).TextBody);
    }

    [Fact]
    public void Iso2022Jp_round_trip_of_windows_extended_characters()
    {
        JapaneseEncodings.EnsureRegistered();
        const string text = "株式会社①㈱髙橋様　ｶﾀｶﾅ";
        var body = Encoding.GetEncoding(50220).GetBytes(text);
        var mail = new MailBuilder()
            .Header("Content-Type: text/plain; charset=iso-2022-jp")
            .EndHeaders()
            .Raw(body);

        // 半角カナは ISO-2022-JP（50220）では全角に変換されて送られる
        Assert.Equal("株式会社①㈱髙橋様　カタカナ", Parse(mail).TextBody);
    }

    [Fact]
    public void Shift_JIS_declared_body_is_decoded_as_CP932()
    {
        // あ ① ㈱ 髙（IBM 拡張）
        var mail = new MailBuilder()
            .Header("Content-Type: text/plain; charset=Shift_JIS")
            .Header("Content-Transfer-Encoding: 8bit")
            .EndHeaders()
            .Raw(0x82, 0xA0, 0x87, 0x40, 0x87, 0x8A, 0xFB, 0xFC);

        Assert.Equal("あ①㈱髙", Parse(mail).TextBody);
    }

    [Fact]
    public void Body_without_charset_is_detected_as_Shift_JIS()
    {
        JapaneseEncodings.EnsureRegistered();
        const string text = "お世話になっております。請求書を送付いたします。";
        var mail = new MailBuilder()
            .Header("Content-Type: text/plain")
            .EndHeaders()
            .Raw(Encoding.GetEncoding(932).GetBytes(text));

        Assert.Equal(text, Parse(mail).TextBody);
    }

    [Fact]
    public void Body_without_charset_is_detected_as_EUC_JP()
    {
        JapaneseEncodings.EnsureRegistered();
        const string text = "お世話になっております。会議の資料をお送りします。";
        var mail = new MailBuilder()
            .Header("Content-Type: text/plain")
            .EndHeaders()
            .Raw(Encoding.GetEncoding(51932).GetBytes(text));

        Assert.Equal(text, Parse(mail).TextBody);
    }

    [Fact]
    public void Body_without_charset_is_detected_as_UTF8()
    {
        const string text = "こんにちは、世界 🌏";
        var mail = new MailBuilder()
            .Header("Content-Type: text/plain")
            .EndHeaders()
            .Raw(Encoding.UTF8.GetBytes(text));

        Assert.Equal(text, Parse(mail).TextBody);
    }

    [Fact]
    public void Body_without_charset_is_detected_as_ISO_2022_JP()
    {
        var mail = new MailBuilder()
            .Header("Content-Type: text/plain")
            .EndHeaders()
            .Raw(MailBuilder.Iso2022JpA1());

        Assert.Equal("あ①", Parse(mail).TextBody);
    }

    [Fact]
    public void Body_declared_ISO_2022_JP_but_actually_Shift_JIS_is_detected()
    {
        JapaneseEncodings.EnsureRegistered();
        const string text = "ご確認よろしくお願いいたします。";
        var mail = new MailBuilder()
            .Header("Content-Type: text/plain; charset=ISO-2022-JP")
            .Header("Content-Transfer-Encoding: 8bit")
            .EndHeaders()
            .Raw(Encoding.GetEncoding(932).GetBytes(text));

        Assert.Equal(text, Parse(mail).TextBody);
    }

    [Fact]
    public void Body_declared_Shift_JIS_but_actually_UTF8_is_detected()
    {
        const string text = "ご確認よろしくお願いいたします。";
        var mail = new MailBuilder()
            .Header("Content-Type: text/plain; charset=Shift_JIS")
            .Header("Content-Transfer-Encoding: 8bit")
            .EndHeaders()
            .Raw(Encoding.UTF8.GetBytes(text));

        Assert.Equal(text, Parse(mail).TextBody);
    }

    [Fact]
    public void Base64_encoded_body_is_decoded()
    {
        const string text = "本文はBase64で送られています。";
        var mail = new MailBuilder()
            .Header("Content-Type: text/plain; charset=UTF-8")
            .Header("Content-Transfer-Encoding: base64")
            .EndHeaders()
            .Ascii(Convert.ToBase64String(Encoding.UTF8.GetBytes(text)));

        Assert.Equal(text, Parse(mail).TextBody);
    }

    [Fact]
    public void Encoded_word_subject_in_ISO_2022_JP_is_decoded()
    {
        var encoded = Convert.ToBase64String(MailBuilder.Iso2022JpA1());
        var mail = new MailBuilder()
            .Header($"Subject: =?ISO-2022-JP?B?{encoded}?=")
            .Header("Content-Type: text/plain")
            .EndHeaders()
            .Ascii("body");

        Assert.Equal("あ①", Parse(mail).Subject);
    }

    [Fact]
    public void Raw_8bit_Shift_JIS_subject_is_decoded()
    {
        JapaneseEncodings.EnsureRegistered();
        var mail = new MailBuilder()
            .Ascii("Subject: ")
            .Raw(Encoding.GetEncoding(932).GetBytes("見積書の送付"))
            .Ascii("\r\n")
            .Header("Content-Type: text/plain")
            .EndHeaders()
            .Ascii("body");

        Assert.Equal("見積書の送付", Parse(mail).Subject);
    }

    [Fact]
    public void Raw_8bit_UTF8_subject_is_decoded()
    {
        var mail = new MailBuilder()
            .Ascii("Subject: ")
            .Raw(Encoding.UTF8.GetBytes("見積書の送付"))
            .Ascii("\r\n")
            .Header("Content-Type: text/plain")
            .EndHeaders()
            .Ascii("body");

        Assert.Equal("見積書の送付", Parse(mail).Subject);
    }

    [Fact]
    public void From_display_name_in_encoded_word_is_decoded()
    {
        var name = Convert.ToBase64String(Encoding.UTF8.GetBytes("山田 太郎"));
        var mail = new MailBuilder()
            .Header($"From: =?UTF-8?B?{name}?= <taro@example.jp>")
            .Header("To: a@example.com, =?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes("佐藤")) + "?= <sato@example.jp>")
            .Header("Content-Type: text/plain")
            .EndHeaders()
            .Ascii("body");

        var parsed = Parse(mail);
        Assert.Equal("山田 太郎", parsed.FromName);
        Assert.Equal("taro@example.jp", parsed.FromAddress);
        Assert.Equal("a@example.com, 佐藤 <sato@example.jp>", parsed.ToText);
    }

    [Fact]
    public void Attachment_file_names_in_RFC2231_and_encoded_word_are_decoded()
    {
        var iso = Convert.ToBase64String([.. MailBuilder.EscKanji, 0x38, 0x2B, 0x40, 0x51, 0x3D, 0x71, .. MailBuilder.EscAscii]); // 見積書
        var mail = new MailBuilder()
            .Header("MIME-Version: 1.0")
            .Header("Content-Type: multipart/mixed; boundary=\"b\"")
            .EndHeaders()
            .Ascii("--b\r\nContent-Type: text/plain; charset=UTF-8\r\n\r\nhello\r\n")
            .Ascii("--b\r\nContent-Type: application/pdf\r\nContent-Disposition: attachment; filename*=UTF-8''%E8%AB%8B%E6%B1%82%E6%9B%B8.pdf\r\nContent-Transfer-Encoding: base64\r\n\r\nAAAA\r\n")
            .Ascii($"--b\r\nContent-Type: application/pdf; name=\"=?ISO-2022-JP?B?{iso}?=.pdf\"\r\nContent-Disposition: attachment; filename=\"=?ISO-2022-JP?B?{iso}?=.pdf\"\r\nContent-Transfer-Encoding: base64\r\n\r\nAAAA\r\n")
            .Ascii("--b--\r\n");

        var parsed = Parse(mail);
        Assert.True(parsed.HasAttachments);
        Assert.Equal(["請求書.pdf", "見積書.pdf"], parsed.Attachments.Select(a => a.FileName));
        Assert.Equal("hello", parsed.TextBody?.Trim());
    }

    [Fact]
    public void Html_body_uses_meta_charset_when_header_has_none()
    {
        JapaneseEncodings.EnsureRegistered();
        var html = "<html><head><meta http-equiv=\"Content-Type\" content=\"text/html; charset=Shift_JIS\"><style>p{}</style></head><body><p>ご案内</p><p>詳細&amp;日程</p></body></html>";
        var mail = new MailBuilder()
            .Header("Content-Type: text/html")
            .EndHeaders()
            .Raw(Encoding.GetEncoding(932).GetBytes(html));

        var parsed = Parse(mail);
        Assert.Contains("ご案内", parsed.HtmlBody, StringComparison.Ordinal);
        Assert.Null(parsed.TextBody);
        Assert.Equal("ご案内\n詳細&日程", parsed.PlainText);
        Assert.Equal("ご案内 詳細&日程", parsed.Preview);
    }

    [Fact]
    public void Multipart_alternative_prefers_plain_text_and_keeps_html()
    {
        var mail = new MailBuilder()
            .Header("Content-Type: multipart/alternative; boundary=\"x\"")
            .EndHeaders()
            .Ascii("--x\r\nContent-Type: text/plain; charset=UTF-8\r\n\r\n")
            .Raw(Encoding.UTF8.GetBytes("テキスト版"))
            .Ascii("\r\n--x\r\nContent-Type: text/html; charset=UTF-8\r\n\r\n")
            .Raw(Encoding.UTF8.GetBytes("<b>HTML版</b>"))
            .Ascii("\r\n--x--\r\n");

        var parsed = Parse(mail);
        Assert.Equal("テキスト版", parsed.TextBody?.Trim());
        Assert.Equal("<b>HTML版</b>", parsed.HtmlBody?.Trim());
        Assert.False(parsed.HasAttachments);
    }

    [Fact]
    public void Missing_date_falls_back_to_unix_epoch()
    {
        var mail = new MailBuilder().Header("Subject: x").EndHeaders().Ascii("y");
        Assert.Equal(DateTimeOffset.UnixEpoch, Parse(mail).Date);
    }
}
