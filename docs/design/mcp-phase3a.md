# 詳細設計: MCP サーバー（stdio）の基盤と読み取り系ツール（Phase 3a）

- 対象ブランチ: `MCP-Tool`
- 全体設計: `docs/MCP_TOOL_DESIGN.md`（3.3 実行形態、4.0〜4.6 ツール、4.10 パスワード、5.1 instructions、5.4 ログ、5.5 エラー処理、8.2 Phase 3a）
- ここでは Phase 3a で作るものの具体的な仕様と分担を決める。デプロイ・削除のツール（Phase 3b・3c）は扱わない

## 1. スコープ
| 項目 | Phase 3a |
|---|---|
| `TTDeployTool.Controller.Mcp`（アセンブリ名 `ttdeploytool-mcp`）の新設。stdio トランスポート | ✔ |
| `get_deployment_options` / `validate_deployment` / `generate_compose` / `list_subscriptions` | ✔ |
| サーバーの設定（環境変数 `TT_MCP_*`。4.0） | ✔（HTTP 用の設定は除く） |
| サーバー instructions（5.1） | ✔（Phase 3a で使えるツールの範囲） |
| 全エラーを列挙する検証（`DeploymentValidator`）と警告（`DeploymentAdvisor`）を Domain に追加 | ✔ |
| `plan_*` / `start_*` / `get_job_status` / `list_jobs` / `get_deployment_info` | ✘（Phase 3b） |
| `plan_deletion` / `start_deletion`、Resources / Prompts、配布（release.yml）、README の MCP の章 | ✘（Phase 3c） |
| HTTP トランスポート | ✘（Phase 4） |

## 2. 使う SDK
- `ModelContextProtocol` 2.2.0（2026-08-13 公開。実装時点の最新の安定版）
- `Microsoft.Extensions.Hosting`（Generic Host。ログ・DI・終了処理のため）
- 実装の前に、小さなプロジェクトで次を確認した
  - `WithTools<T>(JsonSerializerOptions)` に snake_case の命名規則を渡すと、結果（`structuredContent`）と `outputSchema` が snake_case になる
  - `UseStructuredContent = true` のとき、`structuredContent` と同じ JSON がテキストとしても返る（`structuredContent` を使えないクライアントでも、テキストで続けられる。全体設計 5.7）
  - ツール引数の名前は C# の引数名がそのまま使われる（命名規則は適用されない）
  - `McpException` を投げると `isError: true` になり、メッセージがクライアントに届く。それ以外の例外は詳細を伏せた定型文になる
  - ロガーの出力先を stderr にすると、stdout は JSON-RPC だけになる

## 3. プロジェクト構成
```
src/TTDeployTool.Controller.Mcp/          (新規) ttdeploytool-mcp（Exe）
  Program.cs                             McpServerHost を作って実行するだけ
  McpServerHost.cs                       Generic Host の組み立て（ログを stderr に、ツールの登録、instructions）
  McpServerSettings.cs                   環境変数 TT_MCP_* の読み取りと検証
  DeploymentParameters.cs                ツール引数 → DeployInput への変換、パスワードの取得元の決定
  Tools/DeploymentTools.cs               4 つのツール（[McpServerToolType]）
  Tools/ToolResults.cs                   ツールの結果の型（outputSchema の元）
  ServerInstructions.cs                  instructions の文面
src/TTDeployTool.Domain/Deployment/
  DeploymentValidator.cs                 (新規) 全エラーの列挙（ComposeGenerator.Validate はこれを使う）
  DeploymentAdvisor.cs                   (新規) 警告
src/TTDeployTool.Infrastructure.Azure/
  ISubscriptionDirectory.cs / AzureSubscriptionDirectory.cs   (新規) サブスクリプションの一覧
tests/TTDeployTool.Controller.Mcp.Tests/  (新規) ツールの単体テストと、プロセスを起動するプロトコル結合テスト
```
- MCP は Application を参照しない（Phase 3a のツールは Domain と Infrastructure だけで足りる。Phase 3b で参照を追加する）
- `Program.cs` は 1 行だけにし、組み立ては `McpServerHost` に置いてテストする（CLI と同じ考え方）

