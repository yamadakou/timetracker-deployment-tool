<#
.SYNOPSIS
  デスクトップアプリの、本物のサインインと Azure を使う UI テスト（半自動）を実行する。手順は docs/design/desktop-ui-tests.md の 6。

.DESCRIPTION
  導入したデスクトップアプリ（Visual Studio で配置したもの、または導入したパッケージ）を起動し、サブスクリプションの読み込み、
  状態の確認、デプロイ（計画 → 実行）、削除までを UI Automation で操作する。サインインだけ、利用者が WAM の画面で行う。
  Azure にリソースを作るため、実行の前に、作るもの・料金・後片付けを示して確認を求める。
  結果（trx・失敗したときのスクリーンショット・診断用のログの写し）は TestResults/desktop-ui に置く。

.EXAMPLE
  ./scripts/run-desktop-ui-tests.ps1 -SubscriptionId <ID>
  ./scripts/run-desktop-ui-tests.ps1 -SubscriptionId <ID> -ResourceGroup rg-ttui-test -Location japaneast
#>
param(
    [Parameter(Mandatory)] [string] $SubscriptionId,
    [string] $ResourceGroup = "rg-ttui-$(Get-Date -Format 'yyyyMMddHHmm')",
    [string] $AppName = "ttui",
    [string] $Location = "japaneast",
    [string] $PackageName = "TTDeployTool"
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

$package = Get-AppxPackage -Name $PackageName | Select-Object -First 1
if (-not $package) {
    throw "パッケージ $PackageName が導入されていません。TTDeployTool.Desktop.slnx を Visual Studio で開いて配置する（ビルド → ソリューションの配置）か、パッケージを導入してください。"
}

Write-Host @"
次の内容で、本物の Azure を使う UI テストを実行します。
  サブスクリプション: $SubscriptionId
  リソースグループ:   $ResourceGroup（無ければアプリが作ります）
  アプリ名:           $AppName（Container Apps Environment と 3 つの Container App を作り、最後にアプリの削除で消します）
  リージョン:         $Location
  アプリ:             $($package.PackageFullName)
所要時間は 20〜30 分で、Azure Container Apps の料金が発生します。サインインしていなければ、途中で WAM のサインインの画面が出ます。
リソースグループは本ツールでは削除しないため、テストの後に削除するかを確認します。
"@
if ((Read-Host "実行しますか？ (yes/no)") -ne "yes") {
    Write-Host "取りやめました。"
    exit 1
}

# 設定する環境変数は、終了時に元に戻す（あとで普通に dotnet test を実行したときに、料金の発生するテストを実行しないように）
$names = "TT_UITEST_SUBSCRIPTION_ID", "TT_UITEST_RESOURCE_GROUP", "TT_UITEST_APP_NAME", "TT_UITEST_LOCATION",
    "TT_UITEST_PACKAGE_NAME", "TT_UITEST_RESULTS_DIRECTORY"
$saved = @{}
foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }

$results = Join-Path $repo "TestResults/desktop-ui"
# global.json（Microsoft.Testing.Platform を使う設定）を読むため、リポジトリのディレクトリで実行する
Push-Location $repo
try {
    $env:TT_UITEST_SUBSCRIPTION_ID = $SubscriptionId
    $env:TT_UITEST_RESOURCE_GROUP = $ResourceGroup
    $env:TT_UITEST_APP_NAME = $AppName
    $env:TT_UITEST_LOCATION = $Location
    $env:TT_UITEST_PACKAGE_NAME = $PackageName
    $env:TT_UITEST_RESULTS_DIRECTORY = $results

    # Azure を使うテストだけを実行する（Azure を使わないテストは、サインインしていない状態を前提にするため CI で実行する）
    dotnet test --project (Join-Path $repo "tests/TTDeployTool.Controller.Desktop.UITests") -- `
        --filter-class TTDeployTool.Controller.Desktop.UITests.AzureUiTests `
        --output detailed --report-xunit-trx "--results-directory=$results"
    $exitCode = $LASTEXITCODE
}
finally {
    Pop-Location
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
}

# 診断用のログ（パスワード・トークン・アカウント名は含まない）の写しを結果に置く
$logs = Join-Path $env:LOCALAPPDATA "Packages/$($package.PackageFamilyName)/LocalState/logs"
if (Test-Path $logs) {
    New-Item -ItemType Directory -Force -Path $results | Out-Null
    Copy-Item (Join-Path $logs "ttdeploytool-desktop-*.log") $results
    Write-Host "診断用のログを $results に写しました。"
}

Write-Host ($exitCode -eq 0 ? "UI テストは成功しました。" : "UI テストは失敗しました（結果: $results）。途中で止まった場合は、作ったリソースが残っています。")
if (Get-Command az -ErrorAction SilentlyContinue) {
    if ((Read-Host "リソースグループ $ResourceGroup を削除しますか？（中のリソースもすべて消えます） (yes/no)") -eq "yes") {
        az group delete --subscription $SubscriptionId --name $ResourceGroup --yes --no-wait
        Write-Host "削除を始めました（数分かかります）。"
    }
} else {
    Write-Host "Azure ポータルなどで、リソースグループ $ResourceGroup を削除してください。"
}
exit $exitCode
