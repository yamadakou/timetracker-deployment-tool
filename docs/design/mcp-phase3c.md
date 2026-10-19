# 詳細設計: 削除のツール・Resources / Prompts・配布（Phase 3c）

- 対象ブランチ: `MCP-Tool`
- 全体設計: `docs/MCP_TOOL_DESIGN.md`（4.12 `plan_deletion` / `start_deletion`、5.1 instructions、5.2 Resources、5.3 Prompts、6.1 動作確認のクライアント、6.2 配布形態）
- 前の段階: `docs/design/mcp-phase3b.md`（計画ストア・ジョブ管理・デプロイのツール）

## 1. スコープ
| 項目 | Phase 3c |
|---|---|
| `plan_deletion` / `start_deletion`（計画 → 確認 → 実行。実行は非同期ジョブ） | ✔ |
| `get_job_status` / `list_jobs` で削除のジョブも扱う | ✔ |
| instructions（削除の手順を追加） | ✔ |
| Resources: `ttdeploytool://docs/constraints`、`ttdeploytool://docs/rbac`、`ttdeploytool://deployments/{subscription}/{resource_group}/{app_name}` | ✔ |
| Prompts: `deploy-timetracker`、`local-dry-run`、`delete-timetracker` | ✔（`delete-timetracker` は全体設計 5.3 に追加する） |
| 配布: release.yml で `ttdeploytool-mcp-vX.Y.Z.zip`（win-x64 の単一ファイル EXE）を出す | ✔ |
| README の MCP の章（導入・クライアントの設定例・ツール・安全策） | ✔ |
| リリース前確認（全体設計 6.1。利用者が使うクライアントで一連のシナリオ） | Claude Code で確認する。VS Code（GitHub Copilot）はこの環境で動かせないため、オーナーに依頼する |

## 2. 削除の計画と実行（既存の Application を使う）
CLI の `delete` で使っている `DeletionPlanner` / `DeletionRunner`（Phase 2）を、そのまま使う。削除の規則（命名規則と管理タグ、`include_untagged`、別の所有者のタグは対象にしない、Environment に削除しない Container App が残る場合は Environment を削除しない、リソースグループは削除しない）は CLI と同じになる。Application に新しい規則は加えない。

### 2.1 `plan_deletion`
- 入力: `subscription`、`resource_group`（省略時はサーバーの既定値）、`app_name`（既定 timetracker）、`include_untagged`（既定 false）
- `subscription` / `resource_group` が無い、アプリ名が無効な場合は `isError`（Azure は読まない。`get_deployment_info` と同じ）
- `DeletionPlanner.PlanAsync` で計画を作る（読み取りのみ）
- ブロッカー: 権限の不足（CLI と同じメッセージ）
- `plan_id` を発行するのは、削除する対象が 1 つ以上あり、ブロッカーが無い場合だけ（削除するものが無い計画は実行する意味がないため、`plan_id` は null にし、`note` で理由を返す）
- 警告: DB アプリを削除する場合「DB アプリを削除すると DB のデータはすべて失われます。」（CLI と同じ）。管理タグが無いため削除しない対象がある場合は、`include_untagged=true` で対象にできることを伝える。権限を確認できなかった場合はその理由
- 出力: `plan_id`、`expires_at`、`blockers`、`identity`、`permission_check`、`targets`（`name`、`resource`（container_app / environment）、`exists`、`managed`、`will_delete`、`reason`）、`kept`（`resource_group: <RG>（削除しません）`）、`warnings`、`deletes_db`、`confirmation_required`（「start_deletion の confirm_app_name に '<app>' を指定してください」）、`note`

### 2.2 `start_deletion`
- 入力: `plan_id`（必須）、`confirm_app_name`（必須）
- 処理:
  1. 計画を探す（期限切れ・使用済み・ほかの呼び出し元・削除以外の計画は、`start_deployment` と同じく「見つからない」などを返す）
  2. `confirm_app_name` が計画のアプリ名と完全に一致しなければ拒否する（計画は使用済みにしない。利用者に確認して、同じ計画で実行し直せるように）
  3. 計画を使用済みにする
  4. 直前の再確認: `PlanAsync` をやり直し、ブロッカー（権限の不足）があれば中止する。対象ごとの「削除するか」（名前と `will_delete`）が計画と違えば中止し、計画の作り直しを促す
  5. 同じ対象のジョブが実行中なら拒否する（デプロイ・削除とも。`JobManager` の排他）
  6. ジョブとして `DeletionRunner.RunAsync` を始め、すぐに `job_id` を返す。各リソースの削除の直前の確認（Phase 2）もそのまま働く
