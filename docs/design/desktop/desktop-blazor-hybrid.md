# 試作の設計: デスクトップアプリを Blazor Hybrid で作り直す場合（No.79）

- 対象ブランチ: `feature/desktop-blazor-hybrid`（`feature/desktop-app` の 6123da3 から）。既存の WinUI 3 のアプリ（`src/TTDeployTool.Controller.Desktop`）とそのテスト・CI は変えず、試作は別のプロジェクトとして足す
- 関連: [`desktop-tables.md`](desktop-tables.md)（表の仕様と、WinUI.TableView の不具合・回避策。9・10 章）・[`desktop-ui-tests.md`](desktop-ui-tests.md)（UI テスト。6.2・6.3）・[`desktop-app.md`](desktop-app.md)（サインイン・通知・MSIX）・[`desktop-app-store.md`](desktop-app-store.md)
- 状況: 論点 Q1〜Q5 はオーナーの回答ですべて推奨どおりに決定（2026-10-30。12 章）。試作を実装し（13 章）、CI で確かめた（14 章）。比較と推奨は 10 章（移るかはオーナーの判断を待つ）
- 一次情報の確認日: 2026-10-30（出典は 11 章）
- 記録: `docs/REVIEW_ISSUES.md` の No.79

## 1. 目的と範囲
WinUI 3 + WinUI.TableView 1.5.1 の表は、部品の不具合・制限のために回避策を重ねてきた（2 章）。画面を Blazor Hybrid（.NET の Razor コンポーネントを、アプリの中の WebView2 に描く作り）で作り直した場合に、**安定性・UX・テストのしやすさが上がるか**を、1〜2 日の試作で判断する。

| 項目 | 試作でやること | やらないこと |
|---|---|---|
| 画面 | デプロイの一覧の 1 画面と、行のダブルクリックを確かめるためのジョブの一覧（今のアプリでダブルクリックを使うのはジョブの一覧だけ。ダブルクリックで実行状況の画面を開く代わりに、試作では詳細の区画を出す）。データは Fixtures のモック（`FixtureGateway`・`FixtureSamples`。サインインなし、本物の Azure に届かない） | ウィザード・削除・予定など、ほかの画面 |
| 判断の部分 | 今の `Desktop.Core`（ViewModel・`TableFilters`・`TableLayout`・`TableRows`/`TableRules`・`TextFilterInput`）をそのまま使う | Core の作り直し |
| Windows との連携 | WAM のウィンドウのハンドル・通知領域・Windows の通知・StartupTask・MSIX を、文書と小さなコード（CI の windows ランナーでのビルド）で確かめる（8 章） | 実機・本物の Azure でのサインイン、Store への提出、評価版（Desktop Preview）、Desktop System Test |
| テスト | 画面の部品の単体テスト（bUnit）と、ブラウザーでの操作のテスト（Playwright）。新しいコードは行・分岐（C1）とも 100% | 網羅率を測れない Windows のホストの部分は、今の `Desktop` と同じく対象外（小さく保つ） |
| 結論 | WinUI 3 のまま続ける場合と移る場合の比較（10 章）と推奨 | 本番のアプリの置き換え（判断の後に別に計画する） |

## 2. 今の課題と、Blazor Hybrid で消えるもの
`desktop-tables.md` の 9・10 章の回避策の多くは、表の部品（WinUI.TableView）の中の作りが原因である。Blazor Hybrid では、表は HTML の `<table>` になり、部品の中の見えない処理（後から実行される選択・スクロール・見出しの作り直し）が無くなる。

| # | 今の課題（desktop-tables.md） | 原因 | Blazor Hybrid では |
|---|---|---|---|
| 1 | テンプレートの列が初期値で `CanSort`/`CanFilter`=false（10.5） | 部品の初期値 | 消える。見出しのボタンは自分で描く |
| 2 | 右端の列の境目をつかめない・星の幅の計算がチェックボックスの列を無視（10.6） | 部品の幅の計算 | 消える。幅は CSS（`table-layout: fixed` と最後の列の `auto`）で決まる |
| 3 | `CellsHorizontalOffset` が一時的に NaN になり、GridLength の例外でアプリが落ちた（10.7） | 部品の配置の途中の値 | 消える。余白の列（`TableRules.SpacerWidth`）も要らなくなる |
| 4 | 読み取り専用の表で、セルのダブルクリックが処理済みになる | 部品のイベントの扱い | 消える。行の `@ondblclick` |
| 5 | `Columns.Move` で `HeaderControl` が外れ、幅の変更が届かない（10.8） | 部品の見出しの作り直し | 消える。列の順序は Core の `TableLayout` の並びで描き直すだけ |
| 6 | `ScrollRowIntoView` の範囲外の例外（10.3.1。`KnownUiErrors` で握りつぶしている） | 部品の後から実行する処理 | 消える（後から実行される処理を持たない） |
| 7 | 見出しが UI Automation の木に出ず、キーボードのフォーカスも当たらない（9.1） | 部品の作り | 良くなる見込み。見出しを `<button>` にすれば Tab で届き、`aria-sort` で読み上げられる（WebView2 は Edge と同じ UI Automation の木を出す。試作で確かめる） |
| 8 | 日本語の変換の途中で絞り込まれる（10.5 #7） | 入力欄とイベント | 同じく対処が要る。`compositionstart`/`compositionend` で待つ（Core の `TextFilterInput` をそのまま使う） |
| 9 | ▽ のメニューの中・見出しのドラッグ・6 行の表示が UI テストで届きにくい（10.7） | UI Automation の木に出ない | 良くなる見込み。DOM なので Playwright でセレクターから届き、ドラッグ・キーボード・変換中の入力（CDP の `Input.imeSetComposition`）も合成できる |

