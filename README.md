# Timetracker デプロイ CLI

## ツールの概要
Timetracker（DockerHub の `densocreate/timetracker` イメージ）を、選択可能な DB（PostgreSQL または SQL Server）および Redis（いずれもコンテナ）とともに Azure Container Apps にデプロイするためのコマンドラインツール（exe）です。  
本ツールは以下を行います。
- Azure サブスクリプション／リソースグループの指定・作成
- DockerHub「クイックスタート」準拠で `docker-compose.yml` と `.env` の生成（CLIのDry-run時）
- 実運用では Azure SDK（Azure Container Apps 専用）を用いて、Timetracker/DB/Redis の3コンテナを同一アプリとしてデプロイ
- timetracker コンテナイメージのバージョン（タグ）をオプションで指定可能
- コンテナのスペック（CPU/メモリ）を各コンテナ個別にオプション指定可能
- スケール構成は不要（各コンテナとも 1 台）

将来の GUI 化（デスクトップ/ウェブ）に備え、ドメイン層（Timetracker.Domain）とコントローラー層（Timetracker.Controller.Cli）を分離しています。

---

## 利用者向けマニュアル

### 前提環境（クラウドデプロイのみを行う場合）
- Windows 10/11（PowerShell 5.1 以降推奨）または他 OS（macOS/Linux）
- Azure SDK が利用できる認証方法を準備（Managed Identity または Service Principal）
- インターネット接続（Azure / DockerHub へのアクセスが可能であること）
- Timetracker イメージ（`densocreate/timetracker`）が利用可能

注意:
- クラウドデプロイのみを行う場合、利用者側で Docker は不要です。

### 前提環境（ローカル検証を行う場合）
- Windows 10/11 で Docker Desktop（Linux コンテナモード、WSL2 backend 推奨）  
  または、macOS/Linux で Docker Engine
- Docker Compose（`docker compose` が利用可能）

ローカル検証の目的:
- 生成された `docker-compose.yml` と `.env` を用いて、Timetracker+DB+Redis を手元で起動・動作確認する（Dry-run時）

### 利用方法（事前準備含む）
1. 認証情報準備（Azure SDK 用）
   - 例: Client ID/Secret/Tenant を環境変数に設定（`AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`, `AZURE_TENANT_ID`）、または Managed Identity を利用
2. 配布された `timetracker-cli.exe` を任意のフォルダに展開
3. 次の情報を準備
   - サブスクリプション ID（`SUBSCRIPTION_ID`）
   - リソースグループ名（未作成でも可）
   - リージョン（省略時は `japaneast`）
   - アプリ名（省略時は `TimeTracker`）
   - DB 種別（`postgres` または `sqlserver`）
   - DB パスワード（DB の種類に関わらず同一の CLI パラメータで指定）
   - Timetracker のアプリ用パスワード（DockerHub 記載の `<your password>` に相当）
   - timetracker コンテナのバージョンタグ（省略時は `latest`）
   - コンテナスペック（必要に応じて CPU/メモリを各コンテナ別に指定）
4. 実行（例は「コマンド例」参照）
5. 成功後、Azure ポータルから Container Apps を確認

ローカル検証時の事前準備（任意）:
- CLI の Dry-run で `docker-compose.yml` と `.env` を生成
- `docker compose up -d` で起動し、`http://localhost:8080` などで確認

### コマンドパラメータの説明一覧
- `--subscription` (必須): Azure サブスクリプション ID
- `--resource-group` (必須): リソースグループ名（存在しなければ作成）
- `--location` (任意): リージョン（省略時は `japaneast`）
- `--app-name` (任意): アプリ名（省略時は `TimeTracker`）
- `--db-type` (任意): DB 種別。`postgres` または `sqlserver`（デフォルト: `postgres`）
- `--db-password` (必須): DB パスワード（DB 種別に関わらず統一パラメータ）
- `--db-name` (任意): DB 名。デフォルト `timetracker`
- `--tracker-password` (必須): Timetracker アプリパスワード（DockerHub の `<your password>` に対応）
- `--tt-tag` (任意): timetracker コンテナイメージのタグ（バージョン）。省略時は `latest`（例: `1.2.3`）
- `--dry-run` (任意): true の場合、Azure 反映せず `docker-compose.yml` と `.env` の生成のみ
- コンテナスペック（任意）:
  - Timetracker: `--tt-cpu`（vCPU、小数可。例: `0.5`）、`--tt-memory`（Gi。例: `1.0`）
  - DB: `--db-cpu`、`--db-memory`
  - Redis: `--redis-cpu`、`--redis-memory`
  - 省略時のデフォルト: Timetracker CPU=0.5 / Mem=1.0Gi、DB CPU=0.5 / Mem=1.0Gi、Redis CPU=0.25 / Mem=0.5Gi

補足:
- DB ユーザは DockerHub の「クイックスタート」記載の固定値を使用します（CLI からは変更不可）。
- `.env` に機密情報が出力��れます。取り扱いにはご注意ください。

### コマンド例（PowerShell）

