# Timetracker デプロイ MCP サーバー

[README に戻る](../README.md)

CLI と同じ機能を、MCP（Model Context Protocol）に対応した AI アシスタント（Claude Code、VS Code の GitHub Copilot、Claude Desktop など）から、対話しながら使えます。MCP サーバー `timetracker-mcp.exe` は、アシスタントが子プロセスとして起動し、標準入出力（stdio）で通信します。

前提となる Azure の権限・リソースプロバイダーの登録・認証モードは CLI と共通です。README の[利用者向けマニュアル](../README.md#利用者向けマニュアル)を参照してください。

## できること

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

## 安全策

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

## 導入

1. [Releases](https://github.com/yamadakou/timetracker-deployment-tool/releases) から `timetracker-mcp-vX.Y.Z.zip` を取得し、任意のフォルダーに展開します（例: `C:\tools\timetracker-mcp\timetracker-mcp.exe`）。.NET のインストールは不要です
2. Azure を使う場合は、[必要な RBAC ロールと権限](../README.md#必要な-rbac-ロールと権限)と[リソースプロバイダーの登録](../README.md#azure-リソースプロバイダーの登録)を確認し、`az login` でサインインします
3. 使うアシスタントに、次の例のようにサーバーを登録します

## サーバーの設定（環境変数）

| 環境変数 | 既定 | 説明 |
|---|---|---|
| `TT_MCP_AUTH_MODE` | `azure-cli` | 認証モード: `azure-cli` / `default` / `sp-env` / `device-code` / `managed-identity`（[認証モード別の前提環境と利用方法](../README.md#認証モード別の前提環境と利用方法)） |
| `TT_MCP_DEFAULT_SUBSCRIPTION` | なし | サブスクリプションを省略したときに使う ID |
| `TT_MCP_DEFAULT_RESOURCE_GROUP` | なし | リソースグループを省略したときに使う名前 |
| `TT_MCP_DB_PASSWORD` | なし | DB のパスワード（設定すると、アシスタントはパスワードを尋ねません） |
| `TT_MCP_TRACKER_PASSWORD` | なし | Timetracker のパスワード（同上） |
| `TT_MCP_OUTPUT_ROOT` | なし | 設定すると、`generate_compose` の出力先をこのフォルダーの配下に限ります（絶対パス） |
| `TT_MCP_VERBOSE` | `false` | `true` で詳細ログを出します（ログは標準エラー出力に出ます） |

## アシスタントの設定例

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

## 使い方の例

アシスタントに次のように頼むと、ツールを順に呼びながら進めます（Prompts に対応したアシスタントでは `deploy-timetracker` などを選んでも始められます）。
- 「Timetracker の検証環境を Azure の rg-tt-demo に PostgreSQL で作ってください」→ 設定の確認と検証 → `plan_deployment` の結果（作成するリソース・警告）が示されるので、確認して実行を指示します → 数分後に Timetracker の URL が返ります
- 「ローカルの Docker で試したいので、C:\work\timetracker に compose ファイルを作ってください」
- 「rg-tt-demo の Timetracker を削除してください」→ `plan_deletion` の結果（削除するもの・残すもの）が示されるので、確認してアプリ名を入力します

計画とジョブはサーバーのプロセスの中にだけ保存されます。アシスタントを再起動すると消えますが、Azure の状態は `get_deployment_info` でいつでも確認できます。
