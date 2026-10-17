<#
.SYNOPSIS
  MCP サーバー（HTTP 版）を Azure Container Apps に配置し、Entra ID のアプリ登録を作る（詳細設計 docs/design/mcp-phase4c.md）。

.DESCRIPTION
  次のものを作成・更新する（同じ引数で再実行すると、作成済みのものは作り直さずに設定だけを合わせる）。
  - MCP サーバーを置くリソースグループ、ユーザー割り当てのマネージド ID
  - Entra ID のアプリ登録（サーバー用: スコープ Mcp.Access・アプリロール・v2 のトークン、
    クライアント用: Claude Code / VS Code からサインインする公開クライアント）
  - user 方式を使う場合: Azure Service Management の委任アクセス許可と、マネージド ID を信頼するフェデレーション資格情報
  - Container Apps の Environment と Container App（ghcr.io/<owner>/timetracker-mcp。レプリカは 1 に固定）
  - server 方式を使う場合: 許可するリソースグループと、マネージド ID への Contributor の割り当て
  利用者へのアプリロールの割り当ては scripts/assign-mcp-role.ps1 で行う。

  実行する人に必要な権限: サブスクリプションの所有者（またはロールの割り当てができる権限）と、
  Entra ID でアプリを登録できる権限。管理者の同意（user 方式）にはテナントの管理者（特権ロール管理者など）が必要。

.EXAMPLE
  ./scripts/deploy-mcp-http.ps1 -SubscriptionId <ID> -ResourceGroup rg-tt-mcp -AllowedResourceGroups rg-tt-team1,rg-tt-team2
  ./scripts/deploy-mcp-http.ps1 -SubscriptionId <ID> -ResourceGroup rg-tt-mcp -IdentityModes server,user -AllowedResourceGroups rg-tt-team1
  ./scripts/deploy-mcp-http.ps1 -SubscriptionId <ID> -ResourceGroup rg-tt-mcp -IdentityModes user
#>
#Requires -Version 7
param(
    [Parameter(Mandatory)] [string] $SubscriptionId,
    # MCP サーバーを置くリソースグループ（検証環境を作るリソースグループとは分ける）
    [Parameter(Mandatory)] [string] $ResourceGroup,
    [string] $Location = "japaneast",
    # Container App の名前（Environment・マネージド ID・アプリ登録の名前の元にもなる）
    [string] $Name = "tt-mcp",
    [string] $Image = "ghcr.io/yamadakou/timetracker-mcp",
    [string] $ImageTag = "latest",
    # server / user / server,user（PowerShell から server,user と書くと配列になるため、配列も受け付ける）
    [string[]] $IdentityModes = @("server"),
    # server 方式で操作を許すリソースグループ名（このサブスクリプション。無ければ作成する）
    [string[]] $AllowedResourceGroups = @(),
    [switch] $CanCreateResourceGroup,
    # Claude Code の OAuth のコールバックのポート（claude mcp add の --callback-port と同じ値）
    [int] $ClaudeCodeCallbackPort = 8765
)
$ErrorActionPreference = "Stop"

# az を実行し、失敗したら止める（PowerShell は大文字小文字を区別しないため、関数名を az と別にする）（出力は文字列で返す）
function Invoke-Az {
    $output = & az @args
    if ($LASTEXITCODE -ne 0) { throw "az $($args -join ' ') が失敗しました。" }
    return ($output -join "`n")
}

function Invoke-AzJson { (Invoke-Az @args --output json) | ConvertFrom-Json }

# Microsoft Graph の PATCH / POST（本文は JSON のファイルで渡す。Windows でも引用符が崩れないように）
function Graph([string] $Method, [string] $Uri, $Body) {
    $file = New-TemporaryFile
    try {
        $Body | ConvertTo-Json -Depth 10 | Set-Content -Path $file -Encoding utf8NoBOM
        Invoke-Az rest --method $Method --uri "https://graph.microsoft.com/v1.0$Uri" --headers "Content-Type=application/json" --body "@$file" | Out-Null
    }
    finally { Remove-Item $file -ErrorAction SilentlyContinue }
}

