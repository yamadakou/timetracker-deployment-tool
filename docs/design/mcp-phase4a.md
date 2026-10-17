# 詳細設計: HTTP トランスポートと `server` 方式（Phase 4a）

- 対象ブランチ: `feature/mcp-http`
- 全体設計: `docs/MCP_TOOL_DESIGN.md`（3.3 実行形態、3.4 HTTP 公開の方針、4.5 HTTP の `generate_compose`、4.10 パスワード、4.11 HTTP の Azure 資格情報、4.13 権限）
- 前の段階: `docs/design/mcp-phase3c.md`（stdio のサーバーの完成）

## 1. Phase 4 の分け方
| 段階 | 内容 | 確認 |
|---|---|---|
| **4a（本書）** | HTTP トランスポート（Streamable HTTP・ステートレス）、Entra ID のアクセストークンの検証と保護リソースのメタデータ、アプリロール、呼び出し元ごとの計画・ジョブ、`server` 方式（許可したリソースグループだけ）、HTTP 用の `generate_compose`、ユーザー割り当てのマネージド ID、Dockerfile と GHCR のイメージ | 自動テスト（テスト用の署名鍵で作ったトークン）、Docker でのローカル起動 |
| 4b | `user` 方式（OBO）、`azure_identity` 引数、方式の記録と表示 | 自動テスト |
| 4c | Container Apps への配置、Entra ID のアプリ登録の手順、Claude Code（と VS Code の確認できる範囲）からの接続、README | Azure 上で両方式の一連のシナリオ、範囲外のリソースグループの拒否 |

## 2. 起動とトランスポート
- `timetracker-mcp`（引数なし）または `--transport stdio`: 従来どおり（変更なし）
- `timetracker-mcp --transport http`（または環境変数 `TT_MCP_TRANSPORT=http`）: ASP.NET Core（Kestrel）で起動し、`/mcp` で Streamable HTTP を受ける。待ち受けは `ASPNETCORE_URLS`（既定 `http://+:8080`）。TLS は前段（Container Apps のイングレス）で終端する
- **ステートレスモード**にする（`HttpServerTransportOptions.Stateless = true`）。計画とジョブは ID だけで続けられるため、MCP のセッションに状態を持たない（全体設計 3.4 の 4）。ただし計画・ジョブ自体はプロセス内のメモリにあるため、レプリカは 1 に固定する（4c）
- `/healthz`: 認証なしで 200 を返す（Container Apps のプローブ用）
- ログは標準出力（コンテナのログ）
- `ASPNETCORE_URLS` / `ASPNETCORE_HTTP_PORTS`（または `--urls` / `--http_ports`）が無ければ `http://+:8080` で待ち受ける
- TLS を前段で終端するため、`X-Forwarded-Proto` / `X-Forwarded-Host` を受け入れる（`UseForwardedHeaders`）。401 の `WWW-Authenticate` で示す保護リソースのメタデータの URL を、利用者から見た `https://…` にするため。転送元の制限はしない（このヘッダーで変わるのは、そのリクエストへの応答に含まれる URL だけで、認証の判定には使わない）

## 3. 呼び出し元の認証（Entra ID）
- `/mcp` は認証必須。Microsoft Entra ID が発行したアクセストークン（JWT）を `Microsoft.AspNetCore.Authentication.JwtBearer` で検証する
  - 発行者: `https://login.microsoftonline.com/{テナント ID}/v2.0`（v2 のトークン）
  - 対象（audience）: アプリ登録のクライアント ID、または `api://{クライアント ID}`
  - 署名鍵は発行者のメタデータ（OpenID Connect の discovery）から取得する
  - 委任のスコープ `scp`（スペース区切り）に `Mcp.Access` があることを求める（無ければ 403）。発行者・対象・署名だけでは、このアプリ登録向けの ID トークン（対象がクライアント ID）と区別できないため。アプリのみのトークン（`scp` が無い）も受け付けない