- Container Apps（SDK）でデプロイ（PostgreSQL、タグとコンテナスペック指定）
  ```powershell
  timetracker-cli.exe deploy `
    --subscription "<SUBSCRIPTION_ID>" `
    --resource-group "rg-tt-demo" `
    --location "japaneast" `
    --app-name "TimeTracker" `
    --db-type "postgres" `
    --db-password "Str0ngP@ssw0rd!" `
    --db-name "timetracker" `
    --tracker-password "AppLoginP@ss!" `
    --tt-tag "1.2.3" `
    --tt-cpu 0.5 --tt-memory 1.0 `
    --db-cpu 0.5 --db-memory 1.0 `
    --redis-cpu 0.25 --redis-memory 0.5
  ```

- Dry-run（ローカル検証用ファイル生成）
  ```powershell
  timetracker-cli.exe deploy `
    --subscription "<SUBSCRIPTION_ID>" `
    --resource-group "rg-tt-demo" `
    --db-type "postgres" `
    --db-password "Str0ngP@ssw0rd!" `
    --tracker-password "AppLoginP@ss!" `
    --tt-tag "latest" `
    --dry-run "true"
  ```

ローカル検証（Docker 必須）:
```powershell
docker compose --file .\docker-compose.yml --env-file .\.env up -d
docker compose ps
```

### FAQ
- Q: クラウドデプロイだけなら Docker は必要ですか？  
  A: いいえ。Azure Container Apps へのデプロイのみを行う場合、利用者側で Docker をインストールする必要はありません。

- Q: ローカル検証を行うには何が必要ですか？  
  A: Windows では Docker Desktop、macOS/Linux では Docker Engine と Docker Compose が必要です。CLI の Dry-run でファイルを生成し、`docker compose up -d` で起動してください。

- Q: スケール設定はできますか？  
  A: 本ツールではスケール構成は行いません。各コンテナとも 1 台固定です。

- Q: コンテナのCPU/メモリは後から変更できますか？  
  A: 再実行時にオプションを変更すれば Container Apps 上の設定が更新されます。

- Q: App Service を使ったデプロイは可能ですか？  
  A: 本ツールは Azure Container Apps を対象とした専用ツールです。App Service（マルチコンテナ／Compose）への直接デプロイ機能は含んでいません。App Service を利用する場合は ARM/Bicep テンプレートなど別方式をご検討ください。

---

## 開発者向け情報

### 前提環境
- .NET 8 SDK
- PowerShell 7（推奨）または 5.1 以上
- Azure SDK for .NET（NuGet: Azure.Identity, Azure.ResourceManager, Azure.ResourceManager.AppContainers など）
- （Dry-runのローカル検証を行う場合のみ）Docker Desktop（Windows）または Docker Engine（他 OS）
- 任意: Git（ソース取得用）、エディタ（VS Code 等）

### ビルド方法（前提準備含む）
```powershell
dotnet build .\Timetracker.sln
dotnet publish .\src\Timetracker.Controller.Cli\Timetracker.Controller.Cli.csproj -c Release -r win-x64 `
  -p:PublishSingleFile=true -p:SelfContained=true -p:UseAppHost=true
dotnet publish .\src\Timetracker.Domain\Timetracker.Domain.csproj -c Release -r win-x64 `
  -p:PublishSingleFile=true -p:SelfContained=true -p:UseAppHost=true
```

### 設計概要
- ドメイン層（Timetracker.Domain）
  - パラメータモデル、デフォルト、検証、Compose/.env生成（Dry-run用）、Container Apps のコンテナ仕様生成
- コントローラー層（Timetracker.Controller.Cli）
  - CLIの入出力、オプション解析（System.CommandLine）、Azure SDK 実行（ArmClient）
- 共通（Timetracker.Common）
  - ロガーなどのユーティリティ

生成する Container Apps の構成は常に以下の3コンテナを含みます。
- `timetracker`（`densocreate/timetracker` イメージ）
- `db`（`postgres:16-alpine` または `mcr.microsoft.com/mssql/server:2022-latest`）
- `redis`（`redis:7-alpine`）
- Ingress: 外部公開、ターゲットポート 8080
- スケール: MinReplicas=1 / MaxReplicas=1（固定）
- timetracker のイメージタグは `--tt-tag` で指定した値を使用（省略時は `latest`）

### 注意点
- Container Apps の永続ボリュームが必要な場合は Azure Files 等の設定を追加してください（DB/Redis のデータ保持用途）。
- 認証は Managed Identity もしくは Service Principal を推奨（DefaultAzureCredential 利用）。
- DockerHub「クイックスタート」記載の固定 DB ユーザ名は Domain 層の Defaults に集約しています（必要に応じて正確値に更新）。

### FAQ
- Q: 単一ファイル EXE の起動が遅い  
  A: 初回起動時に一時展開が発生するためです。Self-contained を無効化（ランタイム依存）するか、ReadyToRun などの最適化を検討してください。

- Q: Container Apps デプロイが失敗します  
  A: 認証（Service Principal/Managed Identity）設定、ネットワーク到達性、コンテナイメージ取得可否、環境変数不足を確認してください。必要に応じて Azure Portal の Container Apps のイベント/ログを参照してください.

