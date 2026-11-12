# Hayabumi（はやぶみ）

広告のない、軽くて速い Windows 用メールアプリ。

- 要件定義: [docs/requirements.md](docs/requirements.md)
- 開発の手引き: [docs/development.md](docs/development.md)

## 動作環境

- Windows 10 / 11（64bit）
- 管理者権限は不要（ユーザー単位でインストール）

## インストール

[Releases](../../releases) から `Hayabumi-win-Setup.exe` をダウンロードして実行します。

コード署名をしていないため、初回は「Windows によって PC が保護されました」と表示されます。
［詳細情報］→［実行］の順に選ぶとインストールできます。

## 現在の版（0.1：フェーズ 0）でできること

- IMAP アカウントの追加（パスワード／アプリ パスワード）
- 受信（新着・既読やフラグの変更・削除の反映。数十万通のフォルダでも軽く表示）
- 本文の表示（日本語の文字化け対策、外部画像は既定でブロック）
- 全文検索（日本語の部分一致）
- 既読・未読、フラグ、削除（オフラインでも操作でき、接続時にサーバーへ反映）
- 添付ファイルの保存
- 自動更新

送信、POP3、OAuth（Gmail・Microsoft のサインイン）などはフェーズ 1 で対応します。

## 著作権

Copyright (c) Hayabumi. All rights reserved.