- トークンが無い・無効な場合は 401 と `WWW-Authenticate: Bearer resource_metadata="…"` を返す。保護リソースのメタデータ（RFC 9728、`/.well-known/oauth-protected-resource`）には、`resource`（`TT_MCP_HTTP__PUBLIC_URL`）、認可サーバー（上の発行者）、スコープ（`{公開 URL}/Mcp.Access`。当初は `api://{クライアント ID}/Mcp.Access` としたが、Entra ID がクライアントの付ける `resource` と違うスコープを拒否したため、4c で変更した）を載せる。これは SDK の `AddMcp` 認証ハンドラーで返す
- Entra ID は動的クライアント登録（DCR）に対応していないため、クライアントは事前に登録したクライアント ID を使う（手順は 4c）

### 3.1 呼び出し元（`HttpCallerContext`）
- `ICallerContext.CallerId` を、トークンの `tid` と `oid` から `{tid}/{oid}` で作る。計画・ジョブはこの ID ごとに分かれ、ほかの利用者のものは「存在しない」と同じ扱いになる（Phase 3b の `IPlanStore` / `JobManager` の仕組みをそのまま使う）
- `preferred_username` と `{tid}/{oid}` は、認証されたリクエストごとにサーバーのログにだけ出す（だれの操作かを追えるように）

### 3.2 アプリロール
| アプリロール | 4a で使えるもの |
|---|---|
| `Timetracker.Deploy.Server` | すべてのツール（Azure の操作は `server` 方式） |
| （4b）`Timetracker.Deploy.User` | 4b で `user` 方式を追加する |
| どちらもない | Azure を操作しないツール（`get_deployment_options` / `validate_deployment` / `generate_compose`）と Resources・Prompts の文書。Azure を読む・変更するツールは「アプリロール `Timetracker.Deploy.Server` が必要です」のエラー |

- 確認はツールの中で行う（`IAzureAccessPolicy`。下記）。ツールの一覧からは隠さない（何が使えないかを LLM が利用者に説明できるように）

## 4. `server` 方式とリソースグループの制限（`IAzureAccessPolicy`）
- Azure の資格情報は `TT_MCP_HTTP__SERVER_IDENTITY`（`managed-identity` / `sp-env`）で `CredentialFactory` から作る。`TT_MCP_AUTH_MODE` は HTTP では使わない
- `managed-identity` は、環境変数 `AZURE_CLIENT_ID` があればユーザー割り当て、無ければシステム割り当てを使うよう `CredentialFactory` を拡張する（CLI と stdio の `managed-identity` でも同じになる。README の該当節に Container Apps を加える）
- 操作してよいリソースグループは `TT_MCP_HTTP__SERVER_ALLOWED_RESOURCE_GROUPS`（`/subscriptions/{ID}/resourceGroups/{名前}` をカンマ区切り。大文字小文字は区別しない）に限る。HTTP ではこの設定が必須（空なら起動時のエラー。全サブスクリプションを操作できる状態で公開しないため）
- `IAzureAccessPolicy`（`RequireAzureAccess()`: ロールの確認、`ResourceGroupBlocker(サブスクリプション, リソースグループ)`: 範囲の確認、`CanCreateResourceGroup`、`Identity`）を、Azure を読む前に使う。stdio では常に許可（`UnrestrictedAccessPolicy`）、HTTP では `ServerAccessPolicy`（呼び出し元は `IHttpContextAccessor` から取る）
  | ツール | 範囲外のとき |
  |---|---|
  | `plan_deployment` | Azure を読まずに、ブロッカー「リソースグループ … は、このサーバーで操作できる範囲（TT_MCP_HTTP__SERVER_ALLOWED_RESOURCE_GROUPS）にありません」を返す（plan_id は null） |
  | `plan_deletion` / `get_deployment_info` / デプロイの Resource | Azure を読まずにエラー |
  | `start_*` | 計画は範囲内でしか作られないが、実行の直前にも確かめる（設定の変更に備える） |
  | `list_subscriptions` | サーバーの ID で見えるサブスクリプションを返す（範囲は `get_deployment_options` の `server.allowed_resource_groups` で示す） |
