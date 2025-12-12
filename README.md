<a href="https://github.com/yamadakou/timetracker-deployment-tool/actions/workflows/ci.yml"><img src="https://github.com/yamadakou/timetracker-deployment-tool/actions/workflows/ci.yml/badge.svg"></a>

# Timetracker デプロイ CLI

## ツールの概要
Timetracker（DockerHub の `densocreate/timetracker` イメージ）を、選択可能な DB（PostgreSQL または SQL Server）および Redis とともに Azure Container Apps 上へデプロイするための CLI ツールです。

**重要**: 本ツールは DB および Redis を **コンテナ** として Azure Container Apps にデプロイします。Azure Database for PostgreSQL や Azure SQL Database などの PaaS データベースリソース、または Log Analytics ワークスペースは作成しません。すべてコンテナとして Container Apps 内で実行されます。

本ツールは以下を行います。
- Azure サブスクリプション／リソースグループの指定・作成
- DockerHub「クイックスタート」準拠で `docker-compose.yml` と `.env` の生成（CLIのDry-run時）
- Azure SDK（Azure Container Apps 専用）を用いて、Timetracker/DB/Redis の3つの Container Apps をデプロイ
- timetracker コンテナイメージのバージョン（タグ）をオプションで指定可能
- コンテナのスペック（CPU/メモリ）を各コンテナ個別にオプション指定可能
- スケール構成は不要（各コンテナとも 1 台）

将来の GUI 化（デスクトップ/ウェブ）に備え、ドメイン層（Timetracker.Domain）とコントローラー層（Timetracker.Controller.Cli）を分離しています。

---

## 利用者向けマニュアル

### 作成される Azure リソース

本ツールは以下の Azure リソースを作成します：

1. **リソースグループ** (Resource Group)
   - 指定した名前のリソースグループが存在しない場合、新規作成されます
   - 既に存在する場合は、そのリソースグループを使用します

2. **Container Apps Environment** (Container Apps 環境)
   - すべての Container Apps が実行される環境
   - 名前: `{アプリ名}-env`（例: `timetracker-env`）

3. **3つの Container Apps**
   - **`{アプリ名}-tt`**: Timetracker アプリケーション本体（外部公開、ポート 8080）
   - **`{アプリ名}-db`**: データベース（PostgreSQL または SQL Server、内部専用、TCP接続）
   - **`{アプリ名}-redis`**: Redis キャッシュ（内部専用、TCP接続）

**重要な注意事項**:
- DB および Redis は **コンテナ** として Container Apps 内で実行されます
- Azure Database for PostgreSQL、Azure SQL Database などの PaaS データベースリソースは作成されません
- Log Analytics ワークスペースは自動作成されません（Container Apps Environment が必要に応じて作成する場合があります）
- データの永続化が必要な場合は、Azure Files などの永続ボリュームを別途設定する必要があります

### 必要な RBAC ロールと権限

本ツールを実行するユーザーまたはサービスプリンシパルには、以下のいずれかのロールが必要です：

| ロール | 説明 | 推奨用途 |
|--------|------|----------|
| **Owner** | サブスクリプションまたはリソースグループの完全な管理権限 | 開発・テスト環境 |
| **Contributor** | リソースの作成・変更・削除が可能（RBACの管理は不可） | 本番環境での推奨ロール |
| **Container Apps Contributor** | Container Apps リソースの管理に特化 | 最小権限の原則に基づく運用 |

