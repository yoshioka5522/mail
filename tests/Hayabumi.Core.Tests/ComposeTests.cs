using System.Text;
using Hayabumi.Core.Accounts;
using Hayabumi.Core.Compose;
using Hayabumi.Core.Mime;
using MimeKit;

namespace Hayabumi.Core.Tests;

public class ComposeTests
{
    private static Account Account(string email = "me@example.co.jp", string signature = "") => new()
    {
        Id = 1,
        DisplayName = "自分",
        EmailAddress = email,
        IncomingProtocol = IncomingProtocol.Imap,
        Incoming = new ServerEndpoint("imap.example.co.jp", 993, ConnectionSecurity.SslOnConnect),
        Outgoing = new ServerEndpoint("smtp.example.co.jp", 587, ConnectionSecurity.StartTls),
        UserName = email,
        AuthMethod = AuthMethod.Password,
        Signature = signature,
    };

    private static MimeMessage Original()
    {
        var message = new MimeMessage
        {
            Subject = "RE: 打ち合わせの件",
            MessageId = "orig@example.jp",
            Date = new DateTimeOffset(2026, 11, 10, 9, 30, 0, TimeSpan.FromHours(9)),
            Body = new TextPart("plain") { Text = "明日10時でお願いします。\n> 以前の引用" },
        };
        message.From.Add(new MailboxAddress("山田, 太郎", "yamada@partner.jp"));
        message.To.Add(new MailboxAddress("自分", "me@example.co.jp"));
        message.To.Add(new MailboxAddress("鈴木", "suzuki@example.co.jp"));
        message.Cc.Add(new MailboxAddress("佐藤", "sato@partner.jp"));
        message.References.Add("first@example.jp");
        return message;
    }

    [Theory]
    [InlineData("Re: ", "件名", "Re: 件名")]
    [InlineData("Re: ", "Re: 件名", "Re: 件名")]
    [InlineData("Re: ", "RE: re：件名", "Re: 件名")]
    [InlineData("Re: ", "Fw: 件名", "Re: Fw: 件名")]
    [InlineData("Fw: ", "Fwd: 件名", "Fw: 件名")]
    [InlineData("Fw: ", "", "Fw: ")]
    public void Subject_prefix_is_not_duplicated(string prefix, string subject, string expected)
    {
        Assert.Equal(expected, DraftFactory.AddPrefix(prefix, subject));
    }

