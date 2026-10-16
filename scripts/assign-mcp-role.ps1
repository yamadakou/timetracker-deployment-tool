<#
.SYNOPSIS
  MCP サーバー（HTTP 版）のアプリロールを、利用者またはグループに割り当てる（詳細設計 docs/design/mcp-phase4c.md）。

.DESCRIPTION
  scripts/deploy-mcp-http.ps1 で作ったアプリ登録「Timetracker MCP (<Name>)」のアプリロールを割り当てる。
  - Server: Timetracker.Deploy.Server（server 方式と、有効なら user 方式）
  - User  : Timetracker.Deploy.User（user 方式だけ）
  割り当ては、利用者が次にサインインしたときのトークン（roles）から有効になる。
  グループへの割り当ては Entra ID P1 以上が必要。

.EXAMPLE
  ./scripts/assign-mcp-role.ps1 -Role Server -UserPrincipalName alice@example.com
  ./scripts/assign-mcp-role.ps1 -Name tt-mcp -Role User -GroupObjectId <グループのオブジェクト ID>
#>
#Requires -Version 7
param(
    [string] $Name = "tt-mcp",
    [Parameter(Mandatory)] [ValidateSet("Server", "User")] [string] $Role,
    [string] $UserPrincipalName,
    [string] $GroupObjectId
)
$ErrorActionPreference = "Stop"

function Invoke-Az {
    $output = & az @args
    if ($LASTEXITCODE -ne 0) { throw "az $($args -join ' ') が失敗しました。" }
    return ($output -join "`n")
}

if ([string]::IsNullOrEmpty($UserPrincipalName) -eq [string]::IsNullOrEmpty($GroupObjectId)) {
    throw "-UserPrincipalName か -GroupObjectId のどちらか一方を指定してください。"
}

$serverName = "Timetracker MCP ($Name)"
$appId = Invoke-Az ad app list --display-name $serverName --query "[0].appId" --output tsv
if (-not $appId) { throw "アプリ登録「$serverName」が見つかりません。先に scripts/deploy-mcp-http.ps1 を実行してください。" }
$value = "Timetracker.Deploy.$Role"
$roleId = Invoke-Az ad app show --id $appId --query "appRoles[?value=='$value'].id | [0]" --output tsv
$spId = Invoke-Az ad sp show --id $appId --query id --output tsv
$principalId = if ($UserPrincipalName) { Invoke-Az ad user show --id $UserPrincipalName --query id --output tsv } else { $GroupObjectId }

$existing = Invoke-Az rest --method GET --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$spId/appRoleAssignedTo" `
    --query "value[?principalId=='$principalId' && appRoleId=='$roleId'].id | [0]" --output tsv
if ($existing) {
    Write-Host "既に $value が割り当てられています。"
    return
}

$file = New-TemporaryFile
try {
    @{ principalId = $principalId; resourceId = $spId; appRoleId = $roleId } | ConvertTo-Json | Set-Content -Path $file -Encoding utf8NoBOM
    Invoke-Az rest --method POST --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$spId/appRoleAssignedTo" `
        --headers "Content-Type=application/json" --body "@$file" --output none | Out-Null
}
finally { Remove-Item $file -ErrorAction SilentlyContinue }
Write-Host "$value を割り当てました（$($UserPrincipalName ?? $GroupObjectId)）。利用者はサインインし直してください。"