**参考リンク**:
- [Azure RBAC の概要](https://learn.microsoft.com/ja-jp/azure/role-based-access-control/overview)
- [Azure 組み込みロール](https://learn.microsoft.com/ja-jp/azure/role-based-access-control/built-in-roles)
- [Container Apps 共同作成者ロール](https://learn.microsoft.com/ja-jp/azure/role-based-access-control/built-in-roles#container-apps-contributor)

### Azure リソースプロバイダーの登録

Container Apps を初めて使用する場合、以下のリソースプロバイダーをサブスクリプションに登録する必要があります：

- **Microsoft.App** (必須): Azure Container Apps
- **Microsoft.OperationalInsights** (必須): Container Apps Environment が使用
- **Microsoft.Web** (オプション): 一部のシナリオで必要

**登録方法（Azure CLI）**:
```bash
az provider register --namespace Microsoft.App
az provider register --namespace Microsoft.OperationalInsights
az provider register --namespace Microsoft.Web

# 登録状態の確認
az provider show --namespace Microsoft.App --query "registrationState"
az provider show --namespace Microsoft.OperationalInsights --query "registrationState"
```

**参考リンク**:
- [Azure リソースプロバイダーと種類](https://learn.microsoft.com/ja-jp/azure/azure-resource-manager/management/resource-provider-registration)
- [Azure Container Apps のクイックスタート](https://learn.microsoft.com/ja-jp/azure/container-apps/get-started)

### 共通の前提環境
- Windows 10/11（PowerShell 5.1 以降推奨）または他 OS（macOS/Linux）
- インターネット接続（Azure / DockerHub へのアクセスが可能であること）
- Timetracker イメージ（`densocreate/timetracker`）が DockerHub から利用可能

注意:
- クラウドデプロイのみを行う場合、利用者側で Docker は不要です。

### 認証モード別の前提環境と利用方法

本ツールは5つの認証モードをサポートしています。環境や用途に応じて適切な認証モードを選択してください。

---

#### 認証モード: `default` (DefaultAzureCredential)

##### 前提環境
- 環境変数、Managed Identity、Azure CLI、Visual Studio など、複数の認証方法を自動的に試行
- 開発環境や CI/CD パイプラインで推奨される認証モード
- 必要な RBAC ロール: Owner、Contributor、または Container Apps Contributor
- 参考: [DefaultAzureCredential](https://learn.microsoft.com/ja-jp/dotnet/api/azure.identity.defaultazurecredential)

##### 利用方法（事前準備含む）

1. **認証準備**
   - 以下のいずれかの方法で認証情報を準備：
     - 環境変数（`AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`）を設定
     - Azure CLI でログイン（`az login`）
     - Managed Identity を有効化（Azure VM/App Service 上で実行する場合）
     - Visual Studio で Azure アカウントにサインイン

2. **必要な情報の準備**
   - サブスクリプション ID
   - リソースグループ名（未作成でも可）
   - DB パスワード
   - Timetracker アプリパスワード

3. **デプロイ実行**
   ```powershell
   timetracker-cli.exe deploy `
     --subscription "<SUBSCRIPTION_ID>" `
     --resource-group "rg-tt-demo" `
     --location "japaneast" `
     --app-name "timetracker" `
     --db-type "postgresql" `
     --db-password "Str0ngP@ssw0rd!" `
     --tracker-password "AppLoginP@ss!" `
     --auth-mode "default"
   ```

---

#### 認証モード: `azure-cli` (AzureCliCredential)

##### 前提環境
- Azure CLI がインストールされていること
- 事前に `az login` でログイン済みであること
- ローカル開発環境での利用に最適
- 必要な RBAC ロール: Owner、Contributor、または Container Apps Contributor

##### 利用方法（事前準備含む）

1. **Azure CLI のインストールと認証**
   ```bash
   # Azure CLI がインストールされていない場合はインストール
   # Windows: https://learn.microsoft.com/ja-jp/cli/azure/install-azure-cli-windows
   # macOS: brew install azure-cli
   # Linux: https://learn.microsoft.com/ja-jp/cli/azure/install-azure-cli-linux

   # Azure にログイン
   az login

   # サブスクリプションの確認
   az account show
   ```

2. **必要な情報の準備**
   - サブスクリプション ID（`az account show` で確認可能）
   - リソースグループ名
   - DB パスワード
   - Timetracker アプリパスワード

3. **デプロイ実行**
   ```powershell
   timetracker-cli.exe deploy `
     --subscription "<SUBSCRIPTION_ID>" `
     --resource-group "rg-tt-demo" `
     --db-type "postgresql" `
     --db-password "Str0ngP@ssw0rd!" `
     --tracker-password "AppLoginP@ss!" `
     --auth-mode "azure-cli"
   ```

---

#### 認証モード: `sp-env` (ClientSecretCredential / Service Principal)

##### 前提環境
- サービスプリンシパルが作成されていること
- 環境変数 `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET` が設定されていること
- CI/CD パイプライン（GitHub Actions、Azure DevOps など）での利用に最適
- 必要な RBAC ロール: サービスプリンシパルに Owner、Contributor、または Container Apps Contributor ロールを付与
- 参考: [Service Principal](https://learn.microsoft.com/ja-jp/azure/active-directory/develop/app-objects-and-service-principals)

##### 利用方法（事前準備含む）

1. **サービスプリンシパルの作成とロール割り当て**
   ```bash
   # サービスプリンシパルを作成（出力された JSON を保存）
   az ad sp create-for-rbac --name "timetracker-deployer" --role Contributor --scopes /subscriptions/<SUBSCRIPTION_ID>

   # 出力例:
   # {
   #   "appId": "<CLIENT_ID>",
   #   "password": "<CLIENT_SECRET>",
   #   "tenant": "<TENANT_ID>"
   # }
   ```

2. **環境変数の設定**
   ```powershell
   # PowerShell
   $env:AZURE_TENANT_ID = "<TENANT_ID>"
   $env:AZURE_CLIENT_ID = "<CLIENT_ID>"
   $env:AZURE_CLIENT_SECRET = "<CLIENT_SECRET>"
   ```
   
   ```bash
   # Bash
   export AZURE_TENANT_ID="<TENANT_ID>"
   export AZURE_CLIENT_ID="<CLIENT_ID>"
   export AZURE_CLIENT_SECRET="<CLIENT_SECRET>"
   ```

3. **デプロイ実行**
   ```powershell
   timetracker-cli.exe deploy `
     --subscription "<SUBSCRIPTION_ID>" `
     --resource-group "rg-tt-demo" `
     --db-type "postgresql" `
     --db-password "Str0ngP@ssw0rd!" `
     --tracker-password "AppLoginP@ss!" `
     --auth-mode "sp-env"
   ```

---

#### 認証モード: `device-code` (DeviceCodeCredential)

##### 前提環境
- Web ブラウザが利用可能であること
- インタラクティブにログインする必要がある環境での利用に適している
- ヘッドレス環境や SSH 接続での利用に最適
- 必要な RBAC ロール: ログインするユーザーに Owner、Contributor、または Container Apps Contributor ロールを付与

##### 利用方法（事前準備含む）

1. **認証準備**
   - 特別な事前準備は不要
   - Web ブラウザでアクセスできる環境を用意

2. **デプロイ実行**
   ```powershell
   timetracker-cli.exe deploy `
     --subscription "<SUBSCRIPTION_ID>" `
     --resource-group "rg-tt-demo" `
     --db-type "postgresql" `
     --db-password "Str0ngP@ssw0rd!" `
     --tracker-password "AppLoginP@ss!" `
     --auth-mode "device-code"
   ```

3. **デバイスコード認証の手順**
   - コマンド実行後、以下のようなメッセージが表示されます：
     ```
     デバイスコード認証: To sign in, use a web browser to open the page https://microsoft.com/devicelogin and enter the code XXXXXXXXX to authenticate.
     ```
   - Web ブラウザで指定された URL を開き、表示されたコードを入力
   - Azure アカウントでサインイン
   - 認証完了後、CLI が自動的にデプロイを続行

---

#### 認証モード: `managed-identity` (ManagedIdentityCredential)

##### 前提環境
- Azure VM、Azure App Service、Azure Container Instances、または Azure Functions 上で実行
- 実行環境に Managed Identity が有効化されていること
- 本番環境での推奨認証モード（認証情報の管理が不要）
- 必要な RBAC ロール: Managed Identity に Owner、Contributor、または Container Apps Contributor ロールを付与
- 参考: [Managed Identity](https://learn.microsoft.com/ja-jp/azure/active-directory/managed-identities-azure-resources/overview)

##### 利用方法（事前準備含む）

1. **Managed Identity の有効化とロール割り当て**

   **Azure VM の場合:**
   ```bash
   # システム割り当て Managed Identity を有効化
   az vm identity assign --name <VM_NAME> --resource-group <RESOURCE_GROUP>

   # Managed Identity に Contributor ロールを付与
   az role assignment create \
     --assignee <MANAGED_IDENTITY_PRINCIPAL_ID> \
     --role Contributor \
     --scope /subscriptions/<SUBSCRIPTION_ID>
   ```

   **Azure App Service の場合:**
   ```bash
   # システム割り当て Managed Identity を有効化
   az webapp identity assign --name <APP_NAME> --resource-group <RESOURCE_GROUP>

   # Managed Identity に Contributor ロールを付与
   az role assignment create \
     --assignee <MANAGED_IDENTITY_PRINCIPAL_ID> \
     --role Contributor \
     --scope /subscriptions/<SUBSCRIPTION_ID>
   ```

2. **ツールを Azure リソース上にデプロイ**
   - Azure VM、App Service、Container Instances などに `timetracker-cli.exe` を配置

3. **デプロイ実行**
   ```powershell
   # Azure リソース上で実行（環境変数や認証情報の設定は不要）
   timetracker-cli.exe deploy `
     --subscription "<SUBSCRIPTION_ID>" `
     --resource-group "rg-tt-demo" `
     --db-type "postgresql" `
     --db-password "Str0ngP@ssw0rd!" `
     --tracker-password "AppLoginP@ss!" `
     --auth-mode "managed-identity"
   ```

---

### ローカル検証（Dry-run）

#### 前提環境
- Windows 10/11 で Docker Desktop（Linux コンテナモード、WSL2 backend 推奨）  
  または、macOS/Linux で Docker Engine
- Docker Compose（`docker compose` が利用可能）

#### 利用方法
ローカルで Timetracker+DB+Redis を起動・動作確認する場合：

1. **Dry-run でファイル生成**
   ```powershell
   timetracker-cli.exe deploy `
     --subscription "<SUBSCRIPTION_ID>" `
     --resource-group "rg-tt-demo" `
     --db-type "postgresql" `
     --db-password "Str0ngP@ssw0rd!" `
     --tracker-password "AppLoginP@ss!" `
     --tt-tag "7.0-linux-postgres" `
     --dry-run "true"
   ```

2. **Docker Compose で起動**
   ```powershell
   docker compose --file .\docker-compose.yml --env-file .\.env up -d
   docker compose ps
   ```

3. **動作確認**
   - ブラウザで `http://localhost:8080` にアクセス

### コマンドパラメータの説明一覧
- `--subscription` (必須): Azure サブスクリプション ID
- `--resource-group` (必須): リソースグループ名（存在しなければ作成）
- `--location` (任意): リージョン（省略時は `japaneast`）
- `--app-name` (任意): アプリ名（省略時は `timetracker`）。Azure Container Apps の命名規則に従う必要があります（後述の注意事項参照）。
- `--db-type` (任意): DB 種別。`postgresql` または `sqlserver`（デフォルト: `postgresql`）
- `--db-password` (必須): DB パスワード（DB 種別に関わらず統一パラメータ）
- `--db-name` (任意): DB 名。デフォルト `timetracker`
- `--tracker-password` (必須): Timetracker アプリパスワード（DockerHub の `<your password>` に対応）
- `--tt-tag` (任意): timetracker コンテナイメージのタグ（バージョン）。省略時は `7.0-linux-postgres`（例: `7.0-linux-postgres`, `7.0-linux-mssql`）。利用可能なタグ一覧は [DockerHub](https://hub.docker.com/r/densocreate/timetracker/tags) を参照してください。
- `--dry-run` (任意): true の場合、Azure 反映せず `docker-compose.yml` と `.env` の生成のみ
- `--auth-mode` (任意): 認証モード。`default` | `azure-cli` | `sp-env` | `device-code` | `managed-identity`（デフォルト: `default`）
- コンテナスペック（任意）:
  - Timetracker: `--tt-cpu`（vCPU、小数可。例: `0.75`）、`--tt-memory`（Gi。例: `1.5`）
  - DB: `--db-cpu`、`--db-memory`
  - Redis: `--redis-cpu`、`--redis-memory`
  - 省略時のデフォルト: Timetracker CPU=0.75 / Mem=1.5Gi、DB CPU=1.0 / Mem=2.0Gi、Redis CPU=0.5 / Mem=1.0Gi

補足:
- DB ユーザは DockerHub の「クイックスタート」記載の固定値を使用します（CLI からは変更不可）。
- `.env` に機密情報が出力されます。取り扱いにはご注意ください。
- **timetracker イメージタグについて**: DB 種別に応じて適切なタグを選択してください。PostgreSQL 用のタグ (例: `7.0-linux-postgres`) と SQL Server 用のタグ (例: `7.0-linux-mssql`) があります。異なる DB 種別用のタグを混在させないでください。利用可能なタグ一覧は [DockerHub](https://hub.docker.com/r/densocreate/timetracker/tags) を参照してください。
- **アプリ名の命名規則**: `--app-name` で指定するアプリ名は Azure Container Apps の命名規則に従う必要があります:
  - 使用可能な文字: 英小文字 (a-z)、数字 (0-9)、ハイフン (-)
  - 英小文字で始まる必要があります
  - 英小文字または数字で終わる必要があります
  - 連続するハイフン (`--`) は使用できません
  - 長さは2〜32文字である必要があります
  - 大文字を指定した場合は自動的に小文字に変換されます

### コンテナスペックのカスタマイズ例

各コンテナの CPU とメモリを個別に指定できます：

```powershell
timetracker-cli.exe deploy `
  --subscription "<SUBSCRIPTION_ID>" `
  --resource-group "rg-tt-demo" `
  --location "japaneast" `
  --app-name "timetracker" `
  --db-type "postgresql" `
  --db-password "Str0ngP@ssw0rd!" `
  --db-name "timetracker" `
  --tracker-password "AppLoginP@ss!" `
  --tt-tag "7.0-linux-postgres" `
  --tt-cpu 0.75 --tt-memory 1.5 `
  --db-cpu 1.0 --db-memory 2.0 `
  --redis-cpu 0.5 --redis-memory 1.0 `
  --auth-mode "azure-cli"
```

### FAQ

#### デプロイ全般

- **Q: クラウドデプロイだけなら Docker は必要ですか？**  
  A: いいえ。Azure Container Apps へのデプロイのみを行う場合、利用者側で Docker をインストールする必要はありません。

- **Q: ローカル検証を行うには何が必要ですか？**  
  A: Windows では Docker Desktop、macOS/Linux では Docker Engine と Docker Compose が必要です。CLI の Dry-run でファイルを生成し、`docker compose up -d` で起動して確認してください。

- **Q: Azure Database for PostgreSQL や Azure SQL Database は作成されますか？**  
  A: いいえ。本ツールは DB を **コンテナ** として Container Apps にデプロイします。PaaS データベースリソースは作成されません。

- **Q: データの永続化はどうなりますか？**  
  A: デフォルトではコンテナのエフェメラルストレージを使用します。データの永続化が必要な場合は、Azure Files などの永続ボリュームを別途設定する必要があります。

#### リソースとスケーリング

- **Q: スケール設定はできますか？**  
  A: 本ツールではスケール構成は行いません。各コンテナとも 1 台固定です。

- **Q: コンテナのCPU/メモリは後から変更できますか？**  
  A: 再実行時にオプションを変更すれば Container Apps 上の設定が更新されます。

- **Q: Log Analytics ワークスペースは作成されますか？**  
  A: 本ツールは明示的に作成しません。ただし、Container Apps Environment が必要に応じて自動作成する場合があります。

#### 認証とアクセス

- **Q: どの認証モードを使えばよいですか？**  
  A: 
  - ローカル開発: `azure-cli`（`az login` 後）
  - CI/CD パイプライン: `sp-env`（サービスプリンシパル）
  - Azure VM/App Service 上: `managed-identity`
  - 自動選択: `default`（推奨）

- **Q: 認証エラーが発生します**  
  A: 
  1. 適切な RBAC ロール（Owner、Contributor、Container Apps Contributor）が付与されているか確認
  2. `--auth-mode` を明示的に指定してみる
  3. `--verbose` オプションで詳細ログを確認
  4. リソースプロバイダー（Microsoft.App, Microsoft.OperationalInsights）が登録されているか確認

- **Q: どの認証方式が使われているか確認するには？**  
  A: `--verbose` オプションを付けて実行すると、使用される認証モードがログに出力されます。

#### その他

- **Q: App Service を使ったデプロイは可能ですか？**  
  A: 本ツールは Azure Container Apps を対象とした専用ツールです。App Service（マルチコンテナ／Compose）への直接デプロイ機能は含んでいません。App Service 化が必要な場合は別途テンプレート化を検討してください。

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
```
注意: Timetracker.Domain はクラスライブラリ（実行可能ファイルを生成しない）であり、SelfContained/SingleFile オプション付きの publish 対象ではありません。必要な成果物は CLI プロジェクトの publish で生成される単一ファイル EXE のみです。ライブラリ側で配布形態が必要になった場合は `dotnet pack` による NuGet パッケージ化等を検討してください。

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
- timetracker のイメージタグは `--tt-tag` で指定した値を使用（省略時は `7.0-linux-postgres`）

### 注意点
- Container Apps の永続ボリュームが必要な場合は Azure Files 等の設定を追加してください（DB/Redis のデータ保持用途）。
- 認証は Managed Identity もしくは Service Principal を推奨（DefaultAzureCredential 利用）。
- DockerHub「クイックスタート」記載の固定 DB ユーザ名は Domain 層の Defaults に集約しています（必要に応じて正確値に更新）。

### FAQ
- Q: 単一ファイル EXE の起動が遅い  
  A: 初回起動時に一時展開が発生するためです。Self-contained を無効化（ランタイム依存）するか、ReadyToRun などの最適化を検討してください。

- Q: Container Apps デプロイが失敗します  
  A: 認証（Service Principal/Managed Identity）設定、ネットワーク到達性、コンテナイメージ取得可否、環境変数不足を確認してください。必要に応じて`--verbose`で詳細ログを取得してください。