## 4. サーバーの設定（環境変数）
| 環境変数 | 既定 | 扱い |
|---|---|---|
| `TT_MCP_AUTH_MODE` | `azure-cli` | `CredentialFactory` の認証モード。不明な値なら**起動時にエラー**（stderr に理由を出して終了コード 1） |
| `TT_MCP_DEFAULT_SUBSCRIPTION` / `TT_MCP_DEFAULT_RESOURCE_GROUP` | なし | ツール引数を省略したときに使う |
| `TT_MCP_DB_PASSWORD` / `TT_MCP_TRACKER_PASSWORD` | なし | 4.10。値はログ・結果に出さない |
| `TT_MCP_OUTPUT_ROOT` | なし | 設定すると `generate_compose` の出力先をこの配下に限る。絶対パスでなければ起動時にエラー |
| `TT_MCP_VERBOSE` | `false` | `true` / `1` で詳細ログ（stderr）。それ以外の値は false |
- 空文字の環境変数は未設定として扱う
- 起動引数 `--transport` は Phase 4 で追加する（Phase 3a は stdio だけ）

## 5. ツール
引数名は CLI のオプションと 1 対 1 の snake_case（全体設計 4.1・4.2）。C# の引数名をそのまま snake_case にする（SDK が引数名をそのまま使うため）。

### 5.1 共通パラメータ
`subscription`, `resource_group`, `location`, `app_name`, `db_type`, `db_name`, `tt_tag`, `db_password`, `tracker_password`, `tt_cpu`, `tt_memory_gi`, `db_cpu`, `db_memory_gi`, `redis_cpu`, `redis_memory_gi`（すべて省略可）。

- 既定値の解決は `DeployOptionsResolver`（CLI と同じ）。`subscription` / `resource_group` は、引数が無ければ `TT_MCP_DEFAULT_*` を使う
- パスワード: 引数 → 環境変数の順。どちらを使ったかを `resolved` に示す（`********（引数）` / `********（環境変数）` / `（未設定）`）
- 数値は JSON の number で受け取る。0 以下などは検証エラーになる（CLI と同じメッセージ）

### 5.2 `get_deployment_options`
全体設計 4.3 のとおり。値はすべて `Defaults` / `AppNameValidator` / `ResourceNaming` / `ContainerImages` から作る。`server` には `transport`、`auth_mode`、`password_env`（`db_password` / `tracker_password` の環境変数が設定済みか。値は返さない）、`default_subscription_set`、`output_root`（設定値。無ければ null）を返す。

### 5.3 `validate_deployment`
- 追加の引数 `target`: `azure`（既定）/ `compose`。それ以外は検証エラーにする
- 結果: `valid`、`errors`（全件）、`warnings`、`resolved`（パスワードはマスク）、`resource_names`
- 検証エラーはツール結果（`valid: false`）で返す（プロトコルのエラーにしない）

### 5.4 `generate_compose`
- 追加の引数 `output_dir`（必須。絶対パス）、`overwrite`（既定 false）
- 次の場合は `isError: true`（`McpException`）にし、何も書き込まない
  - 検証エラー（`target=compose` で検証。全エラーをメッセージに含める）
  - `output_dir` が無い・絶対パスでない
  - `TT_MCP_OUTPUT_ROOT` の配下でない（`..` を含むパスは正規化してから判定する）
  - `TT_MCP_OUTPUT_ROOT` を設定した場合に、ルートより下の既存の階層か、書き込む `docker-compose.yml` / `.env` がシンボリックリンク（Windows ではジャンクションも）である（リンクの先はルートの外かもしれないため。ルート自体はリンクでもよい）
  - `docker-compose.yml` か `.env` が既にあり、`overwrite` が false
