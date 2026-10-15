# システムテスト（Azure 上での動作確認）

単体テスト（`tests/*.Tests`）では Azure SDK をモックしているため、Azure の実際の挙動（タグの扱い、権限の判定、削除の所要時間など）は確認できません。システムテスト（`tests/Timetracker.SystemTests`）は、CLI を**本物の Azure** に対して実行し、結果を Azure から直接読んで確かめます。

- 環境変数 `TT_SYSTEMTEST_SUBSCRIPTION_ID` が無い場合はスキップされます。通常の `dotnet test Timetracker.sln` や PR の CI では実行されません
- **料金が発生します**（Azure Container Apps。1 回 20 分前後）。リリース前などに実行してください
- テスト用のリソースは、テストの最後に削除します（下記「後片付け」）

## シナリオ
| テスト | 内容 |
|---|---|
| `DeploymentLifecycleTests` | 実行ごとに専用のリソースグループを作り、次を順に確認する。① デプロイすると 4 つのリソースに管理タグが付き、Timetracker が HTTP で応答する ② 手で付けたタグが再デプロイ後も残る ③ DB アプリの管理タグを外すと、DB アプリと Environment が削除の対象外になる（`--include-untagged` で対象になる） ④ 確認の入力が一致しなければ中止する ⑤ 管理タグがあるものだけ削除される ⑥ 残りを削除してもリソースグループは残る ⑦ 再実行しても失敗しない |
| `LimitedPermissionTests` | 権限の少ないサービスプリンシパルで、権限の事前確認が Azure の実際の挙動と合っていることを確認する。① Environment の新規作成が必要なデプロイは止まり、何も作成しない ② 既存の Environment への Container App の更新は成功する（`containerApps/*/write` で `containerApps/write` が許可される） ③ Environment の削除権限が無いため、削除は止まり何も削除しない |

権限の少ないサービスプリンシパルの設定が無い場合、`LimitedPermissionTests` はスキップされます。

## 準備（最初に 1 回）
### 1. 権限の少ないサービスプリンシパルと専用のリソースグループ
`LimitedPermissionTests` 用です。サービスプリンシパルの権限は、専用のリソースグループの中だけに限ります。
```bash
az group create --name rg-ttst-limited --location japaneast --tags purpose=timetracker-deployment-tool-systemtest
az ad sp create-for-rbac --name ttcli-systemtest-limited   # 表示される appId と password（シークレット）を控える
for role in "Reader" "Container Apps Contributor"; do
  az role assignment create --assignee <appId> --role "$role" \
    --scope /subscriptions/<サブスクリプション ID>/resourceGroups/rg-ttst-limited
done
```
- `password` が `TT_SYSTEMTEST_LIMITED_CLIENT_SECRET` の値です。作成済みの場合（`ttcli-systemtest-limited`）は、シークレットだけを発行します: `az ad app credential reset --id <appId> --append --display-name systemtest --years 1`。他の人と共有せず、ローカルでは環境変数（または実行時の入力）、CI では GitHub のシークレットに保存してください
- `rg-ttst-limited` は削除しないでください（空なら料金はかかりません）。テストはこの中にアプリを作り、最後に削除します

### 2. CI 用（GitHub Actions から Azure への OIDC ログイン）
シークレットを保存せずに Azure にログインできるよう、フェデレーション資格情報を使います。
```bash
az ad app create --display-name timetracker-deployment-tool-ci                     # appId を控える
az ad sp create --id <appId>
az ad app federated-credential create --id <appId> --parameters '{
  "name": "github-systemtest",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:yamadakou/timetracker-deployment-tool:environment:systemtest",
  "audiences": ["api://AzureADTokenExchange"]
}'
az role assignment create --assignee <appId> --role Contributor --scope /subscriptions/<サブスクリプション ID>
```
- Contributor をサブスクリプションに割り当てるのは、テストが実行ごとにリソースグループを作成・削除するためです。ロールの割り当て（`rg-ttst-limited`）は手順 1 で済ませているので、Owner は不要です
- GitHub のリポジトリの Settings → Environments に `systemtest` を作り、次を登録します

