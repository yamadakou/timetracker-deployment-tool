# 詳細設計: デスクトップアプリの UI テスト（Phase 6）

- 対象ブランチ: `feature/desktop-app`
- 前の段階: `docs/design/desktop-app.md`（Phase 5。13 のテストの方針では、WinUI 3 の画面を自動テストの対象にしていなかった）
- 状況: 案。オーナーの回答で決めたこと: 道具は FlaUI、範囲は CI とローカルの半自動の両方を段階的に、診断用のログ（`desktop-app.md` の 7.2）を先に入れる。8 の論点もオーナーの回答で決めた（CI は署名した MSIX・Dry-run は保存まで・半自動は実行と削除まで）。一次情報の確認日は 2026-10-19
- 記録: `docs/REVIEW_ISSUES.md` の No.51（予定）

## 1. 目的
Windows の実機での確認で、次の問題が見つかった。どれも `Desktop.Core` の単体テスト（Linux で動く ViewModel のテスト）では見つけられない。
- Visual Studio でビルドすると、デスクトップアプリのプロジェクトが省かれて起動できない（`.slnx` のプラットフォームの対応付け）
- 起動プロファイルが無く、MSIX として起動できない
- サインインの後にサブスクリプションを読めないなど、本物のサインイン（WAM）と Azure での動きが不安定

UI テストでは、実際に起動したアプリを Windows の UI Automation で操作し、次を確かめる。
- 画面と ViewModel の結び付き（`x:Bind`、選択の変更イベント、ボタンの有効・無効、表示の切り替え）が正しいこと（CI。PR ごと）
- 本物のサインインと Azure で、サブスクリプションの読み込みから計画・実行・削除までが通ること（ローカルの半自動。サインインだけ人が行う）

## 2. スコープ
| 項目 | Phase 6 |
|---|---|
| 画面の要素に AutomationId を付ける | ✔（6a） |
| CI で、Azure を使わない操作の UI テスト | ✔（6a） |
| ローカルの半自動の UI テスト（本物のサインインと Azure） | ✔（6b） |
| 偽のサインインと Azure を差し込むテスト用のビルド | ✘（範囲の回答で選ばなかった。製品のコードにテスト用の入口を作らない） |
| UI テストの網羅率の測定 | ✘（`Desktop` のプロジェクトは網羅率の対象外のまま。ViewModel の網羅率 100% は `Desktop.Core` のテストで保つ） |
| 画面の見た目（スクリーンショットの比較） | ✘。失敗したときのスクリーンショットだけを成果物に残す |

## 3. 道具（決定: FlaUI）
| 候補 | 内容 | 判断 |
|---|---|---|
| **FlaUI**（FlaUI.UIA3 5.0.0、2025-02 公開、OSS） | .NET から UI Automation（UIA3）を使うライブラリ。xUnit v3 のテストとして書け、System Test と同じ実行方法・結果の形式（trx）にできる | **採用**（オーナー判断） |
| winapp CLI（`winapp ui`） | Microsoft の公式のコマンド。`inspect` / `invoke` / `set-value` / `wait-for` / `screenshot` と、アプリの登録・起動（`winapp run`）。UI Automation のパターンで操作する命令は、ロックされた画面や CI でも動くと記載されている | 不採用。パブリック プレビューで、テストは PowerShell からコマンドを呼ぶ形になる。調査（`winapp ui inspect`）には使える |
| WinAppDriver / Appium の Windows ドライバー | WebDriver の形式 | 不採用。WinAppDriver は 2022 年以降更新されておらず、Appium の Windows ドライバーもその上に作られている |

操作は UI Automation のパターン（Invoke・Value・SelectionItem・Toggle）で行い、マウスやキーボードの入力の合成は使わない。入力の合成は、前面のウィンドウと操作できるデスクトップが要り、CI で不安定になるため（winapp CLI の説明でも、CI ではパターンを使う命令を勧めている）。

## 4. 構成
### 4.1 プロジェクト
- `tests/TTDeployTool.Controller.Desktop.UITests`（新規。`net10.0-windows10.0.19041.0`、xUnit v3、FluentAssertions、FlaUI.UIA3）。Windows でしか動かないため `TTDeployTool.sln` には入れず、`TTDeployTool.Desktop.slnx` に入れる
- テストの種類はクラスで分ける: `CiUiTests`（Azure を使わない。常に実行）と `AzureUiTests`（本物の Azure。設定が無ければスキップする。System Test の `TT_SYSTEMTEST_SUBSCRIPTION_ID` と同じ考え方）
- 共通の部品: アプリの起動と終了、画面の切り替え（`NavigationView` の項目の選択）、要素の待ち合わせ（`Retry.WhileNull` などで最大 N 秒）、失敗したときのスクリーンショットの保存

