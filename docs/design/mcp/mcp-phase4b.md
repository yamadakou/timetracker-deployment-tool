# 詳細設計: `user` 方式（利用者本人の権限。Phase 4b）

- 対象ブランチ: `feature/mcp-http`
- 全体設計: `docs/MCP_TOOL_DESIGN.md`（4.11 HTTP 公開時の Azure 資格情報）
- 前の段階: `docs/design/mcp/mcp-phase4a.md`（HTTP トランスポートと `server` 方式）

## 1. 目的
HTTP のサーバーで、サーバーの ID（`server` 方式）のほかに、**サインインした利用者本人の Azure の権限**（`user` 方式）でも操作できるようにする。利用者は自分のロールの範囲でだけ操作でき、Azure のアクティビティログにも本人の操作として残る。

## 2. 設定
| 環境変数 | 既定 | 説明 |
|---|---|---|
| `TT_MCP_HTTP__IDENTITY_MODES` | `server` | 使える方式。`server` / `user` / `server,user` |
| `TT_MCP_HTTP__DEFAULT_IDENTITY_MODE` | `IDENTITY_MODES` の最初 | 両方使える場合の既定。`IDENTITY_MODES` に無い値はエラー |
| `TT_MCP_HTTP__SERVER_ALLOWED_RESOURCE_GROUPS` | なし | `server` が有効な場合だけ必須（4a のまま）。`user` だけの場合は不要（指定しても使わない） |
| `TT_MCP_HTTP__USER_CLIENT_CREDENTIAL` | `managed-identity` | `user` 方式のトークン交換（OBO）で、サーバーのアプリ登録が使う資格情報。`managed-identity` / `client-secret` |
| `TT_MCP_HTTP__CLIENT_SECRET` | なし | `USER_CLIENT_CREDENTIAL=client-secret` の場合だけ必須 |

- 既定は 4a と同じ（`server` だけ）。4a の設定のままで動きが変わらない
- 不正な値・必須の不足は、4a と同じく起動時のエラー（理由を出して終了コード 1）

### OBO のクライアント資格情報
- `managed-identity`（既定・推奨）: サーバーのマネージド ID のトークン（対象 `api://AzureADTokenExchange`）をクライアントアサーションとして使う。アプリ登録に、このマネージド ID を信頼するフェデレーション資格情報を追加しておく（手順は 4c）。シークレットを持たずに済む。ユーザー割り当てのマネージド ID は `AZURE_CLIENT_ID`（4a と同じ）
- `client-secret`: アプリ登録のクライアントシークレット。マネージド ID の無い環境（ローカルでの確認など）向け。シークレットはツールの結果・ログに出さない

## 3. アプリロールと方式
| アプリロール | 使える方式 |
|---|---|
| `TTDeployTool.Deploy.User` | `user`（`user` が有効な場合） |
| `TTDeployTool.Deploy.Server` | `server`（`server` が有効な場合）と、`user` が有効なら `user` |
| どちらもない | 4a と同じく、Azure を操作しないツールと文書だけ |

- サーバーで有効でない方式は、ロールがあっても使えない

## 4. 方式の選び方（`azure_identity` 引数）
- `list_subscriptions` / `plan_deployment` / `plan_deletion` / `get_deployment_info` に任意の引数 `azure_identity`（`server` | `user`）を追加する
- 省略した場合: 既定の方式（`DEFAULT_IDENTITY_MODE`）を、呼び出し元が使えればそれを、使えなければ呼び出し元が使えるもう一方を使う（`TTDeployTool.Deploy.User` だけを持つ利用者が毎回指定しなくて済むように）。どちらも使えなければ、必要なロールを示すエラー
- 指定した方式がサーバーで無効、またはロールが無い場合はエラー（使える方式を示す）
- stdio では指定できない（指定するとエラー。stdio はサーバーの認証モードだけ）
- **計画に方式を記録する**。`start_deployment` / `start_deletion` は計画の方式で実行し、方式を指定する引数は持たない。実行の直前に、呼び出し元がその方式をまだ使えるかを確かめ直す
- 計画などの結果の `identity` に方式を表示する（`user`: 「サインインした利用者本人の権限（user 方式）」）。instructions で、計画を示すときに方式も利用者に伝えるよう求める

## 5. `user` 方式の動き
- 資格情報: 要求の `Authorization: Bearer` のアクセストークン（このサーバー向け）を、OBO（`OnBehalfOfCredential`）で Azure Resource Manager 用のトークンに交換する。交換はこのサーバーのアプリ登録で行うため、アプリ登録に Azure Service Management の委任アクセス許可（`user_impersonation`）と管理者の同意が必要（手順は 4c）
- 操作の範囲: サーバー側では制限しない（`SERVER_ALLOWED_RESOURCE_GROUPS` は使わない）。利用者本人の Azure RBAC で決まり、権限の事前確認（4.13）は本人の権限で行う。リソースグループの作成も、本人に権限があれば行う
- ジョブ: `start_*` の要求のトークンで資格情報を作り、ジョブが終わるまで使う。交換したトークンは通常 1 時間程度有効で、デプロイ（10 分程度）・削除（15 分程度）の間に切れない想定。切れた場合は、ジョブの失敗として記録する
- 失敗したとき: トークン交換の失敗（同意が無い、条件付きアクセスで MFA が必要など）は、Azure の認証の失敗として「サインインし直す。続けて失敗する場合は、管理者に委任アクセス許可と同意を確認してもらう」を案内する

