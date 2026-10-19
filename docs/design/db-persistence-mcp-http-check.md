# HTTP 版 MCP サーバーでの DB データの永続化の確認手順

PR #38（REVIEW_ISSUES No.46）で追加した DB データの永続化（`persist_db`）と、削除のときにデータを残す指定（`keep_db_data`）を、Azure 上の HTTP 版の MCP サーバーから AI アシスタント経由で確かめる手順です。

- **前提とする環境**: Phase 4c の手順（[`docs/design/mcp-phase4c.md`](mcp-phase4c.md) の「4. 手順（スクリプト）」）で、すでに Azure Container Apps に配置してある HTTP 版の MCP サーバー。まだ配置していない場合は、先にその手順で配置してください
- **所要時間**: 1 時間ほど（Environment の削除に、1 回 10〜15 分かかります）
- **料金**: Container Apps とストレージアカウントの料金が少しかかります。最後にすべて削除します
- **結果の記録先**: `docs/REVIEW_ISSUES.md` の No.46 と、[`docs/design/db-persistence.md`](db-persistence.md) の「14. 実施状況」

## 1. この手順で使う言葉

| 言葉 | 意味 |
|---|---|
| MCP サーバー（HTTP 版） | AI アシスタントから URL で接続して使う、本ツールのサーバー（[MCP_SERVER.md の HTTP 版](../MCP_SERVER.md#http-版開発中)）。Azure Container Apps の [Container App](https://learn.microsoft.com/ja-jp/azure/container-apps/overview) として動いている |
| [Environment](https://learn.microsoft.com/ja-jp/azure/container-apps/environment) | Container App を入れる箱（Container Apps 環境）。MCP サーバー用と、確認で作る Timetracker 用の 2 つが出てくる |
| [マネージド ID](https://learn.microsoft.com/ja-jp/entra/identity/managed-identities-azure-resources/overview) | MCP サーバーが Azure を操作するときに使う ID。パスワードを持たない |
| `server` 方式 | MCP サーバーのマネージド ID の権限で Azure を操作する方式。操作できるのは、管理者が許可したリソースグループだけ |
| `user` 方式 | サインインした利用者本人の Azure の権限で操作する方式 |
| 許可したリソースグループ | `server` 方式で操作してよいリソースグループ。配置のスクリプトの `-AllowedResourceGroups` で指定し、マネージド ID に [Contributor（共同作成者）](https://learn.microsoft.com/ja-jp/azure/role-based-access-control/built-in-roles/general#contributor) が割り当てられている |
| [ストレージアカウント](https://learn.microsoft.com/ja-jp/azure/storage/common/storage-account-overview) / [Azure Files](https://learn.microsoft.com/ja-jp/azure/storage/files/storage-files-introduction) | DB のデータを置く場所。永続化を指定すると、ストレージアカウントの中にファイル共有 `{アプリ名}-db` を作り、DB のコンテナに[マウント](https://learn.microsoft.com/ja-jp/azure/container-apps/storage-mounts)する |
| [アクティビティログ](https://learn.microsoft.com/ja-jp/azure/azure-monitor/platform/activity-log) | Azure のリソースに対して、だれがいつ何をしたかの記録 |
| 計画（`plan_id`） | MCP のツールは、Azure を変更する前に計画を作り、利用者が確認してから実行する。計画を作るツールが `plan_deployment` / `plan_deletion`、実行するツールが `start_deployment` / `start_deletion` |

## 2. 準備
### 2.1 使うツール
| ツール | 確かめ方 | 入れ方 |
|---|---|---|
| PowerShell 7 以上 | `pwsh --version` | [PowerShell のインストール](https://learn.microsoft.com/ja-jp/powershell/scripting/install/installing-powershell) |
| Azure CLI と、拡張機能 `containerapp` | `az version`（`extensions` に `containerapp` がある） | [Azure CLI のインストール](https://learn.microsoft.com/ja-jp/cli/azure/install-azure-cli)。拡張機能は `az extension add --name containerapp` |
| Claude Code（または VS Code） | MCP サーバーに接続できること（[MCP_SERVER.md の HTTP 版の導入](../MCP_SERVER.md#導入-1)、[Claude Code での MCP の使い方](https://code.claude.com/docs/ja/mcp)） | Phase 4c の「4.3 クライアントの設定」 |

この手順のコマンドは PowerShell で実行します（以下の `$` で始まる名前は、PowerShell の変数です）。

### 2.2 Azure にサインインする
[Azure CLI でサインイン](https://learn.microsoft.com/ja-jp/cli/azure/authenticate-azure-cli-interactively)し、確認に使うサブスクリプションを選びます。

```powershell
az login
az account set --subscription <サブスクリプション ID>
az account show --query "{user:user.name, subscription:id}" -o table   # 自分とサブスクリプションが表示される
```

### 2.3 前提の確認
- [ ] リソースプロバイダー `Microsoft.Storage` が登録されている（[リソースプロバイダーの登録](https://learn.microsoft.com/ja-jp/azure/azure-resource-manager/management/resource-providers-and-types)）
  ```powershell
  az provider show --namespace Microsoft.Storage --query registrationState -o tsv   # Registered と表示される
  # 表示されない場合は登録する（数分かかる）
  az provider register --namespace Microsoft.Storage --wait
  ```
- [ ] 永続化の機能を含む版（v0.9.0 以降）のイメージが GHCR にある（[リリースの一覧](https://github.com/yamadakou/timetracker-deployment-tool/releases)の「ダウンロード」に `ghcr.io/yamadakou/timetracker-mcp:<版>` がある）
- [ ] 自分に、`server` 方式で使うリソースグループの Contributor など、次の 2 つができるロールがある（4 の「データの確認」で使う。MCP サーバーのマネージド ID の権限とは別に必要）
  - ストレージアカウントの[アクセスキー](https://learn.microsoft.com/ja-jp/azure/storage/common/storage-account-keys-manage)の取得（`Microsoft.Storage/storageAccounts/listKeys/action`）
  - [コンテナのコンソールへの接続](https://learn.microsoft.com/ja-jp/azure/container-apps/container-console)（`az containerapp exec`）
- [ ] （`user` 方式も確かめる場合）自分に Contributor があり、`server` 方式では許可していないリソースグループがある

## 3. MCP サーバーのイメージを新しい版にする
配置のスクリプト `scripts/deploy-mcp-http.ps1` を、**MCP サーバーを配置したときと同じ引数**で、`-ImageTag` だけを新しい版にして実行し直します。スクリプトは、作成済みのものを作り直さずに、設定とイメージだけを合わせます（`latest` のままでは新しいリビジョンが作られないことがあるため、版を指定します）。

### 3.1 配置したときの引数を調べる
配置したときの引数が分からない場合は、Azure にある MCP サーバーの設定から調べます。

1. MCP サーバーの Container App を探します（Container App の名前の既定は `tt-mcp`）
   ```powershell
   az containerapp list --query "[?contains(properties.template.containers[0].image, 'timetracker-mcp')].{name:name, resourceGroup:resourceGroup, image:properties.template.containers[0].image}" -o table
   ```
   表示された `name` と `resourceGroup` を変数に入れておきます。
   ```powershell
   $McpName = "tt-mcp"        # 上の name
   $McpRg   = "rg-tt-mcp"     # 上の resourceGroup
   ```
2. Container App の環境変数（`TT_MCP_HTTP__` で始まるもの）を表示します（[az containerapp show](https://learn.microsoft.com/ja-jp/cli/azure/containerapp#az-containerapp-show)）
   ```powershell
   az containerapp show -n $McpName -g $McpRg --query "properties.template.containers[0].env[?starts_with(name, 'TT_MCP_HTTP__')].{name:name, value:value}" -o table
   az group show -n $McpRg --query location -o tsv   # 場所
   ```
3. 表示された値を、スクリプトの引数に置き換えます

   | スクリプトの引数 | 調べ方 |
   |---|---|
   | `-SubscriptionId` | 2.2 で選んだサブスクリプション ID |
   | `-ResourceGroup` | `$McpRg` |
   | `-Name` | `$McpName`（`tt-mcp` なら省略できる） |
   | `-Location` | `az group show` の結果（`japaneast` なら省略できる） |
   | `-IdentityModes` | 環境変数 `TT_MCP_HTTP__IDENTITY_MODES` の値（例: `server,user`） |
   | `-AllowedResourceGroups` | 環境変数 `TT_MCP_HTTP__SERVER_ALLOWED_RESOURCE_GROUPS` の値の、`/resourceGroups/` の後ろの名前（複数あればカンマ区切り）。この環境変数が無ければ指定しない |
   | `-CanCreateResourceGroup` | 環境変数 `TT_MCP_HTTP__SERVER_CAN_CREATE_RESOURCE_GROUP` が `true` のときだけ付ける |
   | `-ClaudeCodeCallbackPort` | 既定の 8765 から変えていなければ省略する |

### 3.2 スクリプトを実行する
リポジトリのフォルダーで、3.1 で調べた引数に `-ImageTag <新しい版>` を加えて実行します。Phase 4c の確認環境の場合は、次のとおりです。

```powershell
pwsh scripts/deploy-mcp-http.ps1 -SubscriptionId <サブスクリプション ID> -ResourceGroup rg-tt-mcp `
  -IdentityModes server,user -AllowedResourceGroups rg-tt-mcp-demo -ImageTag 0.9.0
```

- 最後に「完了しました。」と、MCP のエンドポイントとスコープが表示されれば成功です
- テナントの管理者でない場合、「管理者の同意に失敗しました。」という警告が出ます。`user` 方式を自分で使う分には、そのまま続けて問題ありません（詳細は Phase 4c の「10. 確認結果」の 4）

### 3.3 新しい版で動いていることを確かめる
- [ ] 実行中のイメージが新しい版になっている
  ```powershell
  az containerapp show -n $McpName -g $McpRg --query "properties.template.containers[0].image" -o tsv   # ghcr.io/yamadakou/timetracker-mcp:0.9.0
  ```
- [ ] `https://<FQDN>/healthz` をブラウザーで開くと `ok` と表示される（`<FQDN>` は、スクリプトの出力の「MCP のエンドポイント」の `/mcp` より前の部分）
- [ ] AI アシスタントから MCP サーバーに接続し（Claude Code なら `/mcp` で `timetracker` を選んでサインイン）、「timetracker の get_deployment_options を呼んで、制約を見せて」と頼むと、`constraints` に `persist_db=true` の説明がある
- [ ] （任意）サーバーのログに、版と接続した利用者が出ている（[ログの表示](https://learn.microsoft.com/ja-jp/azure/container-apps/log-streaming)）
  ```powershell
  az containerapp logs show -n $McpName -g $McpRg --tail 50 --type console   # Server (timetracker-deploy 0.9.0) と、利用者名が出る
  ```

## 4. server 方式での確認
- **アプリ名**: 既存の環境と重ならない名前にします。以下では `ttmcp` とします
- **リソースグループ**: 許可したリソースグループ（3.1 の `-AllowedResourceGroups`）を使います。以下では `rg-tt-mcp-demo` とします
- 「依頼」の列は、AI アシスタントへの頼み方の例です。アシスタントは、ツールの結果（計画の内容など）を表示して、実行してよいかを聞いてきます。結果の中の `persist_db` などの値が見当たらないときは、「ツールの結果をそのまま見せて」と頼んでください

| # | 依頼・操作 | 期待する結果 |
|---|---|---|
| S1 | 「サブスクリプション <ID> のリソースグループ rg-tt-mcp-demo に、アプリ名 `ttmcp` で、DB のデータを永続化してデプロイする計画を作って」（`plan_deployment` に `persist_db=true`。パスワードを聞かれたら、確認用の値を答える） | `persist_db` が true、`storage_account` にストレージアカウント名がある。`actions` に `storage_account` / `file_share` / `environment_storage` の作成が並ぶ。`warnings` に料金と削除での扱いの案内がある。`permission_check` が `ok`、`requires_data_loss_ack` が false、`identity` が server 方式 |
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
   $Rg      = "rg-tt-mcp-demo"   # 確認に使うリソースグループ
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
2. 確認に使ったリソースグループの、ストレージアカウントの操作と操作者を表示します（[az monitor activity-log list](https://learn.microsoft.com/ja-jp/cli/azure/monitor/activity-log#az-monitor-activity-log-list)。`--offset 3h` は直近 3 時間）
   ```powershell
   az monitor activity-log list -g $Rg --offset 3h --query "[?contains(operationName.value, 'Microsoft.Storage/storageAccounts')].{time:eventTimestamp, operation:operationName.value, caller:caller}" -o table
   ```
   `caller` が 1 のプリンシパル ID と同じなら、マネージド ID の操作です。

## 5. user 方式での確認（user 方式を有効にしている場合）
自分に Contributor があり、`server` 方式では許可していないリソースグループを使います。依頼に「azure_identity=user で」と付けます（例: 「azure_identity=user で、リソースグループ <名前> に `ttmcp2` を DB を永続化してデプロイする計画を作って」）。

| # | 依頼・操作 | 期待する結果 |
|---|---|---|
| U1 | `persist_db=true` で計画 → 実行 | `identity` が user 方式。デプロイが成功し、`storage_account` が返る |
| U2 | 既定の削除の計画 → 実行 | ストレージアカウントまで削除される |
| U3 | アクティビティログ（4 の「アクティビティログの確認」の 2） | ストレージアカウントの作成・削除の `caller` が、自分のユーザー名 |

`user` 方式では、MCP サーバーへのサインインで MFA を行っている必要があります。「多要素認証（MFA）が必要です」というエラーになった場合は、Phase 4c の「10. 確認結果」の 3 を参照してください。

## 6. 後片付けと記録
- [ ] 「`ttmcp` の状態を教えて」と頼み、`exists` が false になっている（残っていれば、削除の計画 → 実行を頼む）
- [ ] 確認用にリソースグループを新しく作った場合は削除する（[az group delete](https://learn.microsoft.com/ja-jp/cli/azure/group#az-group-delete)。`az group delete --name <リソースグループ名>`）
- [ ] 結果を `docs/REVIEW_ISSUES.md` の No.46（未確認の「HTTP 版の MCP サーバーでの確認」）と、[`docs/design/db-persistence.md`](db-persistence.md) の「14. 実施状況」に記入する

## 7. うまくいかないとき
| 症状 | 考えられる原因と対処 |
|---|---|
| 3.2 のスクリプトが `az login` を求めて止まる、権限の不足で失敗する | サインインしていない、またはサブスクリプションの所有者（ロールを割り当てられる権限）でない。2.2 をやり直す |
| 3.3 の `constraints` に `persist_db` が無い、S3 の `persistence` が無い | イメージが古い。3.3 の 1 つ目でイメージの版を確かめ、3.2 をやり直す |
| S1 のブロッカーに `Microsoft.Storage/...` の権限の不足が出る | `server` 方式のマネージド ID に、許可したリソースグループの Contributor が無い。3.2 のスクリプトを再実行する（割り当て直す） |
| S2 が「ストレージアカウント名は…使われているため作成できません」 | アプリ名を変える（名前はサブスクリプション・リソースグループ・アプリ名から作るため、別の組み合わせなら変わる） |
| S2 で DB アプリの起動に失敗する、マウントのエラーが出る | [Azure Policy](https://learn.microsoft.com/ja-jp/azure/governance/policy/overview) で、ストレージアカウントのアカウントキーによるアクセス（`allowSharedKeyAccess`）やパブリック ネットワーク アクセスを禁止している。管理者に確認する |
| 4 の `az storage file list` が権限の不足（`AuthorizationFailed`）で失敗する | 自分にアクセスキーを取得できるロールが無い（2.3 の前提を確かめる） |
| `az containerapp exec` がつながらない | DB のコンテナが起動中、または停止している。S3 の状態を確かめ、数分待ってからやり直す |
| ジョブが見つからない（`get_job_status` のエラー） | MCP サーバーが再起動した。Azure 側の処理は続いているので、「`ttmcp` の状態を教えて」と頼んで状態を確かめる |
