# デスクトップアプリの画面のデザイン案（2026-11-13）

オーナーの依頼「全体的に UX を考慮したモダンなデザインにして欲しい。デザイン案として画面イメージの画像を複数案」に対する画面イメージ。
データはモック（実在のサブスクリプション・リソースではない）。

| 案 | デプロイの一覧 | 新しくデプロイ |
|---|---|---|
| A: Fluent（Windows 11 の流儀） | [A-list.png](A-list.png) | [A-wizard.png](A-wizard.png) |
| B: ダッシュボード | [B-list.png](B-list.png) | [B-wizard.png](B-wizard.png) |
| C: コンパクト・ダーク | [C-list.png](C-list.png) | [C-wizard.png](C-wizard.png) |

作り直すとき: `python3 build.py`（HTML を作る）→ `node shot.js`（Playwright の Chromium で PNG にする）。日本語は Noto Sans JP、英数字は Inter。
