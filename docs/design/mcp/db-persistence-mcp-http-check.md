# HTTP 版 MCP サーバーでの DB データの永続化の確認手順

PR #38（REVIEW_ISSUES No.46）で追加した DB データの永続化（`persist_db`）と、削除のときにデータを残す指定（`keep_db_data`）を、Azure 上の HTTP 版の MCP サーバーから AI アシスタント経由で確かめる手順です。

Azure にも Entra ID にも何もない状態から、次の順に進めます。

1. 準備（ツール・サインイン・権限）……2 章
2. HTTP 版の MCP サーバーを Azure に配置し、Claude Code から接続する……3 章
3. AI アシスタントに頼んで、永続化の動きを確かめる……4・5 章
4. 作ったものをすべて削除し、結果を記録する……6 章

- **所要時間**: 1 時間半ほど（MCP サーバーの配置に 10 分、Environment の削除に 1 回 10〜15 分かかります）
- **料金**: Container Apps とストレージアカウントの料金が少しかかります。最後にすべて削除します
- **結果の記録先**: `docs/REVIEW_ISSUES.md` の No.46 と、[`docs/design/common/db-persistence.md`](../common/db-persistence.md) の「14. 実施状況」
- MCP サーバーの配置の仕組みの詳細は、[`docs/design/mcp/mcp-phase4c.md`](mcp-phase4c.md) にあります（この手順を進めるだけなら、読まなくてかまいません）

## 1. この手順で使う言葉

