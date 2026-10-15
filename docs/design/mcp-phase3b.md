# 詳細設計: デプロイの計画・実行・ジョブ・状態確認（Phase 3b）

- 対象ブランチ: `MCP-Tool`
- 全体設計: `docs/MCP_TOOL_DESIGN.md`（3.4 状態の扱い、4.7 `plan_deployment` / `start_deployment`、4.8 `get_job_status` / `list_jobs`、4.9 `get_deployment_info`、5.4 ログ、5.6 同時実行）
- 前の段階: `docs/design/mcp-phase3a.md`（サーバーの基盤と読み取り系ツール）

## 1. スコープ
| 項目 | Phase 3b |
|---|---|
| `plan_deployment` / `start_deployment`（計画 → 確認 → 実行の 2 段階。実行は非同期ジョブ） | ✔ |
| `get_job_status` / `list_jobs` | ✔ |
| `get_deployment_info` | ✔ |
| Application: 計画（`DeploymentPlanner.PlanAsync`）、計画ストア、ジョブ管理、状態の読み取り（`DeploymentInspector`）、呼び出し元（`ICallerContext`） | ✔ |
| 進捗の通知（`notifications/progress`） | ✘（下記 6.2。ポーリングで完結させる） |
| 削除のツール、Resources / Prompts、配布 | ✘（Phase 3c） |

## 2. 計画（Application `DeploymentPlanner.PlanAsync`）
読み取りだけで、デプロイで何が起きるかを決める（全体設計 4.7 の 2〜6）。

| 対象 | 判定 | 結果 |
|---|---|---|
| リソースグループ | 無い | `create` |
| | ある・リージョンが同じ | `use_existing` |
| | ある・リージョンが違う | `use_existing` + 警告（CLI と同じ。Container Apps は指定したリージョンに作る） |
| Environment（`{app}-env`） | 無い | `create`（所要時間が長い） |
| | ある・リージョンが同じ | `use_existing` |
| | ある・リージョンが違う | **ブロッカー**（CLI と同じメッセージ。実行すると途中で失敗するため） |
| `-db` / `-redis` / `-tt` | 無い / ある | `create` / `update` |
| `-db` が既にある | – | `data_loss: true` + 警告。`start_deployment` で `acknowledge_data_loss=true` が必須 |
| 権限 | 不足 | **ブロッカー**（不足している権限と、README の該当箇所の案内） |
| | 確認できない | 警告（理由。処理は止めない。CLI と同じ考え方） |

- リージョンの比較は `AzureLocation` で行う（`japaneast` と `Japan East` を同じとみなす）
- 権限の確認は、既存の `CheckPermissionsAsync` と同じ規則（Phase 2）。読み取った状態を使い回し、Azure への読み取りを重ねない
- CLI は従来どおり `CheckPermissionsAsync` を使う（CLI の挙動は変えない）

### 2.1 実行の直前の再確認
`start_deployment` は、ジョブを始める前にもう一度 `PlanAsync` を実行し、計画時と比べる。
- 操作の一覧（リソースの種類・名前・操作・データ消失の有無）が変わっていれば中止し、「計画時から状態が変わりました。`plan_deployment` をやり直してください」と返す（例: 計画時に無かった DB アプリが作られていた）
- 新たにブロッカーがあれば中止する
- 計画は使用済みにする（中止した場合も、同じ計画では実行しない）

## 3. 計画ストアとジョブ（Application）
### 3.1 呼び出し元（`ICallerContext`）
- `CallerId`（stdio は固定値 `local`。HTTP では Entra ID の `oid`）と、利用者に見せる `Display`
- 計画とジョブには作成者を記録し、ほかの呼び出し元からは「存在しない」と同じに扱う（全体設計 3.4 の 3）

### 3.2 計画ストア（`IPlanStore` / `InMemoryPlanStore`）
- `plan_id` は `pln_` + 乱数（32 桁の 16 進数）。推測されにくい値にする
- 有効期限は 15 分。時刻は `TimeProvider` で取得する（テストで時刻を進められるように）
- `Take(planId, callerId)` は、計画を取り出して同時に無効にする（同じ計画で 2 回実行できない）。結果は「取り出せた / 存在しない / 期限切れ」
- 期限切れの計画は、保存・取り出しのときに捨てる
- 計画には解決済みの `DeployOptions`（パスワードを含む）を持つ。メモリ内だけに置く（永続ストアにする場合はパスワードを保存しない。Phase 4 以降）

### 3.3 ジョブ（`JobManager` / `IJobStore` / `InMemoryJobStore`）
- `job_id` は `job_` + 乱数。種類は `deploy`（Phase 3c で `delete` を追加）
- 状態: `running` → `succeeded` / `failed`（`queued` は使わない。開始したらすぐ実行する）
- 同じ対象（`subscription/resource_group/app_name`。大文字小文字を区別しない）のジョブが実行中なら、新しいジョブを拒否する（全体設計 5.6）。確認と登録は排他して行う
- ジョブの処理は `Task.Run` で動かし、ツールはすぐに `job_id` を返す
- ジョブのログ（`JobLog`）: レベル（info / verbose / warn / success / error）・メッセージ・時刻。`IDeploymentLogger` を実装した `JobLogger` が書き込み、サーバーのログ（stderr）にも出す
  - `get_job_status` の `log_offset` で、続きから取得できる
  - デバイスコード認証のメッセージ（`デバイスコード認証:` で始まる）は `device_code_message` にも入れる
- 進捗: `DeploymentRunner` の `IProgress<DeploymentProgress>` を受けて、`current_step`（何番目か・全体の数・内容）を更新する
- 失敗: 例外のメッセージを `error` に入れ、どの手順で失敗したかを示す。スタックトレースはサーバーのログにだけ出す
- 実行中のジョブはキャンセルしない（全体設計 4.8。Azure の操作を途中で止めると中途半端な状態が残るため）
- ジョブはプロセス内のメモリに置く（stdio ではサーバーの終了で消える。Azure の状態は `get_deployment_info` で確認できる）

