# 詳細設計

詳細設計の文書を、共通部分と、入り口（CLI・MCP サーバー・デスクトップアプリ）ごとのフォルダーに分けています。各フォルダーの `README.md` が、その部分の構造（図）です。全体の設計は [`../MCP_TOOL_DESIGN.md`](../MCP_TOOL_DESIGN.md)、計画は [`../ROADMAP.md`](../ROADMAP.md) にあります。

| フォルダー | 構造 | 文書 |
|---|---|---|
| [`common/`](common/) | [共通部分の構造](common/README.md)（層・計画 → 確認 → 実行・ドメインモデル・Azure に作るもの・入り口ごとの違い） | [`db-persistence.md`](common/db-persistence.md)（DB のデータの永続化）・[`resource-group-deletion.md`](common/resource-group-deletion.md)（リソースグループの削除）・[`deployment-groups.md`](common/deployment-groups.md)（デプロイのグループ） |
| [`cli/`](cli/) | [CLI の構造](cli/README.md)（構成・deploy と delete の流れ） | [`cli-delete.md`](cli/cli-delete.md)（delete） |
| [`mcp/`](mcp/) | [MCP サーバーの構造](mcp/README.md)（構成・ツール・計画 → 確認 → 実行・HTTP 版の守り） | [`mcp-phase3a.md`](mcp/mcp-phase3a.md)〜[`mcp-phase4c.md`](mcp/mcp-phase4c.md)（段階ごと）・[`db-persistence-mcp-http-check.md`](mcp/db-persistence-mcp-http-check.md)（HTTP 版での永続化の確認） |
| [`desktop/`](desktop/) | [デスクトップの構造](desktop/README.md)（構成・Desktop.Core の中・予定のデプロイ・ジョブの状態・PC に残すデータ） | [`desktop-app.md`](desktop/desktop-app.md)（詳細設計）・[`desktop-jobs.md`](desktop/desktop-jobs.md)（ジョブ・予定・期限・記録）・[`desktop-wizard.md`](desktop/desktop-wizard.md)（画面の流れ）・[`desktop-settings.md`](desktop/desktop-settings.md)・[`desktop-ui-tests.md`](desktop/desktop-ui-tests.md)・[`desktop-app-check.md`](desktop/desktop-app-check.md)（実機の確認）・[`desktop-app-store.md`](desktop/desktop-app-store.md)（Store への提出）・[`desktop-preview.md`](desktop/desktop-preview.md)（評価版）・[`desktop-tables.md`](desktop/desktop-tables.md)（表の使い勝手の検討）・[`upstream-tableview-issue.md`](desktop/upstream-tableview-issue.md)（WinUI.TableView への不具合の報告の下書き）・[`desktop-blazor-hybrid.md`](desktop/desktop-blazor-hybrid.md)（Blazor Hybrid の試作）・[`desktop-blazor-hybrid-check.md`](desktop/desktop-blazor-hybrid-check.md)（試作の実機での確認） |