新しく生まれる課題は、WebView2 の分のメモリと起動時間、WebView2 Runtime の有無、Windows との連携をホスト（WPF など）に置くことによる作り直し、HTML と CSS で Windows らしい見た目に寄せる手間である（8・10 章）。

## 3. 論点 Q1: ホスト（BlazorWebView を置くアプリの枠）
Blazor Hybrid の BlazorWebView は、.NET MAUI・WPF・Windows Forms の 3 つに用意されている（出典 1）。WinUI 3 に直接置く公式の部品は無い（MAUI の Windows 版が WinUI 3 の上で動く）。

| 案 | 内容 | メリット | デメリット |
|---|---|---|---|
| **A（推奨）** | **WPF の BlazorWebView**（`Microsoft.AspNetCore.Components.WebView.Wpf` 10.0.120。`net10.0-windows10.0.19041.0`。.NET 10 から `WebView2CompositionControl` を使う、出典 2）。窓・通知領域・WAM のハンドルは WPF、画面は Razor | WPF は成熟しており、ホストの部分が小さい（窓 1 つ）。MAUI のワークロードが要らず、CI のビルドが `dotnet build` で済む見込み。WAM のハンドルは `WindowInteropHelper` で取れる（出典 6）。Windows App SDK は WPF からも使える（通知・単一インスタンス。出典 8） | MSIX は、公式には「Windows Application Packaging Project（.wapproj）」でまとめる（単一プロジェクトの MSIX は WinUI 向け、出典 9）。今のリリースのワークフロー（単一プロジェクトの MSIX・バンドル）を作り直す。通知領域は `H.NotifyIcon.Wpf` に替える |
| B | **.NET MAUI の BlazorWebView**（Windows だけを対象にする） | Windows 版は WinUI 3 と単一プロジェクトの MSIX の上で動くため、今の MSIX・Store の流れ、`AppNotificationService`・`StartupTaskService`・`H.NotifyIcon.WinUI` をほぼそのまま使える | MAUI の層（ワークロード・ハンドラー・窓の管理）が増え、その不具合も受ける。窓を通知領域にしまう・ハンドルを取るには WinUI のネイティブの窓まで降りる。CI に MAUI のワークロードの導入が要る。WinUI 3 の上に乗るため、WinUI の起動の問題（Windows App SDK の版の組み合わせ）は残る |
| C | Windows Forms の BlazorWebView | 最も軽い | 高 DPI・見た目の扱いが WPF より手間。WPF より得るものが無い |

推奨は A。理由は、試作の目的が「安定性」であり、画面の層を WinUI 3 から外したときの効果を最も単純な形で確かめられるため。MSIX の作り（.wapproj）は試作の CI で確かめる（8 章）。B は、A の MSIX で詰まった場合の代わりとして残す。

## 4. 論点 Q2: 表の部品
どの部品でも、並べ替え・絞り込み・ページ・列の配置・コピーの**決まりは Core が持つ**（今の WinUI でも、TableView の内蔵の並べ替え・絞り込みを止めて Core を呼んでいる）。部品の内蔵の機能を止めて Core を呼ぶ形は、2 章の #1・#5 と同じ「部品の作りとのずれ」をまた生む。

| 案 | 内容・ライセンス | メリット | デメリット |
|---|---|---|---|
| **A（推奨）** | **自作の HTML の表のコンポーネント**（`<table>`・`<thead>` の `<button>`・`<input type=checkbox>`。幅のつまみと見出しのドラッグは、小さな JavaScript のモジュール〔数十行〕か HTML のドラッグのイベント） | Core がすべての決まりを持つため、表は「Core の `Rows`・`TableLayout.Current`・`Filters` を描くだけ」になる。部品の中の見えない処理が無く、2 章の不具合の種類が生まれない。依存が増えない。DOM が単純で、bUnit・Playwright で確かめやすい | 幅のつまみ・ドラッグでの順序・▽ のポップアップ・キーボードの操作を自分で書く（試作の 1 日の大半）。見た目（Windows らしさ）を CSS で整える |
| B | **FluentUI Blazor の `FluentDataGrid`**（`Microsoft.FluentUI.AspNetCore.Components` 5.0.1、MIT） | Microsoft の Fluent の見た目。列の幅（`ResizableColumns`。WCAG 2.2 のための 1 回で幅を変える UI もある）・ドラッグでの順序（`ReorderableColumns`）・行のダブルクリック（`OnRowDoubleClick`）・ページ（`Pagination`）がある（出典 4） | 幅は「保存されない」と API 文書にあり、保存と復元は自分で結ぶ。並べ替えは部品の `GridSort`（3 回目で既定の順に戻す作りは、部品の動きを止めて Core に任せる）。5.0 は出て間もない大きな版 |
| C | **MudBlazor の `MudDataGrid`**（9.12.0、MIT） | 並べ替えの 3 つ目の状態（`AllowUnsorted`）・幅（`ColumnResizeMode`）・ドラッグでの順序（`DragDropColumnReordering`）・複数選択（`MultiSelection`）・絞り込み（`FilterMode`）がそろう（出典 4） | Material Design の見た目で、Windows のアプリとして浮く。絞り込みは部品の決まり（Core の ▽ の値と件数・日付の範囲と別物）で、止めて自作の ▽ を出す |
| D | **Radzen Blazor の `RadzenDataGrid`**（12.1.8、MIT） | 幅（`AllowColumnResize`）・順序（`AllowColumnReorder`）・列の配置の保存（`Settings`/`SettingsChanged`）・行のダブルクリック（`RowDoubleClick`）・複数選択がそろい、機能が最も多い（出典 4） | 機能が多い分、部品の決まり（絞り込み・並べ替え・設定の形）と Core の決まりの二重持ちになる。テーマが独自 |
| E | **QuickGrid**（`Microsoft.AspNetCore.Components.QuickGrid` 10.0.14、MIT） | ASP.NET Core に入っている。軽い | 列の幅の変更・ドラッグでの順序・複数選択が無く、Microsoft Learn に「ドラッグでの列の順序などを足す予定は無い」とある（出典 3）。今回の仕様を満たさない |

