<a href="https://github.com/yamadakou/timetracker-deployment-tool/actions/workflows/ci.yml"><img src="https://github.com/yamadakou/timetracker-deployment-tool/actions/workflows/ci.yml/badge.svg"></a>

# TTDeployTool

## ツールの概要
[TimeTracker](https://www.timetracker.jp/)（Docker Hub の [`densocreate/timetracker`](https://hub.docker.com/r/densocreate/timetracker) イメージ）を、[Azure Container Apps](https://learn.microsoft.com/ja-jp/azure/container-apps/overview) の上に、DB（PostgreSQL または SQL Server）と Redis と一緒に、コマンド 1 つで用意するツールです。Timetracker の検証環境（評価・動作確認・デモ）を手早く作ることを目的としています。

TimeTracker のコンテナイメージの詳細（クイックスタート、タグの一覧など）は、[Docker Hub の TimeTracker のページ](https://hub.docker.com/r/densocreate/timetracker)を参照してください。

できること:
- Azure へのデプロイ（`deploy`）: Timetracker・DB・Redis の 3 つのコンテナを、Azure Container Apps のアプリとして作成・更新します。リソースグループが無ければ作成します
- 作ったリソースの削除（`delete`）: 本ツールで作ったものだけを削除します（リソースグループは、`--delete-resource-group` を付けた場合だけ、中が空になれば削除します）
- ローカルでの検証（Dry-run）: Azure を使わずに、Docker Compose で起動するための `docker-compose.yml` と `.env` を作ります（[Docker Hub の TimeTracker のページ](https://hub.docker.com/r/densocreate/timetracker)の「クイックスタート」に沿った構成）
- 設定の指定: Timetracker のイメージのバージョン（タグ）と、コンテナごとの CPU・メモリを指定できます。台数は 1 台に固定です
- DB のデータの永続化（`--persist-db`）: 既定では、DB のデータは DB コンテナの再起動で消えます。指定すると、Azure Files に置いて残します（[DB のデータの扱い](#db-のデータの扱いと永続化--persist-db)）
- 利用可能期限（`--expires-on`）: デプロイに期限日を記録します（省略すると今日から 32 日後。再デプロイは今の期限を引き継ぎます）。期限を過ぎると、デスクトップアプリが自動で削除します（CLI と MCP サーバーは削除しません）
- AI アシスタントからの操作: MCP サーバー（`ttdeploytool-mcp`）で、同じ操作を AI アシスタントとの対話で行えます（[MCP サーバーの利用者向けマニュアル](docs/MCP_SERVER.md)）
- 画面での操作（開発中）: Windows のデスクトップアプリで、同じ操作を画面から行えるようにします（[デスクトップアプリ（開発中）](#デスクトップアプリ開発中)）

> [!NOTE]
> DB と Redis も **コンテナ** として Container Apps の中で動かします。Azure Database for PostgreSQL や Azure SQL Database などの PaaS の DB、Azure Cache for Redis は作りません。

---

## 本ツールの目的と注意事項

> [!IMPORTANT]
> 本ツールは、Timetracker の **検証環境（評価・動作確認・デモ）を手早く作るための支援ツール** です。本番環境の構築は対象外です。本番の運用に必要な構成（バックアップ、冗長化、ネットワークの設計、ライセンスに合った DB のエディションの選択など）は行いません。

検証用に割り切った作りのため、次の点にご注意ください。

| 項目 | 内容 |
|---|---|
| DB のデータは、既定では DB コンテナの再起動で消える | 既定では、Azure 上の DB はデータの保存先（ボリューム）を持たずに動きます。設定を変えた再デプロイ、Azure 基盤のメンテナンスなどで DB コンテナが再起動すると、**DB のデータは失われます**。データを残したい場合は、`--persist-db` を付けてデプロイすると、データを Azure Files に置いて再起動の後も残せます（ストレージの料金がかかります）。違いと注意点は [DB のデータの扱いと永続化](#db-のデータの扱いと永続化--persist-db) を参照してください。なお、ローカルの検証（Dry-run の docker-compose）では、データは Docker のボリュームに残ります |
| SQL Server は Developer エディション | `--db-type sqlserver` では、SQL Server を `MSSQL_PID=Developer` で起動します。[Developer エディション](https://learn.microsoft.com/ja-jp/sql/sql-server/editions-and-components-of-sql-server-2022)は開発・テスト用で、本番環境では使えません |
| Azure 上の SQL Server ではサンプル DB を作れない | サンプル DB を使う場合は、PostgreSQL（`--db-type` の既定値）を使ってください。SQL Server では、セットアップウィザードで「空のデータベースを作成する」を選んで評価してください（SQL Server の復元は DB コンテナの中のファイルを読みますが、Azure では Timetracker のコンテナの中にあるサンプル DB のファイルを DB コンテナから読めないため）。ローカルの検証（Dry-run の docker-compose）では、SQL Server でもサンプル DB を作れます |
| 台数は 1 台に固定 | どのコンテナも 1 台で動きます。冗長化や台数の増減（スケールアウト）は行いません |
| パスワードの扱い | Azure 上では、パスワードを Container Apps の[シークレット](https://learn.microsoft.com/ja-jp/azure/container-apps/manage-secrets)として登録します。Dry-run で作る `.env` には、DB のパスワードなどが平文で書かれます。共有したり、リポジトリにコミットしたりしないでください |
| 使い終わったら削除する | Azure のリソースは、置いてある間は料金がかかります（[Container Apps の料金](https://azure.microsoft.com/ja-jp/pricing/details/container-apps/)）。検証が終わったら [`delete`](#リソースの削除delete) で削除してください（`--delete-resource-group` で、空になったリソースグループも削除できます）。リソースグループごと削除することもできます（例: [`az group delete --name <リソースグループ名>`](https://learn.microsoft.com/ja-jp/cli/azure/group#az-group-delete)）。既存のリソースグループを使った場合は、他のリソースを巻き込まないようご注意ください |

---

## 利用者向けマニュアル

### 使い始めるまでの流れ

初めて使う場合は、次の順に進めてください。

1. **ツールの入手**: [Releases](https://github.com/yamadakou/timetracker-deployment-tool/releases) から `ttdeploytool-cli-vX.Y.Z.zip` を取得し、任意のフォルダーに展開します（`ttdeploytool-cli.exe`。Windows 用の単一ファイルで、.NET のインストールは不要です）
2. **Azure の準備**（初回だけ。管理者に依頼する場合があります）
   - [リソースプロバイダーの登録](#azure-リソースプロバイダーの登録)
   - 実行する人（または ID）への[ロールの割り当て](#必要な-rbac-ロールと権限)（通常は Contributor）
3. **Azure へのサインイン**: 通常は [Azure CLI](https://learn.microsoft.com/ja-jp/cli/azure/install-azure-cli) で `az login` を実行します（ほかの方法は[認証モード](#認証モード別の前提環境と利用方法)を参照）
4. **デプロイ**:
   ```powershell
   ttdeploytool-cli.exe deploy `
     --subscription "<サブスクリプション ID>" `
     --resource-group "rg-tt-demo" `
     --db-password "Str0ngP@ssw0rd!" `
     --tracker-password "AppLoginP@ss!" `
     --auth-mode "azure-cli"
   ```
   完了すると、最後に Timetracker の URL が表示されます（`[SUCCESS] Timetracker endpoint: https://...`）
5. **Timetracker の初期設定**: 表示された URL をブラウザーで開き、セットアップウィザードでライセンスキーを入力して DB を作ります（「空のデータベース」または「サンプルデータベース」）
6. **使い終わったら削除**: [`delete`](#リソースの削除delete) で、作ったリソースを削除します

サブスクリプション ID は、`az account show` の `id`、または [Azure ポータル](https://learn.microsoft.com/ja-jp/azure/azure-portal/get-subscription-tenant-id)で確認できます。

### 作成される Azure リソース

本ツールは、次の Azure リソースを作成します。

| リソース | 名前 | 内容 |
|---|---|---|
| [リソースグループ](https://learn.microsoft.com/ja-jp/azure/azure-resource-manager/management/manage-resource-groups-cli) | `--resource-group` の値 | 無ければ作成します。既にある場合は、変更せずにそのまま使います（タグなどは残ります） |
| [Container Apps 環境](https://learn.microsoft.com/ja-jp/azure/container-apps/environment)（Environment） | `{アプリ名}-env`（例: `timetracker-env`） | 3 つのアプリが動く環境です。既にある場合は、設定を変えずにそのまま使います |
| Container App（Timetracker） | `{アプリ名}-tt` | Timetracker 本体。インターネットに公開します（HTTPS、[イングレス](https://learn.microsoft.com/ja-jp/azure/container-apps/ingress-overview)のポート 8080） |
| Container App（DB） | `{アプリ名}-db` | PostgreSQL または SQL Server。環境の中からだけ接続できます（TCP） |
| Container App（Redis） | `{アプリ名}-redis` | Redis。環境の中からだけ接続できます（TCP） |
| [ストレージアカウント](https://learn.microsoft.com/ja-jp/azure/storage/common/storage-account-overview)（`--persist-db` を指定した場合だけ） | `tt` ＋ アプリ名の英数字（先頭 14 文字まで）＋ 8 文字（例: `tttimetracker1a2b3c4d`） | DB のデータを置くファイル共有 `{アプリ名}-db`（[Azure Files](https://learn.microsoft.com/ja-jp/azure/storage/files/storage-files-introduction)。SMB）を持ちます（StorageV2 / Standard_LRS）。名前の最後の 8 文字は、サブスクリプション・リソースグループ・アプリ名から作ります（ストレージアカウントの名前は Azure 全体で重複できないため）。あわせて、Container Apps 環境にストレージ `dbdata` を登録します（既存の環境にも登録します。環境のほかの設定は変えません） |

- アプリ名は `--app-name` で指定します（既定は `timetracker`）
- Log Analytics ワークスペースは、本ツールでは作りません（Container Apps 環境が必要に応じて作る場合があります。[Container Apps のログの監視](https://learn.microsoft.com/ja-jp/azure/container-apps/log-monitoring)）

**管理タグ**:

本ツールは、作ったリソースに次の[タグ](https://learn.microsoft.com/ja-jp/azure/azure-resource-manager/management/tag-resources)を付けます。[削除](#リソースの削除delete)では、名前が一致し、かつこのタグが付いているリソースだけを削除します（同じリソースグループに同じ名前の別のリソースがあっても、誤って削除しないため）。

| タグのキー | 値 |
|---|---|
| `managed-by` | `ttdeploytool` |
| `tt-app-name` | アプリ名（`--app-name`） |
| `tt-expires-at` | 利用可能期限（UTC。例: `2026-11-23T14:59:59Z`）。期限なしは `none`（`--expires-on`。3 つの Container App・ストレージアカウント。Container Apps 環境は作成したときの値） |
| `tt-deployed-at` | 最後にデプロイした時刻（UTC。3 つの Container App・ストレージアカウント。Container Apps 環境は作成したときの値） |

- 3 つの Container App には、作成するときと更新するときに付けます。更新するときは、利用者が付けたほかのタグを残したまま、管理タグを加えます
- Container Apps 環境とストレージアカウントには、新しく作成するときだけ付けます（既存のものは変更しないため）
- `tt-expires-at` と `tt-deployed-at` は、デプロイのたびに 3 つの Container App と、本ツールで作ったストレージアカウント（`--persist-db` の場合）へ書きます（期限を省略した再デプロイでは、`tt-expires-at` は今の値のまま）。ストレージアカウントには作った直後に書くため、途中で失敗して残った場合も期限で削除できます。既存のストレージアカウントには、[タグの API](https://learn.microsoft.com/ja-jp/azure/azure-resource-manager/management/tag-resources) でこの 2 つのタグだけを書き加え、設定とほかのタグは変えません。リソースグループには付けません
- Container Apps 環境には、新しく作成するときだけ、作成の要求にこの 2 つのタグを含めます。環境はタグを変えるだけでも数分「更新中」になり、その間は Container App を作成できないため、再デプロイと期限の変更では書き換えません（環境のタグは作成したときの値のまま残ります）。期限は Container App とストレージアカウントのタグで判断し、環境のタグは、Container App もストレージアカウントも残っていない場合だけ使います
- Container App とストレージアカウントの `tt-expires-at` が食い違っている場合（期限の変更の途中の失敗、ポータルでの書き換えなど）は、`none` が 1 つでもあれば期限なし、ほかは最も遅い期限とみなします（誤って早く削除しないため）
- リソースグループは、本ツールが新しく作成したときだけ `managed-by` を付けます（`tt-app-name` は付けません。同じリソースグループに複数のアプリ名でデプロイできるため）。既存のリソースグループは変更しません
- 以前の名前（timetracker-deployment-tool）の版（v0.8.1 まで）は `managed-by` に `timetracker-deployment-tool` を付けていました。この値のリソースも本ツールのものとして扱い、削除の対象にします。更新すると `ttdeploytool` に置き換わります
- タグは誤って削除しないための目印で、セキュリティの制限ではありません（削除できるかどうかは、Azure の RBAC で決まります）
- 本ツールで作ったリソースは、タグで一覧できます（[`az resource list`](https://learn.microsoft.com/ja-jp/cli/azure/resource#az-resource-list) は `--tag` と `--resource-group` を同時に指定できないため、リソースグループは `--query` で絞り込みます）:
  ```bash
  az resource list --tag managed-by=ttdeploytool --query "[?resourceGroup=='<リソースグループ名>'].{name:name, type:type}" -o table
  ```

### 名前の変更（v0.9.0 から）

本ツールの名前を、Timetracker（デプロイする製品の名前）と区別するため、TTDeployTool に改めました（v0.9.0 から。v0.9.0 は、名前を改めて作り直しました）。v0.8.1 までの版から移る場合は、次の点に注意してください。

| 変わったもの | v0.8.1 まで | v0.9.0 から | 必要な作業 |
|---|---|---|---|
| CLI の実行ファイル・Release の zip | `timetracker-cli.exe`・`timetracker-cli-vX.Y.Z.zip` | `ttdeploytool-cli.exe`・`ttdeploytool-cli-vX.Y.Z.zip` | スクリプトなどで実行ファイル名を指定していれば直す |
| MCP サーバー（stdio）の実行ファイル | `timetracker-mcp.exe` | `ttdeploytool-mcp.exe` | AI アシスタントの設定の `command` を直す（[MCP サーバーの利用者向けマニュアル](docs/MCP_SERVER.md)） |
| MCP サーバー（HTTP）のイメージ | `ghcr.io/yamadakou/timetracker-mcp` | `ghcr.io/yamadakou/ttdeploytool-mcp` | `scripts/deploy-mcp-http.ps1` を再実行する（下記）。以前のイメージ（`timetracker-mcp`）は GHCR から削除しました |
| MCP サーバーの名前・Resources の URI | `timetracker-deploy`・`timetracker://…` | `ttdeploytool`・`ttdeploytool://…` | 不要（AI アシスタントはツールの一覧から読み直す） |
| Entra ID のアプリ登録・アプリロール（HTTP 版） | `Timetracker MCP (<名前>)`・`Timetracker.Deploy.Server` / `.User` | `TTDeployTool MCP (<名前>)`・`TTDeployTool.Deploy.Server` / `.User` | `scripts/deploy-mcp-http.ps1` を同じ引数で再実行する。既存のアプリ登録を引き継ぎ、表示名とロールの値を新しくする（ロールの ID は変えないため、利用者への割り当てはそのまま） |
| 管理タグ `managed-by` の値 | `timetracker-deployment-tool` | `ttdeploytool` | 不要（以前の値も本ツールのものとして扱う） |

### 必要な RBAC ロールと権限

本ツールを実行するユーザー、サービスプリンシパル、またはマネージド ID には、次のどれかの[ロールの割り当て](https://learn.microsoft.com/ja-jp/azure/role-based-access-control/overview)が必要です。

| ロール（割り当てる範囲） | 本ツールでできること | おすすめの使い方 |
|--------|------|----------|
| **[Contributor（共同作成者）](https://learn.microsoft.com/ja-jp/azure/role-based-access-control/built-in-roles/privileged#contributor)**（サブスクリプション） | すべての操作。リソースグループが無ければ作成します。[リソースプロバイダーの登録](#azure-リソースプロバイダーの登録)（事前の準備）も自分で行えます | 通常の使い方 |
| **Contributor**（リソースグループ） | 事前に作ったリソースグループへのデプロイと削除。リソースグループの作成と、リソースプロバイダーの登録はできないため、管理者に依頼します | 権限を絞りたい場合 |
| **[Owner（所有者）](https://learn.microsoft.com/ja-jp/azure/role-based-access-control/built-in-roles/privileged#owner)**（サブスクリプションまたはリソースグループ） | Contributor と同じ（ロールの割り当ての管理もできますが、本ツールでは使いません） | Contributor を使えない場合 |

> [!WARNING]
> **[Container Apps Contributor（Container Apps 共同作成者）](https://learn.microsoft.com/ja-jp/azure/role-based-access-control/built-in-roles/containers#container-apps-contributor)だけでは、本ツールを使えません。** このロールには、本ツールが使う次の権限が含まれていないためです。
> - `Microsoft.Resources/subscriptions/resourceGroups/read` / `write`（リソースグループの確認・作成。本ツールは最初にこれを確認します）
> - `Microsoft.App/managedEnvironments/write` / `delete`（Container Apps 環境の作成・削除）

本ツールが行う操作と、必要な権限:

| 操作 | 必要な権限（アクション） | 必要な範囲 |
|---|---|---|
| リソースグループの確認 | `Microsoft.Resources/subscriptions/resourceGroups/read` | リソースグループ |
| リソースグループの作成（無い場合） | `Microsoft.Resources/subscriptions/resourceGroups/write` | サブスクリプション |
| Container Apps 環境の確認・作成 | `Microsoft.App/managedEnvironments/read` / `write` | リソースグループ |
| Container App（DB / Redis / Timetracker）の確認・作成・更新 | `Microsoft.App/containerApps/read` / `write`、`Microsoft.App/managedEnvironments/join/action` | リソースグループ |
| Container App の削除（`delete`） | `Microsoft.App/containerApps/delete` | リソースグループ |
| Container Apps 環境の削除（`delete`） | `Microsoft.App/managedEnvironments/delete` | リソースグループ |
| DB のデータの永続化（`--persist-db`）: ストレージアカウントの確認・作成・キーの取得 | `Microsoft.Storage/storageAccounts/read` / `write`（作成する場合）/ `listkeys/action` | リソースグループ |
| DB のデータの永続化: ファイル共有の確認・作成 | `Microsoft.Storage/storageAccounts/fileServices/shares/read` / `write`（作成する場合） | リソースグループ |
| DB のデータの永続化: Container Apps 環境へのストレージの登録 | `Microsoft.App/managedEnvironments/storages/read` / `write` | リソースグループ |
| 永続化した DB アプリの更新（停止 → 更新 → 開始） | `Microsoft.App/containerApps/stop/action` / `start/action` | リソースグループ |
| ストレージアカウントの削除（`delete`。`--keep-db-data` なし） | `Microsoft.Storage/storageAccounts/delete` | リソースグループ |
| リソースグループの削除（`delete --delete-resource-group`） | `Microsoft.Resources/subscriptions/resourceGroups/delete`（中のリソースの一覧に `Microsoft.Resources/subscriptions/resourceGroups/resources/read`） | リソースグループ |
| デスクトップアプリのデプロイの一覧（Azure Resource Graph） | 読み取り（`*/read`。Reader など。権限の無いサブスクリプションのものは出ません） | サブスクリプションまたはリソースグループ |
| デスクトップアプリの一覧の「パスワードを表示」 | `Microsoft.App/containerApps/listSecrets/action` | リソースグループ |
| デスクトップアプリの一覧の「期限を変える」（タグの書き換え） | `Microsoft.Resources/tags/write`、またはリソースへの書き込み（Contributor など） | リソースグループ |
| 権限の事前確認（下記） | `Microsoft.Authorization/permissions/read`（自分の権限の一覧。通常はどのロールにも含まれます） | リソースグループ |

**権限の事前確認**: `deploy` と `delete` は、Azure のリソースを変更する前に、上の表の権限が実行する ID にあるかを確かめます。足りなければ、足りない権限を表示して、何も変更せずに終了します（途中で権限のエラーになり、一部のリソースだけが作成・削除された状態になるのを防ぐため）。既存のリソースの更新・削除は、リソースグループでの権限に加えて、そのリソースに割り当てられたロールでも判定します（例: リソースグループでは閲覧者で、各 Container App と環境に共同作成者が割り当てられている場合）。新しく作るリソースには、リソースグループでの権限が必要です。次の場合は、確認をせずに続けます（`--verbose` で理由を表示します）。
- リソースグループがまだ無い場合（権限の一覧はリソースグループ単位でしか取得できないため。作成する権限が無ければ最初の手順で失敗し、リソースは作られません）
- 権限の一覧を取得できなかった場合

この確認には、[拒否割り当て](https://learn.microsoft.com/ja-jp/azure/role-based-access-control/deny-assignments)や [Azure Policy](https://learn.microsoft.com/ja-jp/azure/governance/policy/overview) による拒否は反映されません。これらで拒否された場合は、実行中のエラーとして表示されます。

ロールの割り当ての例（リソースグループの範囲。詳しくは [Azure CLI を使用して Azure ロールを割り当てる](https://learn.microsoft.com/ja-jp/azure/role-based-access-control/role-assignments-cli)）:
```bash
az role assignment create \
  --assignee <ユーザー / サービスプリンシパル / マネージド ID のオブジェクト ID> \
  --role Contributor \
  --scope /subscriptions/<サブスクリプション ID>/resourceGroups/<リソースグループ名>
```

リソースグループの範囲で Contributor を割り当てる場合は、次の作業を、事前にサブスクリプションの管理者に依頼してください。
- リソースグループの作成（`az group create --name <リソースグループ名> --location japaneast`）
- リソースプロバイダーの登録（[次の節](#azure-リソースプロバイダーの登録)）

**参考**:
- [Azure ロールベースのアクセス制御（Azure RBAC）とは](https://learn.microsoft.com/ja-jp/azure/role-based-access-control/overview)
- [Azure 組み込みロール](https://learn.microsoft.com/ja-jp/azure/role-based-access-control/built-in-roles)

### Azure リソースプロバイダーの登録

Azure では、サービスを使う前に、そのサービスの[リソースプロバイダー](https://learn.microsoft.com/ja-jp/azure/azure-resource-manager/management/resource-providers-and-types)をサブスクリプションに登録しておく必要があります。本ツールは登録を**行わない**ため、初めて使う前に、次のコマンドで登録してください。登録には、サブスクリプションの範囲の権限（Contributor または Owner。アクションは `*/register/action`）が必要です。リソースグループの範囲の権限しか無い場合は、サブスクリプションの管理者に依頼してください。

| リソースプロバイダー | 用途 | 必要か |
|---|---|---|
| `Microsoft.App` | Azure Container Apps | 必須 |
| `Microsoft.OperationalInsights` | Container Apps 環境が使う | 必須 |
| `Microsoft.Web` | 一部のシナリオで必要 | 任意 |
| `Microsoft.Storage` | DB のデータを置くストレージアカウント | `--persist-db` を使う場合 |

```bash
az provider register --namespace Microsoft.App
az provider register --namespace Microsoft.OperationalInsights
az provider register --namespace Microsoft.Web
az provider register --namespace Microsoft.Storage   # --persist-db を使う場合

# 登録の状態の確認（Registered になれば完了）
az provider show --namespace Microsoft.App --query "registrationState"
az provider show --namespace Microsoft.OperationalInsights --query "registrationState"
```

**参考**:
- [Azure リソースプロバイダーと種類](https://learn.microsoft.com/ja-jp/azure/azure-resource-manager/management/resource-providers-and-types)
- [`az provider register`](https://learn.microsoft.com/ja-jp/cli/azure/provider#az-provider-register)

### 共通の前提環境
- Windows 10 / 11（配布している `ttdeploytool-cli.exe` は Windows 用です。ほかの OS では、[ソースからビルド](#ビルド方法前提準備含む)して使えます）
- インターネットに接続できること（Azure と Docker Hub）
- Azure にデプロイするだけなら、Docker は不要です（Docker は[ローカルの検証](#ローカル検証dry-run)だけで使います）

### 認証モード別の前提環境と利用方法

本ツールは、Azure にサインインする方法（認証モード）を 5 つ用意しています。`--auth-mode` で選びます（省略時は `default`）。

| 認証モード | サインインの方法 | 向いている場面 |
|---|---|---|
| `azure-cli` | Azure CLI の `az login` のサインインを使う | 自分の PC で使う（**おすすめ**） |
| `default` | 環境変数・マネージド ID・Azure CLI などを順に試す | 環境に合わせて自動で選ばせたい |
| `sp-env` | サービスプリンシパル（環境変数のクライアント シークレット） | CI/CD のパイプライン |
| `device-code` | 表示されたコードを、ブラウザーで入力してサインイン | ブラウザーの無い環境（SSH 接続など） |
| `managed-identity` | Azure の VM などに割り当てたマネージド ID | Azure の上で実行する |

> [!IMPORTANT]
> 動作を確認しているのは `azure-cli` だけです。

どの認証モードでも、サインインした ID に[必要な RBAC ロール](#必要な-rbac-ロールと権限)が必要です。

---

#### 認証モード: `azure-cli` (AzureCliCredential)

##### 前提環境
- [Azure CLI](https://learn.microsoft.com/ja-jp/cli/azure/install-azure-cli) をインストールし、`az login` でサインインしていること（[Azure CLI でのサインイン](https://learn.microsoft.com/ja-jp/cli/azure/authenticate-azure-cli)）
- 参考: [AzureCliCredential](https://learn.microsoft.com/ja-jp/dotnet/api/azure.identity.azureclicredential)

##### 利用方法（事前準備含む）

1. **Azure CLI のインストールとサインイン**
   ```bash
   # Azure CLI のインストール
   #   Windows: https://learn.microsoft.com/ja-jp/cli/azure/install-azure-cli-windows
   #   macOS:   https://learn.microsoft.com/ja-jp/cli/azure/install-azure-cli-macos
   #   Linux:   https://learn.microsoft.com/ja-jp/cli/azure/install-azure-cli-linux

   # Azure にサインイン
   az login

   # 使うサブスクリプションの確認（id がサブスクリプション ID）
   az account show
   ```

2. **必要な情報の準備**
   - サブスクリプション ID（`az account show` で確認できます）
   - リソースグループ名（まだ無くても構いません）
   - DB のパスワード
   - Timetracker のパスワード

3. **デプロイの実行**
   ```powershell
   ttdeploytool-cli.exe deploy `
     --subscription "<サブスクリプション ID>" `
     --resource-group "rg-tt-demo" `
     --db-type "postgresql" `
     --db-password "Str0ngP@ssw0rd!" `
     --tracker-password "AppLoginP@ss!" `
     --auth-mode "azure-cli"
   ```

---

#### 認証モード: `default` (DefaultAzureCredential)

##### 前提環境
- 環境変数、マネージド ID、Azure CLI、Visual Studio など、いくつかのサインインの方法を順に試し、最初に使えたものを使います（[資格情報チェーン](https://learn.microsoft.com/ja-jp/dotnet/azure/sdk/authentication/credential-chains)）
- どの方法が使われたかは、`--verbose` で確認できます
- 参考: [DefaultAzureCredential](https://learn.microsoft.com/ja-jp/dotnet/api/azure.identity.defaultazurecredential)

##### 利用方法（事前準備含む）

1. **サインインの準備**: 次のどれか 1 つを用意します
   - 環境変数（`AZURE_TENANT_ID`、`AZURE_CLIENT_ID`、`AZURE_CLIENT_SECRET`）を設定する
   - Azure CLI でサインインする（`az login`）
   - マネージド ID を有効にする（Azure の VM や App Service の上で実行する場合）
   - Visual Studio で Azure アカウントにサインインする

2. **必要な情報の準備**: `azure-cli` と同じです

3. **デプロイの実行**
   ```powershell
   ttdeploytool-cli.exe deploy `
     --subscription "<サブスクリプション ID>" `
     --resource-group "rg-tt-demo" `
     --location "japaneast" `
     --app-name "timetracker" `
     --db-type "postgresql" `
     --db-password "Str0ngP@ssw0rd!" `
     --tracker-password "AppLoginP@ss!" `
     --auth-mode "default"
   ```

---

#### 認証モード: `sp-env` (ClientSecretCredential / サービスプリンシパル)

##### 前提環境
- [サービスプリンシパル](https://learn.microsoft.com/ja-jp/entra/identity-platform/app-objects-and-service-principals)を作成し、[必要な RBAC ロール](#必要な-rbac-ロールと権限)を割り当てていること
- 環境変数 `AZURE_TENANT_ID`、`AZURE_CLIENT_ID`、`AZURE_CLIENT_SECRET` を設定していること（どれかが無い場合は、エラーで停止します）
- 参考: [ClientSecretCredential](https://learn.microsoft.com/ja-jp/dotnet/api/azure.identity.clientsecretcredential)

##### 利用方法（事前準備含む）

1. **サービスプリンシパルの作成とロールの割り当て**（[`az ad sp create-for-rbac`](https://learn.microsoft.com/ja-jp/cli/azure/ad/sp#az-ad-sp-create-for-rbac)）
   ```bash
   # サービスプリンシパルを作成する（出力される JSON は安全な場所に保管してください）
   az ad sp create-for-rbac --name "timetracker-deployer" --role Contributor --scopes /subscriptions/<サブスクリプション ID>

   # 出力の例:
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

3. **デプロイの実行**
   ```powershell
   ttdeploytool-cli.exe deploy `
     --subscription "<サブスクリプション ID>" `
     --resource-group "rg-tt-demo" `
     --db-type "postgresql" `
     --db-password "Str0ngP@ssw0rd!" `
     --tracker-password "AppLoginP@ss!" `
     --auth-mode "sp-env"
   ```

---

#### 認証モード: `device-code` (DeviceCodeCredential)

##### 前提環境
- 別の端末でもよいので、ブラウザーを使えること（[デバイス コード フロー](https://learn.microsoft.com/ja-jp/entra/identity-platform/v2-oauth2-device-code)）
- 事前の準備は不要です。サインインするユーザーに[必要な RBAC ロール](#必要な-rbac-ロールと権限)を割り当てておいてください
- 参考: [DeviceCodeCredential](https://learn.microsoft.com/ja-jp/dotnet/api/azure.identity.devicecodecredential)

##### 利用方法（事前準備含む）

1. **デプロイの実行**
   ```powershell
   ttdeploytool-cli.exe deploy `
     --subscription "<サブスクリプション ID>" `
     --resource-group "rg-tt-demo" `
     --db-type "postgresql" `
     --db-password "Str0ngP@ssw0rd!" `
     --tracker-password "AppLoginP@ss!" `
     --auth-mode "device-code"
   ```

2. **サインイン**
   - 実行すると、次のようなメッセージが表示されます。
     ```
     デバイスコード認証: To sign in, use a web browser to open the page https://microsoft.com/devicelogin and enter the code XXXXXXXXX to authenticate.
     ```
   - ブラウザーで表示された URL を開き、コードを入力して、Azure のアカウントでサインインします
   - サインインが終わると、デプロイが自動的に続きます

---

#### 認証モード: `managed-identity` (ManagedIdentityCredential)

##### 前提環境
- Azure VM、Azure App Service、Azure Container Instances、Azure Container Apps、Azure Functions のどれかの上で実行すること
- その実行環境で[マネージド ID](https://learn.microsoft.com/ja-jp/entra/identity/managed-identities-azure-resources/overview) を有効にし、[必要な RBAC ロール](#必要な-rbac-ロールと権限)を割り当てていること。環境変数 `AZURE_CLIENT_ID` があれば、そのクライアント ID のユーザー割り当てマネージド ID を、無ければシステム割り当てマネージド ID を使います
- 資格情報（パスワードやシークレット）を管理しなくて済むため、Azure の上で実行する場合におすすめです
- 参考: [ManagedIdentityCredential](https://learn.microsoft.com/ja-jp/dotnet/api/azure.identity.managedidentitycredential)

##### 利用方法（事前準備含む）

1. **マネージド ID の有効化とロールの割り当て**

   **Azure VM の場合:**
   ```bash
   # システム割り当てマネージド ID を有効にする
   az vm identity assign --name <VM 名> --resource-group <リソースグループ名>

   # マネージド ID に Contributor ロールを割り当てる
   az role assignment create \
     --assignee <マネージド ID のプリンシパル ID> \
     --role Contributor \
     --scope /subscriptions/<サブスクリプション ID>
   ```

   **Azure App Service の場合:**
   ```bash
   # システム割り当てマネージド ID を有効にする
   az webapp identity assign --name <アプリ名> --resource-group <リソースグループ名>

   # マネージド ID に Contributor ロールを割り当てる
   az role assignment create \
     --assignee <マネージド ID のプリンシパル ID> \
     --role Contributor \
     --scope /subscriptions/<サブスクリプション ID>
   ```

2. **ツールを Azure のリソースに置く**: Azure VM、App Service、Container Instances などに `ttdeploytool-cli.exe` を置きます

3. **デプロイの実行**（環境変数や資格情報の設定は不要です）
   ```powershell
   ttdeploytool-cli.exe deploy `
     --subscription "<サブスクリプション ID>" `
     --resource-group "rg-tt-demo" `
     --db-type "postgresql" `
     --db-password "Str0ngP@ssw0rd!" `
     --tracker-password "AppLoginP@ss!" `
     --auth-mode "managed-identity"
   ```

---

### DB のデータの扱いと永続化（--persist-db）

Azure 上の DB のデータを、DB コンテナの再起動の後も残すかどうかを選べます。**既定では残しません**（検証環境を手早く作り、使い終わったら消すことを前提にしているため）。データを残したい場合は、`deploy` に `--persist-db` を付けます。

| | 既定（`--persist-db` なし） | `--persist-db` あり |
|---|---|---|
| DB のデータの置き場所 | DB コンテナの中（[一時的なストレージ](https://learn.microsoft.com/ja-jp/azure/container-apps/storage-mounts)） | [Azure Files](https://learn.microsoft.com/ja-jp/azure/storage/files/storage-files-introduction)（ストレージアカウントのファイル共有。SMB） |
| DB コンテナが再起動したとき（設定を変えた再デプロイ、Azure 基盤のメンテナンスなど） | **データは失われます**。既存の DB アプリがある状態で再デプロイすると、警告を表示します | データは残ります |
| 追加で作るリソース | なし | ストレージアカウント・ファイル共有・Container Apps 環境のストレージ（[作成される Azure リソース](#作成される-azure-リソース)） |
| 追加の料金 | なし | Azure Files の料金（下記） |
| 再デプロイの動き | DB アプリをそのまま更新します | DB アプリを**停止してから更新し、開始します**（その間は Timetracker を使えません） |
| `delete` での扱い | DB アプリと一緒にデータも消えます | 既定でストレージアカウントもデータごと削除します。`--keep-db-data` で残せます |

```powershell
ttdeploytool-cli.exe deploy --subscription "<サブスクリプション ID>" --resource-group "rg-tt-demo" `
  --db-password "<DB のパスワード>" --tracker-password "<Timetracker のパスワード>" --persist-db
```

**`--persist-db` の詳しい動き**:
- **マウント先**: DB コンテナの `/var/lib/postgresql/data`（PostgreSQL。データはその下の `pgdata`）または `/var/opt/mssql`（SQL Server）に、ファイル共有をマウントします（[Container Apps で Azure Files を使う](https://learn.microsoft.com/ja-jp/azure/container-apps/storage-mounts-azure-files)）
- **再デプロイで `--persist-db` を省略した場合**: 既存の DB アプリの今の状態を引き継ぎます（永続化していれば、永続化を続けます）。新しく作る場合は永続化しません
- **停止 → 更新 → 開始の理由**: Container Apps は、新しい[リビジョン](https://learn.microsoft.com/ja-jp/azure/container-apps/revisions)が準備できるまで古いものを止めません。そのまま更新すると、新旧の DB コンテナが同じデータを同時に開いてしまうため、先に停止します。データは失われないので、データが消える警告は出ません（MCP の `acknowledge_data_loss` も不要です）
- **途中で切り替える場合**（どちらも警告を表示します）:
  - 永続化していない環境に `--persist-db` を付けると、**今の DB のデータは消えます**。切り替えた後の DB は、以前に永続化していたときのデータ（初めての場合は空の DB）になります。今のデータを残したい場合は、事前に Timetracker でエクスポートしてください
  - 永続化をやめる（`--persist-db false`）と、Timetracker からは空の DB に見えます。これまでのデータは消えずにストレージアカウントに残り、もう一度 `--persist-db` を付けてデプロイすると元に戻ります
- **DB アプリの再起動は、本ツールの再デプロイで行ってください**: 同じ設定で `deploy` を実行すると、停止 → 更新 → 開始で再起動します。Azure ポータルの「再起動」や [`az containerapp revision restart`](https://learn.microsoft.com/ja-jp/cli/azure/containerapp/revision#az-containerapp-revision-restart) は使わないでください。新旧の DB コンテナが同じデータを同時に開こうとします。Azure での確認では、SQL Server は新しいコンテナが起動に失敗し続けて再起動が終わらなくなりました（データは無事で、本ツールの再デプロイで回復しました）。PostgreSQL は、新旧が一時的に同時に動いた後、自動の復旧で起動しました（データは無事でした）
- **削除**: `delete` は、既定でストレージアカウントも DB のデータごと削除します。`--keep-db-data` を付けると残せます（料金はかかり続けます。同じアプリ名で `--persist-db` を付けてデプロイすると、データを引き継げます）
- **料金**: Azure Files（Standard、LRS）の保存容量とトランザクションの料金がかかります。DB は読み書きが多いため、主にトランザクションの料金になります。金額は [Azure Files の料金](https://azure.microsoft.com/ja-jp/pricing/details/storage/files/) を確認してください
- **前提**: リソースプロバイダー `Microsoft.Storage` の登録（[Azure リソースプロバイダーの登録](#azure-リソースプロバイダーの登録)）と、ストレージの権限（[必要な RBAC ロールと権限](#必要な-rbac-ロールと権限)。Contributor には含まれます）が必要です。ファイル共有のマウントにはストレージアカウントのアカウントキーを使い、Container Apps 環境は仮想ネットワーク（VNet）を使いません。そのため、Azure Policy で[アカウントキーによるアクセス（共有キー）](https://learn.microsoft.com/ja-jp/azure/storage/common/shared-key-authorization-prevent)やパブリック ネットワーク アクセスを禁止している環境では、DB コンテナへのマウントが失敗します
- **制約**: 検証環境のための補助です。バックアップはなく、冗長化は [LRS](https://learn.microsoft.com/ja-jp/azure/storage/common/storage-redundancy)（1 つのデータセンターの中の 3 つのコピー）だけです。Azure 基盤のメンテナンスによる再起動は本ツールでは制御できず、まれにデータが壊れるおそれがあります。重要なデータは、Timetracker の機能でエクスポートしておいてください。Azure 上の SQL Server でサンプル DB を作れない制約は、`--persist-db` を付けても変わりません
- **設計の詳細**: [docs/design/db-persistence.md](docs/design/db-persistence.md)

### ローカル検証（Dry-run）

Azure を使わずに、自分の PC の Docker で Timetracker・DB・Redis を起動して確かめられます。

#### 前提環境
- Windows 10 / 11 では [Docker Desktop](https://docs.docker.com/desktop/setup/install/windows-install/)（Linux コンテナのモード。WSL 2 のバックエンドを推奨）、macOS / Linux では [Docker Engine](https://docs.docker.com/engine/install/)
- [Docker Compose](https://docs.docker.com/compose/)（`docker compose` が使えること）

#### 利用方法

1. **Dry-run でファイルを作る**（Azure を使わないため、`--subscription` と `--resource-group` は不要です）
   ```powershell
   ttdeploytool-cli.exe deploy `
     --db-type "postgresql" `
     --db-password "Str0ngP@ssw0rd!" `
     --tracker-password "AppLoginP@ss!" `
     --tt-tag "11.0.1-linux-postgres" `
     --dry-run "true"
   ```
   今いるフォルダーに `docker-compose.yml` と `.env` を作ります（`.env` にはパスワードが平文で書かれます）

2. **Docker Compose で起動する**
   ```powershell
   docker compose --file .\docker-compose.yml --env-file .\.env up -d
   docker compose ps
   ```

3. **動作を確認する**
   - ブラウザーで `http://localhost:8080` を開き、セットアップウィザードでライセンスキーを入力して DB を作ります（「空のデータベース」または「サンプルデータベース」）
   - DB（PostgreSQL のデータベース）はセットアップウィザードが作ります。本ツールは事前に作りません
   - SQL Server でサンプルデータベースを作れるよう、作られる compose では、共有のボリューム `ttsample` を両方のコンテナにマウントしています（SQL Server が Timetracker のコンテナの中にあるバックアップのファイルを読めるようにするため）

4. **データと後片付け**
   - DB のデータは Docker の[名前付きボリューム](https://docs.docker.com/engine/storage/volumes/)に置くため、コンテナを再起動しても残ります
   - 終わったら `docker compose down` で停止します。[`docker compose down -v`](https://docs.docker.com/reference/cli/docker/compose/down/) にすると、ボリュームごと（DB のデータも）削除します

### リソースの削除（delete）
`deploy` で作ったリソースを削除します。**リソースグループは、既定では削除しません**（他のリソースを巻き込まないため）。`--delete-resource-group` を付けると、本ツールのリソースを削除した後にリソースグループが空になる場合だけ、最後にリソースグループも削除します（下の「リソースグループの削除」）。

1. **削除する対象を確認する**（何も変更しません）
   ```powershell
   ttdeploytool-cli.exe delete `
     --subscription "<サブスクリプション ID>" `
     --resource-group "rg-tt-demo" `
     --app-name "timetracker" `
     --dry-run
   ```
   出力の例:
   ```
   [INFO] 削除計画（リソースグループ rg-tt-demo は削除しません）
   [INFO]   削除する     timetracker-tt (Container App)
   [INFO]   削除する     timetracker-redis (Container App)
   [INFO]   削除する     timetracker-db (Container App)
   [INFO]   削除する     timetracker-env (Environment)
   [WARN] DB アプリを削除すると DB のデータはすべて失われます。
   [WARN] Dry-run のため削除は行いません。
   ```

2. **削除する**（確認のため、アプリ名の入力を求めます。`--yes` で省略できます）
   ```powershell
   ttdeploytool-cli.exe delete --subscription "<サブスクリプション ID>" --resource-group "rg-tt-demo" --app-name "timetracker"
   ```
   `{アプリ名}-tt` → `-redis` → `-db` → `-env` → ストレージアカウント（`--persist-db` で作ったもの）の順に削除します。途中で失敗した場合は、もう一度実行すると残りを削除します（削除済みのものは飛ばします）。
   Container Apps 環境の削除は、終わるまで十数分かかることがあります。

**削除の対象になるもの**:
- 名前が `{アプリ名}-tt` / `-redis` / `-db` / `-env`、または `--persist-db` で作ったストレージアカウントの名前で、かつ[管理タグ](#作成される-azure-リソース)が付いているもの（ストレージアカウントは `--keep-db-data` で残せます）
- Container Apps 環境は、その中に削除しない Container App（管理タグの無いもの、本ツール以外で作ったものなど）が残る場合は削除しません

**管理タグの無いリソース**（管理タグを付けるようになる前の版で作ったものなど）は、既定では削除しません。次のどちらかで対応してください。
- 削除する対象を確認したうえで、`--include-untagged` を付けて削除する
- 先に Azure CLI で管理タグを付けてから削除する（[`az tag update`](https://learn.microsoft.com/ja-jp/cli/azure/tag#az-tag-update)）
  ```bash
  az tag update --resource-id <リソース ID> --operation merge \
    --tags managed-by=ttdeploytool tt-app-name=<アプリ名>
  ```

逆に、特定の環境を削除から守りたい場合は、ポータルなどで `managed-by` タグを外してください。

**リソースグループの削除**（`--delete-resource-group`）:
- 計画の時点で、リソースグループの中のリソースを一覧し、今回削除するもの以外のリソース（別のアプリ名の環境、本ツール以外のリソース、`--keep-db-data` で残すストレージアカウントなど）が 1 つでもあれば、何も削除せずに終了します（残るリソースの名前と種類を表示します）
- 本ツールのリソースを削除した後に、リソースグループが空になったのを確かめてから削除します（削除の直後は一覧に反映されないことがあるため、最大 2 分待ちます）。空にならなければ、リソースグループは削除せずにエラーで終了します
- リソースグループの削除は元に戻せません（[Microsoft Learn](https://learn.microsoft.com/ja-jp/azure/azure-resource-manager/management/delete-resource-group)）。[ロック](https://learn.microsoft.com/ja-jp/azure/azure-resource-manager/management/lock-resources)があると削除できません。その場合は、本ツールのリソースは削除済みで、リソースグループだけが残ります（ロックを外してもう一度実行すると、リソースグループだけを削除します）
- 以前に本ツールのリソースだけを削除した空のリソースグループも、同じアプリ名を指定して `--delete-resource-group` を付けて実行すると削除できます

`delete` のオプション:
- `--subscription`（必須）: Azure のサブスクリプション ID
- `--resource-group`（必須）: リソースグループ名
- `--app-name`（任意）: アプリ名（省略時は `timetracker`）
- `--auth-mode`（任意）: 認証モード（`deploy` と同じ）
- `--include-untagged`（任意）: 管理タグの無いリソースも削除の対象にする（別のアプリ名や、別のツールの管理タグ（例: `managed-by=terraform`）が付いているものは、指定しても削除しません）
- `--keep-db-data`（任意）: `--persist-db` で DB のデータを置いたストレージアカウントを残す（既定ではデータごと削除します）。残すと料金がかかり続けますが、同じアプリ名で `--persist-db` を付けてデプロイすると、データを引き継げます
- `--delete-resource-group`（任意）: 本ツールのリソースを削除した後にリソースグループが空になる場合、リソースグループも削除する（上の「リソースグループの削除」）
- `--dry-run`（任意）: 削除する対象を表示するだけで、削除しない
- `--yes`（任意）: 確認の入力（アプリ名）を省略する
- `--verbose`（任意）: 詳しいログを出力する

`deploy` 専用のオプション（`--db-password` など）を `delete` に指定した場合や、`delete` 専用のオプションを `deploy` に指定した場合は、エラーになります。

### MCP サーバー（AI アシスタントから使う）

CLI と同じ機能を、[MCP（Model Context Protocol）](https://modelcontextprotocol.io/)に対応した AI アシスタント（Claude Code、VS Code の GitHub Copilot、Claude Desktop など）から、対話しながら使えます。提供しているのは、利用者の PC で動く stdio 版です（チームで共有する HTTP 版は開発中です）。導入、アシスタントの設定、使い方は [MCP サーバーの利用者向けマニュアル（docs/MCP_SERVER.md）](docs/MCP_SERVER.md) を参照してください。

### デスクトップアプリ（開発中）

CLI や AI アシスタントを使わずに、Windows の画面の操作で同じことを行うアプリを開発しています（WinUI 3。Microsoft Store の限定公開で配布する予定です）。まだ Store では配布していません。評価者には、自己署名の証明書で署名した評価版（`.msix`）を渡せます（作り方: [docs/design/desktop-preview.md](docs/design/desktop-preview.md)、評価者の導入の手順: [docs/DESKTOP_PREVIEW_INSTALL.md](docs/DESKTOP_PREVIEW_INSTALL.md)）。

- できること: Azure へのサインイン（Windows のアカウント。`az login` は不要）、サブスクリプションの選択、デプロイ（計画 → 確認 → 実行）、状態の確認、削除（アプリ名の入力で確認）、ローカル検証（Dry-run）の docker-compose.yml と .env の保存
- 安全策は CLI・MCP サーバーと同じです（実行の前に計画を示す、DB のデータが失われる更新は同意のチェックが必要、実行の直前に計画を作り直して状態が変わっていれば実行しない）
- デプロイの一覧: サインインしたアカウントで読めるサブスクリプションから、本ツールでデプロイした環境（別の PC・CLI・MCP サーバーでデプロイしたものを含む）を読み、サブスクリプション・リソースグループ・デプロイ日・利用可能期限日で絞り込み・並べ替えできます。行から、状態の確認・削除・「対象に入れる」（ウィザードに入れる。再デプロイのため）・期限の変更・パスワードの表示ができます。パスワードは押したときだけ Azure から読み、コピーしてもクリップボードの履歴には残しません。Azure に反映されるまで数分かかることがあり、このアプリで直前に行った操作は反映済みとして表示します
- 画面の流れ: 「新しくデプロイ」は 5 画面のウィザードです（① 対象（テンプレート・サブスクリプション・リソースグループ・アプリ名・リージョン）→ ② DB → ③ パスワード → ④ スペックと利用可能期限 → ⑤ 確認と実行）。入力に誤りがあれば次へ進めません。⑤ で計画を確かめて「今すぐ実行」（実行状況の画面が開きます）か「予定に追加」（ジョブの画面が開きます）を押します。削除と状態の確認は「デプロイの一覧」の行から行います（一覧はサインインすると使えます）
- 一覧の操作（No.63）: 「デプロイの一覧」と「ジョブの一覧」は表で、見出しを押すと並べ替え、見出しの「▽」で絞り込めます。複数の行を選んで、期限の変更・削除（デプロイの一覧。2 件以上の削除は、計画を並べて件数を入力して確認します）、取り消し・一時停止・再開・予定の変更（ジョブの一覧）をまとめて行えます。「新しくデプロイ」「デプロイの一覧」「ジョブの一覧」はサインインするまで押せません。同時に実行する数・サインイン時の起動・診断用のログは、メニューの下の「設定」にあります
- 設定の便利な機能: 名前を付けてテンプレートとして保存し、選んで入力できます（パスワードは保存しません）。どの画面でも「初期値に戻す」と Markdown でのコピー（パスワードは既定で伏せ字。「パスワードも含める」で値を出す）ができます。実行状況・削除の計画・ジョブの内容も Markdown でコピーでき、削除のアプリ名の入力欄の横には「アプリ名をコピー」があります
- 利用可能期限: サインインしたときに一覧を読み、その後は最も近い期限（3 日前・前日を含む）の時刻と 6 時間ごとに読み直して、期限の 3 日前と前日に通知します（通知を押すと一覧が開きます）。期限を過ぎたデプロイは、確認なしで削除のジョブを登録して、本ツールで作ったリソース（DB のデータを含む。リソースグループは本ツールが作ったもので、空になる場合だけ）を削除します。削除の直前に Azure のタグを読み直し、別の PC や一覧で期限が延ばされていれば削除しません
- 期限での削除は、アプリが動いている間に行います。期限日のあるデプロイがあればウィンドウを閉じても通知領域に残り、メニューの下の「設定」の画面で Windows のサインイン時に通知領域で起動するようにできます（期限日のあるデプロイを初めて実行したときにも尋ねます）。どの PC でもアプリが動いていなければ、次にどれかの PC で動いたときに削除します
- 予定（No.67）: 削除の予定は、アプリを終了しても次に起動してサインインしたときに戻します（始める前に計画を作り直し、変わっていれば削除しません）。デプロイの予定はパスワードを持つため保存せず、アプリの終了・Windows の再起動・サインアウトで消えます（次の起動で、消えたことを通知します）
- 「設定」の画面の「Windows との連携」で、サインイン時の起動の状態と前回の起動の日時、通知の許可、今ウィンドウを閉じたらどうなるか、消える予定と戻す予定を確かめられます（Windows のスタートアップ アプリ・通知の設定も開けます）
- パスワードは保存しません（アプリを終了すると消えます）
- 対象: Windows 10（1809 以降）、Windows 11（x64 / arm64）
- 設計: [docs/design/desktop-app.md](docs/design/desktop-app.md)、Windows での確認の手順: [docs/design/desktop-app-check.md](docs/design/desktop-app-check.md)、Store への提出の手順: [docs/design/desktop-app-store.md](docs/design/desktop-app-store.md)、評価版の配布: [docs/design/desktop-preview.md](docs/design/desktop-preview.md)、プライバシー ポリシー（案）: [docs/PRIVACY.md](docs/PRIVACY.md)

### コマンドパラメータの説明一覧
`deploy`（サブコマンドを省略した場合も `deploy`）のオプションです。`delete` のオプションは[リソースの削除（delete）](#リソースの削除delete)を参照してください。

**Azure の指定とサインイン**:
- `--subscription`（必須。Dry-run では不要）: Azure のサブスクリプション ID
- `--resource-group`（必須。Dry-run では不要）: リソースグループ名（無ければ作成します）
- `--location`（任意）: リージョン（省略時は `japaneast`）
- `--auth-mode`（任意）: [認証モード](#認証モード別の前提環境と利用方法)。`default` | `azure-cli` | `sp-env` | `device-code` | `managed-identity`（省略時は `default`）

**アプリと DB**:
- `--app-name`（任意）: アプリ名（省略時は `timetracker`）。リソースの名前に使います（[名前の規則](#アプリ名の規則)を参照）
- `--db-type`（任意）: DB の種類。`postgresql` または `sqlserver`（省略時は `postgresql`）
  - サンプル DB を使う場合は `postgresql` を使ってください。`sqlserver` は Developer エディションで起動し、Azure 上では空のデータベースで評価します（[本ツールの目的と注意事項](#本ツールの目的と注意事項)）
- `--db-password`（必須）: DB のパスワード（DB の種類に関係なく共通。8 文字以上。`sqlserver` の場合は、英大文字・英小文字・数字・記号のうち 3 種類以上を含める必要があります）
- `--db-name`（任意）: DB 名（省略時は `timetracker`）
- `--tracker-password`（必須）: Timetracker のパスワード（[Docker Hub の TimeTracker のページ](https://hub.docker.com/r/densocreate/timetracker)の説明にある `<your password>` に当たるもの）
- `--tt-tag`（任意）: Timetracker のイメージのタグ（版）。省略時は、DB の種類に合わせて `11.0.1-linux-postgres`（postgresql）または `11.0.1-linux-mssql`（sqlserver）。使えるタグは [Docker Hub](https://hub.docker.com/r/densocreate/timetracker/tags) を参照してください。**DB の種類に合ったタグ**（PostgreSQL 用は `-linux-postgres`、SQL Server 用は `-linux-mssql`）を指定してください
- `--persist-db`（任意）: DB のデータを Azure Files に置き、DB コンテナの再起動の後も残す（[DB のデータの扱いと永続化](#db-のデータの扱いと永続化--persist-db)）。省略時は、既存の DB アプリの今の状態を引き継ぎます（新しく作る場合は永続化しない）。永続化をやめるには `--persist-db false`。Dry-run では使いません（警告を表示します）
- `--expires-on`（任意）: 利用可能期限日（`yyyy-MM-dd`）。期限は期限日の終わり（実行する PC の時刻帯の 23:59:59）です。`none` で期限なし。今日より前の日付は誤りになります。省略時は、既存のデプロイの今の期限を引き継ぎ、無ければ（新しくデプロイする場合、この機能より前にデプロイした環境の場合）**今日から 32 日後**にします。期限は Container App のタグ（`tt-expires-at`）に記録します。**期限を過ぎると、TTDeployTool のデスクトップアプリが自動で削除します**（DB のデータも消えます。CLI と MCP サーバーは期限での削除をしません）。残したい環境は、期限日を指定し直すか `none` を指定して再デプロイしてください。Dry-run では使いません

**コンテナの CPU とメモリ**（任意。[組み合わせの一覧](https://learn.microsoft.com/ja-jp/azure/container-apps/containers#allocations)にある値を指定してください）:

| コンテナ | CPU（vCPU。小数可） | メモリ（Gi） | 省略時 |
|---|---|---|---|
| Timetracker | `--tt-cpu` | `--tt-memory` | 1.5 / 3.0Gi |
| DB | `--db-cpu` | `--db-memory` | 1.0 / 2.0Gi |
| Redis | `--redis-cpu` | `--redis-memory` | 0.5 / 1.0Gi |

- Timetracker のメモリを 2Gi 以下にすると、Azure 上でセットアップウィザードの「サンプルデータベースを作成する」がメモリ不足（`OutOfMemoryException`）で失敗します。サンプル DB を使う場合は、既定値（1.5 / 3.0Gi）以上を指定してください

**動作**:
- `--dry-run`（任意）: 指定すると（または `--dry-run true`）、Azure には反映せず、`docker-compose.yml` と `.env` を作るだけにします（[ローカル検証](#ローカル検証dry-run)）。`--subscription` と `--resource-group` は不要です。`--dry-run false` で無効
- `--verbose`（任意）: 詳しいログを出力する
- `--help` / `-h`: 使い方を表示する

**引数の書き方**:
- 知らないオプションや、値の無いオプションはエラーになります（打ち間違いを防ぐため）
- `--` で始まる値（例: パスワード）は、`--db-password=--xxxx` のように `=` で指定してください

**補足**:
- DB のユーザーは、[Docker Hub の TimeTracker のページ](https://hub.docker.com/r/densocreate/timetracker)の「クイックスタート」に書かれた固定の値を使います（CLI からは変えられません）
- `.env` には機密情報が書かれます。扱いにご注意ください（[本ツールの目的と注意事項](#本ツールの目的と注意事項)）

#### アプリ名の規則
`--app-name` は、[Azure Container Apps の名前の規則](https://learn.microsoft.com/ja-jp/azure/azure-resource-manager/management/resource-name-rules)に従う必要があります。
- 使える文字: 英小文字（a-z）、数字（0-9）、ハイフン（-）
- 英小文字で始め、英小文字か数字で終わる
- ハイフンを続けない（`--` は不可）
- 2〜26 文字（Container App の名前の上限は 32 文字ですが、`{アプリ名}-redis` などの接尾辞が付くため）
- 大文字は、自動的に小文字に変換します

### コンテナスペックのカスタマイズ例

各コンテナの CPU とメモリを、個別に指定できます。

```powershell
ttdeploytool-cli.exe deploy `
  --subscription "<サブスクリプション ID>" `
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

- **Q: Azure にデプロイするだけなら、Docker は必要ですか？**  
  A: いいえ。Docker は、[ローカルの検証（Dry-run）](#ローカル検証dry-run)で使うだけです。

- **Q: Azure Database for PostgreSQL や Azure SQL Database は作られますか？**  
  A: いいえ。DB は **コンテナ** として Container Apps の中で動かします。PaaS の DB は作りません。

- **Q: DB のデータは残りますか？**  
  A: 既定では、DB コンテナが再起動すると消えます。`--persist-db` を付けると、Azure Files に置いて残せます。違いは [DB のデータの扱いと永続化](#db-のデータの扱いと永続化--persist-db) を参照してください。ローカルの検証（Dry-run）では、Docker のボリュームに残ります。

#### リソースとスケーリング

- **Q: 台数を増やせますか？**  
  A: いいえ。どのコンテナも 1 台に固定です（[Container Apps のスケーリング](https://learn.microsoft.com/ja-jp/azure/container-apps/scale-app)は使いません）。

- **Q: コンテナの CPU やメモリは、後から変えられますか？**  
  A: はい。オプションを変えてもう一度 `deploy` を実行すると、Container Apps の設定を更新します。
  > [!WARNING]
  > DB アプリ（`{アプリ名}-db`）の設定を変えると DB コンテナが再起動し、**DB のデータは失われます**（`--persist-db` で永続化している場合を除く）。既存の DB アプリがある状態で再デプロイすると、警告を表示します。

- **Q: Log Analytics ワークスペースは作られますか？**  
  A: 本ツールでは作りません。Container Apps 環境が、必要に応じて自動で作る場合があります。

#### 認証とアクセス

- **Q: どの認証モードを使えばよいですか？**  
  A: 自分の PC で使う場合は `azure-cli`（`az login` の後）がおすすめです。ほかは[認証モードの一覧](#認証モード別の前提環境と利用方法)を参照してください（CI/CD は `sp-env`、Azure の VM などの上では `managed-identity`）。

- **Q: 認証や権限のエラーになります**  
  A: 次を確かめてください。
  1. [必要な RBAC ロール](#必要な-rbac-ロールと権限)（Contributor または Owner）が割り当てられているか（Container Apps Contributor だけでは足りません）
  2. `--auth-mode` を明示しているか
  3. `--verbose` で詳しいログを確認する
  4. [リソースプロバイダー](#azure-リソースプロバイダーの登録)（`Microsoft.App`、`Microsoft.OperationalInsights`。`--persist-db` では `Microsoft.Storage` も）が登録されているか

- **Q: どの認証方式が使われたかを確かめるには？**  
  A: `--verbose` を付けて実行すると、使った認証モードがログに出ます。

#### その他

- **Q: App Service にデプロイできますか？**  
  A: いいえ。本ツールは Azure Container Apps 専用です。App Service（マルチコンテナ / Compose）へのデプロイ機能はありません。

---

## 開発者向け情報

### 前提環境
- [.NET 10 SDK](https://learn.microsoft.com/ja-jp/dotnet/core/install/)
- PowerShell 7（推奨）または 5.1 以上
- （Dry-run のローカル検証をする場合だけ）Docker Desktop（Windows）または Docker Engine（ほかの OS）
- 任意: Git（ソースの取得）、エディター（VS Code など）

使っている主な NuGet パッケージ: Azure.Identity、Azure.ResourceManager、Azure.ResourceManager.AppContainers、Azure.ResourceManager.Storage、ModelContextProtocol など。

### ビルド方法（前提準備含む）
```powershell
dotnet build .\TTDeployTool.sln
dotnet publish .\src\TTDeployTool.Controller.Cli\TTDeployTool.Controller.Cli.csproj -c Release -r win-x64 `
  -p:PublishSingleFile=true -p:SelfContained=true -p:UseAppHost=true
```
- 配布する成果物は、CLI プロジェクト（と MCP サーバーのプロジェクト）を publish して作る[単一ファイルの EXE](https://learn.microsoft.com/ja-jp/dotnet/core/deploying/single-file/overview) だけです
- TTDeployTool.Domain / TTDeployTool.Application / TTDeployTool.Infrastructure.Azure / TTDeployTool.Common はクラスライブラリ（実行ファイルを作らない）で、単一ファイルの publish の対象ではありません。ライブラリとして配布する必要が出た場合は、`dotnet pack` で NuGet パッケージにすることなどを検討してください
- ほかの OS 向けには、`-r` を `linux-x64` や `osx-arm64` などに変えて publish します
- デスクトップアプリ（WinUI 3）は Windows でしかビルドできないため、`TTDeployTool.sln` には入れず、`TTDeployTool.Desktop.slnx` を Visual Studio で開いてビルドします。Store 用のパッケージ（`.msixupload`）は、CI の `desktop` ジョブと同じく `msbuild src\TTDeployTool.Controller.Desktop\TTDeployTool.Controller.Desktop.csproj /restore /p:Configuration=Release /p:Platform=x64 /p:AppxBundlePlatforms="x64|arm64" /p:AppxBundle=Always /p:UapAppxPackageBuildMode=StoreUpload /p:AppxPackageSigningEnabled=false /p:GenerateAppxPackageOnBuild=true` で作ります。画面の状態と操作（`TTDeployTool.Controller.Desktop.Core`）とそのテストは `TTDeployTool.sln` に入っていて、Linux でもビルド・テストできます

### テスト
- 単体テスト: `dotnet test TTDeployTool.sln`（Azure には接続しません）
- システムテスト（本物の Azure に対するテスト。料金がかかります）: 準備と実行の方法は [docs/SYSTEM_TESTS.md](docs/SYSTEM_TESTS.md) を参照してください。ローカルでは `scripts/run-system-tests.ps1`（または `.sh`）、CI では GitHub Actions の **System Test** ワークフローを手動で実行します
- セットアップウィザードとログインの確認（ブラウザーを使う E2E テスト。Playwright）: Timetracker のライセンスキーとサンプルデータベースのユーザー（`TT_E2E_LICENSE_KEY` / `TT_E2E_SAMPLE_LOGIN` / `TT_E2E_SAMPLE_PASSWORD`）がある場合に、サンプルデータベースの作成と、そのユーザーでのログインまでを確かめます。ローカルの docker compose（PostgreSQL / SQL Server）では PR の CI（ubuntu）で毎回、Azure ではシステムテストの中で実行します（[docs/SYSTEM_TESTS.md](docs/SYSTEM_TESTS.md)）
- CI（`.github/workflows/ci.yml`）: PR と `main` への push のたびに、ビルドと単体テスト・スクリプトのテスト・上の E2E テスト・MCP サーバーのイメージの確認・デスクトップアプリの Store 用のパッケージの作成と確認・デスクトップアプリの UI テスト（署名した MSIX を導入して画面を操作する）を実行します。変更がドキュメント（`docs/` の下と `*.md`）だけのときは省きます（Actions の **CI** → Run workflow で手動実行すると、変更にかかわらずすべて実行します）
- リンク切れのチェック（[lychee](https://github.com/lycheeverse/lychee)。設定は `lychee.toml`）: Markdown の中のファイルと見出し（`#...`）へのリンクは、CI の `links` ジョブで PR と `main` への push のたびに確かめます（ドキュメントだけの変更でも省きません）。外部のサイトへのリンクは、相手の都合で一時的に失敗することがあるため、**Links** ワークフロー（`.github/workflows/links.yml`）で月 1 回確かめ、切れていればラベル `link-check` の Issue で知らせます（Actions の **Links** → Run workflow で手動でも実行できます）

### 設計概要
将来の GUI（デスクトップ / ウェブ）に備えて、層を分けています。

- ドメイン層（TTDeployTool.Domain）
  - パラメーターのモデル（DeployOptions）、既定値（Defaults）と既定値の適用（DeployOptionsResolver）、検証（DeploymentValidator。すべてのエラーを列挙する）と警告（DeploymentAdvisor）、リソース名の規則（ResourceNaming）、管理タグ（ManagedTags）、権限の判定（PermissionEvaluator）、Compose と .env の生成（Dry-run 用）
- アプリケーション層（TTDeployTool.Application）
  - デプロイの手順の実行（DeploymentRunner: リソースグループ → 環境 →（--persist-db の場合）DB のストレージ → DB / Redis / Timetracker）と結果（DeploymentResult）
  - 削除の計画の作成と削除の実行（DeletionPlanner / DeletionRunner）、権限の事前確認（PermissionChecker / DeploymentPlanner）
  - 計画 → 確認 → 実行の安全策（DeploymentWorkflow / DeletionWorkflow: 計画の保存、実行の直前の再確認、データ消失への同意、アプリ名の確認、同じ対象の二重実行の拒否）。MCP サーバーとデスクトップアプリで共有する
  - Dry-run のファイルの保存（ComposeExporter）
- インフラストラクチャ層（TTDeployTool.Infrastructure.Azure）
  - Azure SDK による実行（AzureSdkExecutor / IAzureDeploymentGateway）、Container Apps のコンテナの仕様の生成（ContainerAppSpecBuilder）、認証（CredentialFactory）、テナントの一覧（AzureTenantDirectory / TenantScopedCredential）
- コントローラー層（TTDeployTool.Controller.Cli）
  - CLI の入出力、オプションの解析（CliArgs）
- コントローラー層（TTDeployTool.Controller.Mcp）
  - MCP サーバー（`ttdeploytool-mcp`。stdio と HTTP）。ツール・Resources・Prompts と、計画 → 確認 → 実行の流れは [MCP サーバーの利用者向けマニュアル](docs/MCP_SERVER.md) を参照してください（設計は [docs/MCP_TOOL_DESIGN.md](docs/MCP_TOOL_DESIGN.md) と [docs/design/](docs/design/) の mcp-phase*.md）
- コントローラー層（TTDeployTool.Controller.Desktop.Core）
  - デスクトップアプリ（開発中）の画面の状態と操作（ViewModel）。UI のフレームワークに依存しないため、Linux でもテストできる（設計は [docs/design/desktop-app.md](docs/design/desktop-app.md)）
- 共通（TTDeployTool.Common）
  - ロガー（IDeploymentLogger / SimpleLogger）などのユーティリティ

依存の向きは「コントローラー層 → アプリケーション層 → インフラストラクチャ層 → ドメイン層」です。CLI 以外のフロントエンド（MCP サーバー、デスクトップアプリなど）を加える場合も、アプリケーション層から下を共有します。

作る Container Apps の構成:
- 3 つのコンテナ
  - `timetracker`（[`densocreate/timetracker`](https://hub.docker.com/r/densocreate/timetracker)。タグは `--tt-tag` の値。省略時は DB の種類に合わせて `11.0.1-linux-postgres` / `11.0.1-linux-mssql`）
  - `db`（[`postgres:16`](https://hub.docker.com/_/postgres) または [`mcr.microsoft.com/mssql/server:2022-latest`](https://learn.microsoft.com/ja-jp/sql/linux/sql-server-linux-docker-container-deployment)）
  - `redis`（[`redis:7`](https://hub.docker.com/_/redis)）
- Dry-run で作る `docker-compose.yml` も、同じイメージと環境変数を使います
- イングレス: 外部に公開、ターゲットのポートは 8080
- スケール: 最小 1 台・最大 1 台（固定）

### 将来の検討
今後の機能の候補（ストア配布できるデスクトップアプリなど）と優先度は、[docs/ROADMAP.md](docs/ROADMAP.md) にまとめています。

### 注意点
- 検証環境の構築が目的のため、Azure Container Apps の DB と Redis には、既定では永続ボリュームを付けていません（Dry-run の docker-compose は名前付きボリュームを使います）。DB だけは、`--persist-db` で Azure Files をマウントできます（設計は [docs/design/db-persistence.md](docs/design/db-persistence.md)）
- DB のパスワードと Timetracker のパスワードは、Container Apps のシークレット（`db-password` / `app-password`）として登録し、環境変数からは `secretRef` で参照します（ポータルの環境変数の一覧に平文で表示されません）
- 認証には、マネージド ID かサービスプリンシパルをおすすめします（DefaultAzureCredential で使えます）
- [Docker Hub の TimeTracker のページ](https://hub.docker.com/r/densocreate/timetracker)の「クイックスタート」に書かれた固定の DB ユーザー名は、ドメイン層の Defaults にまとめています

### FAQ
- **Q: 単一ファイルの EXE の起動が遅い**  
  A: 初回の起動時に、一時的な展開が行われるためです。self-contained を無効にする（.NET ランタイムに依存させる）か、ReadyToRun などの最適化を検討してください。

- **Q: Container Apps へのデプロイが失敗します**  
  A: 認証（サービスプリンシパル / マネージド ID）の設定、ネットワークの到達性、コンテナイメージを取得できるか、環境変数の不足を確かめてください。必要に応じて `--verbose` で詳しいログを取ってください。
