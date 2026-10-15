# 詳細設計: 管理タグと delete コマンド（feature/cli-delete）

- 対象ブランチ: `feature/cli-delete`
- 全体設計: `MCP-Tool` ブランチの `docs/MCP_TOOL_DESIGN.md`（4.12 削除、4.13 必要な Azure 権限）
- ここでは CLI 向けの具体的な仕様と実装の分担を決める。MCP の `plan_deletion` / `start_deletion` も、ここで作る Application 層を使う

## 1. スコープ
| 項目 | このブランチで行う |
|---|---|
| デプロイ時の管理タグの付与 | ✔ |
| Container App の更新時に既存のタグを引き継ぐ（現状は消える可能性がある不具合の修正） | ✔ |
| `delete` サブコマンド（計画の表示・確認・削除） | ✔ |
| 権限の事前確認（`deploy` と `delete`） | ✔ |
| 読み取り専用の計画処理（`DeletionPlanner` / `DeploymentPlanner`） | ✔ |
| ツールのバージョンのタグ（`tt-tool-version`） | ✘（プロジェクトにバージョンが設定されていないため見送る。判定には使わない） |

## 2. 管理タグ
| キー | 値 |
|---|---|
| `managed-by` | `timetracker-deployment-tool` |
| `tt-app-name` | `--app-name` の値（小文字化後） |

- 付ける対象: Environment（新規作成時のみ）と、3 つの Container App（作成時・更新時とも）
- **既存の Environment は従来どおり変更しない**（タグも付けない）
- Container App の更新時は、既存のタグを読み取り、そこに管理タグを上書き追加して送る。利用者が付けたタグは残る
- 「本ツールが管理している」と判定する条件: `managed-by` が `timetracker-deployment-tool`、かつ `tt-app-name` が対象のアプリ名と一致する（値は大文字小文字を区別しない）
- 定数と判定は Domain の `ManagedTags` に置く。タグの組み立ては `ContainerAppSpecBuilder` で行う（送信内容をテストで検証できるようにするため）

## 3. delete コマンド
### 3.1 書式
```
timetracker-cli delete --subscription <ID> --resource-group <RG> [--app-name <名前>]
                       [--include-untagged] [--dry-run] [--yes] [--auth-mode <モード>] [--verbose]
```
- `deploy` 専用のオプション（`--db-type`、`--db-password` など）を指定するとエラーにする。`delete` 専用のオプション（`--include-untagged`、`--yes`）を `deploy` で指定した場合もエラーにする
- `--app-name` の既定値と検証規則は `deploy` と同じ
- `--dry-run`: 削除計画を表示するだけで、何も変更しない
- `--yes`: 確認の入力を省略する（CI 等での利用向け）
- `--include-untagged`: 管理タグのないリソースも削除の対象にする

### 3.2 処理の流れ
1. 引数の解析と検証（subscription / resource-group は必須。アプリ名の規則）
2. 削除計画の作成（`DeletionPlanner`。読み取りのみ）
3. 計画の表示
4. `--dry-run` なら終了（終了コード 0）
5. 削除するものがなければ「削除するリソースはありません」と表示して終了（0）
6. 権限が不足していれば、不足している権限を表示して終了（1）
7. `--yes` がなければ、アプリ名の入力を求める。一致しなければ中止（1）
8. 削除の実行（`DeletionRunner`）。`-tt` → `-redis` → `-db` → `-env` の順

### 3.3 削除計画の規則
- リソースグループ内の Container App の一覧を 1 回取得し、そこから `{app}-tt` / `{app}-redis` / `{app}-db` を探す
- Container App:
  - 存在しない → 対象外（「存在しません」）
  - 管理タグあり、または `--include-untagged` → 削除する
  - それ以外 → 削除しない（「管理タグがありません（--include-untagged で対象にできます）」）
- Environment（`{app}-env`）:
  - 存在しない → 対象外
  - 管理タグがなく、`--include-untagged` もない → 削除しない
  - リソースグループ内に、この Environment に属していて**削除しない** Container App があれば → 削除しない（「Environment 内に削除しない Container App（名前）が残るため」）
  - それ以外 → 削除する
- 制約: 他のリソースグループにあって同じ Environment に属する Container App は確認しない（本ツールは同じリソースグループに作るため）。その場合は Azure 側で Environment の削除が失敗し、エラーとして表示される
- 削除する DB アプリがあれば「DB のデータはすべて失われます」と警告する
- リソースグループは常に削除しない（計画の表示にも明記する）

### 3.4 削除の実行
- 計画で「削除する」となったものだけを、上の順に削除する
- 計画の作成後に削除されていたものは、何もせず成功扱いにする（もう一度実行しても同じ結果になる）
- 途中で失敗した場合は、そこで止めてエラーを表示する（終了コード 1）。もう一度実行すれば、残りが削除される

