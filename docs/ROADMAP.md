# 将来の検討

まだ着手していない機能の候補を、優先度の高い順にまとめる。着手するときは、詳細設計（`docs/design/`）を書いてから進める。

| 優先度 | 項目 | 状況 |
|---|---|---|
| 最優先 | [1. DB データの永続化オプション](#1-db-データの永続化オプション) | 未着手 |
| 最優先 | [2. ストア配布できるデスクトップアプリ](#2-ストア配布できるデスクトップアプリ) | 設計中（`docs/design/desktop-app.md`） |
| 通常 | [3. HTTP 版での Key Vault のシークレット参照](#3-http-版での-key-vault-のシークレット参照) | 未着手 |
| 通常 | [4. MCP の Tasks への移行](#4-mcp-の-tasks-への移行) | 仕様の安定待ち |
| 通常 | [5. WebAPI のフロントエンド](#5-webapi-のフロントエンド) | 未着手 |

## 1. DB データの永続化オプション
- **目的**: Azure 上の DB のデータを、DB コンテナの再起動（再デプロイ、リビジョンの更新、Azure 基盤のメンテナンス）の後も残せるようにする
- **背景**: 今は検証環境向けとして、DB を永続ボリュームなしで動かしている（REVIEW_ISSUES No.4 のオーナー判断。README の「本ツールの目的と注意事項」）。既定はこのままにし、指定したときだけ永続化する
- **案**: ストレージアカウントの Azure Files を Container Apps の Environment のストレージとして登録し、DB コンテナのデータのディレクトリにマウントする。CLI の引数（例 `--persist-db`）と MCP のツールの引数で有効にする
- **検討すること**
  - DB ごとの相性: PostgreSQL・SQL Server のデータファイルを Azure Files（SMB / NFS）に置けるか、権限や性能の制約。NFS は VNet 統合と Premium のファイル共有が必要
  - 追加の Azure リソースと権限: ストレージアカウントとファイル共有の作成、Environment へのストレージの登録に必要なロール（README の「必要な RBAC ロールと権限」と権限の事前確認を更新）
  - 削除（`delete` / `plan_deletion`）での扱い: ストレージとデータを残すか消すか、確認の方法
  - 再デプロイの警告: 永続化した場合は、データが失われる警告と MCP の `acknowledge_data_loss` を不要にできるか
  - 料金の案内
  - 関連: Azure 上の SQL Server でサンプル DB を作れない制約（REVIEW_ISSUES No.19）も、同じ共有ストレージで解消できる可能性がある

## 2. ストア配布できるデスクトップアプリ
- **目的**: CLI や AI アシスタントを使わない利用者も、画面の操作でデプロイ・状態の確認・削除ができるようにする。ストア（Microsoft Store を想定）から導入・更新できるようにする
- **背景**: README の「将来の GUI 化（デスクトップ/ウェブ）に備え、ドメイン層とコントローラー層を分離」の方針どおり、CLI・MCP サーバーと同じアプリケーション層（`Timetracker.Application`）の上に、フロントエンドの一つとして載せる
- **検討すること**
  - UI のフレームワーク: WinUI 3、.NET MAUI、Avalonia など（Windows だけか、macOS も対象にするか）
  - ストアでの配布: MSIX のパッケージ、署名、ストアの審査の要件、自動更新
  - Azure へのサインイン: ブラウザーでの対話サインイン（`InteractiveBrowserCredential` や Windows の WAM）。CLI の `az login` に頼らない
  - パスワードの入力と保持: 画面で入力し、保存しない（保存する場合は OS の資格情報の保管庫を使う）
  - 画面の流れ: 計画（変更内容・権限の事前確認・警告）→ 確認 → 実行（進み具合の表示）を、MCP のツールと同じ安全策で行う
  - ローカル検証（Dry-run）の docker-compose の生成と保存

## 3. HTTP 版での Key Vault のシークレット参照
- HTTP 版の MCP サーバーで、パスワードを引数で渡さずに済むよう、Azure Key Vault のシークレット参照（`db_password_secret_uri`）を受け付ける（`docs/MCP_TOOL_DESIGN.md` の 4.10）

## 4. MCP の Tasks への移行
- MCP 仕様の Tasks（長時間の処理の標準化）が C# SDK と主要なクライアントで安定したら、`start_*` と `get_job_status` から移行するか検討する（`docs/MCP_TOOL_DESIGN.md` の 4.7）

## 5. WebAPI のフロントエンド
- MCP のツールと 1 対 1 に対応する REST API を、同じアプリケーション層の上に載せる（`docs/MCP_TOOL_DESIGN.md` の 3.4）。認証は HTTP 版の MCP サーバーと同じ仕組み（4.11）を使う