- リソースグループが無い場合: `TT_MCP_HTTP__SERVER_CAN_CREATE_RESOURCE_GROUP=true` でなければ、`plan_deployment` のブロッカー「管理者にリソースグループの作成を依頼してください」にする（既定は false。全体設計 4.11）。計画（`DeploymentPlan`）の `Blockers` に加えるため、実行の直前の確認でも同じ判定になる
- 計画の結果の `identity` に「サーバーの ID（マネージド ID）で実行します」と表示する

## 5. HTTP でのツールの違い
| 項目 | stdio | HTTP |
|---|---|---|
| パスワード | 引数または環境変数 `TT_MCP_DB_PASSWORD` / `TT_MCP_TRACKER_PASSWORD` | 引数だけ（環境変数は読まない。全利用者で共有になるため） |
| `generate_compose` | `output_dir` に書き込む | 引数は `app_name` / `db_type` / `db_name` / `tt_tag` だけ（`output_dir` / `overwrite` / パスワード / リソースの引数は無い）。`docker-compose.yml` の内容と、パスワードをプレースホルダー（`<DB パスワードを設定>` / `<Timetracker のパスワードを設定>`）にした `.env` の内容を返す（Azure・ファイルとも変更しないため `readOnlyHint: true`）。検証は、要件を満たす仮のパスワードで行う（結果には出さない）。`next_steps` で、SQL Server のパスワード要件を案内する |
| `get_deployment_options` の `server` | `transport: stdio`、認証モード、パスワードの環境変数の有無、出力先 | `transport: http`、`identity_modes: ["server"]`、`allowed_resource_groups`、`can_create_resource_group` |
| instructions | 現行 | HTTP 用（パスワードは引数で渡す必要があること、`server` 方式で操作できる範囲、`generate_compose` はファイルを書かないこと） |
| Prompts | 現行 | `deploy-timetracker` の最後の注意と、`local-dry-run` の手順（ファイルを保存してプレースホルダーを置き換える）を HTTP 用にする |
| パスワードが無い場合の案内 | 環境変数（推奨）か引数 | 引数（環境変数は使えない） |

- `generate_compose` は、stdio 用（`ComposeFileTools`）と HTTP 用（`ComposeContentTools`）に分ける（引数と結果の型が違うため）。ほかのツールは共通

## 6. 構成
- `McpServerHost.Build`（stdio）と `McpServerHost.BuildHttp`（HTTP）。ツール・Resources・Prompts と Application のサービスの登録は共通の関数にまとめ、トランスポートごとに `ICallerContext`、`IAzureAccessPolicy`、資格情報、`generate_compose` の実装を差し替える
- HTTP の設定（`McpHttpSettings`、環境変数 `TT_MCP_HTTP__*`）
  | 環境変数 | 必須 | 説明 |
  |---|---|---|
  | `TT_MCP_HTTP__TENANT_ID` | ✔ | Entra ID のテナント ID |
  | `TT_MCP_HTTP__CLIENT_ID` | ✔ | MCP サーバーのアプリ登録のクライアント ID |
  | `TT_MCP_HTTP__PUBLIC_URL` | ✔ | 公開する MCP のエンドポイントの URL（`https://…/mcp`）。保護リソースのメタデータの `resource` |
  | `TT_MCP_HTTP__SERVER_IDENTITY` | – | `managed-identity`（既定） / `sp-env` |
  | `TT_MCP_HTTP__SERVER_ALLOWED_RESOURCE_GROUPS` | ✔ | 操作してよいリソースグループ |
  | `TT_MCP_HTTP__SERVER_CAN_CREATE_RESOURCE_GROUP` | – | 既定 `false` |
  - 不正な値・必須の不足は起動時のエラー（stderr に理由、終了コード 1）
- 単一ファイルの EXE（stdio）にも ASP.NET Core が含まれるため、サイズが増える（全体設計 6.2 で想定済み。win-x64 で約 115MB。IIS 用の `aspnetcorev2_inprocess.dll` も出力に含まれるが、使わない）

