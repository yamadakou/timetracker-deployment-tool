<#
.SYNOPSIS
  サブスクリプションにある、利用可能期限を過ぎた本ツールのデプロイを探し、CLI（ttdeploytool-cli delete）で削除する。

.DESCRIPTION
  Desktop System Test（.github/workflows/desktop-system-test.yml）の前に実行する。テスト中のデスクトップアプリは、期限を過ぎたデプロイを
  見つけると自動で削除のジョブを登録し、同時に実行できる数の枠を使うため、テストの手順がずれる（2026-10-29）。テストで使うサブスクリプションは
  開発用で、期限を過ぎたデプロイは消してよい（オーナーの判断、2026-10-29）。

  本ツールの管理タグ（managed-by が ttdeploytool か、以前の版の timetracker-deployment-tool）と tt-app-name の付いたリソースを、
  リソースグループとアプリ名ごとにまとめ、tt-expires-at（yyyy-MM-ddTHH:mm:ssZ。none は期限なし）のうち最も早いものが今より前なら、期限切れとする。
  削除は CLI の delete（管理タグの一致するリソースだけ）。リソースグループが本ツールで作ったもの（管理タグ付き）で、削除の後に空なら、
  リソースグループも削除する（デスクトップアプリの期限での削除と同じ）。

.EXAMPLE
  ./.github/scripts/remove-expired-deployments.ps1 -SubscriptionId <ID>
  ./.github/scripts/remove-expired-deployments.ps1 -SubscriptionId <ID> -ResourcesFile resources.json -PlanOnly
#>
#Requires -Version 7
param(
    [Parameter(Mandatory)] [string] $SubscriptionId,
    # az resource list の出力（JSON）。テストで使う。省略時は az で読む
    [string] $ResourcesFile,
    # 今の時刻（UTC。テストで使う）
    [DateTimeOffset] $Now = [DateTimeOffset]::UtcNow,
    # 削除せず、期限切れのデプロイ（ResourceGroup・AppName・ExpiresAt）を出すだけにする
    [switch] $PlanOnly
)
$ErrorActionPreference = "Stop"

$managedBy = @("ttdeploytool", "timetracker-deployment-tool")

function Get-Tag($Resource, [string] $Name) {
    if ($null -eq $Resource.tags) { return $null }
    $property = $Resource.tags.PSObject.Properties | Where-Object { $_.Name -ieq $Name } | Select-Object -First 1
    if ($property) { return $property.Value }
    return $null
}

# tt-expires-at の値を UTC の日時にする（読めない・none なら null）。ConvertFrom-Json は ISO 8601 の文字列を日時に変えることがあるため、どちらも受ける
function ConvertTo-Expiry($Value) {
    if ($Value -is [datetime]) { return [DateTimeOffset]$Value.ToUniversalTime() }
    $style = [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal
    $parsed = [DateTimeOffset]::MinValue
    if ($Value -and [DateTimeOffset]::TryParseExact([string]$Value, "yyyy-MM-ddTHH:mm:ssZ", [cultureinfo]::InvariantCulture, $style, [ref]$parsed)) { return $parsed }
    return $null
}

# 期限切れのデプロイ（リソースグループとアプリ名ごと。大文字と小文字は区別しない）
function Get-ExpiredDeployments($Resources, [DateTimeOffset] $At) {
    $Resources |
        Where-Object { (Get-Tag $_ "managed-by") -in $managedBy -and (Get-Tag $_ "tt-app-name") } |
        Group-Object { "$($_.resourceGroup.ToLowerInvariant())/$(([string](Get-Tag $_ 'tt-app-name')).ToLowerInvariant())" } |
        ForEach-Object {
            $first = $_.Group[0]
            $dates = @($_.Group | ForEach-Object { ConvertTo-Expiry (Get-Tag $_ "tt-expires-at") } | Where-Object { $null -ne $_ })
            if ($dates.Count -gt 0) {
                $earliest = ($dates | Sort-Object | Select-Object -First 1)
                if ($earliest -lt $At) {
                    [pscustomobject]@{ ResourceGroup = $first.resourceGroup; AppName = [string](Get-Tag $first "tt-app-name"); ExpiresAt = $earliest }
                }
            }
        } |
        Sort-Object ResourceGroup, AppName
}

if ($ResourcesFile) {
    $resources = Get-Content -Raw $ResourcesFile | ConvertFrom-Json
} else {
    $json = az resource list --subscription $SubscriptionId --output json
    if ($LASTEXITCODE -ne 0) { throw "サブスクリプション $SubscriptionId のリソースを読めませんでした。" }
    $resources = $json | ConvertFrom-Json
}

$expired = @(Get-ExpiredDeployments $resources $Now)
if ($expired.Count -eq 0) {
    Write-Host "利用可能期限を過ぎたデプロイはありません。"
}
foreach ($deployment in $expired) {
    Write-Host "利用可能期限を過ぎたデプロイ: $($deployment.ResourceGroup) / $($deployment.AppName)（期限 $($deployment.ExpiresAt.ToString('yyyy-MM-dd HH:mm:ss')) UTC）"
}
if ($PlanOnly) {
    return $expired
}

$cli = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) "src/TTDeployTool.Controller.Cli"
foreach ($deployment in $expired) {
    Write-Host "CLI で削除します: $($deployment.ResourceGroup) / $($deployment.AppName)"
    dotnet run --project $cli -c Release -- delete --subscription $SubscriptionId --resource-group $deployment.ResourceGroup `
        --app-name $deployment.AppName --auth-mode azure-cli --yes true
    if ($LASTEXITCODE -ne 0) { throw "$($deployment.ResourceGroup) / $($deployment.AppName) を削除できませんでした。" }

    # 本ツールで作ったリソースグループが空になれば、リソースグループも削除する
    $group = az group show --subscription $SubscriptionId --name $deployment.ResourceGroup --output json | ConvertFrom-Json
    $left = @(az resource list --subscription $SubscriptionId --resource-group $deployment.ResourceGroup --output json | ConvertFrom-Json)
    if ((Get-Tag $group "managed-by") -in $managedBy -and $left.Count -eq 0) {
        az group delete --subscription $SubscriptionId --name $deployment.ResourceGroup --yes --no-wait
        if ($LASTEXITCODE -ne 0) { throw "リソースグループ $($deployment.ResourceGroup) の削除を始められませんでした。" }
        Write-Host "リソースグループ $($deployment.ResourceGroup) の削除を始めました。"
    }
}
