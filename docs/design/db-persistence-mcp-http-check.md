# HTTP 版 MCP サーバーでの DB データの永続化の確認手順

PR #38（REVIEW_ISSUES No.46）で追加した `persist_db` / `keep_db_data` を、Azure Container Apps に配置した HTTP 版の MCP サーバーで確かめるための手順です。Phase 4c の環境（`docs/design/mcp-phase4c.md`）をそのまま使います。

- 所要時間: 1 時間ほど（Environment の削除に 1 回 10〜15 分かかります）
- 料金: Container Apps とストレージアカウントの料金が少しかかります。最後にすべて削除します
- 結果の記録先: `docs/REVIEW_ISSUES.md` の No.46 と、`docs/design/db-persistence.md` の 14

## 1. 前提
- [ ] 永続化の機能を含む版をリリースし、GHCR にそのイメージがある（`main` の `f5b7391` 以降をタグ付けしたもの）
- [ ] 確認に使うサブスクリプションで、リソースプロバイダー `Microsoft.Storage` が登録されている
  ```bash
  az provider show --namespace Microsoft.Storage --query registrationState -o tsv   # Registered
  ```
- [ ] `server` 方式で使う、許可したリソースグループ（`TT_MCP_HTTP__SERVER_ALLOWED_RESOURCE_GROUPS`）がある。MCP サーバーのマネージド ID には、配置スクリプトがそこに Contributor を割り当てている（Storage の権限も含まれる）
- [ ] （`user` 方式も確かめる場合）自分に Contributor があるリソースグループがある
- [ ] Claude Code（または VS Code）から HTTP 版の MCP サーバーにサインインできる（Phase 4c の 4.3）

## 2. MCP サーバーのイメージを更新する
前回の配置と同じ引数で、`-ImageTag` だけを新しい版にしてスクリプトを再実行します（`latest` のままでは新しいリビジョンが作られないことがあります）。

```powershell
pwsh scripts/deploy-mcp-http.ps1 <前回と同じ引数> -ImageTag <新しい版（例: 0.9.0）>
```

- [ ] `https://<FQDN>/healthz` が 200 `ok` を返す
- [ ] クライアントで `get_deployment_options` を呼び、`constraints` に `persist_db=true` の説明がある（新しいイメージで動いていることの確認）

## 3. server 方式での確認
アプリ名は、既存の環境と重ならないもの（例: `ttmcp`）にします。リソースグループは、許可したリソースグループを使います。以下の「依頼」は、AI アシスタントへの頼み方の例です。

| # | 依頼・操作 | 期待する結果 |
|---|---|---|
| S1 | 「`ttmcp` を、DB のデータを永続化してデプロイする計画を作って」（`plan_deployment` に `persist_db=true`） | `persist_db` が true、`storage_account` にストレージアカウント名がある。`actions` に `storage_account` / `file_share` / `environment_storage` の作成が並ぶ。`warnings` に料金と削除での扱いの案内がある。`permission_check` が `ok`、`requires_data_loss_ack` が false、`identity` が server 方式 |
| S2 | 計画を承認して実行（`start_deployment`）→ `get_job_status` | `succeeded`。手順は 4 つ（`current_step` が 4/4）。`result` に Timetracker の URL と `storage_account` がある |
| S3 | 「`ttmcp` の状態を教えて」（`get_deployment_info`） | `persistence` が `db_persisted: true`、`storage_account` が S1 の名前、`storage_account_managed: true` |
| S4 | DB のデータが Azure Files に置かれていることを確かめる（下の「データの確認」） | 共有に `pgdata/PG_VERSION` がある。テスト用の行を入れておく |
| S5 | 「`ttmcp` の DB の CPU を 1.25 にして再デプロイする計画を作って」（`persist_db` は指定しない） | `persist_db` が true（今の状態を引き継ぐ）、`requires_data_loss_ack` が false。`warnings` に「データを守るために一度停止してから更新し、開始します」がある |
| S6 | S5 の計画を、`acknowledge_data_loss` なしで実行 | 実行できる（同意を求めない）。ジョブのログに DB アプリの開始が出る。S4 で入れた行が残っている |
| S7 | 「永続化をやめて再デプロイする計画を作って」（`persist_db=false`） | `requires_data_loss_ack` が true。`warnings` に「Timetracker からは空の DB に見えます。これまでのデータは消えずにストレージアカウントに残り…」がある。**実行はしない**（同意なしで `start_deployment` を呼ぶと、実行されずにエラーになることだけ確かめる） |
| S8 | 「`ttmcp` を、DB のデータを残して削除する計画を作って」（`plan_deletion` に `keep_db_data=true`） | ストレージアカウントの対象が `will_delete: false`（理由: `--keep-db-data の指定により残します`）。`keeps_db_data` が true、`kept` にストレージアカウントの案内がある。「DB のデータはすべて失われます」の警告は出ない |
| S9 | S8 を実行（アプリ名を入力して `start_deletion`）→ `get_job_status` | `succeeded`。Container App と Environment は削除され、ストレージアカウントは残る（`get_deployment_info` の `persistence.storage_account` が残っている） |
| S10 | 「`ttmcp` を削除する計画を作って」（既定。`keep_db_data` なし） | ストレージアカウントの対象が `will_delete: true`、`deletes_db_data` が true。「ストレージアカウント…も削除します。元に戻せません（残すには keep_db_data=true）」の警告がある |
| S11 | S10 を実行 → `get_job_status` | `succeeded`。`deletion_result.deleted` にストレージアカウントがある。`get_deployment_info` の `exists` が false |
| S12 | アクティビティログ（リソースグループ） | ストレージアカウントの作成・削除の操作者が、MCP サーバーのマネージド ID |