推奨は A。今回の仕様（Q1〜Q7 で決めた表）は、決まりがすでに Core にあり、部品に求めるのは描くことと入力を Core に渡すことだけのため。B〜D は機能が多いが、どれも「内蔵の決まりを止めて Core を呼ぶ」作りになり、WinUI.TableView と同じ種類のずれ（内蔵の処理が Core の前に一瞬動く、など）を持ち込む。見た目は、FluentUI の CSS の色と文字の決まり（デザイントークン）だけを借りる形も、実装の段階で選べる。

## 5. 論点 Q3: Core の ViewModel との結び方
Core の ViewModel は `ObservableObject`（`INotifyPropertyChanged`）・`ObservableCollection`・`AsyncCommand` で、WinUI の `x:Bind` から使っている。Blazor は、状態が変わったことを `StateHasChanged` で知らせて描き直す。

| 案 | 内容 | メリット | デメリット |
|---|---|---|---|
| **A（推奨）** | 薄い基底のコンポーネント（例: `ViewModelComponent<TViewModel>`）が、`PropertyChanged`・`CollectionChanged`・`TableFilters.Changed` を購読し、`InvokeAsync(StateHasChanged)` で描き直す。ボタンは `AsyncCommand.ExecuteAsync`・`CanExecute` をそのまま呼ぶ。購読は `Dispose` で外す | Core を変えずに使え、WinUI 3 の画面と同じ ViewModel を両方から使える（移行の途中も並べて動かせる）。基底のコンポーネントは小さく、bUnit で 100% にできる | 変わるたびに画面全体を描き直すと遅くなるため、表の行は `@key` を付け、コンポーネントを行・見出しに分ける |
| B | Blazor 向けに状態を作り直す（Fluxor などの状態の管理、または Razor の中に状態を持つ） | Blazor の作法に沿う | Core（単体テストで 100%）を作り直すことになり、移行の費用が大きい。試作の目的から外れる |
| C | MVVM のライブラリ（ReactiveUI.Blazor・CommunityToolkit.Mvvm など）を足す | 結び付けの書き方が短くなる | 依存が増える。A の基底のコンポーネントで足りる |

スレッド: WPF の BlazorWebView では、コンポーネントの描き直しは WPF の UI のスレッドで行う（`Dispatcher`）。Core の `JobQueue` の 1 秒ごとの更新など、別のスレッドからの変更は `InvokeAsync` で UI のスレッドに戻す（A の基底のコンポーネントが行う）。

## 6. 論点 Q4: テストの方法
Razor のコンポーネントは Windows に依存しない（`net10.0` の Razor のクラスライブラリにできる）。そのため、画面の部品のテストを **Linux でも**動かせる。

| 案 | 内容 | メリット | デメリット |
|---|---|---|---|
| **A（推奨）** | 3 段にする。① **bUnit**（2.13.3、MIT。xUnit v3 で使う）で、コンポーネントの描画とイベント（見出しを押す・チェック・入力・変換の開始と終了・ダブルクリック）を確かめる。`TTDeployTool.sln` に入れ、今の `build` ジョブ（と Linux）で動く。行・分岐 100% をここで取る。② **Playwright**（`Microsoft.Playwright`。MIT。リポジトリの System Test と同じ 1.56.0 を使う。最新は 1.64.1）で、同じコンポーネントをテスト用の小さな ASP.NET Core のホスト（Blazor Server。モックのデータ）に載せ、Chromium で実際のマウス・キーボード・ドラッグ・変換中の入力（CDP）・CSS の効き（6 行で切れる・右端の列の幅）を確かめる。Linux の CI で動く。③ Windows のランナーで、WPF のホストを `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=<port>` で起動し、Playwright の `ConnectOverCDPAsync` で WebView2 につなぐ少数のテスト（起動・表が出る・WAM のハンドルが取れる、出典 5・7） | 画面の判断の多くが bUnit で速く確かめられる（今は FlaUI の UI テストでしか確かめられない部分）。② は本物のブラウザーのエンジン（WebView2 と同じ Chromium 系）で、FlaUI より速く安定した操作ができる。③ で WebView2 の中でも同じに動くことを確かめる | テストの段が 3 つになる。② は Edge の WebView2 そのものではない（③ で補う）。WebView2 の CDP の接続は、Playwright の文書の例が Node.js で、.NET の手順は自分で書く |
| B | ① bUnit と、今と同じ FlaUI の UI テスト（WebView2 の中身は UI Automation の木に出る） | 今の UI テストの作りと道具を引き継げる | FlaUI からの DOM の操作は、UI Automation の木を通すため遅く、ドラッグ・変換中の入力の合成は今と同じく難しい |
| C | ① bUnit だけ | 最も簡単 | CSS（幅・6 行・ツールチップ）・ドラッグ・本物の入力を確かめられない |