# 実行したアカウントの分だけ、サーバーのアプリに Azure Service Management の user_impersonation を同意する（作れたか、既にあれば true）
function Grant-ConsentForSelf([string] $AppId) {
    try {
        $clientSp = Invoke-Az ad sp show --id $AppId --query id --output tsv
        $resourceSp = Invoke-Az ad sp show --id $armAppId --query id --output tsv
        $me = Invoke-Az ad signed-in-user show --query id --output tsv
        $grants = Invoke-AzJson rest --method GET --uri "https://graph.microsoft.com/v1.0/oauth2PermissionGrants?`$filter=clientId eq '$clientSp' and principalId eq '$me'"
        if (-not @($grants.value | Where-Object { $_.resourceId -eq $resourceSp -and ($_.scope -split " ") -contains "user_impersonation" })) {
            Graph POST "/oauth2PermissionGrants" @{ clientId = $clientSp; consentType = "Principal"; principalId = $me; resourceId = $resourceSp; scope = "user_impersonation" }
        }
        return $true
    }
    catch { return $false }
}

function Step([string] $Message) { Write-Host "==> $Message" -ForegroundColor Cyan }

# 無ければ作る（既にあるリソースグループは、場所が違っても変えない。az group create は場所の変更を拒否するため）
function New-ResourceGroupIfMissing([string] $Group) {
    if ((Invoke-Az group exists --name $Group) -ne "true") {
        Invoke-Az group create --name $Group --location $Location --output none | Out-Null
    }
}

# pwsh -File で渡すと「a,b」が 1 つの文字列になるため、カンマでも分ける
$AllowedResourceGroups = @($AllowedResourceGroups | ForEach-Object { $_ -split "," } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$modes = @($IdentityModes | ForEach-Object { $_ -split "," } | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ } | Select-Object -Unique)
if ($modes.Count -eq 0 -or ($modes | Where-Object { $_ -notin @("server", "user") })) {
    throw "-IdentityModes には server、user、または server,user を指定してください。"
}
$identityModesValue = $modes -join ","
$serverMode = $modes -contains "server"
$userMode = $modes -contains "user"
if ($serverMode -and $AllowedResourceGroups.Count -eq 0) {
    throw "server 方式を使う場合は、-AllowedResourceGroups に操作を許すリソースグループを 1 つ以上指定してください。"
}

# Azure Resource Manager（Azure Service Management）と、その委任アクセス許可 user_impersonation（どちらも Microsoft の固定の ID）
$armAppId = "797f4846-ba00-4fd7-ba43-dac1f8f63013"
$armUserImpersonation = "41094075-9dad-400e-a0bd-54e686782033"

az account show --output none 2>$null
if ($LASTEXITCODE -ne 0) {
    az login --output none
    if ($LASTEXITCODE -ne 0) { throw "Azure CLI へのサインインに失敗しました。" }
}
Invoke-Az account set --subscription $SubscriptionId | Out-Null
$tenantId = Invoke-Az account show --query tenantId --output tsv

Step "リソースプロバイダーを登録します（登録済みなら何もしません）"
Invoke-Az provider register --namespace Microsoft.App --wait | Out-Null

Step "リソースグループ $ResourceGroup とマネージド ID $Name-id"
New-ResourceGroupIfMissing $ResourceGroup
$identity = Invoke-AzJson identity create --resource-group $ResourceGroup --name "$Name-id" --location $Location