### 3.5 表示例
```
[INFO] 削除計画（リソースグループ rg-tt-verify は削除しません）
[INFO]   削除する     demo-tt (Container App)
[INFO]   削除する     demo-redis (Container App)
[INFO]   削除しない   demo-db (Container App): 管理タグがありません（--include-untagged で対象にできます）
[INFO]   削除しない   demo-env (Environment): Environment 内に削除しない Container App（demo-db）が残るため
[WARN] DB アプリを削除すると DB のデータはすべて失われます。
削除するには、アプリ名 'demo' を入力してください:
```

## 4. 権限の事前確認
### 4.1 方法
- `GetAzurePermissionsForResourceGroupsAsync`（Permissions - List For Resource Group）で、実行する ID のリソースグループでの `actions` / `notActions` を取得する
- 必要な権限ごとに、Domain の `PermissionEvaluator` で判定する。いずれかの権限セットで `actions` に一致し、かつ同じセットの `notActions` に一致しなければ「あり」。ワイルドカード `*` は任意の文字列に一致する。大文字小文字は区別しない
- `/*/` は 0 階層にも一致させる（例: `containerApps/*/write` は `containerApps/write` にも一致）。Azure 上で、Reader と Container Apps Contributor だけを持つサービスプリンシパルが Container App を更新できる（`containerApps/*/write` で `containerApps/write` が許可される）ことを確認した。判定に迷う場合も、事前確認が誤って処理を止めないよう許可側に倒す
- リソースグループが存在しない場合は確認できない（リソースグループより上のスコープで一覧を取得する API がない）。この場合は「確認できなかった」として処理を続ける。デプロイでは最初の手順がリソースグループの作成なので、権限がなければそこで失敗し、リソースは何も作られない
- 一覧の取得自体に失敗した場合も「確認できなかった」とし、警告を出して処理を続ける
- 制約: 拒否割り当てや Azure Policy による拒否は反映されない（実行時のエラーとして表示される）

### 4.2 必要な権限
| 処理 | 必要な権限 |
|---|---|
| デプロイ（共通） | `Microsoft.App/containerApps/read`、`Microsoft.App/containerApps/write`、`Microsoft.App/managedEnvironments/read`、`Microsoft.App/managedEnvironments/join/action` |
| デプロイ（Environment を新規作成する場合） | 上記 ＋ `Microsoft.App/managedEnvironments/write` |
| 削除（Container App を削除する場合） | `Microsoft.App/containerApps/delete` |
| 削除（Environment を削除する場合） | `Microsoft.App/managedEnvironments/delete` |

### 4.3 CLI での扱い
- `deploy`: 検証のあと、デプロイの前に確認する。不足があれば、不足している権限を表示して終了（1）。確認できなかった場合は `--verbose` 時に理由を表示して続ける
- `delete`: 計画の作成時に確認し、計画の表示に含める。不足があれば削除を実行しない（`--dry-run` では表示だけ）

## 5. 実装の分担
| 層 | 追加・変更 |
|---|---|
| Domain | `ManagedTags`（定数・判定）、`PermissionSet`（`actions` / `notActions`）、`PermissionEvaluator`、`AzurePermissions`（必要な権限の定数） |
| Infrastructure.Azure | `IAzureDeploymentGateway` に読み取り（リソースグループ・Environment・Container App の一覧）、削除、権限の取得を追加。`ContainerAppSpecBuilder` に管理タグの付与。`AzureSdkExecutor` で既存タグの引き継ぎ |
| Application | `PermissionChecker` / `PermissionCheckResult`、`DeploymentPlanner`（デプロイに必要な権限の確認）、`DeletionPlanner` / `DeletionPlan` / `DeletionTarget`、`DeletionRunner` |
| CLI | `CliArgs` にサブコマンド（`deploy` / `delete`）とサブコマンドごとの使えるオプション、`CliApplication` に delete の処理と確認入力（`TextReader` を差し替え可能に）、deploy の権限確認 |

## 6. テスト
- C1（分岐網羅）100% を維持する
- Domain: `ManagedTags` の判定、`PermissionEvaluator`（完全一致、ワイルドカード、`notActions`、大文字小文字、複数の権限セット、Contributor / Reader / Container Apps Contributor の定義）
- Infrastructure: 管理タグの付与と既存タグの引き継ぎ（送信する `ContainerAppData` を検証）、Environment の新規作成時のタグ、読み取り・削除・権限の取得（`ArmClient` のモック）
- Application: 削除計画の各規則（3.3）、削除の順序と、計画後に消えていたものの扱い、権限の判定、確認できなかった場合
- CLI: サブコマンドとオプションの組み合わせ、計画の表示、`--dry-run`、確認の入力（一致・不一致・入力なし）、`--yes`、権限不足、削除の失敗、deploy の権限不足