## 4. 状態の読み取り（Application `DeploymentInspector`、Infrastructure）
- リソースグループ（有無・リージョン）、Environment（有無・リージョン・プロビジョニング状態・管理タグ）、3 つの Container App の詳細を返す
- Container App の詳細は、Infrastructure に `ListContainerAppDetailsAsync` を追加して読む: プロビジョニング状態、稼働状態、イメージ、CPU、メモリ、最新のリビジョン、FQDN、管理タグ
- Timetracker の URL は `-tt` の FQDN から作る（`https://...`）

## 5. ツール
### 5.1 `plan_deployment`
- 入力: 共通パラメータ（Phase 3a と同じ）
- 検証で誤りがあれば、Azure は読まずに `blockers` に誤りを入れて返す（`plan_id` は null）
- ブロッカーがあれば `plan_id` は null（実行できない計画は発行しない）
- 出力: `plan_id`、`expires_at`、`blockers`、`identity`（`auth_mode` と表示用の文）、`permission_check`（`status`: ok / missing / not_checked、`missing`、`note`）、`actions`、`warnings`、`requires_data_loss_ack`、`estimated_duration`、`resolved`（Phase 3a と同じ。パスワードはマスク）

### 5.2 `start_deployment`
- 入力: `plan_id`（必須）、`acknowledge_data_loss`（既定 false）
- デプロイのパラメータは受け取らない（計画と違う内容で実行されないように）
- 次の場合は `isError: true` で返し、ジョブを始めない
  - 計画が無い・期限切れ・使用済み・ほかの呼び出し元のもの（「存在しない」と同じメッセージ）
  - データ消失の同意が必要なのに `acknowledge_data_loss` が true でない（このとき計画は使用済みにしない。同意して再実行できるように）
  - 直前の再確認（2.1）で状態が変わった、またはブロッカーがある
  - 同じ対象のジョブが実行中
- 出力: `job_id`、`kind`、`status`、`poll_hint`

### 5.3 `get_job_status`
- 入力: `job_id`（必須）、`log_offset`（既定 0）
- 出力: 全体設計 4.8 の形。`result`（成功時: リソースグループ、Environment、各アプリ名、`endpoint`）、`error`（失敗時: メッセージと失敗した手順）、`device_code_message`
- 存在しない・ほかの呼び出し元のジョブは `isError: true`

### 5.4 `list_jobs`
- 入力: `status`（任意。running / succeeded / failed）
- 出力: 呼び出し元のジョブの一覧（新しい順。ログは含めない）

### 5.5 `get_deployment_info`
- 入力: `subscription` / `resource_group`（省略時はサーバーの既定値）、`app_name`（既定 timetracker）
- 出力: 4 の内容。`exists`（リソースグループがあり、いずれかのリソースがあるか）と `endpoint`

### 5.6 annotations
| ツール | readOnly | destructive | idempotent | openWorld |
|---|---|---|---|---|
| `plan_deployment` | ✔ | – | ✔ | ✔ |
| `start_deployment` | – | ✔ | – | ✔ |
| `get_job_status` | ✔ | – | ✔ | – |
| `list_jobs` | ✔ | – | ✔ | – |
| `get_deployment_info` | ✔ | – | ✔ | ✔ |

## 6. その他
### 6.1 Azure の資格情報
- サーバーの認証モード（`TT_MCP_AUTH_MODE`）で、ツールの呼び出しごと（ジョブでは開始時）に `AzureSdkExecutor` を作る。ジョブのログに書けるよう、ロガーにはジョブの `JobLogger` を渡す

### 6.2 進捗の通知を送らない理由
`start_deployment` はジョブを始めてすぐに返すため、ジョブの進捗はツールの呼び出しが終わった後に起きる。MCP の進捗通知はリクエストに結び付くものなので、終わったリクエストの進捗を送ることになり、クライアントによっては扱われない。全体設計 5.7 のとおり `get_job_status` のポーリングで同じ情報が得られるため、Phase 3b では送らない。MCP の Tasks（長時間の処理の標準）が安定したら見直す（全体設計 4.7 の「将来」）。

### 6.3 instructions
Phase 3a の内容に、次を加える。
- デプロイは必ず `plan_deployment` → 結果（actions・warnings）を利用者に示して確認 → `start_deployment` の順に行う。`acknowledge_data_loss` は、DB のデータが消えることに利用者が明示的に同意した場合だけ true にする
- `start_deployment` の後は、`get_job_status` を 15〜30 秒の間隔で呼ぶ
- 「デプロイと削除のツールはまだ無い」の記述を、削除だけに直す

## 7. テスト
- C1（分岐網羅）100% を維持する（`Program.cs` の終了処理を除く）
- Application: 計画の各規則（2 の表）、再確認での中止、計画ストア（期限切れ・使用済み・別の呼び出し元）、ジョブ（成功・失敗・同じ対象の二重実行の拒否・ログの差分取得・進捗・デバイスコードのメッセージ）、`DeploymentInspector`
- Infrastructure: `ListContainerAppDetailsAsync`（`ArmClient` のモック）
- MCP: 各ツールの正常・エラーの分岐（ゲートウェイのモック）、パスワードが結果・ログに含まれないこと
- Azure（手動）: MCP のツールで新規デプロイ → URL が返る → 再デプロイ（データ消失の同意が必要）→ `get_deployment_info`。`list_subscriptions` の成功も確認する（Phase 3a で残した確認）。後片付けは CLI の `delete` で行う