Step "Entra ID: サーバーのアプリ登録"
$serverName = "Timetracker MCP ($Name)"
$serverAppId = Invoke-Az ad app list --display-name $serverName --query "[0].appId" --output tsv
if (-not $serverAppId) {
    $serverAppId = Invoke-Az ad app create --display-name $serverName --sign-in-audience AzureADMyOrg --query appId --output tsv
}
$serverApp = Invoke-AzJson ad app show --id $serverAppId
# 再実行でスコープやロールの ID が変わらないよう、既存の ID を使う
$scopeId = ($serverApp.api.oauth2PermissionScopes | Where-Object value -eq "Mcp.Access").id ?? (New-Guid).Guid
$serverRoleId = ($serverApp.appRoles | Where-Object value -eq "Timetracker.Deploy.Server").id ?? (New-Guid).Guid
$userRoleId = ($serverApp.appRoles | Where-Object value -eq "Timetracker.Deploy.User").id ?? (New-Guid).Guid

$requiredAccess = @()
if ($userMode) {
    # user 方式: 利用者のトークンを OBO で Azure 用のトークンに交換するため
    $requiredAccess = @(@{ resourceAppId = $armAppId; resourceAccess = @(@{ id = $armUserImpersonation; type = "Scope" }) })
}
$api = @{
    # MCP サーバーは v2 のトークン（発行者 https://login.microsoftonline.com/<テナント>/v2.0）だけを受け付ける
    requestedAccessTokenVersion = 2
    oauth2PermissionScopes = @(@{
        id = $scopeId; value = "Mcp.Access"; type = "User"; isEnabled = $true
        adminConsentDisplayName = "Timetracker MCP サーバーを使う"
        adminConsentDescription = "Timetracker のデプロイ用 MCP サーバーのツールを、サインインした利用者として呼び出します。"
        userConsentDisplayName = "Timetracker MCP サーバーを使う"
        userConsentDescription = "Timetracker のデプロイ用 MCP サーバーのツールを、あなたとして呼び出します。"
    })
    # 再実行のときも、事前承認済みのクライアントを残す（下で設定する）
    preAuthorizedApplications = @($serverApp.api.preAuthorizedApplications | Where-Object { $_ })
}
Graph PATCH "/applications/$($serverApp.id)" @{
    identifierUris = @("api://$serverAppId")
    api = $api
    appRoles = @(
        @{ id = $serverRoleId; value = "Timetracker.Deploy.Server"; allowedMemberTypes = @("User"); isEnabled = $true
           displayName = "Timetracker.Deploy.Server"; description = "サーバーの ID（server 方式）と、有効なら利用者本人の権限（user 方式）で Azure を操作できる" },
        @{ id = $userRoleId; value = "Timetracker.Deploy.User"; allowedMemberTypes = @("User"); isEnabled = $true
           displayName = "Timetracker.Deploy.User"; description = "利用者本人の権限（user 方式）で Azure を操作できる" }
    )
    requiredResourceAccess = $requiredAccess
}
az ad sp show --id $serverAppId --output none 2>$null
if ($LASTEXITCODE -ne 0) { Invoke-Az ad sp create --id $serverAppId --output none | Out-Null }

if ($userMode) {
    Step "Entra ID: マネージド ID を信頼するフェデレーション資格情報（user 方式のトークン交換に使う。シークレット不要）"
    $ficName = "$Name-managed-identity"
    $existing = Invoke-Az ad app federated-credential list --id $serverAppId --query "[?name=='$ficName'].name" --output tsv
    if (-not $existing) {
        $file = New-TemporaryFile
        try {
            @{
                name = $ficName
                issuer = "https://login.microsoftonline.com/$tenantId/v2.0"
                subject = $identity.principalId
                audiences = @("api://AzureADTokenExchange")
                description = "Container App $Name のマネージド ID"
            } | ConvertTo-Json | Set-Content -Path $file -Encoding utf8NoBOM
            Invoke-Az ad app federated-credential create --id $serverAppId --parameters "@$file" --output none | Out-Null
        }
        finally { Remove-Item $file -ErrorAction SilentlyContinue }
    }

    Step "Entra ID: Azure Service Management の委任アクセス許可への管理者の同意"
    az ad app permission admin-consent --id $serverAppId --output none
    if ($LASTEXITCODE -ne 0) {
        # 管理者でなくても、実行した本人の分の同意は作れることがある（テナントの同意の設定による）。同意が無いと、MFA を済ませても OBO が AADSTS65001 で失敗する
        $self = if (Grant-ConsentForSelf $serverAppId) { "実行したアカウントの分だけ同意しました。" } else { "実行したアカウントの分の同意も作れませんでした。" }
        Write-Warning "管理者の同意に失敗しました。$self ほかの利用者が user 方式を使うには、テナントの管理者に、Entra 管理センターの「$serverName」の「API のアクセス許可」で同意を付与してもらってください。"
    }
}

