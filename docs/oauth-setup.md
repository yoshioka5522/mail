# OAuth（Google・Microsoft でサインイン）の準備

「Google でサインイン」「Microsoft でサインイン」を使うには、Hayabumi を各社にアプリとして登録し、
発行された ID を GitHub リポジトリに設定します。登録は無料で、最初に一度だけ行います。

登録が済むと、次のリリースから setup.exe に ID が埋め込まれ、サインインのボタンがそのまま使えるようになります。
（登録前でも、Hayabumi の［ツール］→［OAuth の設定］に直接入力すれば試せます。）

---

## 1. Google（Gmail）

1. [Google Cloud コンソール](https://console.cloud.google.com/) に、Google アカウントでログインします。
2. 画面上部のプロジェクト選択から［新しいプロジェクト］を作ります（名前は「Hayabumi」など）。
3. ［API とサービス］→［ライブラリ］で「Gmail API」を検索し、［有効にする］を押します。
4. ［API とサービス］→［OAuth 同意画面］（または［Google Auth Platform］）を開きます。
   - アプリ名: `Hayabumi`、ユーザー サポートのメール: ご自身のアドレス
   - 対象（User Type）: **外部**
   - 公開ステータス: **テスト** のままにします
   - ［対象］または［テストユーザー］に、Hayabumi で使う Gmail アドレスを追加します（最大 100 人）
5. ［データアクセス］（または［スコープ］）で［スコープを追加］を押し、`https://mail.google.com/` を追加します。
6. ［認証情報］（または［クライアント］）→［認証情報を作成］→［OAuth クライアント ID］を選びます。
   - アプリケーションの種類: **デスクトップ アプリ**
   - 名前: `Hayabumi`
7. 表示された **クライアント ID** と **クライアント シークレット** を控えます。

> テスト中のアプリでは、サインイン時に「Google はこのアプリを確認していません」と表示されます。
> ［続行］を押して進めてください。また、テスト中はおおむね 7 日ごとに再サインインが必要になります。
> これを避けるには Google の審査（一般配布を始めるときに予定）が必要です。

## 2. Microsoft（Outlook.com / Microsoft 365）

1. [Microsoft Entra 管理センター](https://entra.microsoft.com/) に Microsoft アカウントでサインインします。
   （個人のアカウントでも作れます。）
2. ［アプリの登録］→［新規登録］を開きます。
   - 名前: `Hayabumi`
   - サポートされているアカウントの種類: **任意の組織のディレクトリ内のアカウントと、個人用の Microsoft アカウント**
   - リダイレクト URI: プラットフォーム **パブリック クライアント/ネイティブ（モバイルとデスクトップ）**、URI `http://localhost`
3. 登録後、［認証］で **パブリック クライアント フローを許可する** を［はい］にして保存します。
4. ［API のアクセス許可］→［アクセス許可の追加］→［Microsoft Graph］→［委任されたアクセス許可］で、次を追加します。
   - `IMAP.AccessAsUser.All`
   - `POP.AccessAsUser.All`
   - `SMTP.Send`
   - `offline_access`、`openid`、`email`
5. ［概要］の **アプリケーション (クライアント) ID** を控えます（シークレットは不要です）。

> Microsoft 365（会社のアカウント）では、管理者が IMAP・SMTP 認証を無効にしている場合や、
> アプリの利用に管理者の同意が必要な場合があります。

## 3. GitHub に設定する

リポジトリの［Settings］→［Secrets and variables］→［Actions］で、次を登録します。

| 種類 | 名前 | 値 |
|---|---|---|
| Variables | `OAUTH_GOOGLE_CLIENT_ID` | Google のクライアント ID |
| Secrets | `OAUTH_GOOGLE_CLIENT_SECRET` | Google のクライアント シークレット |
| Variables | `OAUTH_MICROSOFT_CLIENT_ID` | Microsoft のアプリケーション ID |

次のリリースから、これらの値が組み込まれます。
