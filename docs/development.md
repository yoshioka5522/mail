# 開発の手引き

## 構成

| プロジェクト | 内容 |
|---|---|
| `src/Hayabumi.Core` | メールの解析・保存・検索・同期（画面に依存しない。Linux でもビルド・テスト可能） |
| `src/Hayabumi.App` | WPF の画面、資格情報マネージャー、自動更新 |
| `tests/Hayabumi.Core.Tests` | Core の自動テスト |

### データの保存場所

| 場所 | 内容 |
|---|---|
| `%LOCALAPPDATA%\Hayabumi` | アプリ本体（Velopack が管理。更新・アンインストールで置き換わる） |
| `%LOCALAPPDATA%\HayabumiData` | データ（`hayabumi.db`、`messages\`（.eml）、`logs\`、`WebView2\`） |

データフォルダをアプリ本体と分けているのは、更新やアンインストールでメールが消えないようにするためです。

### 主な設計

- **メール本体**は 1 通ずつ `.eml` ファイルとして保存し、SQLite には一覧・検索用の情報だけを持つ。
- **全文検索**は SQLite FTS5。日本語は 2 文字ずつ（バイグラム）に分けて登録する（`SearchTokenizer`）。
  50 万通で検索 0.1 秒前後（`PerformanceTests` で確認）。
- **同期**は `IMailStoreSession` / `IRemoteFolder` で抽象化し、テストではメモリ上の偽サーバー（`FakeMailServer`）を使う。
  IMAP の実装は `ImapMailStoreSession`（MailKit）。
- **利用者の操作**（既読・移動・削除）はまずローカルに反映し、`pending_operations` に積んで接続時にサーバーへ反映する。
- **文字コード**は `MessageParser` で処理する。charset の宣言がない・誤っているメールは内容から推定する。

## ビルドとテスト

.NET 10 SDK が必要です。

```bash
dotnet build
```

```bash
dotnet test tests/Hayabumi.Core.Tests
```

50 万通での速度確認（時間がかかります）:

```bash
HAYABUMI_PERF_COUNT=500000 dotnet test tests/Hayabumi.Core.Tests --filter FullyQualifiedName~PerformanceTests --logger "console;verbosity=detailed"
```

実際の IMAP サーバーを相手にした結合テスト（テスト用サーバーのメールボックスを消去して使うので注意）:

```bash
HAYABUMI_IMAP_TEST=127.0.0.1:10143:ユーザー名:パスワード dotnet test tests/Hayabumi.Core.Tests --filter FullyQualifiedName~ImapIntegrationTests
```

実サーバーを使うテストは、次の環境変数を設定したときだけ実行されます（設定しなければスキップ）。

| 環境変数 | 例 | 内容 |
|---|---|---|
| `HAYABUMI_IMAP_TEST` | `127.0.0.1:10143:user:pass` | IMAP（暗号化なし） |
| `HAYABUMI_POP3_TEST` | `127.0.0.1:10110:user:pass` | POP3（暗号化なし。IMAP と同じメールボックス） |
| `HAYABUMI_SMTP_TEST` | `127.0.0.1:10025:/tmp/smtp-out` | 受信内容をフォルダに保存するテスト用 SMTP サーバー |

開発中は Dovecot（IMAP・POP3）と、Python の aiosmtpd で作った SMTP サーバーで確認しています。

画面（`Hayabumi.App`）は Linux でもビルドできますが、実行は Windows のみです。

## リリース

`v0.1.0` のようなタグを push すると、GitHub Actions（`.github/workflows/release.yml`）が次を行います。

1. テスト
2. `dotnet publish`（自己完結型、win-x64）
3. Velopack（`vpk`）で `Hayabumi-win-Setup.exe` と更新用パッケージを作成
4. GitHub Releases に登録

バージョンはタグから取ります（`v0.1.0` → `0.1.0`）。