## 7. 論点 Q5: 試作のプロジェクトの構成と CI
既存のプロジェクト・テスト・CI のジョブは変えず、次を足す（案。名前は実装で変えてよい）。

| プロジェクト | 中身 | 対象 | 入れる場所 |
|---|---|---|---|
| `src/TTDeployTool.Controller.Desktop.Blazor` | Razor のクラスライブラリ（`Microsoft.NET.Sdk.Razor`・`net10.0`）。デプロイの一覧の画面・表のコンポーネント・基底のコンポーネント・小さな JavaScript（幅のつまみ・クリップボードは .NET 側の `IClipboardService` を使う） | Linux・Windows | `TTDeployTool.sln` |
| `src/TTDeployTool.Controller.Desktop.Blazor.Wpf` | WPF のホスト（`net10.0-windows10.0.19041.0`・`UseWPF`）。窓・BlazorWebView・モックの組み立て（`Fixtures` を参照）・WAM のハンドルの取得の確認・通知領域の確認 | Windows | `TTDeployTool.Desktop.slnx`（試作のジョブでだけビルド） |
| `src/TTDeployTool.Controller.Desktop.Blazor.Package` | MSIX の確認用の .wapproj（8 章の確認。Q1 が A のとき） | Windows | 同上 |
| `tests/TTDeployTool.Controller.Desktop.Blazor.Tests` | bUnit のテスト（行・分岐 100%） | Linux・Windows | `TTDeployTool.sln` |
| `tests/TTDeployTool.Controller.Desktop.Blazor.BrowserTests` | Playwright のテスト（② はテスト用のホスト、③ は WPF のホストに CDP でつなぐ） | ② Linux、③ Windows | 試作のジョブ |

CI（`ci.yml`）には、試作用のジョブ `blazor-prototype`（ubuntu: ② の Playwright）と `blazor-prototype-windows`（windows: WPF のホストのビルド・.wapproj での MSIX の作成・③）を足す。既存の `build`・`desktop`・`desktop-ui` ジョブは変えない（① の bUnit は `TTDeployTool.sln` に入るため、`build` ジョブで走る）。試作をやめるときは、ジョブとプロジェクトを消すだけで元に戻る。

## 8. Windows との連携の実現性
文書での見立てと、試作で確かめる方法。実機と本物の Azure は使わない。

| 項目 | 今の実装（`src/TTDeployTool.Controller.Desktop/Services`） | WPF のホスト（Q1 の A）での見立て | 試作での確かめ方 |
|---|---|---|---|
| WAM のサインイン | `InteractiveBrowserCredentialBrokerOptions(hwnd)`（Azure.Identity.Broker 1.7.0）に WinUI の窓のハンドル | MSAL の文書は、WAM には親の窓のハンドルが必須で、WPF のハンドルの取り方（`WindowInteropHelper(window).Handle`）を案内している（出典 6）。`WamSignInService` はハンドルを受け取るだけなので、そのまま使える | ホストで `WindowInteropHelper` のハンドルを取り、`InteractiveBrowserCredentialBrokerOptions` を作るところまで（トークンは取らない）を CI のビルドと起動で確かめる |
| Windows の通知 | `AppNotificationManager`（Windows App SDK） | Windows App SDK は WPF からも使える（出典 8）。パッケージの登録（マニフェストの `toastNotificationActivation`・`comServer`）は今と同じ（出典 10） | .wapproj のマニフェストに同じ登録を入れ、ビルドが通ることを確かめる（通知を出すのは実機） |
| サインイン時の起動 | `Windows.ApplicationModel.StartupTask`（パッケージの ID が要る） | MSIX にすれば同じ API が使える（`net10.0-windows10.0.19041.0` で WinRT の API を呼べる） | .wapproj のマニフェストに `StartupTask` を入れ、ビルドで確かめる |
| 通知領域 | `H.NotifyIcon.WinUI` 2.4.1 | 同じ作者の `H.NotifyIcon.Wpf` 2.4.1（MIT） | ホストに置き、ビルドで確かめる |
| 1 つだけ起動 | `AppInstance`（Windows App SDK）。`Program.cs` | WPF からも使える（Windows App SDK）。または名前付きの Mutex | 試作では扱わない（移行の計画で決める） |
| パスワードの暗号化 | DPAPI（`ProtectedData`） | UI に依らないため、そのまま使える | 確かめる必要なし |
| MSIX・Store | 単一プロジェクトの MSIX（`EnableMsixTooling`）。x64・arm64 のバンドル | 単一プロジェクトの MSIX は、公式には WinUI 向け（出典 9）。WPF は .wapproj でまとめる。CI では `msbuild` で作る（今の `desktop` ジョブと同じ道具） | 試作のジョブで、.wapproj から未署名の MSIX（x64）を作れることを確かめる。バンドルと Store への提出は試作では行わない |
| WebView2 Runtime | 要らない | 要る。Windows 11 には OS に含まれ、Windows 10 もほとんどの PC に入っている。入っていない PC への対応（案内か、App Installer の依存の指定）が要る（出典 7） | 起動時に Runtime が無いときの案内を、ホストに置く（`CoreWebView2Environment.GetAvailableBrowserVersionString`）。CI の windows ランナーには入っている |
| メモリと起動時間 | WinUI 3 のみ | WebView2 のプロセス（ブラウザー・描画・GPU など）の分が増える | CI の windows ランナーで、起動から表が出るまでの時間と、アプリと `msedgewebview2.exe` のプロセスの合計のメモリ（Working Set）を、今の WinUI 3 のアプリ（モックのビルド）と同じ手順で測り、10 章に書く（ランナーの値は目安） |

