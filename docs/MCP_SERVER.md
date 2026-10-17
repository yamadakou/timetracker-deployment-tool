# Timetracker デプロイ MCP サーバー

[README に戻る](../README.md)

CLI と同じ機能を、MCP（Model Context Protocol）に対応した AI アシスタント（Claude Code、VS Code の GitHub Copilot、Claude Desktop など）から、対話しながら使えます。

MCP サーバーには次の 2 つの形態があります。現在提供しているのは stdio 版だけで、HTTP 版は開発中です。

| 形態 | 状況 | 動く場所 | 向いている使い方 |
|---|---|---|---|
| [stdio 版](#stdio-版) | 提供中 | 利用者の PC（アシスタントが `timetracker-mcp.exe` を子プロセスとして起動し、標準入出力で通信） | 自分の Azure の権限（`az login` など）で、自分の PC から使う |
| [HTTP 版](#http-版開発中) | **開発中（未提供）** | Azure Container Apps などのサーバー（アシスタントは URL で接続） | チームで 1 つのサーバーを共有し、決まったリソースグループに検証環境を作る |

## stdio 版

アシスタントが `timetracker-mcp.exe` を子プロセスとして起動し、標準入出力（stdio）で通信します。Azure の操作には、利用者の PC の資格情報（既定は `az login` のサインイン）を使います。前提となる Azure の権限・リソースプロバイダーの登録・認証モードは CLI と共通です。README の[利用者向けマニュアル](../README.md#利用者向けマニュアル)を参照してください。

### できること

| ツール | 内容 | Azure を変更するか |
|---|---|---|
| `get_deployment_options` | 既定値・選べる値・注意事項・サーバーの設定 | しない |
| `validate_deployment` | 設定の検証（すべての誤りと警告を返す） | しない |
| `generate_compose` | ローカル用の `docker-compose.yml` と `.env` の生成（CLI の Dry-run と同じ） | しない |
| `list_subscriptions` | 使えるサブスクリプションの一覧 | しない |
| `plan_deployment` → `start_deployment` | デプロイの計画 → 実行（非同期ジョブ） | `start_deployment` だけ |
| `plan_deletion` → `start_deletion` | 削除の計画 → 実行（非同期ジョブ。削除の規則は `delete` コマンドと同じ） | `start_deletion` だけ |
| `get_job_status` / `list_jobs` | ジョブの進捗・ログ・結果（Timetracker の URL など） | しない |
| `get_deployment_info` | デプロイ済みの環境の状態と URL | しない |

ほかに、Resources（`timetracker://docs/constraints`: 目的と注意事項、`timetracker://docs/rbac`: 必要な権限、`timetracker://deployments/{subscription}/{resource_group}/{app_name}`: 環境の状態）と、Prompts（`deploy-timetracker`、`local-dry-run`、`delete-timetracker`: 手順の案内）があります。対応していないアシスタントでも、ツールだけで同じことができます。

### 安全策

アシスタント（LLM）が誤って Azure を変更しないよう、サーバーの側で次のことを強制します。
- Azure を変更するのは `start_deployment` と `start_deletion` だけです。どちらも、先に `plan_*` で作った計画（`plan_id`。15 分で期限切れ、一度だけ使える）が必要で、計画と違う内容では実行しません。実行の直前に Azure の状態を確かめ直し、計画の後に状態が変わっていれば中止します
- 既存の DB アプリを更新する計画は、`acknowledge_data_loss=true`（DB のデータが失われることへの同意）が無ければ実行しません
- 削除は、`confirm_app_name` に計画と同じアプリ名を指定しないと実行しません（アシスタントには、利用者にアプリ名を入力してもらうよう指示しています）
- 同じ対象（サブスクリプション・リソースグループ・アプリ名）のジョブは、同時に 1 つしか実行しません
- パスワードはツールの結果・ログに出しません

利用者の側では、次の点に注意してください。
- `start_deployment` と `start_deletion` は、アシスタントの設定で**自動承認にしないでください**（実行の前に、計画の内容を確認できるように）
- パスワードは、ツールの引数ではなくサーバーの環境変数（`TT_MCP_DB_PASSWORD` / `TT_MCP_TRACKER_PASSWORD`）で渡すことを推奨します。引数で渡すと会話の履歴に残ります
- 認証モードはサーバーの設定（`TT_MCP_AUTH_MODE`）で決まり、会話の中では変えられません。既定は `azure-cli` です（事前に `az login` が必要で、`az` コマンドがアシスタントから起動されたサーバーの `PATH` にあること）

### 導入

1. [Releases](https://github.com/yamadakou/timetracker-deployment-tool/releases) から `timetracker-mcp-vX.Y.Z.zip` を取得し、任意のフォルダーに展開します（例: `C:\tools\timetracker-mcp\timetracker-mcp.exe`）。.NET のインストールは不要です
2. Azure を使う場合は、[必要な RBAC ロールと権限](../README.md#必要な-rbac-ロールと権限)と[リソースプロバイダーの登録](../README.md#azure-リソースプロバイダーの登録)を確認し、`az login` でサインインします
3. 使うアシスタントに、次の例のようにサーバーを登録します

### サーバーの設定（環境変数）

| 環境変数 | 既定 | 説明 |
|---|---|---|
| `TT_MCP_AUTH_MODE` | `azure-cli` | 認証モード: `azure-cli` / `default` / `sp-env` / `device-code` / `managed-identity`（[認証モード別の前提環境と利用方法](../README.md#認証モード別の前提環境と利用方法)） |
| `TT_MCP_DEFAULT_SUBSCRIPTION` | なし | サブスクリプションを省略したときに使う ID |
| `TT_MCP_DEFAULT_RESOURCE_GROUP` | なし | リソースグループを省略したときに使う名前 |
| `TT_MCP_DB_PASSWORD` | なし | DB のパスワード（設定すると、アシスタントはパスワードを尋ねません） |
| `TT_MCP_TRACKER_PASSWORD` | なし | Timetracker のパスワード（同上） |
| `TT_MCP_OUTPUT_ROOT` | なし | 設定すると、`generate_compose` の出力先をこのフォルダーの配下に限ります（絶対パス） |
| `TT_MCP_VERBOSE` | `false` | `true` で詳細ログを出します（ログは標準エラー出力に出ます） |

### アシスタントの設定例

**Claude Code**（コマンドで登録する例。`-e` の値は Claude Code の設定ファイルに平文で保存されます）:
```powershell
claude mcp add timetracker `
  -e TT_MCP_DEFAULT_SUBSCRIPTION=<SUBSCRIPTION_ID> `
  -e TT_MCP_OUTPUT_ROOT=C:\work\timetracker `
  -- C:\tools\timetracker-mcp\timetracker-mcp.exe
```
パスワードを設定ファイルに書きたくない場合は、プロジェクトの `.mcp.json` で環境変数を参照できます（Claude Code を起動する前に、端末で `TT_DB_PASSWORD` などを設定しておきます）:
```json
{
  "mcpServers": {
    "timetracker": {
      "command": "C:\\tools\\timetracker-mcp\\timetracker-mcp.exe",
      "env": {
        "TT_MCP_DB_PASSWORD": "${TT_DB_PASSWORD}",
        "TT_MCP_TRACKER_PASSWORD": "${TT_TRACKER_PASSWORD}"
      }
    }
  }
}
```

**VS Code**（パスワードは初回の起動時に入力欄で尋ねられ、ファイルには書かれません）:

設定ファイル `mcp.json` は、次のどちらかに置きます。どのフォルダーを開いていても使えるよう、ユーザー単位の設定をおすすめします。
- **ユーザー単位**: コマンド パレット（`Ctrl+Shift+P`）で **MCP: Open User Configuration** を実行すると開くファイル（Windows の既定では `%APPDATA%\Code\User\mcp.json`。プロファイルを使い分けている場合はプロファイルごとのフォルダー）
- **ワークスペース単位**: VS Code で開いているフォルダーの直下の `.vscode\mcp.json`（**MCP: Open Workspace Folder MCP Configuration** で開けます）。そのフォルダーを開いたときだけ使われ、ほかのフォルダーを開いているときや、ファイルを置いたフォルダーの親を開いているときは認識されません

既に `mcp.json` がある場合は、ファイルを置き換えずに、既存の `servers` と `inputs` の中に下の `timetracker` と 2 つの入力を追記します。入力の参照は `${input:<id>}`（`input` は単数形）と書きます（[VS Code の MCP 設定のリファレンス](https://code.visualstudio.com/docs/copilot/reference/mcp-configuration)）。`${input:<id>}` で読み込みエラーになるときは、`<id>` と同じ `id` の入力がトップレベルの `inputs` 配列に無いことがよくある原因です（`inputs` が `servers` の中にある、`inputs` キーが 2 つある、など）。`${inputs:<id>}` などと書くとエラーにはなりませんが、その文字列がそのままパスワードとして使われるので注意してください。保存したあと、拡張機能ビューの「MCP サーバー」または **MCP: List Servers** で `timetracker` を起動します。
```json
{
  "inputs": [
    { "type": "promptString", "id": "db-password", "description": "Timetracker の DB パスワード", "password": true },
    { "type": "promptString", "id": "tracker-password", "description": "Timetracker のパスワード", "password": true }
  ],
  "servers": {
    "timetracker": {
      "type": "stdio",
      "command": "C:\\tools\\timetracker-mcp\\timetracker-mcp.exe",
      "env": {
        "TT_MCP_DB_PASSWORD": "${input:db-password}",
        "TT_MCP_TRACKER_PASSWORD": "${input:tracker-password}"
      }
    }
  }
}
```

**Claude Desktop**（`claude_desktop_config.json`）:
```json
{
  "mcpServers": {
    "timetracker": {
      "command": "C:\\tools\\timetracker-mcp\\timetracker-mcp.exe",
      "env": { "TT_MCP_DEFAULT_SUBSCRIPTION": "<SUBSCRIPTION_ID>" }
    }
  }
}
```

### 使い方の例

アシスタントに次のように頼むと、ツールを順に呼びながら進めます（Prompts に対応したアシスタントでは `deploy-timetracker` などを選んでも始められます）。
- 「Timetracker の検証環境を Azure の rg-tt-demo に PostgreSQL で作ってください」→ 設定の確認と検証 → `plan_deployment` の結果（作成するリソース・警告）が示されるので、確認して実行を指示します → 数分後に Timetracker の URL が返ります
- 「ローカルの Docker で試したいので、C:\work\timetracker に compose ファイルを作ってください」
- 「rg-tt-demo の Timetracker を削除してください」→ `plan_deletion` の結果（削除するもの・残すもの）が示されるので、確認してアプリ名を入力します

計画とジョブはサーバーのプロセスの中にだけ保存されます。アシスタントを再起動すると消えますが、Azure の状態は `get_deployment_info` でいつでも確認できます。

## HTTP 版（開発中）

> [!NOTE]
> HTTP 版は開発中で、まだ提供していません。サーバーの本体（HTTP トランスポート、Entra ID による呼び出し元の認証、`server` 方式と `user` 方式）とコンテナイメージはできていますが、Azure Container Apps への配置と Entra ID のアプリ登録のスクリプト・手順は用意しましたが、Azure と Entra ID での確認はまだです（下の「導入（確認前の手順）」）。内容は提供までに変わることがあります。

### 開発の段階

| 段階 | 内容 | 状況 |
|---|---|---|
| 4a | HTTP トランスポート、Entra ID のアクセストークンの検証、アプリロール、呼び出し元ごとの計画・ジョブ、`server` 方式（許可したリソースグループだけ）、HTTP 用の `generate_compose`、コンテナイメージ | 実装済み（自動テストとローカルの Docker で確認） |
| 4b | `user` 方式（利用者本人の権限）と、方式を選ぶ引数 `azure_identity` | 実装済み（Entra ID でのトークン交換も 4c で確認） |
| 4c | Azure Container Apps への配置、Entra ID のアプリ登録、アシスタントからの接続の手順 | Azure と Entra ID で配置とツールを確認済み（Claude Code・VS Code からの接続は未確認） |

### stdio 版との違い

| 項目 | stdio 版 | HTTP 版 |
|---|---|---|
| 配布形態 | 単一ファイルの EXE（GitHub Releases の zip） | コンテナイメージ `ghcr.io/yamadakou/timetracker-mcp`（リリースのタグで公開）。Azure Container Apps などに配置する |
| アシスタントの接続 | 子プロセスとして起動する（`command` にパスを指定） | URL で接続する（`https://<サーバー>/mcp`。Streamable HTTP）。TLS は前段（Container Apps のイングレスなど）で終端し、コンテナはポート 8080 で待ち受ける |
| 利用者の認証 | 不要（PC の利用者がそのまま使う） | **必須**。Microsoft Entra ID のアクセストークン（スコープ `Mcp.Access`）で認証する。Entra ID は動的クライアント登録に対応していないため、アシスタントには事前に登録したクライアント ID を設定する |
| Azure を操作する権限 | 利用者の PC の資格情報（`TT_MCP_AUTH_MODE`） | サーバーの設定で、次のどちらか、または両方を使えるようにする<br/>・`server` 方式: サーバーのマネージド ID（またはサービスプリンシパル）の権限で、管理者が許可したリソースグループだけを操作する<br/>・`user` 方式: サインインした利用者本人の権限で操作する（範囲は本人の Azure のロールで決まる）<br/>両方を使える場合は、ツールの引数 `azure_identity` で選ぶ。計画に方式を記録し、実行は計画の方式で行う |
| だれが使えるか | PC の利用者 | Entra ID のアプリロールで決める。`Timetracker.Deploy.Server` は両方の方式、`Timetracker.Deploy.User` は `user` 方式だけ。どちらも無ければ Azure を操作しないツール（`get_deployment_options` / `validate_deployment` / `generate_compose`）と文書だけ |
| パスワードの渡し方 | 環境変数（推奨）または引数 | 引数だけ（サーバーの環境変数は全員で共有になるため読まない） |
| `generate_compose`（Dry-run） | 利用者の PC にファイルを書き出す | ファイルは書き出さず、`docker-compose.yml` と `.env` の内容を返す（パスワードはプレースホルダー） |
| 計画・ジョブ | サーバーのプロセスの中だけに保存 | 同じ。利用者ごとに分かれ、作成した本人だけが実行・参照できる。レプリカは 1 つに固定する |

ツール・安全策（計画 → 確認 → 実行、削除時のアプリ名の確認など）は stdio 版と同じです。

### サーバーの設定（環境変数）

| 環境変数 | 必須 | 説明 |
|---|---|---|
| `TT_MCP_HTTP__TENANT_ID` | ✔ | Entra ID のテナント ID |
| `TT_MCP_HTTP__CLIENT_ID` | ✔ | MCP サーバーのアプリ登録のクライアント ID |
| `TT_MCP_HTTP__PUBLIC_URL` | ✔ | 公開する MCP のエンドポイントの URL（`https://…/mcp`） |
| `TT_MCP_HTTP__IDENTITY_MODES` | – | 使える方式。`server`（既定）/ `user` / `server,user` |
| `TT_MCP_HTTP__DEFAULT_IDENTITY_MODE` | – | `azure_identity` を省略したときの方式（既定は `IDENTITY_MODES` の最初）。利用者がその方式を使えなければ、使えるほうになる |
| `TT_MCP_HTTP__SERVER_ALLOWED_RESOURCE_GROUPS` | `server` 方式が有効なら ✔ | `server` 方式で操作してよいリソースグループ（`/subscriptions/<ID>/resourceGroups/<名前>` のカンマ区切り） |
| `TT_MCP_HTTP__SERVER_IDENTITY` | – | `server` 方式で使う資格情報。`managed-identity`（既定）/ `sp-env`。ユーザー割り当てのマネージド ID は `AZURE_CLIENT_ID` でクライアント ID を指定する |
| `TT_MCP_HTTP__SERVER_CAN_CREATE_RESOURCE_GROUP` | – | `server` 方式で、許可したリソースグループが無いときに作成してよいか。既定 `false` |
| `TT_MCP_HTTP__USER_CLIENT_CREDENTIAL` | – | `user` 方式のトークン交換で、サーバーのアプリ登録が使う資格情報。`managed-identity`（既定。マネージド ID をフェデレーション資格情報として使い、シークレットを持たない）/ `client-secret` |
| `TT_MCP_HTTP__CLIENT_SECRET` | `client-secret` なら ✔ | アプリ登録のクライアントシークレット（ツールの結果・ログには出さない） |

必須の設定が無い、または値が不正な場合、サーバーは理由を出して起動を止めます。`TT_MCP_AUTH_MODE` と、パスワードの環境変数（`TT_MCP_DB_PASSWORD` / `TT_MCP_TRACKER_PASSWORD`）は HTTP 版では使いません。

### `server` 方式の前提

- 管理者が、検証環境を作るリソースグループを事前に作成し、`TT_MCP_HTTP__SERVER_ALLOWED_RESOURCE_GROUPS` に登録します。許可していないリソースグループへの操作は、Azure を読む前に拒否します
- サーバーのマネージド ID には、そのリソースグループのスコープで Contributor を割り当てます。リソースプロバイダーの登録も管理者が事前に行います
- 計画の結果には「サーバーの ID（マネージド ID）で実行します」と表示されます
- Azure のアクティビティログには、すべてサーバーの ID の操作として残ります。だれの操作かは、サーバーのログ（利用者のユーザー名と ID を記録）で確認します

### `user` 方式の前提

- 利用者本人に、CLI を自分で実行する場合と同じロールが必要です（[必要な RBAC ロールと権限](../README.md#必要な-rbac-ロールと権限)）。権限が足りない場合は、計画の段階で示されます
- サーバーは、利用者のアクセストークンを OBO（On-Behalf-Of）で Azure 用のトークンに交換します。MCP サーバーのアプリ登録に、Azure Service Management の委任アクセス許可（`user_impersonation`）を追加し、テナント管理者の同意を得ておきます（同意が無いと AADSTS65001 で失敗します。スクリプトを実行した本人の分だけは、管理者でなくても同意できることがあります）
- Azure の管理 API には MFA が必須です。MCP サーバーへのサインインで MFA を行っていないと、交換が AADSTS50076 で失敗し、サインインし直しても解消しません。テナントの管理者が、条件付きアクセスで MCP サーバーのアプリ（エンタープライズ アプリケーション「Timetracker MCP (…)」）へのサインインに MFA を要求するポリシーを設定してください
- 計画の結果には「サインインした利用者本人の権限（user 方式）」と表示され、Azure のアクティビティログにも本人の操作として残ります

### 導入

> [!NOTE]
> Azure と Entra ID で、配置・アプリ登録・ツール（`server` 方式と `user` 方式のデプロイと削除）を確認しました。Claude Code と VS Code からのサインインは未確認です。確認の結果は [Phase 4c の詳細設計](design/mcp-phase4c.md)の「確認結果」にまとめています。

1. 管理者が、PowerShell 7 と Azure CLI で次を実行します。MCP サーバーを置くリソースグループ、マネージド ID、Entra ID のアプリ登録（サーバー用とクライアント用）、Container Apps の Environment と Container App を作り、`server` 方式なら許可するリソースグループにマネージド ID の Contributor を割り当てます
   ```powershell
   ./scripts/deploy-mcp-http.ps1 -SubscriptionId <ID> -ResourceGroup rg-tt-mcp -IdentityModes server,user -AllowedResourceGroups rg-tt-team1
   ```
2. 管理者が、利用者にアプリロールを割り当てます
   ```powershell
   ./scripts/assign-mcp-role.ps1 -Role Server -UserPrincipalName alice@example.com
   ```
3. 利用者が、アシスタントに登録します。Claude Code の例（クライアントのアプリの ID と URL は、手順 1 の出力のもの）:
   ```powershell
   claude mcp add --transport http --client-id <クライアントのアプリの appId> --callback-port 8765 timetracker https://<FQDN>/mcp
   ```
   追加した後、Claude Code で `/mcp` を開いてサインインします。VS Code の設定例は詳細設計を参照してください

詳しい設計は [HTTP 版の詳細設計（Phase 4a）](design/mcp-phase4a.md)、[`user` 方式の詳細設計（Phase 4b）](design/mcp-phase4b.md)、[配置とアプリ登録の詳細設計（Phase 4c）](design/mcp-phase4c.md)と、[MCP ツール設計書](MCP_TOOL_DESIGN.md)の「3.3 MCP サーバーの実行形態」「4.10 秘密情報（パスワード）の扱い」「4.11 HTTP 公開時の Azure 資格情報」を参照してください。
