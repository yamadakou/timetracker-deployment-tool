<#
.SYNOPSIS
  システムテスト（本物の Azure に対するテスト）をローカルで実行する。手順は docs/SYSTEM_TESTS.md を参照。

.DESCRIPTION
  Azure CLI にサインインしていなければデバイスコード認証でサインインし、環境変数を設定して
  tests/Timetracker.SystemTests を実行する。権限不足のシナリオは、権限の少ないサービスプリンシパルの
  クライアント ID とシークレットがある場合だけ実行する（シークレットは環境変数か、入力で渡す）。

.EXAMPLE
  ./scripts/run-system-tests.ps1 -SubscriptionId <ID>
  ./scripts/run-system-tests.ps1 -SubscriptionId <ID> -LimitedClientId <appId>
#>
param(
    [Parameter(Mandatory)] [string] $SubscriptionId,
    [string] $Location = "japaneast",
    [string] $LimitedResourceGroup = "rg-ttst-limited",
    [string] $LimitedClientId = $env:TT_SYSTEMTEST_LIMITED_CLIENT_ID
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

az account show --output none 2>$null
if ($LASTEXITCODE -ne 0) {
    az login --use-device-code --output none
    if ($LASTEXITCODE -ne 0) { throw "Azure CLI へのサインインに失敗しました。" }
}
az account set --subscription $SubscriptionId
if ($LASTEXITCODE -ne 0) { throw "サブスクリプション $SubscriptionId を選択できません。" }

# 設定する環境変数は、終了時に元に戻す（対話中の PowerShell で実行した後、通常の dotnet test が
# 料金の発生するシステムテストを実行してしまわないように。入力したシークレットも残さない）
$names = "TT_SYSTEMTEST_SUBSCRIPTION_ID", "TT_SYSTEMTEST_LOCATION", "TT_SYSTEMTEST_LIMITED_RESOURCE_GROUP",
    "TT_SYSTEMTEST_LIMITED_CLIENT_ID", "TT_SYSTEMTEST_LIMITED_TENANT_ID", "TT_SYSTEMTEST_LIMITED_CLIENT_SECRET"
$saved = @{}
foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }

# global.json（Microsoft.Testing.Platform を使う設定）を読むため、リポジトリのディレクトリで実行する
Push-Location $repo
try {
    $env:TT_SYSTEMTEST_SUBSCRIPTION_ID = $SubscriptionId
    $env:TT_SYSTEMTEST_LOCATION = $Location
    $env:TT_SYSTEMTEST_LIMITED_RESOURCE_GROUP = $LimitedResourceGroup
    if ($LimitedClientId) {
        $env:TT_SYSTEMTEST_LIMITED_CLIENT_ID = $LimitedClientId
        $env:TT_SYSTEMTEST_LIMITED_TENANT_ID = (az account show --query tenantId --output tsv)
        if (-not $env:TT_SYSTEMTEST_LIMITED_CLIENT_SECRET) {
            $secure = Read-Host "権限の少ないサービスプリンシパルのシークレット" -AsSecureString
            $env:TT_SYSTEMTEST_LIMITED_CLIENT_SECRET = [System.Net.NetworkCredential]::new("", $secure).Password
        }
    } else {
        Write-Warning "-LimitedClientId が無いため、権限不足のシナリオはスキップします。"
    }

    Write-Host "システムテストを実行します（20 分前後かかり、Azure Container Apps の料金が発生します）。"
    # --results-directory は "=" でつなぐ（既存のディレクトリをスペース区切りで渡すと、dotnet test がプロジェクトの指定と誤解する）
    $results = Join-Path $repo "TestResults/system"
    dotnet test --project (Join-Path $repo "tests/Timetracker.SystemTests") -- `
        --output detailed --report-xunit-trx "--results-directory=$results"
    $exitCode = $LASTEXITCODE
}
finally {
    Pop-Location
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
}
exit $exitCode