### 4.2 AutomationId
`MainWindow.xaml` の、テストで使う要素に `AutomationProperties.AutomationId` を付ける（Microsoft Learn: 言語によらず同じ値にし、テストの自動化に使う）。名前は画面と要素で決める（例: `Nav.Target`、`SignIn.Button`、`SignIn.Account`、`Target.Subscription`、`Target.Reload`、`Target.ResourceGroup`、`Target.Error`、`DryRun.Save`、`Jobs.LogFolder`）。表示の文言は変わることがあるため、テストでは文言ではなく AutomationId で要素を探す（文言は、表示の内容を確かめるときだけ使う）。

### 4.3 起動する対象
| 実行の場所 | 起動のしかた |
|---|---|
| CI | x64 の MSIX を、ジョブの中で作った一時的な自己署名の証明書（発行者は `CN=TTDeployTool`）で署名し、公開鍵の証明書を「信頼されたユーザー」に入れて導入し、AppUserModelID で起動する（Q1） |
| ローカル | Visual Studio の配置（開発者モード）または導入したパッケージを、AppUserModelID（`<パッケージ ファミリ名>!App`）で起動する（`Application.LaunchStoreApp`） |

テストの前後で、アプリのデータ（サインインの情報・診断用のログ）を消すかどうかは、CI では毎回新しい環境のため気にしない。ローカルでは、利用者のサインインの情報を残す（半自動のテストでサインインを何度も求めないため）。

## 5. CI のテスト（6a）
Azure を使わず、サインインもしない。
| テスト | 確かめること |
|---|---|
| 起動 | ウィンドウが開き、タイトルが「TTDeployTool」。処理されなかった例外が無い（診断用のログに `Critical` が無い） |
| 画面の切り替え | `NavigationView` の 7 つの項目を選ぶと、対応する画面だけが表示される |
| サインインしていない状態 | 「サインインしていません」の表示。サインインのボタンが押せ、サインアウトのボタンは押せない |
| 対象の入力の確かめ | 何も入れないと「サブスクリプションを選んでください。」。アプリ名に誤った値を入れると誤りの表示が変わる（`TargetError` の結び付き） |
| 設定の結び付き | DB の種別・永続化の ComboBox を選ぶと、ViewModel に戻る（選択の変更イベント。5b で `BindBack` から直した箇所） |
| Dry-run | 誤った設定（パスワードなし）ではエラーを表示して保存しない。正しい設定（SQL Server・永続化を選ぶ）では、OS のフォルダーの選択のダイアログで一時フォルダーを選び（共通のダイアログの ID: 入力欄 1152・選択 1）、`docker-compose.yml` と `.env` ができ、SQL Server のイメージの内容・永続化を使わない旨の警告・次の手順が表示される（Q2） |
| 診断用のログ | 「実行の記録」にログのフォルダーが表示され、`ttdeploytool-desktop-yyyyMMdd.log` に「起動しました」がある |
| 閉じる | 実行中のジョブが無ければ、確認なしで閉じる |

CI のジョブ: `ci.yml` に `desktop-ui`（windows-latest）を加える。`desktop` ジョブと同じ条件（コードの変更があるとき）で動かし、テストの結果（trx）と失敗したときのスクリーンショットを成果物に残す。GitHub のホストされた Windows ランナーで UI Automation が動くか（操作できるデスクトップのセッションがあるか）は公式の記載を見つけられなかったため、6a の最初に、起動とタイトルの確認だけのテストで確かめる（動かない場合は、Q1 と合わせて方式を見直す）。

## 6. ローカルの半自動のテスト（6b）
本物のサインイン（WAM）と Azure を使う。サインインだけ人が行う（`scripts/run-system-tests.ps1` と同じ考え方）。
| 段階 | 内容 |
|---|---|
| 準備 | `scripts/run-desktop-ui-tests.ps1` が、対象のサブスクリプションとリソースグループ（既定は `rg-ttui-<日時>`）を受け取り、実行の前に、作るもの・料金・後片付けを示して確認を求める |
| サインイン | サインインしていなければ、テストが「サインイン」を押し、利用者が WAM の画面でサインインするのを最大 5 分待つ |
| サブスクリプションの読み込み | 一覧に対象のサブスクリプションがあること、エラーの表示が無いこと。読めなかったテナントの注意は、内容を結果に書き出す（失敗にはしない）。「読み直す」を続けて押しても、一覧が重複しないこと |
| 状態の確認 | まだ何も無いリソースグループで「見つからない」と表示されること |
| デプロイと削除 | 計画 → 実行で、Timetracker の URL が表示されるまで。その後、状態の確認と、削除（アプリ名の入力）まで行う（Q3） |
| 結果 | テストの結果（trx）、スクリーンショット、診断用のログの写しを、`TestResults` に置く。ログはオーナーが確かめてから、REVIEW_ISSUES に要約を書く |

## 7. 実施の順序
| フェーズ | 内容 | 完了条件 |
|---|---|---|
| 6a | AutomationId、テストのプロジェクト、CI のテスト（5）、`desktop-ui` ジョブ | CI で全件成功。テストを壊した版（例: 選択の変更イベントを外す）で失敗することを確かめる |
| 6b | ローカルの半自動のテスト（6）とスクリプト、手順（`desktop-app-check.md` に追記） | オーナーの PC で成功（Azure での実行は、実行の前にオーナーの確認を取る） |