## 9. 確かめる UX と、HTML での作り
仕様は `desktop-tables.md`（Q1〜Q7、10 章）のとおり。どれも決まりは Core にあり、画面は描くことと入力を渡すことだけを行う。

| UX | 作り（Blazor） | Core | 確かめるテスト |
|---|---|---|---|
| 見出しでの並べ替え（3 回で既定の順・並べ替えない列） | 見出しの `<button>`（`aria-sort`）。並べ替えない列（「ジョブ」）はボタンにしない | `SortBy`・`ClearSort`・`CanSortBy` | bUnit・Playwright |
| 見出しの絞り込み（▽） | 見出しの右の ▽ の `<button>` から、値の一覧（チェック・検索・件数）のポップアップ | `TableFilters.ValuesOf`・`SetValues`・`ClearValues` | bUnit・Playwright |
| 含む文字（変換の途中で絞り込まない） | `compositionstart`/`compositionend`（Blazor の独自のイベント）と `input` を `TextFilterInput` に渡し、0.4 秒のタイマーは `System.Threading.Timer`・`TimeProvider` | `TextFilterInput` | bUnit（イベント）・Playwright（CDP の `Input.imeSetComposition` で変換中を合成） |
| 20 件ずつのページ | 表の下のページの切り替え・件数の選択 | `PageSize`・`PreviousPageCommand`・`NextPageCommand`・`PageText` | bUnit |
| 列の幅と順序の変更・保存・初期値に戻す | 見出しの右端のつまみ（ポインターのイベント。JavaScript）・見出しのドラッグ（HTML のドラッグのイベント）。`<colgroup>` の幅は `TableLayout.Current` から描く。チェックボックスの列は左端に固定 | `TableLayout.Current`・`Save`・`Reset` | bUnit（並び・幅の描画）・Playwright（ドラッグ） |
| 右端の列が残りの幅いっぱい | `table-layout: fixed`、最後の列だけ幅を指定せず `min-width`。表がウィンドウより広いときは表の区画だけ横にスクロール | — | Playwright（ウィンドウの幅を変えて測る） |
| 6 行で切れる表示とツールチップ | `-webkit-line-clamp: 6`。切れたとき（`scrollHeight > clientHeight`）だけ `title` を付ける（小さな JavaScript） | `TableRules` のツールチップの決まり | Playwright |
| セルのコピー・選んだ行の TSV・Markdown | 右クリックのメニュー（自作）と Ctrl+C（Shift で見出しつき）。クリップボードは .NET 側の `IClipboardService` | `TableRows.CopyText`・`CopyMarkdown`・`TableRules.Target` | bUnit（モックの `IClipboardService`） |
| 左上のチェックボックス | 見出しの左端の `<input type=checkbox>`（今のページの行を選ぶ・外す。一部を選んでいるときは `indeterminate`） | `SetSelection`・`TableSelection` | bUnit・Playwright |
| 行のダブルクリック（ジョブの一覧） | 行の `@ondblclick`（今の WinUI では、そのジョブの実行状況の画面を開く。試作では詳細の区画を出す） | `JobListViewModel` | bUnit・Playwright |
| キーボード（2 章 #7） | 見出しのボタン・▽・チェックボックスは Tab で届く。幅は見出しのボタンにフォーカスがあるときの Alt+←/→ など（試作で決める） | — | Playwright |

## 10. 比較（試作の結果から。2026-10-30）
数字は CI の windows ランナー（run 41109148255）の値で、実機とは違う（目安）。WinUI 3 は今のアプリの UI テストのモックのビルド（最初の画面〔サインイン〕まで）、Blazor Hybrid は試作（モックでサインインし、デプロイの一覧を 20 行出した後）。