Step "Entra ID: クライアント（Claude Code / VS Code）のアプリ登録"
$clientName = "Timetracker MCP client ($Name)"
$clientAppId = Invoke-Az ad app list --display-name $clientName --query "[0].appId" --output tsv
if (-not $clientAppId) {
    $clientAppId = Invoke-Az ad app create --display-name $clientName --sign-in-audience AzureADMyOrg --is-fallback-public-client true --query appId --output tsv
}
$clientApp = Invoke-AzJson ad app show --id $clientAppId
Graph PATCH "/applications/$($clientApp.id)" @{
    isFallbackPublicClient = $true
    # Claude Code（--callback-port）と VS Code のループバック。VS Code の URI は docs/design/mcp-phase4c.md の確認事項を参照
    # 管理者が後から加えたリダイレクト URI は残す
    publicClient = @{ redirectUris = @(@($clientApp.publicClient.redirectUris) + @("http://localhost:$ClaudeCodeCallbackPort/callback", "http://127.0.0.1:33418") |
        Where-Object { $_ } | Select-Object -Unique) }
    requiredResourceAccess = @(@{ resourceAppId = $serverAppId; resourceAccess = @(@{ id = $scopeId; type = "Scope" }) })
}
# クライアントを事前承認し、利用者に Mcp.Access への同意を求めない（スコープを作った後に、api 全体を送り直す）
# 管理者が後から事前承認したクライアント（VS Code など）は残し、このスクリプトのクライアントだけを置き換える
$api.preAuthorizedApplications = @(@($api.preAuthorizedApplications | Where-Object { $_.appId -ne $clientAppId }) +
    @(@{ appId = $clientAppId; delegatedPermissionIds = @($scopeId) }))
Graph PATCH "/applications/$($serverApp.id)" @{ api = $api }

Step "Container Apps の Environment $Name-env"
az containerapp env show --resource-group $ResourceGroup --name "$Name-env" --output none 2>$null
if ($LASTEXITCODE -ne 0) {
    # コンテナのログは az containerapp logs show で見る（長く残す場合は Log Analytics を設定する）
    Invoke-Az containerapp env create --resource-group $ResourceGroup --name "$Name-env" --location $Location --logs-destination none --output none | Out-Null
}
$domain = Invoke-Az containerapp env show --resource-group $ResourceGroup --name "$Name-env" --query properties.defaultDomain --output tsv
$publicUrl = "https://$Name.$domain/mcp"

# MCP のクライアントは認可の要求に resource（公開 URL）を付け、Entra ID はスコープの対象と違う resource を拒否する（AADSTS9010010）。
# そのため公開 URL もアプリケーション ID の URI にし、サーバーは公開 URL のスコープ（<公開 URL>/Mcp.Access）を示す。管理者が加えた URI は残す
Step "Entra ID: 公開 URL $publicUrl をサーバーのアプリのアプリケーション ID の URI に加える"
Graph PATCH "/applications/$($serverApp.id)" @{
    identifierUris = @(@($serverApp.identifierUris) + @("api://$serverAppId", $publicUrl) | Where-Object { $_ } | Select-Object -Unique)
}