## 6. 構成
- `IAzureAccessPolicy` を方式ごとに分ける
  - `Resolve(azure_identity)`: ロールと方式を確かめ、`AzureAccess`（方式、表示、範囲の確認、リソースグループの作成可否、資格情報の作成、認証の失敗時の案内）を返す
  - `ForMode(mode)`: 計画に記録した方式で `AzureAccess` を作り直す（`start_*`）
  - stdio: `UnrestrictedAccessPolicy`（`azure_identity` は指定不可）。HTTP: `HttpAccessPolicy`（4a の `ServerAccessPolicy` を置き換え）
- Azure の操作（`IAzureDeploymentGateway`）とサブスクリプションの一覧は、`AzureAccess` が作った資格情報で作る（`Func<IDeploymentLogger, TokenCredential, IAzureDeploymentGateway>` / `Func<TokenCredential, ISubscriptionDirectory>`）
- 計画は `IdentityPlan<TPlan>(TPlan Plan, string IdentityMode)` として保存する（計画の種類と方式を一緒に取り出す）
- `get_deployment_options` の `server` に `identity_modes`（サーバーで有効な方式）、`default_identity_mode`、`caller_identity_modes`（呼び出し元が使える方式）を加える
- instructions と Prompts は、有効な方式に合わせて組み立てる

## 7. テスト
- C1（分岐網羅）100% を維持する（`Program.cs` の終了処理を除く）
- 単体: 設定（方式・既定・必須の組み合わせ・OBO の資格情報）、方式の選び方（省略・指定・無効・ロールなし・stdio での指定）、`user` 方式の範囲と資格情報の作成、計画に記録した方式での実行、実行の直前のロールの確かめ直し
- 結合（TestServer）: `user` だけを持つ利用者が省略時に `user` 方式になること、`server` を指定するとエラーになること、計画の `identity` の表示、`user` 方式では許可したリソースグループの外も計画できること、`user` 方式の資格情報にリクエストのトークンが渡ること（OBO 自体は Entra ID が必要なため 4c で確認）

## 8. 確認結果
- 自動テスト: 全件成功（ソリューション全体 626 件、うち Azure と E2E のシステムテスト 4 件はスキップ）。分岐網羅率は `Program.cs` の終了処理と正規表現の生成コードを除き 100%
- 単体: 設定（既定は server だけ、user だけなら許可するリソースグループは不要、両方と既定、無効な方式・既定、client-secret のシークレットの要否、`ToString` にシークレットを出さない）、ロールと有効な方式の組み合わせごとに使える方式、省略時の方式（既定を使えなければ使えるほう）、方式の指定（大文字小文字、無効、サーバーで無効、ロールなし）、stdio での指定の拒否、user 方式の範囲・表示・OBO の資格情報（マネージド ID とクライアントシークレット）、トークンが無い要求の拒否、Authorization ヘッダーの読み取り（空白なしの `Bearer`、間の空白が 2 つ、ヘッダーが複数ある場合は使わない）、server 方式の範囲外のメッセージで user 方式を案内すること、instructions と Prompts の方式ごとの説明
- ツール: user 方式で許可したリソースグループの外を計画できること、`start_deployment` / `start_deletion` が計画の方式の資格情報で実行の直前の確認とジョブを行うこと、計画の後にロールが外れた場合に計画を使用済みにせずエラーにすること、`start_*` が計画のときではなく実行の要求のトークンを交換すること（計画と実行でトークンを変え、実行の直前の確認とジョブで実行のトークンだけが使われること。計画のトークンを使うように変えるとテストが失敗することも確認）、トークン交換の失敗で再サインインと同意の確認を案内すること、`get_deployment_info` と `list_subscriptions` の `azure_identity`、`get_deployment_options` の方式の表示
- 結合（TestServer。server と user の両方を有効にしたサーバー）: `TTDeployTool.Deploy.User` だけを持つ利用者が、`azure_identity` を省略して user 方式で計画できること（OBO の資格情報が使われる）、`azure_identity=server` を指定するとロールのエラーになること、instructions に方式の説明があること。既存の server 方式のテストも、両方を有効にしたサーバーで成功
- 4c で確認: Container Apps 上で、マネージド ID のフェデレーション資格情報でのトークン交換（OBO）が成功し、利用者本人の権限でデプロイ・削除できた。交換には MFA 済みのサインインと同意が必要（`docs/design/mcp/mcp-phase4c.md` の 10）
