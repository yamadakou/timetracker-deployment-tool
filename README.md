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
- スケール構成は不可（各コンテナとも 1 台）
- 本ツールでデプロイしたリソースの削除（`delete` コマンド。リソースグループは削除しません）
- MCP サーバー（`timetracker-mcp`）による、AI アシスタントからの同じ操作（[MCP サーバーの利用者向けマニュアル](docs/MCP_SERVER.md)）

将来の GUI 化（デスクトップ/ウェブ）に備え、ドメイン層（Timetracker.Domain）とコントローラー層（Timetracker.Controller.Cli）を分離しています。

---

## 本ツールの目的と注意事項

> [!IMPORTANT]
> 本ツールは、Timetracker の **検証環境（評価・動作確認・デモ用途）を手早く構築するための支援ツール** です。本番環境の構築は対象外です。本番運用に必要な構成（データの永続化、バックアップ、冗長化、ネットワーク設計、ライセンスに合った DB エディションの選択など）は、本ツールでは行いません。

検証用途に割り切った設計のため、以下の点にご注意ください。

| 項目 | 内容 |
|---|---|
| Azure 上の DB データは永続化されない | Azure Container Apps では、DB はボリュームを持たないコンテナとして動作します。DB コンテナが再起動すると（ツールの再実行による設定変更、リビジョン更新、Azure 基盤のメンテナンス等）、**DB のデータは失われます**。既存の DB アプリがある状態で再実行すると、ツールは警告を表示します。残したいデータは事前にエクスポートしてください。なお、Dry-run で生成する docker-compose（ローカル検証）は名前付きボリュームを使用するため、コンテナを再起動してもデータは保持されます（`docker compose down -v` でボリュームごと削除されます）。 |
| SQL Server は Developer エディション | `--db-type sqlserver` の場合、SQL Server は `MSSQL_PID=Developer` で起動します。Developer エディションは開発・テスト用途に限られ、本番環境では使用できません。 |
| SQL Server ＋ Azure ではサンプル DB を作成できない | サンプル DB を利用する場合は、PostgreSQL（`--db-type` の既定値）を使用してください。SQL Server を使用する場合は、セットアップウィザードで「空のデータベースを作成する」を選択して評価してください（SQL Server の復元処理は DB コンテナ側のファイルを読むため、Azure Container Apps ではアプリコンテナ内のサンプル DB を参照できません。なお、Dry-run で生成する docker-compose によるローカル検証では、SQL Server でもサンプル DB を作成できます）。 |
| スケールは 1 台固定 | 各コンテナとも 1 台で動作し、冗長化・スケールアウトは行いません。 |
| 機密情報の取り扱い | Dry-run で生成する `.env` には DB パスワード等が平文で出力されます。共有やリポジトリへのコミットは避けてください。Azure 上ではパスワードを Container Apps の secret として登録します。 |
| 不要になった環境の削除 | 検証が終わったら、リソースグループごと削除すると課金を止められます（例: `az group delete --name <リソースグループ名>`）。既存のリソースグループを指定した場合は、他のリソースを巻き込まないようご注意ください。 |

---

## 利用者向けマニュアル

### 作成される Azure リソース

本ツールは以下の Azure リソースを作成します：

1. **リソースグループ** (Resource Group)
   - 指定した名前のリソースグループが存在しない場合、新規作成されます
   - 既に存在する場合は、そのリソースグループを変更せずに使用します（タグ等は保持されます）

2. **Container Apps Environment** (Container Apps 環境)
   - すべての Container Apps が実行される環境
   - 名前: `{アプリ名}-env`（例: `timetracker-env`）
   - 既に存在する場合は、設定を変更せずにそのまま使用します

3. **3つの Container Apps**
   - **`{アプリ名}-tt`**: Timetracker アプリケーション本体（外部公開、ポート 8080）
   - **`{アプリ名}-db`**: データベース（PostgreSQL または SQL Server、内部専用、TCP接続）
   - **`{アプリ名}-redis`**: Redis キャッシュ（内部専用、TCP接続）

**管理タグ**:

本ツールは、作成したリソースに次のタグを付けます。[削除](#リソースの削除delete)の際は、名前が一致し、かつこのタグが付いているリソースだけを削除します（同じリソースグループに同じ名前の別のリソースがあっても、誤って削除しないため）。

| タグのキー | 値 |
|---|---|
| `managed-by` | `timetracker-deployment-tool` |
| `tt-app-name` | アプリ名（`--app-name`） |

- 3 つの Container Apps には、作成時・更新時とも付けます。更新時は、利用者が付けた他のタグを残したまま管理タグを追加します
- Container Apps Environment には、新規作成時だけ付けます（既存の Environment は変更しないため）
- タグは誤って削除しないための目印で、セキュリティ上の制限ではありません（削除できるかどうかは Azure の RBAC で決まります）
- 本ツールで作成したリソースは、タグで一覧できます（`az resource list` では `--tag` と `--resource-group` を同時に指定できないため、リソースグループは `--query` で絞り込みます）: `az resource list --tag managed-by=timetracker-deployment-tool --query "[?resourceGroup=='<RG名>'].{name:name, type:type}" -o table`

**重要な注意事項**:
- DB および Redis は **コンテナ** として Container Apps 内で実行されます
- Azure Database for PostgreSQL、Azure SQL Database などの PaaS データベースリソースは作成されません
- Log Analytics ワークスペースは自動作成されません（Container Apps Environment が必要に応じて作成する場合があります）
- Azure 上の DB のデータは永続化されません（[本ツールの目的と注意事項](#本ツールの目的と注意事項) を参照）

### 必要な RBAC ロールと権限

本ツールを実行するユーザー、サービスプリンシパル、またはマネージド ID には、次のいずれかのロール割り当てが必要です。

| ロール（割り当てるスコープ） | 本ツールでできること | 推奨用途 |
|--------|------|----------|
| **Contributor**（サブスクリプション） | すべての操作。リソースグループが存在しなければ作成する。[リソースプロバイダーの登録](#azure-リソースプロバイダーの登録)（事前準備）も自分で行える | 通常利用での推奨 |
| **Contributor**（リソースグループ） | 事前に作成したリソースグループへのデプロイと削除。リソースグループの作成はできない。リソースプロバイダーの登録（事前準備）もできないため管理者に依頼する | 権限を絞りたい場合の推奨 |
| **Owner**（サブスクリプションまたはリソースグループ） | Contributor と同じ（加えてロール割り当ての管理も可能だが、本ツールでは不要） | Contributor を使えない場合 |

> [!WARNING]
> **Container Apps Contributor（Container Apps 共同作成者）だけでは本ツールを使えません。** このロールには、本ツールが使う次の権限が含まれていないためです。
> - `Microsoft.Resources/subscriptions/resourceGroups/read` / `write`（リソースグループの存在確認・作成。本ツールは最初にこの確認を行います）
> - `Microsoft.App/managedEnvironments/write` / `delete`（Container Apps Environment の作成・削除）

本ツールが行う操作と必要な権限:

| 操作 | 必要な権限（Action） | 必要なスコープ |
|---|---|---|
| リソースグループの存在確認 | `Microsoft.Resources/subscriptions/resourceGroups/read` | リソースグループ |
| リソースグループの作成（存在しない場合） | `Microsoft.Resources/subscriptions/resourceGroups/write` | サブスクリプション |
| Container Apps Environment の確認・作成 | `Microsoft.App/managedEnvironments/read` / `write` | リソースグループ |
| Container App（DB / Redis / Timetracker）の確認・作成・更新 | `Microsoft.App/containerApps/read` / `write`、`Microsoft.App/managedEnvironments/join/action` | リソースグループ |
| Container App の削除（`delete`） | `Microsoft.App/containerApps/delete` | リソースグループ |
| Container Apps Environment の削除（`delete`） | `Microsoft.App/managedEnvironments/delete` | リソースグループ |
| 権限の事前確認（下記） | `Microsoft.Authorization/permissions/read`（自分の権限の一覧。通常どのロールでも可） | リソースグループ |

**権限の事前確認**: `deploy` と `delete` は、Azure のリソースを変更する前に、上の表の権限が実行する ID にあるかを確認します。不足していれば、不足している権限を表示して何も変更せずに終了します（途中で権限エラーになり、一部のリソースだけが作成・削除された状態になるのを防ぐため）。既存のリソースの更新・削除は、リソースグループでの権限に加えて、そのリソースに割り当てられたロールでも判定します（例: リソースグループでは閲覧者で、各 Container App と Environment に共同作成者が割り当てられている場合）。新しく作成するリソースには、リソースグループでの権限が必要です。次の場合は確認をせずに続行します（`--verbose` で理由を表示します）。
- リソースグループがまだ存在しない場合（権限の一覧はリソースグループ単位でしか取得できないため。作成する権限が無ければ最初の手順で失敗し、リソースは作成されません）
- 権限の一覧を取得できなかった場合

なお、この確認には拒否割り当てや Azure Policy による拒否は反映されません。これらで拒否された場合は、実行時のエラーとして表示されます。

本ツールはリソースプロバイダーの登録を**行いません**。[次節](#azure-リソースプロバイダーの登録)の Azure CLI コマンドで、利用者または管理者が事前に登録してください。登録には `*/register/action`（例: `Microsoft.App/register/action`）の権限がサブスクリプションのスコープで必要です。

リソースグループのスコープで Contributor を割り当てる場合は、次の作業をサブスクリプションの管理者に事前に依頼してください。
- リソースグループの作成（`az group create --name <RG名> --location japaneast`）
- リソースプロバイダーの登録（[次節](#azure-リソースプロバイダーの登録)）

ロール割り当ての例（リソースグループのスコープ）:
```bash
az role assignment create \
  --assignee <ユーザー / サービスプリンシパル / マネージド ID のオブジェクト ID> \
  --role Contributor \
  --scope /subscriptions/<SUBSCRIPTION_ID>/resourceGroups/<RG名>
```

**参考リンク**:
- [Azure RBAC の概要](https://learn.microsoft.com/ja-jp/azure/role-based-access-control/overview)
- [Azure 組み込みロール](https://learn.microsoft.com/ja-jp/azure/role-based-access-control/built-in-roles)
- [Container Apps 共同作成者ロール](https://learn.microsoft.com/ja-jp/azure/role-based-access-control/built-in-roles#container-apps-contributor)（本ツールには権限が不足します）

### Azure リソースプロバイダーの登録

Container Apps を初めて使用する場合、以下のリソースプロバイダーをサブスクリプションに登録する必要があります。本ツールは登録を行わないため、ツールを実行する前に以下のコマンドで登録してください。登録にはサブスクリプションのスコープでの権限（Contributor または Owner）が必要です。リソースグループのスコープの権限しかない場合は、サブスクリプションの管理者に登録を依頼してください。

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
> [!IMPORTANT]
> 現在は `azure-cli` でのみ動作確認しています。

---

#### 認証モード: `default` (DefaultAzureCredential)

##### 前提環境
- 環境変数、Managed Identity、Azure CLI、Visual Studio など、複数の認証方法を自動的に試行
- 開発環境や CI/CD パイプラインで推奨される認証モード
- 必要な RBAC ロール: [必要な RBAC ロールと権限](#必要な-rbac-ロールと権限) を参照
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
- 必要な RBAC ロール: [必要な RBAC ロールと権限](#必要な-rbac-ロールと権限) を参照

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
- 環境変数 `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET` が設定されていること（いずれかが未設定の場合はエラーで停止します）
- CI/CD パイプライン（GitHub Actions、Azure DevOps など）での利用に最適
- 必要な RBAC ロール: サービスプリンシパルに [必要な RBAC ロールと権限](#必要な-rbac-ロールと権限) のロールを付与
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
- 必要な RBAC ロール: ログインするユーザーに [必要な RBAC ロールと権限](#必要な-rbac-ロールと権限) のロールを付与

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
- Azure VM、Azure App Service、Azure Container Instances、Azure Container Apps、または Azure Functions 上で実行
- 実行環境に Managed Identity が有効化されていること（環境変数 `AZURE_CLIENT_ID` があればそのクライアント ID のユーザー割り当て Managed Identity、無ければシステム割り当て Managed Identity を使います）
- Azure 上で実行する場合の推奨認証モード（認証情報の管理が不要）
- 必要な RBAC ロール: Managed Identity に [必要な RBAC ロールと権限](#必要な-rbac-ロールと権限) のロールを付与
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

1. **Dry-run でファイル生成**（Azure を使わないため `--subscription` / `--resource-group` は不要です）
   ```powershell
   timetracker-cli.exe deploy `
     --db-type "postgresql" `
     --db-password "Str0ngP@ssw0rd!" `
     --tracker-password "AppLoginP@ss!" `
     --tt-tag "11.0.1-linux-postgres" `
     --dry-run "true"
   ```

2. **Docker Compose で起動**
   ```powershell
   docker compose --file .\docker-compose.yml --env-file .\.env up -d
   docker compose ps
   ```

3. **動作確認**
   - ブラウザで `http://localhost:8080` にアクセスし、セットアップウィザードでライセンスキーを入力して DB を作成します（「空のデータベース」または「サンプルデータベース」）
   - DB（PostgreSQL のデータベース）はセットアップウィザードが作成します。ツールは事前に作成しません
   - SQL Server でサンプルデータベースを作成する場合、SQL Server がアプリイメージ内のバックアップファイルを読めるよう、生成される compose では共有ボリューム `ttsample` を両コンテナにマウントしています

### リソースの削除（delete）
`deploy` で作成したリソースを削除します。**リソースグループは削除しません**（他のリソースを巻き込まないため。不要であればポータルや `az group delete` で削除してください）。

1. **削除計画の確認**（何も変更しません）
   ```powershell
   timetracker-cli.exe delete `
     --subscription "<SUBSCRIPTION_ID>" `
     --resource-group "rg-tt-demo" `
     --app-name "timetracker" `
     --dry-run
   ```
   出力例:
   ```
   [INFO] 削除計画（リソースグループ rg-tt-demo は削除しません）
   [INFO]   削除する     timetracker-tt (Container App)
   [INFO]   削除する     timetracker-redis (Container App)
   [INFO]   削除する     timetracker-db (Container App)
   [INFO]   削除する     timetracker-env (Environment)
   [WARN] DB アプリを削除すると DB のデータはすべて失われます。
   [WARN] Dry-run のため削除は行いません。
   ```

2. **削除**（確認のため、アプリ名の入力を求めます。`--yes` で省略できます）
   ```powershell
   timetracker-cli.exe delete --subscription "<SUBSCRIPTION_ID>" --resource-group "rg-tt-demo" --app-name "timetracker"
   ```
   `{アプリ名}-tt` → `-redis` → `-db` → `-env` の順に削除します。途中で失敗した場合は、もう一度実行すると残りを削除します（既に削除済みのものは飛ばします）。
   Container Apps Environment の削除は、完了まで十数分かかることがあります。

削除の対象になる条件:
- 名前が `{アプリ名}-tt` / `-redis` / `-db` / `-env` で、かつ[管理タグ](#作成される-azure-リソース)が付いているもの
- Environment は、その中に削除しない Container App（管理タグの無いもの、本ツール以外で作成したものなど）が残る場合は削除しません

管理タグの無いリソース（管理タグを付けるようになる前のバージョンで作成したものなど）は、既定では削除しません。次のどちらかで対応してください。
- 削除計画で対象を確認したうえで、`--include-untagged` を付けて削除する
- 先に Azure CLI で管理タグを付けてから削除する
  ```bash
  az tag update --resource-id <リソース ID> --operation merge \
    --tags managed-by=timetracker-deployment-tool tt-app-name=<アプリ名>
  ```

逆に、特定の環境を削除から守りたい場合は、ポータル等で `managed-by` タグを外してください。

`delete` のオプション:
- `--subscription` (必須): Azure サブスクリプション ID
- `--resource-group` (必須): リソースグループ名
- `--app-name` (任意): アプリ名（省略時は `timetracker`）
- `--auth-mode` (任意): 認証モード（`deploy` と同じ）
- `--include-untagged` (任意): 管理タグの無いリソースも削除の対象にする（別のアプリ名や別のツールの管理タグ（例: `managed-by=terraform`）が付いているものは、指定しても削除しません）
- `--dry-run` (任意): 削除計画を表示するだけで、削除しない
- `--yes` (任意): 確認の入力（アプリ名）を省略する
- `--verbose` (任意): 詳細ログを出力

`deploy` 専用のオプション（`--db-password` など）を `delete` に指定した場合、また `delete` 専用のオプションを `deploy` に指定した場合はエラーになります。

### MCP サーバー（AI アシスタントから使う）

CLI と同じ機能を、MCP（Model Context Protocol）に対応した AI アシスタント（Claude Code、VS Code の GitHub Copilot、Claude Desktop など）から、対話しながら使えます。現在は利用者の PC で動く stdio 版を提供しています（チームで共有する HTTP 版は開発中）。導入・アシスタントの設定・使い方は [MCP サーバーの利用者向けマニュアル（docs/MCP_SERVER.md）](docs/MCP_SERVER.md) を参照してください。

### コマンドパラメータの説明一覧
以下は `deploy`（サブコマンドを省略した場合も `deploy`）のオプションです。`delete` のオプションは[リソースの削除（delete）](#リソースの削除delete)を参照してください。

- `--subscription` (必須。`--dry-run` 時は不要): Azure サブスクリプション ID
- `--resource-group` (必須。`--dry-run` 時は不要): リソースグループ名（存在しなければ作成）
- `--location` (任意): リージョン（省略時は `japaneast`）
- `--app-name` (任意): アプリ名（省略時は `timetracker`）。Azure Container Apps の命名規則に従う必要があります（後述の注意事項参照）。
- `--db-type` (任意): DB 種別。`postgresql` または `sqlserver`（デフォルト: `postgresql`）
  - サンプル DB を利用する場合は `postgresql`（既定値）を使用してください。`sqlserver` の場合は Developer エディションで起動し、Azure 上では空のデータベースを作成して評価します（[本ツールの目的と注意事項](#本ツールの目的と注意事項) を参照）
- `--db-password` (必須): DB パスワード（DB 種別に関わらず統一パラメータ。8 文字以上。`sqlserver` の場合は英大文字・英小文字・数字・記号のうち 3 種類以上を含む必要があります）
- `--db-name` (任意): DB 名。デフォルト `timetracker`
- `--tracker-password` (必須): Timetracker アプリパスワード（DockerHub の `<your password>` に対応）
- `--tt-tag` (任意): timetracker コンテナイメージのタグ（バージョン）。省略時は DB 種別に応じて `11.0.1-linux-postgres`（postgresql）または `11.0.1-linux-mssql`（sqlserver）。利用可能なタグ一覧は [DockerHub](https://hub.docker.com/r/densocreate/timetracker/tags) を参照してください。
- `--dry-run` (任意): 指定した場合（または `--dry-run true`）、Azure 反映せず `docker-compose.yml` と `.env` の生成のみ。`--subscription` / `--resource-group` は不要。`--dry-run false` で無効
- `--verbose` (任意): 詳細ログを出力
- `--help` / `-h`: 使い方を表示

補足（引数の書式）:
- 未知のオプションや値の無いオプションはエラーになります（タイプミス防止）
- `--` で始まる値（例: パスワード）は `--db-password=--xxxx` のように `=` で指定してください
- `--auth-mode` (任意): 認証モード。`default` | `azure-cli` | `sp-env` | `device-code` | `managed-identity`（デフォルト: `default`）
- コンテナスペック（任意）:
  - Timetracker: `--tt-cpu`（vCPU、小数可。例: `1.5`）、`--tt-memory`（Gi。例: `3.0`）
  - DB: `--db-cpu`、`--db-memory`
  - Redis: `--redis-cpu`、`--redis-memory`
  - 省略時のデフォルト: Timetracker CPU=1.5 / Mem=3.0Gi、DB CPU=1.0 / Mem=2.0Gi、Redis CPU=0.5 / Mem=1.0Gi
  - Timetracker のメモリを 2Gi 以下にすると、Azure 上でセットアップウィザードの「サンプルデータベースを作成する」がメモリ不足（`OutOfMemoryException`）で失敗します。サンプル DB を利用する場合は既定値（1.5 / 3.0Gi）以上を指定してください
  - vCPUとメモリの組み合わせとなりますので、指定値は以下のＭＳドキュメントを参考にしてください。
    - https://learn.microsoft.com/ja-jp/azure/container-apps/containers#allocations


補足:
- DB ユーザは DockerHub の「クイックスタート」記載の固定値を使用します（CLI からは変更不可）。
- `.env` に機密情報が出力されます。取り扱いにはご注意ください（[本ツールの目的と注意事項](#本ツールの目的と注意事項) を参照）。
- **timetracker イメージタグについて**: DB 種別に応じて適切なタグを選択してください。PostgreSQL 用のタグ (例: `11.0.1-linux-postgres`) と SQL Server 用のタグ (例: `11.0.1-linux-mssql`) があります。異なる DB 種別用のタグを混在させないでください。利用可能なタグ一覧は [DockerHub](https://hub.docker.com/r/densocreate/timetracker/tags) を参照してください。
- **アプリ名の命名規則**: `--app-name` で指定するアプリ名は Azure Container Apps の命名規則に従う必要があります:
  - 使用可能な文字: 英小文字 (a-z)、数字 (0-9)、ハイフン (-)
  - 英小文字で始まる必要があります
  - 英小文字または数字で終わる必要があります
  - 連続するハイフン (`--`) は使用できません
  - 長さは2〜26文字である必要があります（Container App 名の上限は 32 文字ですが、`{アプリ名}-redis` などの接尾辞が付くため）
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
  --tt-tag "11.0.1-linux-postgres" `
  --tt-cpu 1.5 --tt-memory 3.0 `
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
  A: Azure 上では永続化されません。本ツールは検証環境の構築が目的のため、Azure Container Apps の DB はコンテナのエフェメラルストレージで動作し、コンテナの再起動でデータが失われます。Dry-run で生成する docker-compose（ローカル検証）は名前付きボリュームを使用するため、コンテナを再起動してもデータは保持されます。

#### リソースとスケーリング

- **Q: スケール設定はできますか？**  
  A: 本ツールではスケール構成は行いません。各コンテナとも 1 台固定です。

- **Q: コンテナのCPU/メモリは後から変更できますか？**  
  A: 再実行時にオプションを変更すれば Container Apps 上の設定が更新されます。  
  > [!WARNING]
  > DB アプリ（`{アプリ名}-db`）の設定が変わると DB コンテナが再起動し、**DB のデータは失われます**。既存の DB アプリがある状態で再実行すると、ツールは警告を表示します。

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
  1. 適切な RBAC ロール（Contributor または Owner。[必要な RBAC ロールと権限](#必要な-rbac-ロールと権限) を参照）が付与されているか確認。Container Apps Contributor だけでは権限が不足します
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
- .NET 10 SDK
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
注意: Timetracker.Domain / Timetracker.Application / Timetracker.Infrastructure.Azure / Timetracker.Common はクラスライブラリ（実行可能ファイルを生成しない）であり、SelfContained/SingleFile オプション付きの publish 対象ではありません。必要な成果物は CLI プロジェクトの publish で生成される単一ファイル EXE のみです。ライブラリ側で配布形態が必要になった場合は `dotnet pack` による NuGet パッケージ化等を検討してください。

### テスト
- 単体テスト: `dotnet test Timetracker.sln`（Azure には接続しません）
- システムテスト（本物の Azure に対するテスト。料金が発生します）: 準備と実行方法は [docs/SYSTEM_TESTS.md](docs/SYSTEM_TESTS.md) を参照。ローカルでは `scripts/run-system-tests.ps1`（または `.sh`）、CI では GitHub Actions の **System Test** ワークフローを手動で実行します
- セットアップウィザードとログインの確認（ブラウザーを使う E2E テスト。Playwright）: Timetracker のライセンスキーとサンプルデータベースのユーザー（`TT_E2E_LICENSE_KEY` / `TT_E2E_SAMPLE_LOGIN` / `TT_E2E_SAMPLE_PASSWORD`）がある場合に、サンプルデータベースの作成と、そのユーザーでのログインまでを確かめます。ローカルの docker compose（PostgreSQL / SQL Server）では PR の CI（ubuntu）で毎回、Azure ではシステムテストの中で実行します（[docs/SYSTEM_TESTS.md](docs/SYSTEM_TESTS.md)）

### 設計概要
- ドメイン層（Timetracker.Domain）
  - パラメータモデル（DeployOptions）、既定値（Defaults）と既定値の適用（DeployOptionsResolver）、検証（DeploymentValidator。すべてのエラーを列挙する）と警告（DeploymentAdvisor）、リソース名の規則（ResourceNaming）、管理タグ（ManagedTags）、権限の判定（PermissionEvaluator）、Compose/.env生成（Dry-run用）
- アプリケーション層（Timetracker.Application）
  - デプロイ手順の実行（DeploymentRunner: リソースグループ → Environment → DB / Redis / Timetracker）と結果（DeploymentResult）
  - 削除計画の作成と削除の実行（DeletionPlanner / DeletionRunner）、権限の事前確認（PermissionChecker / DeploymentPlanner）
- インフラストラクチャ層（Timetracker.Infrastructure.Azure）
  - Azure SDK 実行（AzureSdkExecutor / IAzureDeploymentGateway）、Container Apps のコンテナ仕様生成（ContainerAppSpecBuilder）、認証（CredentialFactory）
- コントローラー層（Timetracker.Controller.Cli）
  - CLIの入出力、オプション解析（CliArgs）
- コントローラー層（Timetracker.Controller.Mcp）
  - MCP サーバー（`timetracker-mcp`、stdio）。ツール・Resources・Prompts と、計画 → 確認 → 実行の流れは[MCP サーバーの利用者向けマニュアル](docs/MCP_SERVER.md)を参照してください（設計は [docs/MCP_TOOL_DESIGN.md](docs/MCP_TOOL_DESIGN.md)、[docs/design/mcp-phase3a.md](docs/design/mcp-phase3a.md)、[docs/design/mcp-phase3b.md](docs/design/mcp-phase3b.md)、[docs/design/mcp-phase3c.md](docs/design/mcp-phase3c.md)）
- 共通（Timetracker.Common）
  - ロガー（IDeploymentLogger / SimpleLogger）などのユーティリティ

依存の向きは「コントローラー層 → アプリケーション層 → インフラストラクチャ層 → ドメイン層」です。CLI 以外のフロントエンド（MCP サーバーなど）を追加する場合も、アプリケーション層以下を共有します。

生成する Container Apps の構成は常に以下の3コンテナを含みます。
- `timetracker`（`densocreate/timetracker` イメージ）
- `db`（`postgres:16` または `mcr.microsoft.com/mssql/server:2022-latest`）
- `redis`（`redis:7`）
- Dry-run で生成する `docker-compose.yml` も同じイメージ・環境変数を使用します
- Ingress: 外部公開、ターゲットポート 8080
- スケール: MinReplicas=1 / MaxReplicas=1（固定）
- timetracker のイメージタグは `--tt-tag` で指定した値を使用（省略時は DB 種別に応じて `11.0.1-linux-postgres` / `11.0.1-linux-mssql`）

### 将来の検討
今後の機能の候補（DB データの永続化オプション、ストア配布できるデスクトップアプリなど）と優先度は [docs/ROADMAP.md](docs/ROADMAP.md) にまとめています。

### 注意点
- 本ツールは検証環境の構築支援が目的のため、Azure Container Apps の DB/Redis には永続ボリュームを設定していません（Dry-run の docker-compose は名前付きボリュームを使用）（[本ツールの目的と注意事項](#本ツールの目的と注意事項) を参照）。
- DB パスワード・Timetracker アプリパスワードは Container Apps の secret（`db-password` / `app-password`）として登録し、環境変数からは `secretRef` で参照します（ポータル上の環境変数一覧に平文で表示されません）。
- 認証は Managed Identity もしくは Service Principal を推奨（DefaultAzureCredential 利用）。
- DockerHub「クイックスタート」記載の固定 DB ユーザ名は Domain 層の Defaults に集約しています（必要に応じて正確値に更新）。

### FAQ
- Q: 単一ファイル EXE の起動が遅い  
  A: 初回起動時に一時展開が発生するためです。Self-contained を無効化（ランタイム依存）するか、ReadyToRun などの最適化を検討してください。

- Q: Container Apps デプロイが失敗します  
  A: 認証（Service Principal/Managed Identity）設定、ネットワーク到達性、コンテナイメージ取得可否、環境変数不足を確認してください。必要に応じて`--verbose`で詳細ログを取得してください。