| 観点 | WinUI 3 のまま続ける | Blazor Hybrid（WPF のホスト）に移る | 評価 |
|---|---|---|---|
| 安定性 | WinUI.TableView の中の不具合を回避策で抑えている（2 章の 6 件。うち 1 件は例外を握りつぶして続ける）。部品の版を上げるたびに確かめ直す | 表の中の見えない処理が無く、2 章の不具合の種類が生まれない。試作では、画面の側の回避策は 0 件。WebView2 Runtime の自動更新による変化は受ける | **移る方が良い** |
| UX | 表計算に近い操作は TableView の範囲。見出しにキーボードで届かない。▽ の中・右クリックの操作は FlaUI でも届きにくい | 9 章の UX をすべて作れた。見出しに Tab で届き、Enter で並べ替え、Alt+←/→ で幅、Alt+Shift+←/→ で順序を変えられる（今の画面より良い）。見た目は CSS で Windows 11 の色と文字に寄せたが、WinUI の部品（日付の選択・ダイアログなど）と同じにはならない | 表は**移る方が良い**。ほかの画面の見た目は、作ってから判断 |
| テストのしやすさ | 画面の側は単体テストが届かず、FlaUI の UI テスト（Windows・MSIX の導入が要る・遅い・ドラッグや変換中の入力の合成が難しい） | 画面の部品の単体テスト（bUnit 31 件、約 3 秒、Linux）で行・分岐とも 100%。ブラウザーでの本物の入力のテスト（Playwright 11 件、約 40 秒、Linux）で、ドラッグ・変換中の入力・6 行の表示・ツールチップ・右クリックまで確かめた。WebView2 の中のテスト（1 件、約 10 秒）も CDP でつながった | **移る方が良い** |
| 起動 | 窓が出るまで 2.5 秒 | 窓が出るまで 0.7 秒（パッケージにしない）・0.8 秒（MSIX）。起動から表が出るまで 3.6 秒（WebView2 に CDP でつなぐ時間を含む） | 同等（窓は早いが、画面が出るまでは WebView2 の起動を待つ） |
| メモリ | 1 プロセス。Working Set 156 MB・プライベート 49 MB | 7 プロセス（アプリと msedgewebview2.exe 6 つ）。Working Set 446〜465 MB・プライベート 145〜153 MB（Working Set には Edge と共有する部分を含む） | **WinUI 3 の方が少ない**（プライベートで約 100 MB 多い） |
| Windows との連携 | 実装済み | WAM（窓のハンドル）・通知領域・DPAPI・サインイン時の起動（MSIX）は、今の実装のファイルを変えずに使えた。通知（`AppNotificationManager.IsSupported`）は、パッケージにしない・MSIX のどちらも false で、まだ確かめ切れていない（14 章） | 通知だけ要確認 |
| 配布 | 単一プロジェクトの MSIX・x64 と arm64 のバンドル・Store への提出の流れができている | .wapproj で MSIX にまとめ、導入して動いた（x64 だけ）。arm64・バンドル・Store への提出は作り直しと確認が要る。WebView2 Runtime（Windows 11 は OS に含まれる）が要る | WinUI 3 の方が手間が無い |
| 移行の費用（見込み） | なし | 画面の書き直し（`MainWindow.xaml` 715 行・ウィザードなど 9 画面。表の部品は試作のものを使う）3〜5 日、ホスト・MSIX・リリースのワークフロー 2〜3 日、UI テストを Playwright に置き換え 2〜3 日。Core（ViewModel と単体テスト）はそのまま | 約 2 週間 |
| リスク | TableView の新しい不具合。Microsoft の TableView は実験版（desktop-tables.md の 9.2） | メモリ、通知、arm64 と Store、WAM の窓が前に出るか（実機）、WebView2 の無い Windows 10 の PC | — |

### 10.1 推奨
**Blazor Hybrid（WPF のホスト）に移る**ことを勧める。理由は、今いちばん手間がかかっている表の不具合と、画面の側を自動で確かめられないことの 2 つが、どちらも作りから無くなるため（試作で、回避策 0 件・画面の部品の網羅率 100%・本物の入力のテストが Linux で動く、を確かめた）。メモリが約 100 MB 増えることは、常駐するアプリとしては受け入れられる範囲と考える（オーナーの判断）。

移る前に確かめること（どれも移ると決めた後の最初の作業とする）:
1. 通知: WPF のホストで `AppNotificationManager` が使えない理由を調べ、使えなければ WinRT の `ToastNotificationManager` に替える（MSIX ならパッケージの ID で使える）
2. 実機: WAM のサインインの窓がアプリの前に出ること・日本語の変換中の入力・見た目（`desktop-app-check.md` と同じ確認）
3. 配布: .wapproj での arm64 とバンドル、Store への提出の形（`desktop-app-store.md` の流れの作り直し）

WinUI 3 のまま続ける場合は、Microsoft の TableView の安定版が出たら乗り換えを検討する（desktop-tables.md の 9.2）。

## 11. 出典（2026-10-30 に確認）
1. Microsoft Learn「ASP.NET Core Blazor Hybrid」（.NET 10 版。BlazorWebView は .NET MAUI・WPF・Windows Forms。コンポーネントはアプリの中で動き、WebAssembly は使わない）https://learn.microsoft.com/aspnet/core/blazor/hybrid/
2. Microsoft Learn「Build a Windows Presentation Foundation (WPF) Blazor app」（`Microsoft.AspNetCore.Components.WebView.Wpf`、SDK は `Microsoft.NET.Sdk.Razor`、.NET 10 では `WebView2CompositionControl` に替わったため TFM に Windows 10 の版を付ける）https://learn.microsoft.com/aspnet/core/blazor/hybrid/tutorials/wpf
3. Microsoft Learn「ASP.NET Core Blazor QuickGrid component」（ドラッグでの列の順序などを足す予定は無い）https://learn.microsoft.com/aspnet/core/blazor/components/quickgrid
4. 各部品の NuGet のパッケージ（版・公開日・ライセンス）と、パッケージに入っている API 文書（XML ドキュメント）: `Microsoft.FluentUI.AspNetCore.Components` 5.0.1（2026-10-14、MIT）、`MudBlazor` 9.12.0（2026-10-29、MIT）、`Radzen.Blazor` 12.1.8（2026-10-28、MIT）、`Microsoft.AspNetCore.Components.QuickGrid` 10.0.14（2026-10-25、MIT）、`bunit` 2.13.3（2026-10-25、MIT）、`Microsoft.Playwright` 1.64.1（2026-10-22、MIT）、`H.NotifyIcon.Wpf` 2.4.1（MIT）。https://www.nuget.org/
5. Playwright「WebView2」（`--remote-debugging-port` と `connectOverCDP`。並べて動かすときは `WEBVIEW2_USER_DATA_FOLDER` を分ける）https://playwright.dev/docs/webview2
6. Microsoft Learn「Using MSAL.NET with Web Account Manager (WAM)」（親の窓のハンドルが必須。WPF などは「Retrieve a window handle (HWND)」を見る）https://learn.microsoft.com/entra/msal/dotnet/acquiring-tokens/desktop-mobile/wam
7. Microsoft Learn「Distribute your app and the WebView2 Runtime」（Windows 11 には Evergreen Runtime が含まれる。Windows 10 もほとんどに入っている。App Installer では依存として指定できる）https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution ／「Automate and test WebView2 apps with Microsoft Edge WebDriver」（`--remote-debugging-port` での接続）https://learn.microsoft.com/microsoft-edge/webview2/how-to/webdriver
8. Microsoft Learn「Use the Windows App SDK in an existing project」（WPF・WinForms・Win32 から使える）https://learn.microsoft.com/windows/apps/windows-app-sdk/use-windows-app-sdk-in-existing-project
9. Microsoft Learn「Package your app using single-project MSIX」（対象は WinUI のテンプレート。WinUI でない desktop アプリは Windows Application Packaging Project）https://learn.microsoft.com/windows/apps/windows-app-sdk/single-project-msix
10. Microsoft Learn「Quickstart: Send and Handle App Notifications」（マニフェストの `toastNotificationActivation`・`comServer`）https://learn.microsoft.com/windows/apps/develop/notifications/app-notifications/app-notifications-quickstart
11. Microsoft Learn「Use browser developer tools with ASP.NET Core Blazor Hybrid」（`AddBlazorWebViewDeveloperTools`、Ctrl+Shift+I）https://learn.microsoft.com/aspnet/core/blazor/hybrid/developer-tools