- `output_dir` が存在しなければ作成する
- 結果: `compose_path`、`env_path`、`compose_content`（秘密情報を含まない）、`warnings`、`next_steps`。**`.env` の内容は返さない**
- CLI の Dry-run と同じ `ComposeGenerator.GenerateCompose` / `GenerateEnv` を使う（生成物は CLI と同じになる）

### 5.5 `list_subscriptions`
- `TT_MCP_AUTH_MODE` の資格情報で `ArmClient.GetSubscriptions()` を読む
- 結果: `subscriptions`（`subscription_id`, `display_name`, `state`, `tenant_id`）、`auth_mode`
- 認証の失敗（`AuthenticationFailedException` / `CredentialUnavailableException`）は `isError: true` にし、認証モードごとの対処（`azure-cli` なら `az login`）を示す
- `RequestFailedException` はステータスとエラーコードを示して `isError: true`

### 5.6 annotations
| ツール | readOnly | destructive | idempotent | openWorld |
|---|---|---|---|---|
| `get_deployment_options` | ✔ | – | ✔ | – |
| `validate_deployment` | ✔ | – | ✔ | – |
| `generate_compose` | – | – | ✔ | – |
| `list_subscriptions` | ✔ | – | ✔ | ✔ |

## 6. Domain の追加
### 6.1 `DeploymentValidator`
- `GetErrors(DeployOptions, ValidationTarget)` → エラーメッセージの一覧（`ValidationTarget`: `Azure` / `Compose`）
- 確認する内容と順序、メッセージは `ComposeGenerator.Validate` と同じ。最初のエラーで止めずに続ける
- ただし、前のエラーがあると意味のない確認は行わない
  - `db-type` が不正なら、SQL Server のパスワード要件と、`db-type` と `tt-tag` の組み合わせは確認しない
  - `db-password` が不足していれば、SQL Server のパスワード要件は確認しない
  - `tt-tag` が空なら、組み合わせは確認しない
- `ComposeGenerator.Validate` / `ValidateForCompose` は `GetErrors` の最初のエラーで `ArgumentException` を投げる。CLI の挙動とメッセージは変わらない

### 6.2 `DeploymentAdvisor`
`GetWarnings(DeployOptions, ValidationTarget)` → 警告の一覧。
- `db_type=sqlserver` かつ `target=azure`: Azure 上の SQL Server 構成ではサンプル DB を作成できない
- `tt_memory_gi <= 2`: サンプル DB の作成が OutOfMemory で失敗することがある（3Gi 以上を推奨）
- `tt_tag` が既定のタグと違う: 動作を確認しているのは既定のタグであること

## 7. ログ
- Generic Host の既定のコンソールロガーを、すべてのレベルで stderr に出す（`LogToStandardErrorThreshold = Trace`）。stdout には JSON-RPC 以外を一切書かない
- `TT_MCP_VERBOSE=true` で最小レベルを Debug にする。パスワードはログに出さない

## 8. テスト
- C1（分岐網羅）100% を維持する（`Program.cs` の 1 行を除く）
- Domain: `DeploymentValidator`（全エラーの列挙、前のエラーで省く確認、`ComposeGenerator.Validate` と最初のエラーが一致すること）、`DeploymentAdvisor`
- Infrastructure: `AzureSubscriptionDirectory`（`ArmClient` のモック）
- MCP（単体）: 設定の読み取り、引数の変換とパスワードの取得元、各ツールの正常・エラーの分岐、出力先の制限、上書きの禁止、パスワードが結果に含まれないこと
- MCP（プロトコル結合）: ビルドした `ttdeploytool-mcp` を SDK の `StdioClientTransport` で起動し、`initialize`（instructions）・`tools/list`（スキーマ・annotations）・`tools/call` を確かめる。stdout に JSON-RPC 以外が出ないことは、プロトコルが成立することで確かめる
- 手動: MCP Inspector（CLI モード）で `tools/list` と `tools/call`。`generate_compose` で生成したファイルで `docker compose up` が成功し、Timetracker が応答すること
