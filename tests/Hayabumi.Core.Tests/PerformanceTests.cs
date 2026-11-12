using System.Diagnostics;
using Hayabumi.Core.Storage;

namespace Hayabumi.Core.Tests;

/// <summary>
/// 大量のメールでの速度を確認する（要件: 50 万通で全文検索 1 秒以内）。
/// CI では件数を減らして実行し、極端な劣化だけを検出する。
/// </summary>
public sealed class PerformanceTests(ITestOutputHelper output) : IDisposable
{
    private static readonly string[] s_words =
    [
        "会議", "請求書", "見積", "打ち合わせ", "納品", "契約", "確認", "資料", "日程", "ご案内", "お知らせ", "報告",
        "プロジェクト", "予算", "出張", "経費", "採用", "面接", "研修", "セミナー", "invoice", "meeting", "report", "schedule",
    ];

    private readonly TestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Search_and_paging_are_fast_with_many_messages()
    {
        var count = int.TryParse(Environment.GetEnvironmentVariable("HAYABUMI_PERF_COUNT"), out var c) ? c : 50_000;
        var account = _db.AddAccount();
        var folder = _db.Folders.Upsert(account.Id, "INBOX", "受信トレイ", '/', FolderRole.Inbox);
        var random = new Random(42);
        var start = DateTimeOffset.UtcNow;

        var sw = Stopwatch.StartNew();
        foreach (var batch in Enumerable.Range(1, count).Chunk(5000))
        {
            _db.Messages.InsertMany(account.Id, folder.Id, batch.Select(i => new NewMessage
            {
                Uid = (uint)i,
                Subject = $"{Pick(random)}の{Pick(random)}について（第{i}回）",
                FromName = $"送信者{i % 500}",
                FromAddress = $"user{i % 500}@example.jp",
                ToText = "me@example.jp",
                Date = start.AddMinutes(-i),
                Flags = i % 3 == 0 ? MessageFlags.None : MessageFlags.Seen | (i % 50 == 0 ? MessageFlags.Flagged : MessageFlags.None),
                MessageId = $"m{i}@example.jp",
                Preview = $"いつもお世話になっております。{Pick(random)}と{Pick(random)}の件でご連絡いたしました。{Pick(random)}をご確認ください。",
            }));
        }

        output.WriteLine($"{count:N0} 通の登録: {sw.ElapsedMilliseconds} ms");

        sw.Restart();
        var page = _db.Messages.GetPage(folder.Id, count / 2, 100);
        var pageMs = sw.ElapsedMilliseconds;
        output.WriteLine($"中ほどのページ取得: {pageMs} ms");
        Assert.Equal(100, page.Count);

        foreach (var query in new[] { "請求書", "会議 日程", "議", "invoice", "送信者42" })
        {
            sw.Restart();
            var hits = _db.Messages.Search(query, limit: 500);
            output.WriteLine($"検索「{query}」: {hits.Count} 件, {sw.ElapsedMilliseconds} ms");
            Assert.NotEmpty(hits);
            Assert.True(sw.ElapsedMilliseconds < 1000, $"検索「{query}」に {sw.ElapsedMilliseconds} ms かかりました");
        }

        Assert.True(pageMs < 300, $"ページの取得に {pageMs} ms かかりました");

        foreach (var view in Enum.GetValues<UnifiedView>())
        {
            sw.Restart();
            var first = _db.Messages.GetUnifiedPage(view, 0, 100);
            var unifiedCount = _db.Messages.CountUnified(view);
            output.WriteLine($"統合ビュー {view}: {unifiedCount:N0} 件, 先頭ページと件数 {sw.ElapsedMilliseconds} ms");
            Assert.True(sw.ElapsedMilliseconds < 1000, $"統合ビュー {view} に {sw.ElapsedMilliseconds} ms かかりました");
        }
    }

    private static string Pick(Random random) => s_words[random.Next(s_words.Length)];
}