## 12. 決定事項（オーナーの回答、2026-10-30）
| No. | 論点 | 決定 |
|---|---|---|
| Q1 | ホスト（3 章） | **A: WPF の BlazorWebView**（推奨どおり） |
| Q2 | 表の部品（4 章） | **A: 自作の HTML の表**（推奨どおり） |
| Q3 | Core の ViewModel との結び方（5 章） | **A: 薄い基底のコンポーネント**（推奨どおり） |
| Q4 | テストの方法（6 章） | **A: bUnit・Playwright〔テスト用のホスト〕・WebView2 への CDP の接続**（推奨どおり） |
| Q5 | 試作のプロジェクトの構成と CI（7 章） | **7 章の案**（推奨どおり） |
| Q6 | WinUI 3 のまま続けるか、Blazor Hybrid に移るか（10 章） | 未定（推奨: Blazor Hybrid〔WPF のホスト〕に移る。10.1） |

## 13. 試作の作り（2026-10-30）
| プロジェクト | 中身 |
|---|---|
| `src/TTDeployTool.Controller.Desktop.Blazor`（`net10.0`。`TTDeployTool.sln`） | `Watcher`・`ViewModelComponent`（Q3）。表: `DataTable<TRow, TItem>`（見出しのボタン・▽・つまみ・ドラッグ・チェックボックス・右クリックのメニュー・Ctrl+C）・`FilterPopup`（▽ の値と件数・探す・並べ替え）・`FilterBar`・`TextFilterBox`・`Pager`。決まり: `HtmlTableRules`（3 回で既定の順・列の移動と幅・右端の列の幅・左上のチェックボックスの状態）。画面: `DeploymentsPage`・`JobsPage`・`PrototypeApp`。`PrototypeSession`（Fixtures のモックで `MainViewModel` を組み立て、サインインして一覧を読む）。JavaScript は `wwwroot/table.js`（約 70 行: つまみのドラッグ・Alt+矢印・6 行で切れたときのツールチップ・変換の始まりと終わり・入力欄への書き戻し）、見た目は `wwwroot/app.css`（Windows 11 の色と文字に寄せる。ダークモード対応） |
| `src/TTDeployTool.Controller.Desktop.Blazor.Wpf`（Windows） | 窓・BlazorWebView・通知領域（`H.NotifyIcon.Wpf`）・WPF のクリップボード。WinUI 3 のアプリの `WamSignInService`・`AppNotificationService`・`StartupTaskService`・`DpapiSecretProtector` を**ファイルを変えずにリンクして**ビルドする。`--check <ファイル>`（MSIX ではアプリのデータの `check-request`）で、Windows との連携を確かめて JSON に書く（`IntegrationCheck`。トークンは取らない） |
| `src/TTDeployTool.Controller.Desktop.Blazor.Package` | .wapproj と、試作用のマニフェスト（名前 `TTDeployTool.BlazorPrototype`。通知の起動とサインイン時の起動は本番と同じ登録） |
| `tests/TTDeployTool.Controller.Desktop.Blazor.Tests`（`TTDeployTool.sln`） | bUnit（2.13.3）のテスト 31 件。対象のプロジェクトの行・分岐とも 100%（coverlet。CI の `blazor-prototype` ジョブで、100% でなければ失敗にする） |
| `tests/TTDeployTool.Controller.Desktop.Blazor.TestHost` | テスト用のホスト（Blazor Server。プリレンダーしない。コピーした文字を `/test/clipboard` で返す） |
| `tests/TTDeployTool.Controller.Desktop.Blazor.BrowserTests` | ② `BrowserHostTests` 11 件（Linux の Chromium）・③ `WebView2Tests` 1 件（Windows。WPF のホストに CDP でつなぐ） |
| CI（`ci.yml`） | `blazor-prototype`（ubuntu: 網羅率・②）と `blazor-prototype-windows`（windows: WPF のビルド・連携の確認〔パッケージにしない・MSIX〕・③・.wapproj の MSIX の作成と導入・起動時間とメモリ〔`.github/scripts/measure-desktop-app.ps1`。WinUI 3 のアプリのモックのビルドと同じ手順〕）。既存のジョブは変えない |