### データの確認（S4・S6）
MCP のツールには DB の中身を見る機能が無いため、Azure CLI で確かめます。

```bash
RG=<リソースグループ>; ACCOUNT=<S1 のストレージアカウント名>
# 共有の上に PostgreSQL のデータがあるか
az storage file list --account-name $ACCOUNT --share-name ttmcp-db --path pgdata --auth-mode key -o table | head
# テスト用の行を入れる（S4）／残っているか見る（S6）。exec は対話のコンソールが開くので、psql で次を実行する
az containerapp exec -n ttmcp-db -g $RG --command "psql -U postgres"
#   CREATE TABLE persist_check(id int, note text); INSERT INTO persist_check VALUES (1, 'before-redeploy');   -- S4
#   SELECT * FROM persist_check;                                                                         -- S6
```

## 4. user 方式での確認（user 方式を有効にしている場合）
自分に Contributor があり、`server` 方式では許可していないリソースグループを使います。`azure_identity=user` を付けて依頼します。

| # | 依頼・操作 | 期待する結果 |
|---|---|---|
| U1 | `persist_db=true` で計画 → 実行 | `identity` が user 方式。デプロイが成功し、`storage_account` が返る |
| U2 | 既定の削除の計画 → 実行 | ストレージアカウントまで削除される |
| U3 | アクティビティログ | ストレージアカウントの作成・削除の操作者が自分 |

## 5. 後片付けと記録
- [ ] `get_deployment_info` で `exists` が false になっている（残っていれば `plan_deletion` → `start_deletion`）
- [ ] 確認用にリソースグループを新しく作った場合は削除する（`az group delete --name <RG名>`）
- [ ] 結果を `docs/REVIEW_ISSUES.md` の No.46（未確認の「MCP のツールでの Azure 上の確認」）と、`docs/design/db-persistence.md` の 14 に記入する

## 6. うまくいかないとき
| 症状 | 考えられる原因 |
|---|---|
| S1 のブロッカーに `Microsoft.Storage/...` の権限の不足が出る | `server` 方式のマネージド ID に、許可したリソースグループの Contributor が無い（配置スクリプトを再実行する） |
| S2 が「ストレージアカウント名は…使われているため作成できません」 | アプリ名を変える（名前はサブスクリプション・リソースグループ・アプリ名から作るため、別の組み合わせなら変わる） |
| S2 で DB アプリの起動に失敗する、マウントのエラーが出る | Azure Policy で、ストレージアカウントのアカウントキーによるアクセス（`allowSharedKeyAccess`）やパブリック ネットワーク アクセスを禁止している |
| S3 の `persistence` が無い | イメージが古い（2 の確認をやり直す） |
| `get_job_status` でジョブが見つからない | MCP サーバーが再起動した（Azure 側の処理は続いている。`get_deployment_info` で状態を確かめる） |
