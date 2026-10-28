# TTDeployTool デスクトップアプリ（評価版）の導入の手順

Microsoft Store を通さずに配布する評価版です。開発者が自分で作った（自己署名の）証明書で署名しているため、**最初に証明書を信頼してから**アプリを導入します。

## 受け取るもの

| ファイル | 内容 |
|---|---|
| `TTDeployTool-preview.cer` | 署名の証明書（公開鍵だけ。秘密鍵は入っていません） |
| `x64\TTDeployTool-<版>-x64.msix` | 一般的な Intel / AMD の PC 用 |
| `arm64\TTDeployTool-<版>-arm64.msix` | Arm の PC（Snapdragon など）用 |
| `x64\Dependencies\`・`arm64\Dependencies\` | 必要な部品（あるときだけ） |

PC の種類は、**設定 → システム → バージョン情報**の「システムの種類」で分かります（「x64 ベース プロセッサ」なら x64、「ARM ベース プロセッサ」なら arm64）。

## 必要なもの

- Windows 10 バージョン 1809 以降、または Windows 11
- 証明書を信頼するための、PC の管理者の権限（初回と、証明書が変わったときだけ）
- アプリでデプロイするには、Azure のサブスクリプションと、そこにリソースを作る権限

## 1. 証明書を信頼する（初回だけ）

1. スタート メニューで「PowerShell」を右クリックし、「**管理者として実行**」で開きます。
2. 受け取ったフォルダーで、次を実行します（パスは置き換えてください）。

   ```powershell
   Import-Certificate -FilePath "C:\Users\<名前>\Downloads\TTDeployTool-preview\TTDeployTool-preview.cer" -CertStoreLocation Cert:\LocalMachine\TrustedPeople
   ```

   `Thumbprint` と `Subject`（`CN=TTDeployTool`）が表示されれば完了です。

画面で行う場合は、`.cer` をダブルクリック →「証明書のインストール」→ 保存場所に「**ローカル コンピューター**」→「証明書をすべて次のストアに配置する」で「**信頼されたユーザー**（Trusted People）」を選びます。

> 証明書が前回と同じなら、2 回目以降の配布ではこの手順は要りません。新しい版が導入できない（「信頼されていない」と出る）ときは、今回の `.cer` で 1 をやり直してください。

## 2. アプリを導入する

PC に合ったフォルダーの `.msix` をダブルクリックし、「インストール」を押します。

PowerShell で導入する場合（管理者でなくてかまいません）:

```powershell
Add-AppxPackage -Path .\x64\TTDeployTool-<版>-x64.msix
# Dependencies フォルダーがあるとき
Add-AppxPackage -Path .\x64\TTDeployTool-<版>-x64.msix -DependencyPath (Get-ChildItem .\x64\Dependencies\*.msix).FullName
```

導入すると、スタート メニューに「TTDeployTool」が出ます。

### 導入できないとき

| 表示 | 対処 |
|---|---|
| 「信頼されていない」「発行元の証明書を確認できない」（0x800B0109 など） | 1 の証明書の信頼をやり直してください（保存場所が「ローカル コンピューター」の「信頼されたユーザー」であること） |
| 「開発者モード」や「サイドロード」を求められる | 設定 → システム → 開発者向け（Windows 10 は 更新とセキュリティ → 開発者向け）で有効にしてください |
| 会社のポリシーで禁止されている | PC の管理者に、ストア以外のアプリの導入（サイドロード）の可否を相談してください |
| より新しい版が既に入っている | 古い版は上書きできません。下の「アンインストール」の後に導入してください |
| 「パッケージ化されていないバージョンを既にインストールしています」（0x80073CFB） | Visual Studio などで開発用にビルドしたアプリ（同じ名前の TTDeployTool）が入っています。PowerShell で `Get-AppxPackage -Name TTDeployTool \| Remove-AppxPackage` を実行して削除してから、もう一度導入してください（開発用のアプリの設定とテンプレートも消えます。残したい場合は、先に `%LOCALAPPDATA%\Packages\TTDeployTool_tgmy1a6xvgpyy\LocalState` を別の場所にコピーし、評価版を導入して一度起動して閉じた後に、同じ場所へ戻します） |

## 3. 更新する

新しい版の `.msix` をダブルクリックして「更新」を押します。設定とテンプレートは残ります。

## 4. アンインストールする

1. 設定 → アプリ → インストールされているアプリ で「TTDeployTool」をアンインストールします。
2. 評価が終わったら、証明書も削除します（管理者の PowerShell）。

   ```powershell
   Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Subject -eq "CN=TTDeployTool" | Remove-Item
   ```

## 5. アプリのデータ（設定・予定・記録・ログ）
アプリは、設定・予定・終わったジョブの記録・診断用のログを、次のフォルダーに置きます。問い合わせのときにログを添付する場合や、設定を初めからやり直したい場合に使います。

```powershell
explorer "$env:LOCALAPPDATA\Packages\$((Get-AppxPackage -Name TTDeployTool).PackageFamilyName)\LocalState"
```

- `logs` のフォルダーは、アプリの「設定」の画面の「ログのフォルダーを開く」でも開けます。ログにパスワード・トークン・アカウント名は含まれません
- 設定・予定・記録を初めからやり直すときは、通知領域のアイコンのメニューの「終了」でアプリを終えてから、`preferences.json`（設定と予定）・`job-history.json`（終わったジョブの記録）を消します
- アンインストール（4）すると、このフォルダーは中身ごと消えます。まだ始めていない予定も消えるため、要る予定はアンインストールの前に確かめてください

## 注意

- この証明書を信頼すると、同じ証明書で署名されたパッケージは導入できるようになります。評価の目的以外で受け取った `.msix` は導入しないでください。
- アプリでデプロイした Azure のリソースには料金がかかります。使い終わったら、アプリの「デプロイの一覧」から削除してください（利用可能期限を設定すると、期限を過ぎたときに、起動している〔通知領域に常駐している〕アプリが削除します）。