### 13.1 作って分かったこと
| 項目 | 内容 |
|---|---|
| 日本語の変換中の入力 | 入力欄を Blazor の値の結び付け（`value="@text"` と `@oninput`）にすると、打つたびに描き直しで入力欄に値を入れ直すため、変換が切れるおそれがある。試作では値を結び付けず（最初の値だけ）、Core の文字が外から変わったとき（「絞り込みをすべて外す」）だけ JavaScript で入れる。変換の始まりと終わりは `compositionstart`/`compositionend` を JavaScript で受け、Core の `TextFilterInput` に渡す（WinUI の画面と同じ決まり）。Playwright で、CDP の `Input.imeSetComposition`（変換中）・`Input.insertText`（確定）で確かめた（変換中は 1.2 秒待っても絞り込まず、文字とフォーカスはそのまま。確定の 0.4 秒後に絞り込み、続けて打てる） |
| Alt+← | WebView（Chromium）では「戻る」に当たるため、見出しの Alt+矢印は JavaScript で止める（`preventDefault`） |
| つまみ | 見出しの右端からはみ出すと、隣の見出し（`position: sticky` の重なり）の下に隠れてつかめなかった（Playwright で分かった）。見出しの内側に置いた |
| 右端の列 | 幅を指定しない列に残りを配る HTML の表の決まり（`table-layout: fixed`）で、「右端の列が残りの幅いっぱい・最小の幅は初期の幅」が CSS だけでできる。右端の列を動かしたら、新しい右端の列が伸び、動かした列は最小の幅になる（WinUI の余白の列・`CellsHorizontalOffset` の扱いが要らない） |
| 見出し | すべてボタン（並べ替えない列は Tab で届く文字）で、`aria-sort` を持つ。Tab で見出しと ▽ に届き、Enter で並べ替え・▽ を開ける（WinUI.TableView では届かなかった） |
| JavaScript からの呼び出し | つまみ・Alt+矢印は `[JSInvokable]` の .NET のメソッドを呼ぶだけで、判断は .NET 側（bUnit で確かめる）。JavaScript の行は網羅率の対象にしていない（Playwright で操作として確かめる） |
| 決まりの場所 | 並べ替え・絞り込み・ページ・列の配置の保存・コピーの文字は、すべて今の Core のまま使えた（Core は 1 行も変えていない）。足した決まりは `HtmlTableRules`（3 回で既定の順・列の移動など）だけ |

## 14. 試作の結果（CI、2026-10-30）
| 確かめたこと | 結果 | 出典 |
|---|---|---|
| 画面の部品の単体テスト（bUnit） | ✔ 31 件。`TTDeployTool.Controller.Desktop.Blazor` の行・分岐とも 100%（Debug・Release。CI の `blazor-prototype` で 100% 未満なら失敗） | run 41104713974 以降 |
| ブラウザーでの本物の入力（Playwright、Linux） | ✔ 11 件: 見出しの 3 回の並べ替えと並べ替えない列・▽（クリック・探す）・含む文字（変換中は絞り込まない・確定の後に絞り込みフォーカスはそのまま・外すと入力欄も空）・20 件ずつのページ・つまみのドラッグでの幅と見出しのドラッグでの順序と起動し直し・初期値に戻す・キーボード（Tab・Enter・Alt+矢印・Esc）・右端の列が残りの幅いっぱいと狭い窓での横のスクロール・6 行で切れる表示とツールチップ・セル・TSV・Markdown のコピー・左上のチェックボックス（一部の印）・行のダブルクリック | 同上 |
| WebView2 の中（Playwright を CDP でつなぐ、Windows） | ✔ 1 件: 表・並べ替え・含む文字（フォーカス）・キーボードでの幅・Ctrl+C（WPF のクリップボード）。CDP のポートは `BlazorWebViewInitializing` で渡す（環境変数 `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS` では開かなかった） | run 41107829426 |
| Windows との連携（パッケージにしない／MSIX） | 窓のハンドル ✔／✔、WAM のサインインの実装（`WamSignInService`・`InteractiveBrowserCredentialBrokerOptions(hwnd)`）✔／✔、WebView2 Runtime 155.0.4283.62、通知領域 ✔／✔、DPAPI ✔／✔、サインイン時の起動 Unavailable（パッケージが要る）／**Disabled（使える）**、通知 `IsSupported` false／**false（要確認）**、表 20 行／20 行 | run 41109148255 |
| MSIX（.wapproj） | ✔ msbuild で作成・署名・導入・起動できた（x64）。ビルドで `wwwroot` を出力に置くこと、参照のライブラリを win-x64 でも復元することが要った | run 41109148255・41110302673 |
| 起動時間とメモリ | 10 章の表 | run 41109148255 |

### 14.1 試作で分かった Windows の部分の注意
| 項目 | 内容 |
|---|---|
| Windows App SDK と WebView2 | WPF のプロジェクトに Windows App SDK を足すと、WebView2 のパッケージが WinUI 用の参照（CsWinRT の projection）だけになり、BlazorWebView が起動時に `Microsoft.Web.WebView2.Wpf` を読めず落ちた。`WebView2EnableCsWinRTProjection=false` で直る（`Microsoft.Web.WebView2` の build/Common.targets） |
| CDP のポート | 試作のテストだけ、環境変数 `TT_BLAZOR_CDP_PORT` で `BlazorWebViewInitializing` に `--remote-debugging-port` を渡す（本番のビルドには入れない作りにする） |
| 通知 | `AppNotificationManager.IsSupported()` が MSIX でも false だった。Register を呼ぶ前の値か、WPF＋自己完結の Windows App SDK の制限かは未確認（10.1 の 1） |
