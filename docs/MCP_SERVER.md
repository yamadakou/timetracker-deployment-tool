# TTDeployTool MCP サーバー

[README に戻る](../README.md)

CLI（`ttdeploytool-cli.exe`）と同じ機能を、AI アシスタント（Claude Code、VS Code の GitHub Copilot、Claude Desktop など）との対話で使えるようにするサーバーです。「Timetracker の検証環境を Azure に作って」のように頼むと、アシスタントがこのサーバーの機能（ツール）を呼び出して、計画の作成 → 利用者の確認 → 実行の順に進めます。

アシスタントとの接続には、[MCP（Model Context Protocol）](https://modelcontextprotocol.io/)という共通の仕組みを使います。MCP に対応したアシスタントなら、どれからでも使えます。

## 2 つの形態

| 形態 | 状況 | 動く場所 | 向いている使い方 |
|---|---|---|---|
| [stdio 版](#stdio-版) | **提供中** | 利用者の PC（アシスタントが `ttdeploytool-mcp.exe` を起動し、標準入出力でやり取りします） | 自分の Azure の権限（`az login` など）で、自分の PC から使う |
| [HTTP 版](#http-版開発中) | 開発中（未提供） | Azure Container Apps などのサーバー（アシスタントは URL で接続します） | チームで 1 つのサーバーを共有し、決まったリソースグループに検証環境を作る |

---

## stdio 版

アシスタントが `ttdeploytool-mcp.exe` を子プロセスとして起動し、標準入出力（[stdio](https://modelcontextprotocol.io/specification/latest/basic/transports)）でやり取りします。Azure の操作には、利用者の PC の資格情報（既定は Azure CLI の `az login` のサインイン）を使います。必要な Azure の権限、リソースプロバイダーの登録、認証モードは CLI と同じです（README の[利用者向けマニュアル](../README.md#利用者向けマニュアル)）。

### 使い始めるまでの流れ

1. [導入](#導入): `ttdeploytool-mcp.exe` を入手し、Azure の準備をして、`az login` でサインインします
2. [アシスタントへの登録](#アシスタントの設定例): 使うアシスタントの設定に、このサーバーを登録します（必要なら[環境変数](#サーバーの設定環境変数)も設定します）
3. [使う](#使い方の例): アシスタントに、やりたいことを日本語で頼みます。Azure を変更する前に、計画の内容が示されるので、確認してから実行を指示します

### 導入

1. [Releases](https://github.com/yamadakou/timetracker-deployment-tool/releases) から `ttdeploytool-mcp-vX.Y.Z.zip` を取得し、任意のフォルダーに展開します（例: `C:\tools\ttdeploytool-mcp\ttdeploytool-mcp.exe`）。Windows 用の単一ファイルで、.NET のインストールは不要です
2. Azure を使う場合は、README の[必要な RBAC ロールと権限](../README.md#必要な-rbac-ロールと権限)と[リソースプロバイダーの登録](../README.md#azure-リソースプロバイダーの登録)を確かめます
3. [Azure CLI](https://learn.microsoft.com/ja-jp/cli/azure/install-azure-cli) で `az login` を実行し、サインインしておきます（既定の認証モード `azure-cli` の場合）
4. 使うアシスタントに、サーバーを登録します（[アシスタントの設定例](#アシスタントの設定例)）

Azure を使わずに、ローカル用の `docker-compose.yml` を作るだけ（`generate_compose`）なら、手順 2 と 3 は不要です。

### できること

アシスタントは、次のツールを組み合わせて使います。利用者がツールの名前を覚える必要はありません。

| ツール | 内容 | Azure を変更するか |
|---|---|---|
| `get_deployment_options` | 既定値・選べる値・注意事項・サーバーの設定 | しない |
| `validate_deployment` | 設定の検証（すべての誤りと警告を返す） | しない |
| `generate_compose` | ローカル用の `docker-compose.yml` と `.env` の作成（CLI の Dry-run と同じ） | しない |
| `list_subscriptions` | 使えるサブスクリプションの一覧 | しない |
| `plan_deployment` → `start_deployment` | デプロイの計画 → 実行（時間がかかるため、ジョブとして裏で進めます） | `start_deployment` だけ |
| `plan_deletion` → `start_deletion` | 削除の計画 → 実行（ジョブ。削除の規則は CLI の `delete` と同じ） | `start_deletion` だけ |
| `get_job_status` / `list_jobs` | ジョブの進み具合・ログ・結果（Timetracker の URL など） | しない |
| `get_deployment_info` | デプロイ済みの環境の状態と URL | しない |

ツールのほかに、次のものも用意しています。対応していないアシスタントでも、ツールだけで同じことができます。
- [Resources](https://modelcontextprotocol.io/specification/latest/server/resources)（アシスタントが読む文書やデータ）: `ttdeploytool://docs/constraints`（目的と注意事項）、`ttdeploytool://docs/rbac`（必要な権限）、`ttdeploytool://deployments/{subscription}/{resource_group}/{app_name}`（環境の状態）
- [Prompts](https://modelcontextprotocol.io/specification/latest/server/prompts)（手順の案内。アシスタントのメニューから選べます）: `deploy-timetracker`、`local-dry-run`、`delete-timetracker`

### 安全策

アシスタント（AI）が誤って Azure を変更しないよう、サーバーの側で次のことを守らせています。
- **計画なしでは変更しない**: Azure を変更するのは `start_deployment` と `start_deletion` だけです。どちらも、先に `plan_*` で作った計画（`plan_id`。15 分で期限切れ、1 回だけ使える）が必要で、計画と違う内容では実行しません。実行の直前に Azure の状態を確かめ直し、計画の後に状態が変わっていれば中止します
- **DB のデータが消える更新には同意が要る**: 既存の DB アプリを更新する計画は、`acknowledge_data_loss=true`（DB のデータが失われることへの同意）が無いと実行しません。ただし、`persist_db` で DB のデータを Azure Files に置いていて、永続化を続ける更新（DB アプリを停止してから更新し、開始する）では、データが失われないため同意は不要です
- **削除にはアプリ名の入力が要る**: 削除は、`confirm_app_name` に計画と同じアプリ名を指定しないと実行しません（アシスタントには、利用者にアプリ名を入力してもらうよう指示しています）
- **同じ環境への同時実行はしない**: 同じ対象（サブスクリプション・リソースグループ・アプリ名）のジョブは、同時に 1 つしか実行しません
- **パスワードを出さない**: パスワードは、ツールの結果やログに出しません

**DB のデータの永続化**: `plan_deployment` / `validate_deployment` に `persist_db=true` を付けると、DB のデータを Azure Files に置き、DB コンテナの再起動の後も残します（ストレージの料金がかかります。省略時は、既存の DB アプリの今の状態を引き継ぎます）。`plan_deletion` は、既定でそのストレージアカウントも削除の対象にし、`keep_db_data=true` で残せます。違いと注意点は README の [DB のデータの扱いと永続化](../README.md#db-のデータの扱いと永続化--persist-db) を参照してください。

**利用可能期限**: `plan_deployment` / `validate_deployment` の `expires_on` に、期限日（`yyyy-MM-dd`。MCP サーバーが動く PC・サーバーの時刻帯で、その日の 23:59:59 まで）または `none`（期限なし）を指定できます。省略すると、既存のデプロイの今の期限を引き継ぎ、無ければ**今日から 32 日後**にします。計画と結果の `expires_on` に、決まった期限が出ます。期限を過ぎると、TTDeployTool のデスクトップアプリが自動で削除します（DB のデータも消えます。MCP サーバーと CLI は期限での削除をしません）。詳しくは README の `--expires-on` を参照してください。

**グループ**: `plan_deployment` / `validate_deployment` の `group` に、用途・利用者ごとにデプロイを分けるグループ名（64 文字まで）を指定できます。Azure のタグ `tt-group` に記録し、TTDeployTool のデスクトップアプリのデプロイの一覧で絞り込み・並べ替えに使えます。省略すると、既存のデプロイは今のグループを引き継ぎます。計画の結果の `group.display` に、付けるグループ（引き継ぐ・変えるの別）が出ます。`get_deployment_info` の `group` で今のグループを確かめられます。グループを外すのは、デスクトップアプリで行います。

**利用者が気をつけること**:
- `start_deployment` と `start_deletion` は、アシスタントの設定で**自動承認にしないでください**（実行の前に、計画の内容を確認できるように）
- パスワードは、ツールの引数ではなく、サーバーの環境変数（`TT_MCP_DB_PASSWORD` / `TT_MCP_TRACKER_PASSWORD`）で渡すことをおすすめします。引数で渡すと、会話の履歴に残ります
- 認証モードはサーバーの設定（`TT_MCP_AUTH_MODE`）で決まり、会話の中では変えられません。既定は `azure-cli` です（事前に `az login` が必要です。また、アシスタントが起動したサーバーから `az` コマンドが見つかるよう、`az` が `PATH` に入っている必要があります）

### サーバーの設定（環境変数）

どれも省略できます。アシスタントの設定ファイルの `env`（または `-e`）で指定します。

| 環境変数 | 既定 | 説明 |
|---|---|---|
| `TT_MCP_AUTH_MODE` | `azure-cli` | 認証モード: `azure-cli` / `default` / `sp-env` / `device-code` / `managed-identity`（README の[認証モード別の前提環境と利用方法](../README.md#認証モード別の前提環境と利用方法)） |
| `TT_MCP_DEFAULT_SUBSCRIPTION` | なし | サブスクリプションを省略したときに使う ID |
| `TT_MCP_DEFAULT_RESOURCE_GROUP` | なし | リソースグループを省略したときに使う名前 |
| `TT_MCP_DB_PASSWORD` | なし | DB のパスワード（設定すると、アシスタントはパスワードを尋ねません） |
| `TT_MCP_TRACKER_PASSWORD` | なし | Timetracker のパスワード（同上） |
| `TT_MCP_OUTPUT_ROOT` | なし | 設定すると、`generate_compose` でファイルを書く場所を、このフォルダーの下に限ります（絶対パス） |
| `TT_MCP_VERBOSE` | `false` | `true` で詳しいログを出します（ログは標準エラー出力に出ます） |

### アシスタントの設定例

#### Claude Code

コマンドで登録します（[Claude Code での MCP の使い方](https://code.claude.com/docs/ja/mcp)）。`-e` の値は、Claude Code の設定ファイルに平文で保存されます。
```powershell
claude mcp add ttdeploytool `
  -e TT_MCP_DEFAULT_SUBSCRIPTION=<サブスクリプション ID> `
  -e TT_MCP_OUTPUT_ROOT=C:\work\timetracker `
  -- C:\tools\ttdeploytool-mcp\ttdeploytool-mcp.exe
```
パスワードを設定ファイルに書きたくない場合は、プロジェクトの `.mcp.json` で環境変数を参照できます（Claude Code を起動する前に、端末で `TT_DB_PASSWORD` などを設定しておきます）。
```json
{
  "mcpServers": {
    "ttdeploytool": {
      "command": "C:\\tools\\ttdeploytool-mcp\\ttdeploytool-mcp.exe",
      "env": {
        "TT_MCP_DB_PASSWORD": "${TT_DB_PASSWORD}",
        "TT_MCP_TRACKER_PASSWORD": "${TT_TRACKER_PASSWORD}"
      }
    }
  }
}
```

#### VS Code（GitHub Copilot）

設定ファイル `mcp.json` に登録します（[VS Code で MCP サーバーを使う](https://code.visualstudio.com/docs/agent-customization/mcp-servers)）。パスワードは初回の起動時に入力欄で尋ねられ、ファイルには書かれません。

`mcp.json` は、次のどちらかに置きます。どのフォルダーを開いていても使えるよう、ユーザー単位の設定をおすすめします。
- **ユーザー単位**: コマンド パレット（`Ctrl+Shift+P`）で **MCP: Open User Configuration** を実行すると開くファイル（Windows の既定では `%APPDATA%\Code\User\mcp.json`。プロファイルを使い分けている場合は、プロファイルごとのフォルダー）
- **ワークスペース単位**: VS Code で開いているフォルダーの直下の `.vscode\mcp.json`（**MCP: Open Workspace Folder MCP Configuration** で開けます）。そのフォルダーを開いたときだけ使われます。ほかのフォルダーや、ファイルを置いたフォルダーの親を開いているときは使われません

```json
{
  "inputs": [
    { "type": "promptString", "id": "db-password", "description": "Timetracker の DB パスワード", "password": true },
    { "type": "promptString", "id": "tracker-password", "description": "Timetracker のパスワード", "password": true }
  ],
  "servers": {
    "ttdeploytool": {
      "type": "stdio",
      "command": "C:\\tools\\ttdeploytool-mcp\\ttdeploytool-mcp.exe",
      "env": {
        "TT_MCP_DB_PASSWORD": "${input:db-password}",
        "TT_MCP_TRACKER_PASSWORD": "${input:tracker-password}"
      }
    }
  }
}
```
- 既に `mcp.json` がある場合は、ファイルを置き換えずに、既存の `servers` と `inputs` の中に、上の `timetracker` と 2 つの入力を書き足します
- 保存したら、拡張機能ビューの「MCP サーバー」、または **MCP: List Servers** で `timetracker` を起動します
- 入力の参照は `${input:<id>}`（`input` は単数形）と書きます（[MCP の設定のリファレンス](https://code.visualstudio.com/docs/agents/reference/mcp-configuration)）
- **うまくいかないとき**:
  - `${input:<id>}` で読み込みのエラーになる: `<id>` と同じ `id` の入力が、トップレベルの `inputs` の配列に無いことがよくある原因です（`inputs` が `servers` の中にある、`inputs` のキーが 2 つある、など）
  - `${inputs:<id>}` などと書き間違えると、エラーにはならず、その文字列がそのままパスワードとして使われます。ご注意ください

#### Claude Desktop

`claude_desktop_config.json` に登録します。
```json
{
  "mcpServers": {
    "ttdeploytool": {
      "command": "C:\\tools\\ttdeploytool-mcp\\ttdeploytool-mcp.exe",
      "env": { "TT_MCP_DEFAULT_SUBSCRIPTION": "<サブスクリプション ID>" }
    }
  }
}
```

### 使い方の例

アシスタントに次のように頼むと、ツールを順に呼びながら進めます（Prompts に対応したアシスタントでは、`deploy-timetracker` などを選んで始めることもできます）。

| 頼み方の例 | 進み方 |
|---|---|
| 「Timetracker の検証環境を、Azure の rg-tt-demo に PostgreSQL で作ってください」 | 設定の確認と検証 → `plan_deployment` の結果（作るリソース・警告）が示されるので、確認して実行を指示します → 数分後に Timetracker の URL が返ります |
| 「DB のデータを残したいので、永続化してデプロイしてください」 | `persist_db=true` で計画します。ストレージの料金と、削除での扱いが案内されます |
| 「研修用のグループでデプロイしてください」 | `group="研修"` で計画します。計画の結果に「グループ: 研修」が出ます |
| 「来月末まで使うので、期限を 2026-11-30 にしてデプロイしてください」 | `expires_on="2026-11-30"` で計画します。期限を過ぎるとデスクトップアプリが自動で削除することが案内されます |
| 「ローカルの Docker で試したいので、C:\work\timetracker に compose ファイルを作ってください」 | `generate_compose` で `docker-compose.yml` と `.env` を作ります（Azure は使いません） |
| 「rg-tt-demo の Timetracker を削除してください」 | `plan_deletion` の結果（削除するもの・残すもの）が示されるので、確認してアプリ名を入力します |

計画とジョブは、サーバーのプロセスの中にだけ保存されます。アシスタントを再起動すると消えますが、Azure の状態は `get_deployment_info` でいつでも確かめられます。

---

## HTTP 版（開発中）

> [!NOTE]
> HTTP 版は開発中で、まだ提供していません。サーバーの本体（HTTP でのやり取り、Entra ID による呼び出し元の認証、`server` 方式と `user` 方式）、コンテナイメージ、Azure Container Apps への配置と Entra ID のアプリ登録のスクリプトはできており、Azure と Entra ID で配置とツールの動作を確認しました。Claude Code からのサインインと接続も確認しました。VS Code からのサインインは、まだ確認していません（下の[導入](#導入-1)）。内容は、提供までに変わることがあります。

### 開発の段階

| 段階 | 内容 | 状況 |
|---|---|---|
| 4a | HTTP でのやり取り、Entra ID のアクセストークンの検証、アプリロール、呼び出し元ごとの計画・ジョブ、`server` 方式（許可したリソースグループだけ）、HTTP 用の `generate_compose`、コンテナイメージ | 実装済み（自動テストとローカルの Docker で確認） |
| 4b | `user` 方式（利用者本人の権限）と、方式を選ぶ引数 `azure_identity` | 実装済み（Entra ID でのトークンの交換も 4c で確認） |
| 4c | Azure Container Apps への配置、Entra ID のアプリ登録、アシスタントからの接続の手順 | Azure と Entra ID で配置とツール、Claude Code からの接続を確認済み（VS Code からの接続は未確認） |

### stdio 版との違い

| 項目 | stdio 版 | HTTP 版 |
|---|---|---|
| 配布の形 | 単一ファイルの EXE（GitHub Releases の zip） | コンテナイメージ `ghcr.io/yamadakou/ttdeploytool-mcp`（リリースのタグで [GitHub Container Registry](https://docs.github.com/ja/packages/working-with-a-github-packages-registry/working-with-the-container-registry) に公開）。Azure Container Apps などに置く |
| アシスタントの接続 | 子プロセスとして起動する（`command` にパスを指定） | URL で接続する（`https://<サーバー>/mcp`。[Streamable HTTP](https://modelcontextprotocol.io/specification/latest/basic/transports)）。TLS は手前（Container Apps のイングレスなど）で終端し、コンテナはポート 8080 で待ち受ける |
| 利用者の認証 | 不要（PC の利用者がそのまま使う） | **必須**。Microsoft Entra ID の[アクセストークン](https://learn.microsoft.com/ja-jp/entra/identity-platform/access-tokens)（スコープ `Mcp.Access`）で認証する（[MCP の認可の仕組み](https://modelcontextprotocol.io/specification/latest/basic/authorization)）。Entra ID は動的なクライアント登録に対応していないため、アシスタントには、事前に登録したクライアント ID を設定する |
| Azure を操作する権限 | 利用者の PC の資格情報（`TT_MCP_AUTH_MODE`） | サーバーの設定で、次のどちらか、または両方を使えるようにする<br/>・`server` 方式: サーバーの[マネージド ID](https://learn.microsoft.com/ja-jp/azure/container-apps/managed-identity)（またはサービスプリンシパル）の権限で、管理者が許可したリソースグループだけを操作する<br/>・`user` 方式: サインインした利用者本人の権限で操作する（範囲は本人の Azure のロールで決まる）<br/>両方を使える場合は、ツールの引数 `azure_identity` で選ぶ。計画に方式を記録し、実行は計画の方式で行う |
| だれが使えるか | PC の利用者 | Entra ID の[アプリロール](https://learn.microsoft.com/ja-jp/entra/identity-platform/howto-add-app-roles-in-apps)で決める。`TTDeployTool.Deploy.Server` は両方の方式、`TTDeployTool.Deploy.User` は `user` 方式だけ。どちらも無ければ、Azure を操作しないツール（`get_deployment_options` / `validate_deployment` / `generate_compose`）と文書だけ |
| パスワードの渡し方 | 環境変数（推奨）または引数 | 引数だけ（サーバーの環境変数は全員で共有になるため読まない） |
| `generate_compose`（Dry-run） | 利用者の PC にファイルを書く | ファイルは書かず、`docker-compose.yml` と `.env` の内容を返す（パスワードはプレースホルダー） |
| 計画・ジョブ | サーバーのプロセスの中だけに保存 | 同じ。利用者ごとに分かれ、作った本人だけが実行・参照できる。レプリカは 1 つに固定する |

ツールと安全策（計画 → 確認 → 実行、削除のときのアプリ名の確認など）は、stdio 版と同じです。

### サーバーの設定（環境変数）

| 環境変数 | 必須 | 説明 |
|---|---|---|
| `TT_MCP_HTTP__TENANT_ID` | ✔ | Entra ID のテナント ID |
| `TT_MCP_HTTP__CLIENT_ID` | ✔ | MCP サーバーの[アプリ登録](https://learn.microsoft.com/ja-jp/entra/identity-platform/quickstart-register-app)のクライアント ID |
| `TT_MCP_HTTP__PUBLIC_URL` | ✔ | 公開する MCP のエンドポイントの URL（`https://…/mcp`。末尾に `/` を付けない） |
| `TT_MCP_HTTP__IDENTITY_MODES` | – | 使える方式。`server`（既定）/ `user` / `server,user` |
| `TT_MCP_HTTP__DEFAULT_IDENTITY_MODE` | – | `azure_identity` を省略したときの方式（既定は `IDENTITY_MODES` の最初）。利用者がその方式を使えなければ、使えるほうになる |
| `TT_MCP_HTTP__SERVER_ALLOWED_RESOURCE_GROUPS` | `server` 方式が有効なら ✔ | `server` 方式で操作してよいリソースグループ（`/subscriptions/<ID>/resourceGroups/<名前>` のカンマ区切り） |
| `TT_MCP_HTTP__SERVER_IDENTITY` | – | `server` 方式で使う資格情報。`managed-identity`（既定）/ `sp-env`。ユーザー割り当てのマネージド ID は、`AZURE_CLIENT_ID` でクライアント ID を指定する |
| `TT_MCP_HTTP__SERVER_CAN_CREATE_RESOURCE_GROUP` | – | `server` 方式で、許可したリソースグループが無いときに作成してよいか。既定は `false` |
| `TT_MCP_HTTP__USER_CLIENT_CREDENTIAL` | – | `user` 方式のトークンの交換で、サーバーのアプリ登録が使う資格情報。`managed-identity`（既定。マネージド ID を[フェデレーション資格情報](https://learn.microsoft.com/ja-jp/entra/workload-id/workload-identity-federation-config-app-trust-managed-identity)として使い、シークレットを持たない）/ `client-secret` |
| `TT_MCP_HTTP__CLIENT_SECRET` | `client-secret` なら ✔ | アプリ登録のクライアント シークレット（ツールの結果・ログには出さない） |

必須の設定が無い、または値が正しくない場合、サーバーは理由を出して起動を止めます。`TT_MCP_AUTH_MODE` と、パスワードの環境変数（`TT_MCP_DB_PASSWORD` / `TT_MCP_TRACKER_PASSWORD`）は、HTTP 版では使いません。

### `server` 方式の前提

- 管理者が、検証環境を作るリソースグループを事前に作り、`TT_MCP_HTTP__SERVER_ALLOWED_RESOURCE_GROUPS` に登録します。許可していないリソースグループへの操作は、Azure を読む前に拒否します
- サーバーのマネージド ID には、そのリソースグループの範囲で Contributor を割り当てます。リソースプロバイダーの登録も、管理者が事前に行います
- 計画の結果には「サーバーの ID（マネージド ID）で実行します」と表示されます
- Azure の[アクティビティログ](https://learn.microsoft.com/ja-jp/azure/azure-monitor/fundamentals/activity-log)には、すべてサーバーの ID の操作として残ります。だれの操作かは、サーバーのログ（利用者のユーザー名と ID を記録）で確かめます

### `user` 方式の前提

- 利用者本人に、CLI を自分で実行する場合と同じロールが必要です（README の[必要な RBAC ロールと権限](../README.md#必要な-rbac-ロールと権限)）。権限が足りない場合は、計画の段階で示されます
- サーバーは、利用者のアクセストークンを [OBO（On-Behalf-Of）](https://learn.microsoft.com/ja-jp/entra/identity-platform/v2-oauth2-on-behalf-of-flow)で Azure 用のトークンに交換します。そのため、MCP サーバーのアプリ登録に、Azure Service Management の委任アクセス許可（`user_impersonation`）を加え、[テナントの管理者の同意](https://learn.microsoft.com/ja-jp/entra/identity/enterprise-apps/grant-admin-consent)を得ておきます。同意が無いと、AADSTS65001 で失敗します（スクリプトを実行した本人の分だけは、管理者でなくても同意できることがあります）
- [Azure の管理 API には MFA（多要素認証）が必須](https://learn.microsoft.com/ja-jp/entra/identity/authentication/concept-mandatory-multifactor-authentication)です。MCP サーバーへのサインインで MFA を行っていないと、交換が AADSTS50076 で失敗し、サインインし直しても解消しません。テナントの管理者が、[条件付きアクセス](https://learn.microsoft.com/ja-jp/entra/identity/conditional-access/overview)で、MCP サーバーのアプリ（エンタープライズ アプリケーション「TTDeployTool MCP (…)」）へのサインインに MFA を求めるポリシーを設定してください
- 計画の結果には「サインインした利用者本人の権限（user 方式）」と表示され、Azure のアクティビティログにも本人の操作として残ります
- エラーコード（AADSTS…）の意味は、[Microsoft Entra の認証と認可のエラーコード](https://learn.microsoft.com/ja-jp/entra/identity-platform/reference-error-codes)で確かめられます

### 導入

> [!NOTE]
> Azure と Entra ID で、配置・アプリ登録・ツール（`server` 方式と `user` 方式のデプロイと削除）を確認しました。Claude Code からのサインインと接続も確認しました。VS Code からのサインインは、まだ確認していません。確認の結果は [Phase 4c の詳細設計](design/mcp/mcp-phase4c.md)の「確認結果」にまとめています。

1. **サーバーを置く**（管理者）: [PowerShell 7](https://learn.microsoft.com/ja-jp/powershell/scripting/install/install-powershell-on-windows) と Azure CLI で、次を実行します。MCP サーバーを置くリソースグループ、マネージド ID、Entra ID のアプリ登録（サーバー用とクライアント用）、Container Apps の環境と Container App を作ります。`server` 方式なら、許可するリソースグループに、マネージド ID の Contributor を割り当てます
   ```powershell
   ./scripts/deploy-mcp-http.ps1 -SubscriptionId <サブスクリプション ID> -ResourceGroup rg-tt-mcp -IdentityModes server,user -AllowedResourceGroups rg-tt-team1
   ```
2. **利用者に使えるようにする**（管理者）: 利用者に[アプリロールを割り当てます](https://learn.microsoft.com/ja-jp/entra/identity/enterprise-apps/assign-user-or-group-access-portal)
   ```powershell
   ./scripts/assign-mcp-role.ps1 -Role Server -UserPrincipalName alice@example.com
   ```
3. **アシスタントに登録する**（利用者）: Claude Code の例です（クライアントのアプリの ID と URL は、手順 1 の出力に表示されます）
   ```powershell
   claude mcp add --transport http --client-id <クライアントのアプリの appId> --callback-port 8765 timetracker https://<FQDN>/mcp
   ```
   追加した後、Claude Code で `/mcp` を開いてサインインします。VS Code の設定例は、詳細設計を参照してください

詳しい設計は、[HTTP 版の詳細設計（Phase 4a）](design/mcp/mcp-phase4a.md)、[`user` 方式の詳細設計（Phase 4b）](design/mcp/mcp-phase4b.md)、[配置とアプリ登録の詳細設計（Phase 4c）](design/mcp/mcp-phase4c.md)と、[MCP ツール設計書](MCP_TOOL_DESIGN.md)の「3.3 MCP サーバーの実行形態」「4.10 秘密情報（パスワード）の扱い」「4.11 HTTP 公開時の Azure 資格情報」を参照してください。