- 手順の進捗: 削除する対象ごとに `n / 全体: <名前> の削除` を `current_step` に入れ、ジョブのログに「削除中: <名前>」を書く
- 結果: `get_job_status` の `deletion_result`（`resource_group`、`deleted`（削除したもの）、`already_deleted`（計画の後に既に無かったもの）、`kept_resource_group`）。デプロイの結果は従来どおり `result`

### 2.3 annotations
| ツール | readOnly | destructive | idempotent | openWorld |
|---|---|---|---|---|
| `plan_deletion` | ✔ | – | ✔ | ✔ |
| `start_deletion` | – | ✔ | –（一度だけ使える計画） | ✔ |

## 3. Resources
| URI | 内容 | MIME |
|---|---|---|
| `ttdeploytool://docs/constraints` | 本ツールの目的と注意事項（README の「本ツールの目的と注意事項」の要約） | text/markdown |
| `ttdeploytool://docs/rbac` | 必要な RBAC ロールと権限、リソースプロバイダーの登録手順（README の該当節の要約） | text/markdown |
| `ttdeploytool://deployments/{subscription}/{resource_group}/{app_name}`（テンプレート） | `get_deployment_info` と同じ JSON | application/json |

- 文書の Resources はサーバーに埋め込む（README のファイルを配布物に含めないため）。README と内容がずれないよう、テストで主要な記述（ロール名・権限名・プロバイダー名）が含まれることを確かめる
- デプロイの Resource は `get_deployment_info` と同じ処理を呼ぶ（入力の確認とエラーも同じ）

## 4. Prompts
| 名前 | 引数 | 内容 |
|---|---|---|
| `deploy-timetracker` | `db_type`（任意） | 設定の確認 → サブスクリプションの選択 → 検証 → 計画 → 利用者の確認 → 実行 → 状態の確認の手順を、LLM への指示として返す |
| `local-dry-run` | `output_dir`（任意） | ローカルの Docker で試す手順（設定の確認 → 検証 → `generate_compose` → `docker compose up`） |
| `delete-timetracker` | `app_name`（任意） | 削除の手順（計画 → 対象と残るものを示して確認 → 利用者にアプリ名を入力してもらい `confirm_app_name` に渡す → 状態の確認） |

- Prompts は案内にすぎず、安全策（2 段階の確認・同意の引数）はツールの側で強制する（全体設計 5.7）
- 引数は会話から渡る未検証の値なので、プロンプトの文に埋め込むだけにし、ツールの呼び出しには使わない（LLM がツールに渡すときに、ツール側で検証される）

## 5. 配布（release.yml）
- タグのプッシュで動く既存のワークフローに、`ttdeploytool-mcp` の publish（CLI と同じ `win-x64`・単一ファイル・self-contained）と zip を追加し、`ttdeploytool-cli-vX.Y.Z.zip` と並べて Release に載せる
- zip の中身: `ttdeploytool-mcp.exe` だけ（CLI と同じ）
- 確認: ワークフローはタグのプッシュでしか動かないため、同じ `dotnet publish` をこの環境（Linux から win-x64 へのクロス publish）とローカルで実行して、EXE ができることを確かめる。実際の Release は次のリリースで確認する

## 6. README の MCP の章
- 「MCP サーバー（AI アシスタントから使う）」を利用者向けマニュアルに追加する
  - 何ができるか（ツールの一覧と、計画 → 確認 → 実行の流れ）
  - 導入（Release の zip を置く）
  - クライアントの設定例: Claude Code（`claude mcp add`）、VS Code（`.vscode/mcp.json`。パスワードは `inputs` で入力させる）、Claude Desktop（`claude_desktop_config.json`）
  - サーバーの設定（`TT_MCP_*` の環境変数の一覧）
  - 安全策（2 段階の確認、`acknowledge_data_loss` / `confirm_app_name`、`start_*` を自動承認にしない、パスワードは環境変数）
  - Resources / Prompts
- 開発者向け情報の「設計概要」の MCP の記述を、章への案内に変える

## 7. テスト
- C1（分岐網羅）100% を維持する（`Program.cs` の終了処理を除く）
- MCP: `plan_deletion`（入力の誤り、削除するものが無い、権限の不足、DB の警告、管理タグの無い対象の案内、計画の保存）、`start_deletion`（アプリ名の不一致では使用済みにしない、再確認での中止（ブロッカー・対象の変化）、二重実行の拒否、成功時の結果、削除の計画以外の plan_id）、`get_job_status` の `deletion_result`、Resources と Prompts（一覧・読み取り・テンプレート）、stdio の結合テスト（Resources / Prompts の一覧）
- Azure（手動）: MCP のツールでデプロイ → `plan_deletion` → アプリ名の不一致で拒否 → `start_deletion` → `get_job_status` で成功 → `get_deployment_info` で消えていること → もう一度 `plan_deletion` で削除するものが無いこと。リソースグループは手動で削除する