$envVars = @(
    "TT_MCP_HTTP__TENANT_ID=$tenantId",
    "TT_MCP_HTTP__CLIENT_ID=$serverAppId",
    "TT_MCP_HTTP__PUBLIC_URL=$publicUrl",
    "TT_MCP_HTTP__IDENTITY_MODES=$identityModesValue",
    "TT_MCP_HTTP__SERVER_IDENTITY=managed-identity",
    "TT_MCP_HTTP__USER_CLIENT_CREDENTIAL=managed-identity",
    # ユーザー割り当てのマネージド ID（server 方式の資格情報と、user 方式のフェデレーション資格情報に使う）
    "AZURE_CLIENT_ID=$($identity.clientId)"
)
if ($serverMode) {
    $envVars += "TT_MCP_HTTP__SERVER_ALLOWED_RESOURCE_GROUPS=" + (($AllowedResourceGroups | ForEach-Object { "/subscriptions/$SubscriptionId/resourceGroups/$_" }) -join ",")
    $envVars += "TT_MCP_HTTP__SERVER_CAN_CREATE_RESOURCE_GROUP=$($CanCreateResourceGroup.IsPresent.ToString().ToLowerInvariant())"
}

Step "Container App $Name（$Image`:$ImageTag）"
az containerapp show --resource-group $ResourceGroup --name $Name --output none 2>$null
if ($LASTEXITCODE -ne 0) {
    # 計画とジョブはプロセスのメモリにあるため、レプリカは 1 に固定する
    Invoke-Az containerapp create --resource-group $ResourceGroup --name $Name --environment "$Name-env" `
        --image "$Image`:$ImageTag" --target-port 8080 --ingress external `
        --min-replicas 1 --max-replicas 1 --cpu 0.5 --memory 1.0Gi `
        --user-assigned $identity.id --env-vars @envVars --output none | Out-Null
}
else {
    Invoke-Az containerapp update --resource-group $ResourceGroup --name $Name --image "$Image`:$ImageTag" `
        --min-replicas 1 --max-replicas 1 --set-env-vars @envVars --output none | Out-Null
}

if ($serverMode) {
    Step "server 方式: 許可するリソースグループと、マネージド ID への Contributor の割り当て"
    foreach ($group in $AllowedResourceGroups) {
        New-ResourceGroupIfMissing $group
        $scope = "/subscriptions/$SubscriptionId/resourceGroups/$group"
        $assigned = Invoke-Az role assignment list --assignee $identity.principalId --role Contributor --scope $scope --query "[0].id" --output tsv
        if (-not $assigned) {
            Invoke-Az role assignment create --assignee-object-id $identity.principalId --assignee-principal-type ServicePrincipal `
                --role Contributor --scope $scope --output none | Out-Null
        }
    }
    if ($CanCreateResourceGroup) {
        Write-Warning "-CanCreateResourceGroup を指定しました。許可したリソースグループが無いときに作成できるよう、マネージド ID にサブスクリプションのスコープで Contributor が必要です（docs/design/mcp-phase4c.md）。"
    }
}

Write-Host ""
Write-Host "完了しました。" -ForegroundColor Green
Write-Host "  MCP のエンドポイント : $publicUrl"
Write-Host "  サーバーのアプリ     : $serverAppId（$serverName）"
Write-Host "  クライアントのアプリ : $clientAppId（$clientName）"
Write-Host "  スコープ             : $publicUrl/Mcp.Access"
Write-Host ""
Write-Host "次の手順:"
Write-Host "  1. 利用者にアプリロールを割り当てる: ./scripts/assign-mcp-role.ps1 -Name $Name -Role Server -UserPrincipalName <UPN>"
Write-Host "  2. Claude Code: claude mcp add --transport http --client-id $clientAppId --callback-port $ClaudeCodeCallbackPort timetracker $publicUrl"
Write-Host "     その後 Claude Code で /mcp を開き、timetracker でサインインする"
Write-Host "  3. 動作確認: docs/design/mcp-phase4c.md の「確認のシナリオ」"