## 7. コンテナイメージ
- `Dockerfile`（リポジトリの直下）: `mcr.microsoft.com/dotnet/sdk:10.0` でビルドし、`mcr.microsoft.com/dotnet/aspnet:10.0` で実行する。非 root（`APP_UID`）、ポート 8080、`ENTRYPOINT ["dotnet", "timetracker-mcp.dll", "--transport", "http"]`
- CI（`ci.yml`）: PR ごとにイメージのビルドだけを確かめる（push しない）
- リリース（`release.yml`）: テスト（`release` ジョブ）が成功した場合だけ、タグの push で `ghcr.io/yamadakou/timetracker-mcp:{バージョン}` と `latest` を push する（`packages: write`）。公開の設定（GHCR のパッケージを public にする）は初回だけ手動

## 8. テスト
- C1（分岐網羅）100% を維持する（`Program.cs` の終了処理を除く）
- 単体: `McpHttpSettings`（必須・不正値）、`HttpCallerContext`、`ServerAccessPolicy`（範囲内・外・大文字小文字・作成の可否・ロール）、HTTP 用の `generate_compose`、`CredentialFactory` のユーザー割り当て
- 結合: `BuildHttp` を TestServer で起動し、テスト用の署名鍵で作ったトークンで SDK のクライアントから接続する。トークンなし（401 と `WWW-Authenticate` の `resource_metadata`）、保護リソースのメタデータ、対象の違うトークンの拒否、ロールなしでの Azure のツールの拒否、別の利用者の計画・ジョブが見えないこと、範囲外のリソースグループのブロッカー
- Docker: イメージをビルドしてローカルで起動し、`/healthz` と、トークンなしの `/mcp` が 401 になることを確かめる（CI の `docker-mcp` でも毎回確かめる）
- テストの注意: `HttpContextAccessor` は値を static な `AsyncLocal` に置くため、単体テストで呼び出し元ごとに別のインスタンスを作っても、後から設定したものに上書きされる。テストでは独立した実装を使う

## 9. 確認結果
- 自動テスト: 全件成功（ソリューション全体 578 件、うち Azure と E2E のシステムテスト 4 件はスキップ）。分岐網羅率は `Program.cs` の終了処理を除き 100%
- 子プロセス: ビルドした `timetracker-mcp.dll` を `--transport http` で起動し、設定が無ければ終了コード 1 と理由、設定があれば実際の Kestrel で `/healthz` が 200、トークンなしの `/mcp` が 401 になることを確認（Program から起動引数が渡ること）
- TestServer: トークンなし・対象の違うトークン・発行者の違うトークン・署名の違うトークンが 401 と `resource_metadata` になること、`scp` に `Mcp.Access` が無いトークン（ID トークンを想定したもの、ほかのスコープ、似た名前）が 403 になること、保護リソースのメタデータ、ロールなしで Azure のツールがエラーになること、範囲外のリソースグループが Azure を読まずにブロッカーになること、別の利用者の計画・ジョブが見えないこと、開始したジョブを同じ利用者が別のリクエストで最後まで追えること、デプロイの Resource がロールなし・範囲外で拒否されること、リクエストのログに `preferred_username`（無ければ省略）と `{tid}/{oid}` が出て、認証のないリクエストは出ないことを確認
- Docker（ローカル）: `aspnet:10.0` のイメージで、非 root（`app`）で起動し、`/healthz` が 200、`X-Forwarded-*` 付きのトークンなしの `/mcp` が 401（`resource_metadata="https://…"`）、必須の設定が無ければ理由を出して終了することを確認。開発環境ではビルド用のコンテナから NuGet に接続できない（プロキシの証明書）ため、ホストで発行した成果物を同じ実行用のステージに載せて確認した。`sdk:10.0` でのビルドは CI で確認する
- 未確認（4c）: Entra ID の実際のトークン、Container Apps 上での動作、MCP クライアントからの接続