| 種類 | 名前 | 値 |
|---|---|---|
| Variables | `AZURE_CLIENT_ID` | 上で作成した CI 用アプリの appId |
| Variables | `AZURE_TENANT_ID` | テナント ID |
| Variables | `AZURE_SUBSCRIPTION_ID` | サブスクリプション ID |
| Variables | `TT_SYSTEMTEST_LIMITED_CLIENT_ID` | 手順 1 のサービスプリンシパルの appId |
| Secrets | `TT_SYSTEMTEST_LIMITED_CLIENT_SECRET` | 手順 1 のシークレット |

必要に応じて、Environment に承認者（Required reviewers）を設定すると、実行前に承認を求められます（料金が発生するため推奨）。

## 実行方法
### ローカル（半自動: サインインだけ手動）
Azure CLI と .NET 10 SDK が必要です。サインインしていなければ、スクリプトがデバイスコード認証を始めます。
```powershell
# Windows（PowerShell）
./scripts/run-system-tests.ps1 -SubscriptionId <サブスクリプション ID> -LimitedClientId <appId>
```
```bash
# macOS / Linux
scripts/run-system-tests.sh <サブスクリプション ID> <appId>
```
- シークレットは環境変数 `TT_SYSTEMTEST_LIMITED_CLIENT_SECRET` から読み、無ければ入力を求めます
- `-LimitedClientId`（第 2 引数）を省略すると、権限不足のシナリオはスキップします
- 結果（TRX）は `TestResults/system` に出力します
- スクリプトが設定した環境変数（`TT_SYSTEMTEST_*`）は、終了時に元に戻します（PowerShell）。そのため、続けて実行した通常の `dotnet test` がシステムテストを実行することはありません
- スクリプトは、どのディレクトリから実行しても構いません（リポジトリのディレクトリに移動して `dotnet test` を実行します）

### CI（自動）
GitHub の Actions → **System Test** → Run workflow で実行します（`workflow_dispatch`）。結果はジョブの成果物（`system-test-results`）に保存されます。

- GitHub の仕様により、手動で実行できるのは既定のブランチ（`main`）にあるワークフローだけです。他のブランチのワークフローは、`main` にマージされるまで Actions の一覧に表示されません
- `main` にマージされた後は、Run workflow の「Use workflow from」で、テストするブランチを選べます

## 設定（環境変数）
| 環境変数 | 必須 | 既定 | 説明 |
|---|---|---|---|
| `TT_SYSTEMTEST_SUBSCRIPTION_ID` | ✔ | – | 無ければすべてスキップ |
| `TT_SYSTEMTEST_LOCATION` | – | `japaneast` | |
| `TT_SYSTEMTEST_LIMITED_RESOURCE_GROUP` | – | `rg-ttst-limited` | |
| `TT_SYSTEMTEST_LIMITED_TENANT_ID` / `_CLIENT_ID` / `_CLIENT_SECRET` | – | – | 無ければ `LimitedPermissionTests` をスキップ |

Azure への接続は、実行環境の Azure CLI のログイン（`az login`、CI では `azure/login`）を使います（CLI は `--auth-mode azure-cli`）。

## 後片付け
- `DeploymentLifecycleTests` は `rg-ttst-<日時>-<乱数>` というリソースグループを作り、テストの最後に削除を開始します（完了は待ちません）
- テストが中断されて残ったリソースグループは、次回の実行の開始時に削除します（名前が `rg-ttst-` で始まり、`ttst-created-at` タグが 3 時間以上前のもの）
- `LimitedPermissionTests` は、`rg-ttst-limited` に作ったアプリをテストの最後に削除します。前回の実行で残ったアプリ（`tt-app-name` タグが `ttst` で始まり、作成から 3 時間以上たったもの）は、開始時に削除します。作成から間もないものは、同時に実行中の別のテスト（ローカルと CI など）のものかもしれないため削除しません。後片付けに失敗した場合は、テストを失敗にします
- 手動で確認する場合: `az group list --query "[?starts_with(name, 'rg-ttst-')].name" -o tsv`
