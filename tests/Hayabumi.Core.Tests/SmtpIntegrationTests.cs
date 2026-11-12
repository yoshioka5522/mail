using System.Text;
using Hayabumi.Core.Accounts;
using Hayabumi.Core.Compose;
using Hayabumi.Core.Mime;
using Hayabumi.Core.Security;

namespace Hayabumi.Core.Tests;

/// <summary>
/// 実際の SMTP サーバーに送る結合テスト。
/// 環境変数 HAYABUMI_SMTP_TEST に「ホスト:ポート:受信内容を保存するフォルダ」を指定したときだけ実行する
/// （受信内容をファイルに保存するテスト用の SMTP サーバーを使う。認証・暗号化なし）。
/// </summary>
public class SmtpIntegrationTests
{
    [Fact]
    public async Task Message_is_delivered_without_bcc_header()
    {
        var value = Environment.GetEnvironmentVariable("HAYABUMI_SMTP_TEST");
        Assert.SkipWhen(string.IsNullOrEmpty(value), "HAYABUMI_SMTP_TEST が設定されていません");
        var parts = value!.Split(':', 3);
        var outputDirectory = parts[2];
        foreach (var file in Directory.EnumerateFiles(outputDirectory))
        {
            File.Delete(file);
        }

        var account = new Account
        {
            Id = 1,
            DisplayName = "送信テスト",
            EmailAddress = "me@example.co.jp",
            IncomingProtocol = IncomingProtocol.Imap,
            Incoming = new ServerEndpoint("localhost", 143, ConnectionSecurity.None),
            Outgoing = new ServerEndpoint(parts[0], int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), ConnectionSecurity.None),
            UserName = "me",
            AuthMethod = AuthMethod.Password,
        };
        var draft = new Draft
        {
            To = "山田 <yamada@partner.jp>",
            Bcc = "secret@example.co.jp",
            Subject = "SMTP 送信テスト①",
            Body = "本文です。\n㈱サンプル",
            Charset = BodyCharset.Iso2022Jp,
        };

        var credentials = new InMemoryCredentialStore();
        credentials.Write(account.CredentialKey, "me", "unused");
        await new SmtpMailSender(new StoredPasswordSecretProvider(credentials))
            .SendAsync(account, MessageBuilder.Build(draft, account), TestContext.Current.CancellationToken);

        var eml = Directory.EnumerateFiles(outputDirectory, "*.eml").Single();
        var recipients = File.ReadAllText(Path.ChangeExtension(eml, ".rcpt")).Split('\n');
        var raw = File.ReadAllBytes(eml);

        Assert.Equal(["yamada@partner.jp", "secret@example.co.jp"], recipients);
        Assert.DoesNotContain("Bcc:", Encoding.ASCII.GetString(raw), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(raw, b => b >= 0x80);

        var parsed = MessageParser.Parse(new MemoryStream(raw));
        Assert.Equal("SMTP 送信テスト①", parsed.Subject);
        Assert.Equal("本文です。\r\n㈱サンプル", parsed.TextBody?.TrimEnd());
    }
}