| 言葉 | 意味 |
|---|---|
| MCP サーバー（HTTP 版） | AI アシスタントから URL で接続して使う、本ツールのサーバー（[MCP_SERVER.md の HTTP 版](../../MCP_SERVER.md#http-版開発中)）。Azure Container Apps の [Container App](https://learn.microsoft.com/ja-jp/azure/container-apps/overview) として動かす |
| [Environment](https://learn.microsoft.com/ja-jp/azure/container-apps/environment) | Container App を入れる箱（Container Apps 環境）。MCP サーバー用と、確認で作る Timetracker 用の 2 つが出てくる |
| [マネージド ID](https://learn.microsoft.com/ja-jp/entra/identity/managed-identities-azure-resources/overview) | MCP サーバーが Azure を操作するときに使う ID。パスワードを持たない |
| [アプリ登録](https://learn.microsoft.com/ja-jp/entra/identity-platform/quickstart-register-app) | Entra ID に登録するアプリの情報。MCP サーバー用（サインインした人を確かめる）と、クライアント用（Claude Code がサインインに使う）の 2 つを作る |
| [アプリロール](https://learn.microsoft.com/ja-jp/entra/identity-platform/howto-add-app-roles-in-apps) | MCP サーバーで Azure を操作してよい人に割り当てる役割。`TTDeployTool.Deploy.Server`（`server` 方式と `user` 方式）と `TTDeployTool.Deploy.User`（`user` 方式だけ）がある |
| `server` 方式 | MCP サーバーのマネージド ID の権限で Azure を操作する方式。操作できるのは、許可したリソースグループだけ |
| `user` 方式 | サインインした利用者本人の Azure の権限で操作する方式 |
| 許可したリソースグループ | `server` 方式で操作してよいリソースグループ。配置のスクリプトが作り、マネージド ID に [Contributor（共同作成者）](https://learn.microsoft.com/ja-jp/azure/role-based-access-control/built-in-roles/general#contributor)を割り当てる |
| [ストレージアカウント](https://learn.microsoft.com/ja-jp/azure/storage/common/storage-account-overview) / [Azure Files](https://learn.microsoft.com/ja-jp/azure/storage/files/storage-files-introduction) | DB のデータを置く場所。永続化を指定すると、ストレージアカウントの中にファイル共有 `{アプリ名}-db` を作り、DB のコンテナに[マウント](https://learn.microsoft.com/ja-jp/azure/container-apps/storage-mounts)する |
| [アクティビティログ](https://learn.microsoft.com/ja-jp/azure/azure-monitor/platform/activity-log) | Azure のリソースに対して、だれがいつ何をしたかの記録 |
| 計画（`plan_id`） | MCP のツールは、Azure を変更する前に計画を作り、利用者が確認してから実行する。計画を作るツールが `plan_deployment` / `plan_deletion`、実行するツールが `start_deployment` / `start_deletion` |

## 2. 準備
### 2.1 使うツール
| ツール | 確かめ方 | 入れ方 |
|---|---|---|
| PowerShell 7 以上 | `pwsh --version` | [PowerShell のインストール](https://learn.microsoft.com/ja-jp/powershell/scripting/install/installing-powershell) |
| Azure CLI と、拡張機能 `containerapp` | `az version`（`extensions` に `containerapp` がある） | [Azure CLI のインストール](https://learn.microsoft.com/ja-jp/cli/azure/install-azure-cli)。拡張機能は `az extension add --name containerapp` |
| Claude Code | `claude --version` | [Claude Code のセットアップ](https://code.claude.com/docs/ja/setup) |
| このリポジトリ | `scripts/deploy-mcp-http.ps1` がある | `git clone https://github.com/yamadakou/timetracker-deployment-tool.git` |

この手順のコマンドは、リポジトリのフォルダーで、PowerShell（`pwsh`）で実行します。`$` で始まる名前は PowerShell の変数で、一度入れれば、同じ PowerShell の画面の中で使い続けられます。

### 2.2 Azure にサインインする
[Azure CLI でサインイン](https://learn.microsoft.com/ja-jp/cli/azure/authenticate-azure-cli-interactively)し、確認に使うサブスクリプションを選びます。

```powershell
az login
az account list --query "[].{name:name, id:id}" -o table   # サブスクリプションの一覧
$Sub = "<使うサブスクリプションの id>"
az account set --subscription $Sub
az account show --query "{user:user.name, subscription:id}" -o table   # 自分とサブスクリプションが表示される
$Me = az ad signed-in-user show --query userPrincipalName -o tsv       # 自分のユーザー名（3.3 で使う）
```

### 2.3 必要な権限と前提
- [ ] 自分が、サブスクリプションの[所有者](https://learn.microsoft.com/ja-jp/azure/role-based-access-control/built-in-roles/general#owner)（Owner）である。配置のスクリプトが、マネージド ID へのロールの割り当てを行うため（[自分のロールの確かめ方](https://learn.microsoft.com/ja-jp/azure/role-based-access-control/check-access)）。所有者なら、4 章の「データの確認」で使うストレージアカウントの[アクセスキー](https://learn.microsoft.com/ja-jp/azure/storage/common/storage-account-keys-manage)の取得と、[コンテナのコンソールへの接続](https://learn.microsoft.com/ja-jp/azure/container-apps/container-console)もできる
- [ ] 自分が、Entra ID でアプリを登録できる。既定ではだれでも登録できるが、組織で制限している場合は「[アプリケーション開発者](https://learn.microsoft.com/ja-jp/entra/identity/role-based-access-control/permissions-reference#application-developer)」などのロールが必要
- [ ] リソースプロバイダー `Microsoft.App` と `Microsoft.Storage` が登録されている（[リソースプロバイダーの登録](https://learn.microsoft.com/ja-jp/azure/azure-resource-manager/management/resource-providers-and-types)。`Microsoft.App` は配置のスクリプトも登録する）
  ```powershell
  az provider show --namespace Microsoft.Storage --query registrationState -o tsv   # Registered と表示される
  # 表示されない場合は登録する（数分かかる）
  az provider register --namespace Microsoft.Storage --wait
  ```
- [ ] 名前を TTDeployTool に改めた版（作り直した v0.9.0 以降）のイメージ `ghcr.io/yamadakou/ttdeploytool-mcp` が公開されている（以前の名前のイメージ `timetracker-mcp` は削除した）（[リリースの一覧](https://github.com/yamadakou/timetracker-deployment-tool/releases)の「ダウンロード」に `ghcr.io/yamadakou/ttdeploytool-mcp:<版>` がある）

## 3. MCP サーバー（HTTP 版）を配置し、Claude Code から接続する
### 3.1 名前を決める
次の値を決めて、変数に入れます。この手順では、右の例の値を使います（同じサブスクリプションに同じ名前のものが無ければ、例のままでかまいません）。

```powershell
$McpRg      = "rg-tt-mcp"        # MCP サーバーを置くリソースグループ（スクリプトが作る）
$McpName    = "tt-mcp"           # MCP サーバーの Container App の名前（Environment・マネージド ID・アプリ登録の名前の元にもなる）
$AllowedRg  = "rg-tt-mcp-demo"   # server 方式で操作を許すリソースグループ（スクリプトが作る）。4 章の確認で Timetracker を作る場所
$ImageTag   = "<版>"             # MCP サーバーのイメージの版（2.3 で確かめた版。例: 0.9.0）
```

### 3.2 配置のスクリプトを実行する
`scripts/deploy-mcp-http.ps1` を実行します。リソースグループ・マネージド ID・アプリ登録 2 つ・Container Apps の Environment と Container App を作り、許可したリソースグループにマネージド ID の Contributor を割り当てます（10 分ほど）。

```powershell
pwsh scripts/deploy-mcp-http.ps1 -SubscriptionId $Sub -ResourceGroup $McpRg -Name $McpName `
  -IdentityModes server,user -AllowedResourceGroups $AllowedRg -ImageTag $ImageTag
```

- `-IdentityModes server,user` は、`server` 方式と `user` 方式の両方を使えるようにする指定です（5 章で `user` 方式も確かめるため）
- 最後に「完了しました。」と、次のような値が表示されれば成功です。**この 2 つは 3.4 で使います**
  ```text
  MCP のエンドポイント : https://tt-mcp.<ランダムな名前>.japaneast.azurecontainerapps.io/mcp
  クライアントのアプリ : <クライアントのアプリの ID>（TTDeployTool MCP client (tt-mcp)）
  ```
  ```powershell
  $Endpoint = "<表示された MCP のエンドポイント>"
  $ClientId = "<表示されたクライアントのアプリの ID>"
  ```
- テナントの管理者でない場合、「管理者の同意に失敗しました。実行したアカウントの分だけ同意しました。」という警告が出ます。自分が `user` 方式を使う分には、そのまま続けて問題ありません（ほかの人が `user` 方式を使うには、[管理者の同意](https://learn.microsoft.com/ja-jp/entra/identity/enterprise-apps/grant-admin-consent)が必要です）
- 途中で失敗した場合は、原因を直してから同じコマンドをもう一度実行します。作成済みのものは作り直さず、続きから設定を合わせます

### 3.3 自分にアプリロールを割り当てる
MCP サーバーで Azure を操作できるよう、自分にアプリロール `TTDeployTool.Deploy.Server` を割り当てます。

```powershell
pwsh scripts/assign-mcp-role.ps1 -Name $McpName -Role Server -UserPrincipalName $Me
```

割り当ては、次にサインインしたときから有効になります（すでに Claude Code からサインインしている場合は、3.4 でサインインし直します）。

### 3.4 Claude Code に登録してサインインする
1. Claude Code に MCP サーバーを登録します（[Claude Code での MCP の使い方](https://code.claude.com/docs/ja/mcp)）。`--callback-port 8765` は、配置のスクリプトが登録したサインインの戻り先（`http://localhost:8765/callback`）と合わせるための指定です
   ```powershell
   claude mcp add --transport http --client-id $ClientId --callback-port 8765 timetracker $Endpoint
   ```
2. `claude` を起動し、`/mcp` と入力して、一覧から `timetracker` を選び、サインインを選びます
3. ブラウザーが開くので、2.2 と同じアカウントでサインインします。多要素認証（MFA）を求められたら行ってください（`user` 方式には MFA 済みのサインインが必要です）
4. `/mcp` の一覧で、`timetracker` が接続済み（connected）になれば成功です

### 3.5 MCP サーバーが動いていることを確かめる
- [ ] Claude Code に「timetracker の get_deployment_options を呼んで、サーバーの情報と使える方式、制約を見せて」と頼むと、使える方式に `server` と `user` があり、`constraints` に `persist_db=true` の説明がある
- [ ] 「timetracker で、使えるサブスクリプションの一覧を見せて」と頼むと、2.2 のサブスクリプションが表示される
- [ ] （任意）MCP サーバーのログに、版と接続した利用者が出ている（[ログの表示](https://learn.microsoft.com/ja-jp/azure/container-apps/log-streaming)）
  ```powershell
  az containerapp logs show -n $McpName -g $McpRg --tail 50 --type console   # Server (ttdeploytool <版>) と、自分のユーザー名が出る
  ```

## 4. server 方式での確認
- **アプリ名**: 既存の環境と重ならない名前にします。以下では `ttmcp` とします
- **リソースグループ**: 許可したリソースグループ（3.1 の `$AllowedRg`。例では `rg-tt-mcp-demo`）を使います
- 「依頼」の列は、AI アシスタントへの頼み方の例です。アシスタントは、ツールの結果（計画の内容など）を表示して、実行してよいかを聞いてきます。結果の中の `persist_db` などの値が見当たらないときは、「ツールの結果をそのまま見せて」と頼んでください

| # | 依頼・操作 | 期待する結果 |
|---|---|---|
| S1 | 「サブスクリプション <2.2 の ID> のリソースグループ rg-tt-mcp-demo に、アプリ名 `ttmcp` で、DB のデータを永続化してデプロイする計画を作って」（`plan_deployment` に `persist_db=true`。パスワードを聞かれたら、確認用の値を答える） | `persist_db` が true、`storage_account` にストレージアカウント名がある。`actions` に `storage_account` / `file_share` / `environment_storage` の作成が並ぶ。`warnings` に料金と削除での扱いの案内がある。`permission_check` が `ok`、`requires_data_loss_ack` が false、`identity` が server 方式 |
| S2 | 計画の内容を確かめて「実行して」と頼む（`start_deployment`）。終わるまで待つ（アシスタントが `get_job_status` で進み具合を確かめる。10 分ほど） | `succeeded`。手順は 4 つ（`current_step` が 4/4）。`result` に Timetracker の URL と `storage_account` がある |
| S3 | 「`ttmcp` の状態を教えて」（`get_deployment_info`） | `persistence` が `db_persisted: true`、`storage_account` が S1 の名前、`storage_account_managed: true` |
| S4 | DB のデータが Azure Files に置かれていることを確かめ、テスト用の行を入れる（下の「データの確認」） | ファイル共有に `pgdata/PG_VERSION` がある |
| S5 | 「`ttmcp` の DB の CPU を 1.25 にして再デプロイする計画を作って」（`persist_db` は指定しない） | `persist_db` が true（今の状態を引き継ぐ）、`requires_data_loss_ack` が false。`warnings` に「データを守るために一度停止してから更新し、開始します」がある |
| S6 | S5 の計画を実行する（データが消えることへの同意 `acknowledge_data_loss` は付けない）。終わったら、S4 で入れた行を確かめる（下の「データの確認」） | 同意を求められずに実行できる。ジョブのログに DB アプリの開始が出る。S4 で入れた行が残っている |
| S7 | 「`ttmcp` の永続化をやめて再デプロイする計画を作って」（`persist_db=false`） | `requires_data_loss_ack` が true。`warnings` に「Timetracker からは空の DB に見えます。これまでのデータは消えずにストレージアカウントに残り…」がある。**実行はしない**（同意なしで実行を頼むと、実行されずにエラーになることだけ確かめる） |
| S8 | 「`ttmcp` を、DB のデータを残して削除する計画を作って」（`plan_deletion` に `keep_db_data=true`） | ストレージアカウントの対象が `will_delete: false`（理由: `--keep-db-data の指定により残します`）。`keeps_db_data` が true、`kept` にストレージアカウントの案内がある。「DB のデータはすべて失われます」の警告は出ない |
| S9 | S8 を実行する（確認のためアプリ名 `ttmcp` の入力を求められる）。終わるまで待つ（15 分ほど） | `succeeded`。Container App と Environment は削除され、ストレージアカウントは残る（`get_deployment_info` の `persistence.storage_account` が残っている） |
| S10 | 「`ttmcp` を削除する計画を作って」（既定。`keep_db_data` なし） | ストレージアカウントの対象が `will_delete: true`、`deletes_db_data` が true。「ストレージアカウント…も削除します。元に戻せません（残すには keep_db_data=true）」の警告がある |
| S11 | S10 を実行する | `succeeded`。`deletion_result.deleted` にストレージアカウントがある。`get_deployment_info` の `exists` が false |
| S12 | アクティビティログを確かめる（下の「アクティビティログの確認」） | ストレージアカウントの作成・削除の操作者が、MCP サーバーのマネージド ID |

### データの確認（S4・S6）
MCP のツールには DB の中身を見る機能が無いため、Azure CLI で確かめます。

1. 変数を用意します
   ```powershell
   $Rg      = $AllowedRg          # 確認に使うリソースグループ（例: rg-tt-mcp-demo）
   $Account = "<S1 の storage_account の値>"
   ```
2. ファイル共有 `ttmcp-db`（`{アプリ名}-db`）の `pgdata` フォルダーに、PostgreSQL のデータがあることを確かめます（[az storage file list](https://learn.microsoft.com/ja-jp/cli/azure/storage/file#az-storage-file-list)。`--auth-mode key` は、アクセスキーを自動で取得して使う指定）
   ```powershell
   az storage file list --account-name $Account --share-name ttmcp-db --path pgdata --auth-mode key -o table
   # 一覧に PG_VERSION などのファイルとフォルダーが表示される
   ```
3. DB のコンテナのコンソールに入り、PostgreSQL のクライアント（[psql](https://www.postgresql.org/docs/current/app-psql.html)）を開きます（[コンテナのコンソールへの接続](https://learn.microsoft.com/ja-jp/azure/container-apps/container-console)。DB の Container App の名前は `{アプリ名}-db`）
   ```powershell
   az containerapp exec -n ttmcp-db -g $Rg --command "psql -U postgres"
   ```
   `postgres=#` と表示されたら、次の SQL を入力します。終わったら `\q` で抜けます。
   ```sql
   -- S4: テスト用の表と行を作る
   CREATE TABLE persist_check(id int, note text);
   INSERT INTO persist_check VALUES (1, 'before-redeploy');
   -- S6: 再デプロイの後に、行が残っているかを見る（1 | before-redeploy と表示されれば成功）
   SELECT * FROM persist_check;
   ```

### アクティビティログの確認（S12）
1. MCP サーバーのマネージド ID のプリンシパル ID を調べます（マネージド ID の名前は `{Container App の名前}-id`）
   ```powershell
   az identity show -n "$McpName-id" -g $McpRg --query principalId -o tsv
   ```
2. 確認に使ったリソースグループ（`$Rg`）の、ストレージアカウントの操作と操作者を表示します（[az monitor activity-log list](https://learn.microsoft.com/ja-jp/cli/azure/monitor/activity-log#az-monitor-activity-log-list)。`--offset 3h` は直近 3 時間）
   ```powershell
   az monitor activity-log list -g $Rg --offset 3h --query "[?contains(operationName.value, 'Microsoft.Storage/storageAccounts')].{time:eventTimestamp, operation:operationName.value, caller:caller}" -o table
   ```
   `caller` が 1 のプリンシパル ID と同じなら、マネージド ID の操作です。

## 5. user 方式での確認（user 方式を有効にしている場合）
自分に Contributor があり、`server` 方式では許可していないリソースグループを使います。サブスクリプションの所有者なら、新しい名前（例: `rg-tt-mcp-user`。無ければ、自分の権限で作られます）でかまいません。使うリソースグループの名前を変数に入れておきます（6 章の後片付けで使います）。

```powershell
$UserRg = "rg-tt-mcp-user"   # user 方式の確認に使うリソースグループ
```

依頼に「azure_identity=user で」と付けます（例: 「azure_identity=user で、リソースグループ rg-tt-mcp-user に `ttmcp2` を DB を永続化してデプロイする計画を作って」）。

| # | 依頼・操作 | 期待する結果 |
|---|---|---|
| U1 | `persist_db=true` で計画 → 実行 | `identity` が user 方式。デプロイが成功し、`storage_account` が返る |
| U2 | 既定の削除の計画 → 実行 | ストレージアカウントまで削除される |
| U3 | アクティビティログ（4 の「アクティビティログの確認」の 2） | ストレージアカウントの作成・削除の `caller` が、自分のユーザー名 |

`user` 方式では、MCP サーバーへのサインインで MFA を行っている必要があります。「多要素認証（MFA）が必要です」というエラーになった場合は、`/mcp` から `timetracker` にサインインし直し、MFA を行ってください（詳細は [`docs/design/mcp/mcp-phase4c.md`](mcp-phase4c.md) の「10. 確認結果」の 3）。

## 6. 後片付けと記録
### 6.1 確認で作った Timetracker の環境
- [ ] Claude Code に「`ttmcp` の状態を教えて」と頼み、`exists` が false になっている（残っていれば、削除の計画 → 実行を頼む）。5 章で `user` 方式も確かめた場合は、そちらのアプリ名も同じように確かめる

### 6.2 MCP サーバーと、配置のスクリプトが作ったもの
1. Claude Code から MCP サーバーの登録を外します
   ```powershell
   claude mcp remove timetracker
   ```
2. アプリ登録を 2 つ削除します（[az ad app delete](https://learn.microsoft.com/ja-jp/cli/azure/ad/app#az-ad-app-delete)。アプリロールの割り当てと同意も一緒に消えます）
   ```powershell
   az ad app list --display-name "TTDeployTool MCP" --query "[].{name:displayName, appId:appId}" -o table   # 名前に ($McpName) が付いた 2 つ
   az ad app delete --id <サーバーのアプリの appId>
   az ad app delete --id <クライアントのアプリの appId>
   ```
3. リソースグループを削除します（[az group delete](https://learn.microsoft.com/ja-jp/cli/azure/group#az-group-delete)。中のリソースもすべて消えます。Environment があると 10〜15 分かかります）
   ```powershell
   az group delete --name $AllowedRg --yes
   az group delete --name $McpRg --yes
   az group delete --name $UserRg --yes   # 5 章で、このために新しく作った場合だけ（もともとあったリソースグループは消さない）
   ```
4. 残っていないことを確かめます
   ```powershell
   az group list --query "[?name=='$McpRg' || name=='$AllowedRg' || name=='$UserRg'].name" -o tsv   # 何も表示されない（5 章でもともとあったリソースグループを使った場合は、その名前だけが表示される）
   ```

### 6.3 記録
- [ ] 結果を `docs/REVIEW_ISSUES.md` の No.46（未確認の「HTTP 版の MCP サーバーでの確認」）と、[`docs/design/common/db-persistence.md`](../common/db-persistence.md) の「14. 実施状況」に記入する

## 7. うまくいかないとき
| 症状 | 考えられる原因と対処 |
|---|---|
| 3.2 のスクリプトが `az login` を求めて止まる、権限の不足（`AuthorizationFailed`）で失敗する | サインインしていない、またはサブスクリプションの所有者でない。2.2・2.3 を確かめる |
| 3.2 のスクリプトがアプリ登録の作成で失敗する（`Insufficient privileges`） | Entra ID でアプリを登録する権限が無い。2.3 の 2 つ目を確かめる |
| 3.4 のサインインで `AADSTS50011`（リダイレクト URI の不一致）になる | `claude mcp add` の `--callback-port` が 8765 でない。`claude mcp remove timetracker` で外して、3.4 の 1 をやり直す |
| 3.5 で「アプリロールが必要です」というエラーになる | 3.3 の割り当ての前にサインインした。`/mcp` から `timetracker` にサインインし直す |
| 3.5 の `constraints` に `persist_db` が無い、S3 の `persistence` が無い | イメージが古い。`az containerapp show -n $McpName -g $McpRg --query "properties.template.containers[0].image" -o tsv` で版を確かめ、`$ImageTag` を、作り直した v0.9.0 以降にして 3.2 をやり直す |
| S1 のブロッカーに `Microsoft.Storage/...` の権限の不足が出る | `server` 方式のマネージド ID に、許可したリソースグループの Contributor が無い。3.2 のコマンドをもう一度実行する（割り当て直す） |
| S2 が「ストレージアカウント名は…使われているため作成できません」 | アプリ名を変える（名前はサブスクリプション・リソースグループ・アプリ名から作るため、別の組み合わせなら変わる） |
| S2 で DB アプリの起動に失敗する、マウントのエラーが出る | [Azure Policy](https://learn.microsoft.com/ja-jp/azure/governance/policy/overview) で、ストレージアカウントのアカウントキーによるアクセス（`allowSharedKeyAccess`）やパブリック ネットワーク アクセスを禁止している。管理者に確認する |
| 4 の `az storage file list` が権限の不足（`AuthorizationFailed`）で失敗する | 自分にアクセスキーを取得できるロールが無い（2.3 の前提を確かめる） |
| `az containerapp exec` がつながらない | DB のコンテナが起動中、または停止している。S3 の状態を確かめ、数分待ってからやり直す |
| ジョブが見つからない（`get_job_status` のエラー） | MCP サーバーが再起動した。Azure 側の処理は続いているので、「`ttmcp` の状態を教えて」と頼んで状態を確かめる |
