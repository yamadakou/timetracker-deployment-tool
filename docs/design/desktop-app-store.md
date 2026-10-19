# デスクトップアプリの Microsoft Store への提出手順

デスクトップアプリ（詳細設計 [`desktop-app.md`](desktop-app.md)、REVIEW_ISSUES No.49）を、Microsoft Store に**限定公開**（Q2）で出すまでの手順です。オーナーが行う作業（アカウント・アプリ名の予約・アプリ登録・提出）と、リポジトリでの設定を、行う順に並べています。

1. Entra ID にサインイン用のアプリを登録する……2 章
2. Partner Center の開発者アカウントを作り、アプリ名を予約する……3 章
3. 予約した ID とアプリ登録のクライアント ID を、リポジトリの変数に設定し、リリースする……4 章
4. Partner Center で初回の提出をする……5 章
5. 2 回目以降の更新……6 章

- **所要時間**: アカウントの確認に数日（会社のアカウントの場合）、提出の入力に 1 時間ほど、Store の認定に数日
- **費用**: 開発者アカウントの登録料は無料（新しい登録の手順の場合）
- 一次情報の確認日: 2026-10-17（出典は詳細設計の 14）

## 1. この手順で使う言葉
| 言葉 | 意味 |
|---|---|
| [Partner Center](https://learn.microsoft.com/ja-jp/windows/apps/publish/) | Microsoft Store にアプリを出すための管理画面 |
| アプリ名の予約 | Store に出すアプリの名前を Partner Center で確保すること。予約すると、パッケージの ID（下記）が決まる |
| パッケージの ID | パッケージのマニフェスト（`Package.appxmanifest`）の `Identity` の `Name` と `Publisher`、`Properties` の `PublisherDisplayName`。Partner Center の値と一致しないと提出できない |
| `.msixupload` | Store に出すパッケージ（x64 と arm64 のバンドル）。リリースの GitHub Release に `ttdeploytool-desktop-vX.Y.Z.msixupload` として載る。署名は無く、Store が認定の後に署名する |
| [限定公開（Private audience）](https://learn.microsoft.com/ja-jp/windows/apps/publish/publish-your-app/msix/visibility-options) | 指定した Microsoft アカウント（メールアドレスの一覧）の人だけが Store で見つけて導入できる公開の範囲。**Entra ID の職場のアカウントは対象外**。一度公開（Public）にすると限定公開に戻せない |
| アプリ登録（クライアント ID） | 利用者が Azure にサインインするときに、アプリを Entra ID に名乗るための登録（詳細設計 5.1・Q3） |

## 2. サインイン用のアプリを登録する（Entra ID）
[アプリの登録](https://learn.microsoft.com/ja-jp/entra/identity-platform/quickstart-register-app)を、Entra 管理センター（または Azure ポータルの「アプリの登録」）で行います。シークレットや証明書は作りません（公開クライアント）。

1. 「新規登録」で、次のように登録する
   - 名前: `TTDeployTool`（利用者のサインインの画面に表示される）
   - サポートされているアカウントの種類: 「任意の組織ディレクトリ内のアカウント（マルチテナント）」（Q3。社外の利用者のテナントでもサインインできるようにする）
   - リダイレクト URI: 「パブリック クライアント/ネイティブ（モバイルとデスクトップ）」で `http://localhost`
2. 登録した「アプリケーション（クライアント）ID」を控える（4 章で使う）
3. 「認証」で、「モバイルとデスクトップのアプリケーション」のリダイレクト URI に `ms-appx-web://Microsoft.AAD.BrokerPlugin/<クライアント ID>` を追加する（[WAM で必要](https://learn.microsoft.com/ja-jp/entra/msal/dotnet/acquiring-tokens/desktop-mobile/wam)）。「パブリック クライアント フローを許可する」を「はい」にする
4. 「API のアクセス許可」で、「Azure Service Management」の委任されたアクセス許可 `user_impersonation` を追加する。ほかのアクセス許可（Microsoft Graph の `User.Read` など）は不要なら削除する
5. （任意）[発行元の確認](https://learn.microsoft.com/ja-jp/entra/identity-platform/publisher-verification-overview)を行う。利用者のテナントが「確認済みの発行元のアプリだけに同意できる」設定の場合に必要。Partner Center のアカウントと独自ドメインが要る（費用は無い）

> [!NOTE]
> 利用者のテナントの既定の同意の設定では、`user_impersonation` は利用者本人が同意できます。テナントが利用者の同意を制限している場合は、そのテナントの管理者の同意が必要です。

## 3. 開発者アカウントを作り、アプリ名を予約する（Partner Center）
1. [開発者アカウントを作る](https://learn.microsoft.com/ja-jp/windows/apps/publish/partner-center/open-a-developer-account)。新しい登録の手順（`storedeveloper.microsoft.com` から始める）では、個人・会社とも登録料が無料。会社のアカウントは、D-U-N-S 番号などの確認に数日かかる
2. Partner Center の「アプリとゲーム」で「新しい製品」→「MSIX または PWA アプリ」を選び、アプリ名を予約する（[アプリ名の予約](https://learn.microsoft.com/ja-jp/windows/apps/publish/publish-your-app/msix/reserve-your-apps-name)）。Timetracker は株式会社デンソークリエイトの製品のため、製品名を含めてよいかは事前に確かめる
3. 予約したアプリの「製品管理」→「製品 ID」で、次の 3 つを控える（4 章で使う）
   - パッケージ/ID/名前（`Package/Identity/Name`）
   - パッケージ/ID/発行元（`Package/Identity/Publisher`。`CN=` で始まる）
   - パッケージ/プロパティ/発行元の表示名（`Package/Properties/PublisherDisplayName`）

## 4. リポジトリの変数を設定し、リリースする
1. GitHub のリポジトリの「Settings」→「Secrets and variables」→「Actions」→「Variables」で、次のリポジトリの変数を作る（秘密の情報ではないため、Secrets ではなく Variables にする）

   | 変数 | 値 |
   |---|---|
   | `TT_DESKTOP_IDENTITY_NAME` | 3 章の 3 の「パッケージ/ID/名前」 |
   | `TT_DESKTOP_PUBLISHER` | 3 章の 3 の「パッケージ/ID/発行元」 |
   | `TT_DESKTOP_PUBLISHER_DISPLAY_NAME` | 3 章の 3 の「発行元の表示名」 |
   | `TT_DESKTOP_CLIENT_ID` | 2 章の 2 の「アプリケーション（クライアント）ID」 |

2. 通常どおり `vX.Y.Z` のタグを push してリリースする。`release.yml` の `desktop` ジョブが、マニフェストの版を `X.Y.Z.0` に、ID を上の変数の値にして `.msixupload` を作り（`.github/scripts/set-msix-identity.ps1`）、中身を確かめて（`check-msix.ps1`）Release に載せる
3. Release の `ttdeploytool-desktop-vX.Y.Z.msixupload` を取得する

> [!NOTE]
> 変数を設定しないでリリースした場合、パッケージの ID は仮の値（`TTDeployTool` / `CN=TTDeployTool`）のままです。ビルドと確認は通りますが、Store には出せません。

## 5. 初回の提出（Partner Center）
初回の提出は Partner Center で手作業で行う必要があります（[MSIX アプリの提出](https://learn.microsoft.com/ja-jp/windows/apps/publish/publish-your-app/msix/create-app-submission)）。予約したアプリの「申請の開始」から、次の項目を入力します。

| 項目 | 入力する内容 |
|---|---|
| 価格と使用可能状況 | 公開の範囲: **限定公開（Private audience）**。導入できる人の Microsoft アカウントのメールアドレス（またはグループ）を指定する。価格: 無料。市場: 日本（必要に応じて追加） |
| プロパティ | カテゴリ: 開発者ツール。プライバシー ポリシーの URL: `docs/PRIVACY.md` を公開した URL（例: `https://github.com/yamadakou/timetracker-deployment-tool/blob/main/docs/PRIVACY.md`。デスクトップのアプリでは必須。[ポリシー 10.5.1](https://learn.microsoft.com/ja-jp/windows/apps/publish/store-policies)）。サポートの連絡先: GitHub の Issues の URL |
| 年齢区分 | IARC のアンケートに答える（[ポリシー 11.11](https://learn.microsoft.com/ja-jp/windows/apps/publish/store-policies)。暴力・課金・ユーザー間のやり取りなどは無い） |
| パッケージ | 4 章で取得した `.msixupload` をアップロードする。対象のデバイスは「デスクトップ」 |
| ストア登録情報（日本語） | 説明（例: 「Timetracker の検証環境を Azure Container Apps にデプロイ・確認・削除し、ローカル検証用の docker-compose を作ります。」）、スクリーンショット（1 枚以上）、検索用のキーワード |
| 申請オプション | 制限付き機能 `runFullTrust` の用途: 「WinUI 3 のデスクトップアプリ（Windows App SDK）として動作するために必要です。Azure Resource Manager への接続と、利用者が選んだフォルダーへのファイルの保存を行います。」。認定の担当者への注記: 下記 |

認定の担当者への注記（例）:
```
このアプリは、利用者自身の Azure サブスクリプションに検証用の Timetracker 環境をデプロイするツールです。
Azure の機能（サインイン、デプロイ、削除）には Azure のサブスクリプションと Entra ID のアカウントが必要なため、テスト用のアカウントは提供していません。
サインインせずに確かめられる機能: 左のメニューの「設定」で DB のパスワードと Timetracker のパスワード（それぞれ 8 文字以上）を入力し、
「ローカル検証（Dry-run）」の「保存先を選んで保存」で docker-compose.yml と .env を保存できます。
```

> [!IMPORTANT]
> 認定でアカウントが無いことが問題にされた場合（ポリシー 10.3.1）は、認定の担当者に、確かめられる範囲の説明を追加するか、検証用のアカウントの用意を検討します（詳細設計 11 の 6）。

## 6. 2 回目以降の更新
1. 新しい版を `vX.Y.Z` のタグでリリースする（版は前回より大きくする）
2. Partner Center で、前回の申請を複製して新しい申請を作り、「パッケージ」で新しい `.msixupload` に差し替えて提出する
3. 認定の後、Store が利用者の PC に更新を配信する。アプリは起動時に更新があるかを確かめ、あれば「更新があります」と表示する

提出の自動化（`msstore` CLI。GitHub Actions では `microsoft/microsoft-store-apppublisher`）は、Entra ID のアプリ登録と Partner Center の Manager のロールが要り、無料のアプリだけに対応しています（[msstore CLI](https://learn.microsoft.com/ja-jp/windows/apps/publish/msstore-dev-cli/overview)）。必要になったら、`release.yml` に追加します。
