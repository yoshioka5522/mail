using System.IO;
using System.Text;
using Hayabumi.Core.Accounts;
using Hayabumi.Core.Storage;

namespace Hayabumi.App.Services;

/// <summary>
/// 画面の確認用のサンプルデータ（--demo で起動したときだけ使う。サーバーには接続しない）。
/// </summary>
public static class DemoData
{
    private static readonly (string Name, string Address, string Subject, string Preview)[] s_messages =
    [
        ("山田 太郎", "yamada@partner.example.jp", "来週の定例会議の日程について", "お世話になっております。来週の定例会議ですが、火曜日の 10 時からでいかがでしょうか。"),
        ("佐藤 花子", "sato@example.co.jp", "【ご確認】見積書の送付", "先日ご依頼いただいたお見積もりを添付いたします。ご確認のほどよろしくお願いいたします。"),
        ("GitHub", "noreply@github.com", "[yoshioka5522/mail] Release v0.2.2", "A new release is available. Hayabumi 0.2.2 includes automatic updates without a token."),
        ("鈴木 一郎", "suzuki@example.co.jp", "Re: 新サービスの企画書", "企画書を拝見しました。全体の方向性は良いと思います。いくつか気になった点をまとめました。"),
        ("Amazon.co.jp", "shipment@amazon.example", "ご注文の商品を発送しました", "ご注文いただいた商品を発送しました。お届け予定日は 11 月 14 日です。"),
        ("田中 美咲", "tanaka@client.example.jp", "打ち合わせのお礼", "本日はお忙しいところお時間をいただき、ありがとうございました。"),
        ("経理部", "keiri@example.co.jp", "11月分の経費精算の締め切りについて", "11 月分の経費精算は 11 月 25 日（水）締め切りです。お早めにご提出ください。"),
        ("Taro Smith", "taro.smith@global.example.com", "Meeting notes from yesterday", "Hi team, please find the meeting notes attached. Let me know if I missed anything."),
        ("高橋 健", "takahashi@partner.example.jp", "資料送付のご連絡", "ご依頼の資料をお送りします。ご不明な点がございましたらお知らせください。"),
        ("ニュースレター", "news@media.example.jp", "今週のおすすめ記事 5 選", "今週もお疲れさまでした。週末に読みたい記事をまとめてお届けします。"),
    ];

    public static void Seed(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Accounts.GetAll().Count > 0)
        {
            return;
        }

        var work = services.Accounts.Add(new Account
        {
            DisplayName = "仕事",
            EmailAddress = "me@example.co.jp",
            IncomingProtocol = IncomingProtocol.Imap,
            Incoming = new ServerEndpoint("imap.example.invalid", 993, ConnectionSecurity.SslOnConnect),
            Outgoing = new ServerEndpoint("smtp.example.invalid", 587, ConnectionSecurity.StartTls),
            UserName = "me@example.co.jp",
            AuthMethod = AuthMethod.Password,
        });
        var personal = services.Accounts.Add(new Account
        {
            DisplayName = "個人",
            EmailAddress = "me@gmail.example",
            IncomingProtocol = IncomingProtocol.Imap,
            Incoming = new ServerEndpoint("imap.example.invalid", 993, ConnectionSecurity.SslOnConnect),
            Outgoing = new ServerEndpoint("smtp.example.invalid", 587, ConnectionSecurity.StartTls),
            UserName = "me@gmail.example",
            AuthMethod = AuthMethod.Password,
        });

        foreach (var account in new[] { work, personal })
        {
            var inbox = services.Folders.Upsert(account.Id, "INBOX", "受信トレイ", '/', FolderRole.Inbox);
            services.Folders.Upsert(account.Id, "Sent", "送信済み", '/', FolderRole.Sent);
            services.Folders.Upsert(account.Id, "Drafts", "下書き", '/', FolderRole.Drafts);
            services.Folders.Upsert(account.Id, "Trash", "ゴミ箱", '/', FolderRole.Trash);
            services.Folders.Upsert(account.Id, "取引先", "取引先", '/', FolderRole.None);

            var now = DateTimeOffset.Now;
            var offset = account.Id == work.Id ? 0 : 1;
            var list = new List<NewMessage>();
            for (var i = offset; i < s_messages.Length; i += 2)
            {
                var (name, address, subject, preview) = s_messages[i];
                list.Add(new NewMessage
                {
                    Uid = (uint)(100 - i),
                    MessageId = $"demo{i}@example.jp",
                    Subject = subject,
                    FromName = name,
                    FromAddress = address,
                    ToText = account.EmailAddress,
                    Date = now.AddMinutes(-(i * 47) - 5),
                    Flags = i < 4 ? MessageFlags.None : MessageFlags.Seen | (i == 6 ? MessageFlags.Flagged : MessageFlags.None),
                    HasAttachments = i is 1 or 7 or 8,
                    Preview = preview,
                });
            }

            services.Messages.InsertMany(account.Id, inbox.Id, list);
            services.Folders.RefreshCounts(inbox.Id);
        }

        // 1 通目は本文も用意する（本文の表示を確認するため）
        var first = services.Messages.GetUnifiedPage(UnifiedView.AllInboxes, 0, 1)[0];
        var (fromName, fromAddress, firstSubject, _) = s_messages[0];
        var raw = $"From: =?UTF-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(fromName))}?= <{fromAddress}>\r\n" +
                  "To: me@example.co.jp\r\n" +
                  $"Subject: =?UTF-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(firstSubject))}?=\r\n" +
                  $"Date: {first.Date:r}\r\n" +
                  "MIME-Version: 1.0\r\nContent-Type: text/plain; charset=UTF-8\r\nContent-Transfer-Encoding: 8bit\r\n\r\n" +
                  "株式会社サンプル\r\n田中様\r\n\r\nお世話になっております。山田です。\r\n\r\n来週の定例会議ですが、火曜日の 10 時からでいかがでしょうか。\r\n" +
                  "場所は 3 階の会議室 B を予約しております。\r\n\r\nご都合をお知らせいただけますと幸いです。\r\nよろしくお願いいたします。\r\n\r\n-- \r\n山田 太郎\r\nhttps://www.example.jp\r\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        var path = services.Files.SaveAsync(first.AccountId, stream).GetAwaiter().GetResult();
        services.Messages.SetBody(first.Id, path, "お世話になっております。", first.Preview, hasAttachments: false);

        services.Contacts.RecordUsage([new MimeKit.MailboxAddress("山田 太郎", "yamada@partner.example.jp")], sent: true);
    }
}