**実施結果（6a）**
- `MainWindow.xaml` の 66 の要素に AutomationId を付け、`tests/TTDeployTool.Controller.Desktop.UITests`（7 件）と CI の `desktop-ui` ジョブを追加した。GitHub のホストされた Windows ランナー（Windows Server 2025）で、UI Automation のパターンでの操作、フォルダーの選択のダイアログ（ID 1152・1）の操作、失敗したときのスクリーンショットができることを確かめた（テストは約 12 秒。MSIX のビルドと導入を含むジョブ全体は約 10 分）
- CI で直したこと（アプリ）: 文字列の一覧（`ItemsControl`）の各行が UI Automation の要素にならなかったため、各行を `TextBlock` で表示するテンプレート（`LineTemplate`）にした。エラーの `InfoBar` のメッセージが読めなかったため、`AutomationProperties.Name` にもメッセージを出した（スクリーン リーダーでも読めるようになる）。テキストボックスの値は、入力のたびに ViewModel に戻すようにした（`UpdateSourceTrigger=PropertyChanged`。既定はフォーカスが外れたとき）
- CI で直したこと（テスト）: 名前を持たない要素の `Name` を読むと例外になるため、空として読む。一覧の内容は、ウィンドウの中の文字列から確かめる
- 6b の準備: `AzureUiTests`（`TT_UITEST_SUBSCRIPTION_ID` と `TT_UITEST_RESOURCE_GROUP` が無ければスキップ。CI ではスキップされる）と `scripts/run-desktop-ui-tests.ps1`（実行の前に確認し、終わったらリソースグループを削除するかを聞く）を追加した。手順は [`desktop-app-check.md`](desktop-app-check.md) の 5.1。オーナーの PC での実行は未実施（Azure での実行の前にオーナーの確認を取る）
- テストを壊した版で失敗することの確認: DB 種別の選択を ViewModel に戻すイベントを外した版（一時的なブランチ）で、Dry-run の保存のテストだけが失敗した（7 件中 1 件。SQL Server のイメージの `docker-compose.yml` にならない）。CI の run 39349465420（成功）・39350045306（壊した版で失敗）

## 8. 決定事項
| # | 論点 | 選択肢 | 決定 |
|---|---|---|---|
| Q1 | CI でアプリを起動する方式 | (A) パッケージなし（`WindowsPackageType=None`）の自己完結の exe を起動する。簡単だが、MSIX の登録・パッケージの ID・アプリのデータの置き場所は確かめられない / (B) CI の中で一時的な自己署名の証明書を作り、MSIX に署名して導入し、AppUserModelID で起動する。Store に出すものに近い形で確かめられる。証明書はジョブの中だけで使い、保存しない | **(B)**（推奨のとおり） |
| Q2 | Dry-run の保存（OS のフォルダーの選択のダイアログ）を CI で操作するか | (A) ダイアログも UI Automation で操作し、保存したファイルまで確かめる（OS の版・言語でダイアログの作りが変わると壊れやすい） / (B) CI では、誤った設定でエラーが表示されることと、ダイアログが開いて取り消せることまで。保存はローカルの半自動で確かめる | **(A)**（オーナー判断。ダイアログの要素は、表示の言語によらない ID で探す） |
| Q3 | ローカルの半自動のテストで、Azure へのデプロイと削除まで行うか | (A) 計画まで（何も作らない。料金がかからない） / (B) 実行と削除まで（約 15〜20 分、料金がかかる。スクリプトが毎回、実行の前に確認する） | **(B)**（推奨のとおり） |

## 9. テストを壊さないための決まり
- 待ち合わせは固定の時間ではなく、条件（要素が出る・表示が変わる）で待つ。上限を決め、超えたらスクリーンショットを残して失敗にする
- テストごとにアプリを起動し直す（前のテストの状態を引き継がない）
- 文言ではなく AutomationId で要素を探す。AutomationId を変えるときは、テストも同じ変更で直す

## 10. 出典（2026-10-19 に確認）
- winapp CLI の UI Automation（パターンを使う命令は CI・ロックされた画面でも動く）: https://learn.microsoft.com/windows/apps/dev-tools/winapp-cli/ui-automation 、https://learn.microsoft.com/windows/apps/dev-tools/winapp-cli/guides/winui
- AutomationProperties.AutomationId: https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.automation.automationproperties.automationidproperty
- FlaUI: https://github.com/FlaUI/FlaUI 、https://www.nuget.org/packages/FlaUI.UIA3
- WinAppDriver の状況（2022 年以降更新されていない）: https://appium.io/docs/en/3.0/ecosystem/drivers 、https://learn.microsoft.com/answers/questions/1455246/is-the-tool-winappdriver-dead-or-not
- パッケージの署名に使う証明書の作成: https://learn.microsoft.com/windows/msix/package/create-certificate-package-signing 、https://learn.microsoft.com/windows/msix/package/sign-app-package-using-signtool
- WinUI 3 の CI: https://learn.microsoft.com/windows/apps/package-and-deploy/ci-for-winui3
