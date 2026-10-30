# Blazor Hybrid の試作の実機での確認（Windows の通知など）

- 目的: 移行で機能が落ちないか。CI のランナー（Windows Server 2025）には通知を画面に出す仕組みが無く、通知を出して押す流れを確かめられなかった（[`desktop-blazor-hybrid.md`](desktop-blazor-hybrid.md) の 15.1）。オーナーの PC で、試作（WPF の BlazorWebView のホスト）の通知が出るか・押すとウィンドウが出るかを確かめる
- 対象: CI（`ci.yml` の `blazor-prototype-windows` ジョブ）の成果物 `ttdeploytool-blazor-prototype-msix`（7 日で消える）。評価版（Desktop Preview）とは別のアプリ（`TTDeployTool.BlazorPrototype`）で、今の TTDeployTool と並べて入る
- 試作のデータは Fixtures のモックだけで、サインインせず、Azure には届かない
- 記録: `docs/REVIEW_ISSUES.md` の No.79

## 1. 受け取るもの
CI の実行の **Summary** の下の **Artifacts** から `ttdeploytool-blazor-prototype-msix` を取り出し、展開する。

| ファイル | 内容 |
|---|---|
| `TTDeployTool-BlazorPrototype-x64.msix` | 試作のアプリ（x64 だけ。arm64 の PC では確かめられない） |
| `TTDeployTool-BlazorPrototype.cer` | その実行だけで作った署名の証明書（公開鍵だけ。秘密鍵は成果物に入れていない） |
| `CHECK.md` | この手順書 |

## 2. 入れる
1. スタート メニューで「PowerShell」を右クリックし、「**管理者として実行**」で開く。
2. 証明書を「ローカル コンピューター」の**信頼されたユーザー**に入れる（評価版の手順〔`docs/DESKTOP_PREVIEW_INSTALL.md`〕と同じ場所。パスは置き換える）。

   ```powershell
   Import-Certificate -FilePath "C:\Users\<名前>\Downloads\ttdeploytool-blazor-prototype-msix\TTDeployTool-BlazorPrototype.cer" -CertStoreLocation Cert:\LocalMachine\TrustedPeople
   ```
3. 管理者でない PowerShell で、アプリを入れる。

   ```powershell
   Add-AppxPackage -Path "C:\Users\<名前>\Downloads\ttdeploytool-blazor-prototype-msix\TTDeployTool-BlazorPrototype-x64.msix"
   ```
4. スタート メニューの「**TTDeployTool（Blazor Hybrid の試作）**」で起動する。モックのデプロイの一覧（45 件、20 行ずつ）が出る。

## 3. 確かめること
通知領域（タスク バーの右下）の試作のアイコンを右クリックすると、確認のためのメニューが出る。各行の結果（✔ / ✘ と、出た文字）を記録して知らせる。

| # | 操作 | 確かめること |
|---|---|---|
| 1 | メニューの「状態を見る」 | 「通知の許可（今のアプリと同じ: Windows App SDK）」と「通知の許可（OS の通知の API）」の値（Enabled なら許可） |
| 2 | 「通知を試す（今のアプリと同じ: Windows App SDK）」 | Windows の通知（「試作の通知（Windows App SDK）」）が出る |
| 3 | ウィンドウを閉じ（× を押す。通知領域に残る）、2 の通知を押す（消えていたら、通知センター〔Win+N〕の中で押す） | 試作のウィンドウが出て、「通知から開きました（Windows App SDK）。job=job-manual&page=status」が出る |
| 4 | 「通知を試す（代わり: OS の通知の API）」→ ウィンドウを閉じて、その通知を押す | 通知が出て、押すと「通知から開きました（OS の通知の API）」が出る（4 は、2・3 が駄目だったときの代わりの作りを確かめるもの） |
| 5 | 試作を終了し（メニューの「終了」）、2 と同じ通知を出してから終了して、通知センターで押す（任意） | アプリが動いていないときに押すと、アプリが起動してウィンドウが出る（今のアプリの「終わった後に押された場合は普通に起動する」と同じ） |
| 6 | 「サインイン時の起動を切り替える」で有効にし、Windows からサインアウトしてサインインし直し、「状態を見る」（任意） | サインインの後に試作が起動し、起動の種類が StartupTask。確かめたら、もう一度「サインイン時の起動を切り替える」で無効に戻す |
| 7 | 試作を起動したまま、スタート メニューからもう一度起動する | 2 つ目は起動せず、今の試作のウィンドウが前に出る（CI でも確かめ済み） |
| 8 | 表の「含む文字」に日本語を入れる（変換の途中で止める・確定する）（任意） | 変換の途中で絞り込まれず、文字が消えない。確定の 0.4 秒後に絞り込まれ、続けて打てる |

## 4. 片付け
1. 試作を終了する（通知領域のメニューの「終了」）。
2. 試作を外す（管理者でない PowerShell）。

   ```powershell
   Get-AppxPackage -Name TTDeployTool.BlazorPrototype | Remove-AppxPackage
   ```
3. 証明書を外す（管理者の PowerShell。発行先は評価版と同じ `CN=TTDeployTool` のため、拇印で選ぶ）。

   ```powershell
   $cer = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 "C:\Users\<名前>\Downloads\ttdeploytool-blazor-prototype-msix\TTDeployTool-BlazorPrototype.cer"
   Remove-Item "Cert:\LocalMachine\TrustedPeople\$($cer.Thumbprint)"
   ```
4. 試作のデータのフォルダー（`%LOCALAPPDATA%\Packages\TTDeployTool.BlazorPrototype_<発行元のハッシュ>`）は、2 でアプリと一緒に消える。

## 5. 結果の見方
| 結果 | 意味と次の作業 |
|---|---|
| 2・3 が ✔ | 今の通知の実装（Windows App SDK）は、WPF のホストでもそのまま使える。CI での `Unsupported` は、ランナーに通知の仕組みが無いため |
| 2・3 が ✘、4 が ✔ | WPF のホストでは Windows App SDK の通知が使えない。移るときは、通知を OS の通知の API で作り直す（押したときの起動の受け口を自分で書く） |
| 2・3・4 がどれも ✘ | Windows の設定（システム → 通知）で、試作の通知が止められていないかを見る。止められていなければ、移る前に原因を調べる |