    [Fact]
    public void Reply_goes_to_sender_with_quote_and_threading_headers()
    {
        var draft = DraftFactory.CreateReply(Account(signature: "自分\n株式会社サンプル"), Original(), 42, replyAll: false);

        Assert.Equal(ComposeMode.Reply, draft.Mode);
        Assert.Equal("\"山田, 太郎\" <yamada@partner.jp>", draft.To);
        Assert.Equal(string.Empty, draft.Cc);
        Assert.Equal("Re: 打ち合わせの件", draft.Subject);
        Assert.Equal("orig@example.jp", draft.InReplyTo);
        Assert.Equal(["first@example.jp", "orig@example.jp"], draft.References);
        Assert.Equal(42, draft.SourceMessageId);
        Assert.StartsWith("\n\n-- \n自分\n株式会社サンプル\n\n2026/11/10", draft.Body, StringComparison.Ordinal);
        Assert.Contains("> 明日10時でお願いします。\n>> 以前の引用", draft.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Reply_all_excludes_self_and_duplicates()
    {
        var draft = DraftFactory.CreateReply(Account(), Original(), null, replyAll: true);

        var to = MessageBuilder.ParseAddresses("宛先", draft.To).Select(m => m.Address);
        var cc = MessageBuilder.ParseAddresses("CC", draft.Cc).Select(m => m.Address);
        Assert.Equal(["yamada@partner.jp", "suzuki@example.co.jp"], to);
        Assert.Equal(["sato@partner.jp"], cc);
    }

    [Fact]
    public void Reply_to_own_sent_message_goes_to_original_recipients()
    {
        var sent = new MimeMessage { Subject = "ご案内", Body = new TextPart("plain") { Text = "x" } };
        sent.From.Add(new MailboxAddress("自分", "me@example.co.jp"));
        sent.To.Add(new MailboxAddress("顧客", "customer@client.jp"));

        var draft = DraftFactory.CreateReply(Account(), sent, null, replyAll: false);
        Assert.Equal("顧客 <customer@client.jp>", draft.To);
    }

    [Fact]
    public void Forward_includes_original_header_block()
    {
        var draft = DraftFactory.CreateForward(Account(), Original(), 7, ["C:/tmp/a.pdf"]);

        Assert.Equal("Fw: RE: 打ち合わせの件", draft.Subject);
        Assert.Equal(string.Empty, draft.To);
        Assert.Contains("差出人: 山田, 太郎 <yamada@partner.jp>", draft.Body, StringComparison.Ordinal);
        Assert.Contains("明日10時でお願いします。", draft.Body, StringComparison.Ordinal);
        Assert.Equal(["C:/tmp/a.pdf"], draft.Attachments);
        Assert.Equal(ComposeMode.Forward, draft.Mode);
    }

    [Fact]
    public void Built_message_round_trips_in_UTF8()
    {
        var draft = new Draft
        {
            To = "山田 <yamada@partner.jp>; suzuki@example.co.jp",
            Cc = "佐藤 <sato@partner.jp>",
            Bcc = "boss@example.co.jp",
            Subject = "お見積もりの件 🙏",
            Body = "お世話になっております。\n①の件、承知しました。",
            InReplyTo = "orig@example.jp",
            References = ["orig@example.jp"],
        };

        var message = MessageBuilder.Build(draft, Account());
        var parsed = MessageParser.Parse(Reload(message));

        Assert.Equal("お見積もりの件 🙏", parsed.Subject);
        Assert.Equal("お世話になっております。\r\n①の件、承知しました。", parsed.TextBody);
        Assert.Equal("自分 <me@example.co.jp>", $"{parsed.FromName} <{parsed.FromAddress}>");
        Assert.Equal("山田 <yamada@partner.jp>, suzuki@example.co.jp", parsed.ToText);
        Assert.Equal("orig@example.jp", parsed.InReplyTo);
        Assert.Equal("boss@example.co.jp", message.Bcc.Mailboxes.Single().Address);
        Assert.EndsWith("@example.co.jp", message.MessageId, StringComparison.Ordinal);
    }

    [Fact]
    public void Built_message_in_ISO_2022_JP_uses_7bit_JIS()
    {
        var draft = new Draft { To = "a@example.jp", Subject = "見積書①", Body = "㈱サンプル", Charset = BodyCharset.Iso2022Jp };
        var message = MessageBuilder.Build(draft, Account());
        using var stream = Reload(message);
        var raw = Encoding.ASCII.GetString(stream.ToArray());

        Assert.Contains("charset=iso-2022-jp", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("=?iso-2022-jp?", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(stream.ToArray(), b => b >= 0x80);

        stream.Position = 0;
        var parsed = MessageParser.Parse(stream);
        Assert.Equal("見積書①", parsed.Subject);
        Assert.Equal("㈱サンプル", parsed.TextBody?.TrimEnd());
    }

    [Fact]
    public void Attachment_file_name_uses_RFC2047_for_old_clients()
    {
        var path = Path.Combine(Path.GetTempPath(), $"請求書_{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, [1, 2, 3]);
        try
        {
            var message = MessageBuilder.Build(new Draft { To = "a@example.jp", Subject = "s", Body = "b", Attachments = [path] }, Account());
            using var stream = Reload(message);
            var raw = Encoding.ASCII.GetString(stream.ToArray());
            Assert.Contains("filename=\"=?utf-8?", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("filename*=", raw, StringComparison.Ordinal);

            stream.Position = 0;
            var parsed = MessageParser.Parse(stream);
            Assert.Equal(Path.GetFileName(path), parsed.Attachments.Single().FileName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("taro")]
    [InlineData("taro@")]
    [InlineData("@example.jp")]
    [InlineData("taro@localhost")]
    public void Invalid_address_is_rejected(string value)
    {
        Assert.Throws<AddressParseException>(() => MessageBuilder.ParseAddresses("宛先", value));
    }

    [Fact]
    public void Japanese_separators_and_full_width_at_are_accepted()
    {
        var list = MessageBuilder.ParseAddresses("宛先", "a@example.jp、b＠example.jp；c@example.jp");
        Assert.Equal(["a@example.jp", "b@example.jp", "c@example.jp"], list.Select(m => m.Address));
    }

    [Fact]
    public void Send_checks_flag_external_domains_and_missing_attachment()
    {
        var draft = new Draft
        {
            To = "yamada@partner.jp, suzuki@example.co.jp",
            Subject = "資料",
            Body = "資料を添付します。\n\n> 添付の件",
        };

        var result = SendChecks.Analyze(draft, Account());

        Assert.True(result.CanSend);
        Assert.Equal([true, false], result.Recipients.Select(r => r.IsExternal));
        Assert.Contains(result.Warnings, w => w.Kind == SendWarningKind.ExternalRecipients && w.Message.Contains("partner.jp", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Kind == SendWarningKind.MissingAttachment);
    }

    [Fact]
    public void Attachment_word_only_in_quote_is_not_flagged()
    {
        var draft = new Draft { To = "a@example.co.jp", Subject = "Re: x", Body = "了解しました。\n\n> 添付をご確認ください" };
        Assert.DoesNotContain(SendChecks.Analyze(draft, Account()).Warnings, w => w.Kind == SendWarningKind.MissingAttachment);
    }

    [Fact]
    public void Send_checks_block_without_recipients_and_suggest_bcc()
    {
        Assert.False(SendChecks.Analyze(new Draft { Subject = "x" }, Account()).CanSend);

        var many = string.Join(", ", Enumerable.Range(1, 10).Select(i => $"user{i}@company{i % 3}.jp"));
        var result = SendChecks.Analyze(new Draft { To = many, Subject = "一斉送信" }, Account());
        Assert.Contains(result.Warnings, w => w.Kind == SendWarningKind.ManyVisibleRecipients);
    }

    [Fact]
    public void Free_mail_sender_gets_no_external_warning()
    {
        var result = SendChecks.Analyze(new Draft { To = "friend@gmail.com", Subject = "x" }, Account("me@gmail.com"));
        Assert.DoesNotContain(result.Warnings, w => w.Kind == SendWarningKind.ExternalRecipients);
        Assert.True(result.Recipients.Single().IsExternal);
    }

    private static MemoryStream Reload(MimeMessage message)
    {
        var stream = new MemoryStream();
        message.WriteTo(MessageBuilder.FormatOptions, stream);
        stream.Position = 0;
        return stream;
    }
}
