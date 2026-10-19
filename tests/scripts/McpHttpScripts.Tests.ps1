<#
  scripts/deploy-mcp-http.ps1 と scripts/assign-mcp-role.ps1 のテスト（Pester 5 以上。詳細設計 docs/design/mcp-phase4c.md）。
  az は模擬に置き換え、Azure と Entra ID の状態（作成済みのもの、アプリ登録の内容、割り当て）を $global:FakeAzState で表す（Pester のブロックとスクリプトの両方から見えるよう、グローバルに置く）。
  呼び出しの引数と Microsoft Graph に送る本文を記録し、作るもの・作らないもの・送る内容を確かめる。

  実行: Invoke-Pester ./tests/scripts
#>

BeforeAll {
    $repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $script:Deploy = Join-Path $repo "scripts/deploy-mcp-http.ps1"
    $script:Assign = Join-Path $repo "scripts/assign-mcp-role.ps1"

    # Mock は、置き換える対象のコマンドが無いと作れないため、Azure CLI が無い環境では仮の az を置く
    if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
        function global:az { throw "az は模擬に置き換えてから呼んでください。" }
    }

    # 既定の状態: 何も作られていない
    function script:New-State {
        @{
            LoggedIn = $true
            Groups = @()                     # 既にあるリソースグループ
            ServerApp = $null                # 既にあるサーバーのアプリ（ad app show の結果）
            ClientApp = $null                # 既にあるクライアントのアプリ
            LegacyNames = $false             # 既にあるアプリが、本ツールの以前の名前（Timetracker MCP …）で登録されているか
            SpExists = $false
            Fics = @()                       # 既にあるフェデレーション資格情報の名前
            ConsentFails = $false
            SelfGrants = @()                 # 実行したアカウントの既にある同意（oauth2PermissionGrants）
            EnvExists = $false
            AppExists = $false
            RoleAssigned = @()               # Contributor が割り当て済みのスコープ
            FailOn = $null                   # この文字列で始まる呼び出しを失敗させる
            AssignmentPages = @(, @())       # appRoleAssignedTo のページ（割り当ての配列の配列）
            Calls = [System.Collections.Generic.List[string]]::new()
            Bodies = [System.Collections.Generic.List[object]]::new()   # @{ Call; Body }
        }
    }

    # 模擬の az: 引数を記録し、状態に応じた出力と終了コードを返す
    function script:Invoke-FakeAz([string[]] $Arguments) {
        $s = $global:FakeAzState
        $line = $Arguments -join " "
        $s.Calls.Add($line)
        foreach ($a in $Arguments | Where-Object { $_ -like "@*" }) {
            $s.Bodies.Add(@{ Call = $line; Body = (Get-Content -Raw -Path $a.Substring(1) | ConvertFrom-Json -AsHashtable) })
        }
        $global:LASTEXITCODE = 0
        if ($s.FailOn -and $line.StartsWith($s.FailOn)) { $global:LASTEXITCODE = 1; return }

        # 最初に一致したものだけを使う（「TTDeployTool MCP *」が「TTDeployTool MCP client …」にも一致するため、各項目で break する）
        switch -Wildcard ($line) {
            "account show --output none" { if (-not $s.LoggedIn) { $global:LASTEXITCODE = 1 }; break }
            "account show --query tenantId*" { "tenant-1"; break }
            "group exists --name *" { if ($s.Groups -contains $Arguments[3]) { "true" } else { "false" }; break }
            "identity create *" { '{"clientId":"mi-client","principalId":"mi-principal","id":"/subscriptions/sub-1/resourceGroups/rg-mcp/providers/Microsoft.ManagedIdentity/userAssignedIdentities/tt-mcp-id"}'; break }
            "ad app list --display-name TTDeployTool MCP client *" { if ($s.ClientApp -and -not $s.LegacyNames) { "client-app" }; break }
            "ad app list --display-name TTDeployTool MCP *" { if ($s.ServerApp -and -not $s.LegacyNames) { "server-app" }; break }
            "ad app list --display-name Timetracker MCP client *" { if ($s.ClientApp -and $s.LegacyNames) { "client-app" }; break }
            "ad app list --display-name Timetracker MCP *" { if ($s.ServerApp -and $s.LegacyNames) { "server-app" }; break }
            "ad app create --display-name TTDeployTool MCP client *" { $s.ClientApp = @{ id = "client-obj" }; "client-app"; break }
            "ad app create --display-name TTDeployTool MCP *" { $s.ServerApp = @{ id = "server-obj"; api = @{}; appRoles = @() }; "server-app"; break }
            "ad app show --id server-app --query *" { ($s.ServerApp.appRoles | Where-Object value -eq ($line -replace ".*value=='([^']+)'.*", '$1')).id; break }
            "ad app show --id server-app --output json" { $s.ServerApp | ConvertTo-Json -Depth 10; break }
            "ad app show --id client-app --output json" { $s.ClientApp | ConvertTo-Json -Depth 10; break }
            "ad sp show --id server-app --output none" { if (-not $s.SpExists) { $global:LASTEXITCODE = 1 }; break }
            "ad sp show --id server-app --query id*" { "sp-id"; break }
            "ad sp show --id 797f4846-ba00-4fd7-ba43-dac1f8f63013 --query id*" { "arm-sp"; break }
            "ad signed-in-user show --query id*" { "me-id"; break }
            "rest --method GET --uri *oauth2PermissionGrants*" { @{ value = @($s.SelfGrants) } | ConvertTo-Json -Depth 5; break }
            "ad app federated-credential list *" { $s.Fics -join "`n"; break }
            "ad app permission admin-consent *" { if ($s.ConsentFails) { $global:LASTEXITCODE = 1 }; break }
            "containerapp env show * --output none" { if (-not $s.EnvExists) { $global:LASTEXITCODE = 1 }; break }
            "containerapp env show * --query properties.defaultDomain*" { "happy-sea.japaneast.azurecontainerapps.io"; break }
            "containerapp show *" { if (-not $s.AppExists) { $global:LASTEXITCODE = 1 }; break }
            "role assignment list *" { if ($s.RoleAssigned -contains $Arguments[8]) { "assignment-id" }; break }
            "ad user show --id *" { "user-id"; break }
            "rest --method GET --uri *" {
                $page = if ($line -match "page=(\d+)") { [int]$Matches[1] } else { 0 }
                $next = if ($page + 1 -lt $s.AssignmentPages.Count) { "https://graph.microsoft.com/v1.0/servicePrincipals/sp-id/appRoleAssignedTo?page=$($page + 1)" }
                @{ value = @($s.AssignmentPages[$page]); "@odata.nextLink" = $next } | ConvertTo-Json -Depth 5
            }
        }
    }

    function script:Calls([string] $Pattern) { @($global:FakeAzState.Calls | Where-Object { $_ -like $Pattern }) }

    # 指定した呼び出しで送った Graph の本文（最後のもの）
    function script:Body([string] $Pattern) { ($global:FakeAzState.Bodies | Where-Object { $_.Call -like $Pattern } | Select-Object -Last 1).Body }

    # サーバーのアプリに最後に送った api（スコープと事前承認）
    function script:ServerApiBody { ($global:FakeAzState.Bodies | Where-Object { $_.Call -like "*applications/server-obj*" -and $_.Body.api } | Select-Object -Last 1).Body }

    # Container App に設定した環境変数（名前 → 値）
    function script:EnvVars {
        $call = (Calls "containerapp create *") + (Calls "containerapp update *") | Select-Object -Last 1
        $vars = @{}
        foreach ($m in [regex]::Matches($call, "(TT_MCP_[A-Z_]+|AZURE_CLIENT_ID)=(\S*)")) { $vars[$m.Groups[1].Value] = $m.Groups[2].Value }
        $vars
    }

    # 作成済みの状態（同じ引数での再実行を想定）
    function script:Set-ExistingState {
        $s = $global:FakeAzState
        $s.Groups = @("rg-mcp", "rg-a")
        $s.ServerApp = @{
            id = "server-obj"
            identifierUris = @("api://server-app", "https://admin-added.example.com/mcp")
            api = @{
                oauth2PermissionScopes = @(@{ id = "scope-1"; value = "Mcp.Access" })
                preAuthorizedApplications = @(
                    @{ appId = "vscode-app"; delegatedPermissionIds = @("scope-1") },
                    @{ appId = "client-app"; delegatedPermissionIds = @("old-scope") })
            }
            appRoles = @(@{ id = "role-server"; value = "TTDeployTool.Deploy.Server" }, @{ id = "role-user"; value = "TTDeployTool.Deploy.User" })
        }
        $s.ClientApp = @{ id = "client-obj"; publicClient = @{ redirectUris = @("https://admin-added/callback", "http://127.0.0.1:33418") } }
        $s.SpExists = $true
        $s.Fics = @("tt-mcp-managed-identity")
        $s.EnvExists = $true
        $s.AppExists = $true
        $s.RoleAssigned = @("/subscriptions/sub-1/resourceGroups/rg-a")
    }
}

Describe "deploy-mcp-http.ps1" {
    BeforeEach {
        $global:FakeAzState = New-State
        Mock az { Invoke-FakeAz $args }
        Mock Write-Host { }
    }

    Context "新規の配置" {
        It "server 方式と user 方式（配列で指定）: すべてを作り、両方の方式の設定にする" {
            & $script:Deploy -SubscriptionId sub-1 -ResourceGroup rg-mcp -IdentityModes server, user -AllowedResourceGroups rg-a, rg-b

            Calls "group create --name *" | Should -Be @(
                "group create --name rg-mcp --location japaneast --output none",
                "group create --name rg-a --location japaneast --output none",
                "group create --name rg-b --location japaneast --output none")
            Calls "ad app create *" | Should -HaveCount 2
            Calls "ad sp create --id server-app --output none" | Should -HaveCount 1
            Calls "containerapp env create *" | Should -HaveCount 1

            $server = ServerApiBody
            $server.api.requestedAccessTokenVersion | Should -Be 2
            $server.api.oauth2PermissionScopes[0].value | Should -Be "Mcp.Access"
            $server.api.preAuthorizedApplications.appId | Should -Be @("client-app")
            $first = ($global:FakeAzState.Bodies | Where-Object { $_.Call -like "*applications/server-obj*" } | Select-Object -First 1).Body
            $first.identifierUris | Should -Be @("api://server-app")
            # 公開 URL もアプリケーション ID の URI にする（MCP のクライアントが付ける resource と、スコープの対象を合わせるため）
            $identifierUris = ($global:FakeAzState.Bodies | Where-Object { $_.Call -like "*applications/server-obj*" -and $_.Body.identifierUris } | Select-Object -Last 1).Body.identifierUris
            $identifierUris | Should -Be @("api://server-app", "https://tt-mcp.happy-sea.japaneast.azurecontainerapps.io/mcp")
            $first.appRoles.value | Should -Be @("TTDeployTool.Deploy.Server", "TTDeployTool.Deploy.User")
            $first.requiredResourceAccess[0].resourceAppId | Should -Be "797f4846-ba00-4fd7-ba43-dac1f8f63013"
            $first.requiredResourceAccess[0].resourceAccess[0].id | Should -Be "41094075-9dad-400e-a0bd-54e686782033"

            $fic = Body "ad app federated-credential create *"
            Calls "*oauth2PermissionGrants*" | Should -HaveCount 0
            $fic.issuer | Should -Be "https://login.microsoftonline.com/tenant-1/v2.0"
            $fic.subject | Should -Be "mi-principal"
            $fic.audiences | Should -Be @("api://AzureADTokenExchange")
            Calls "ad app permission admin-consent --id server-app --output none" | Should -HaveCount 1

            $client = Body "rest --method PATCH --uri https://graph.microsoft.com/v1.0/applications/client-obj *"
            $client.isFallbackPublicClient | Should -BeTrue
            $client.publicClient.redirectUris | Should -Be @("http://localhost:8765/callback", "http://127.0.0.1:33418")
            $client.requiredResourceAccess[0].resourceAppId | Should -Be "server-app"
            $client.requiredResourceAccess[0].resourceAccess[0].id | Should -Be $server.api.oauth2PermissionScopes[0].id

            $create = @(Calls "containerapp create *")
            $create | Should -HaveCount 1
            $create[0] | Should -BeLike "*--image ghcr.io/yamadakou/ttdeploytool-mcp:latest --target-port 8080 --ingress external --min-replicas 1 --max-replicas 1 *"
            $create[0] | Should -BeLike "*--user-assigned /subscriptions/sub-1/resourceGroups/rg-mcp/providers/Microsoft.ManagedIdentity/userAssignedIdentities/tt-mcp-id *"
            $vars = EnvVars
            $vars.TT_MCP_HTTP__TENANT_ID | Should -Be "tenant-1"
            $vars.TT_MCP_HTTP__CLIENT_ID | Should -Be "server-app"
            $vars.TT_MCP_HTTP__PUBLIC_URL | Should -Be "https://tt-mcp.happy-sea.japaneast.azurecontainerapps.io/mcp"
            $vars.TT_MCP_HTTP__IDENTITY_MODES | Should -Be "server,user"
            $vars.TT_MCP_HTTP__SERVER_IDENTITY | Should -Be "managed-identity"
            $vars.TT_MCP_HTTP__USER_CLIENT_CREDENTIAL | Should -Be "managed-identity"
            $vars.AZURE_CLIENT_ID | Should -Be "mi-client"
            $vars.TT_MCP_HTTP__SERVER_ALLOWED_RESOURCE_GROUPS | Should -Be "/subscriptions/sub-1/resourceGroups/rg-a,/subscriptions/sub-1/resourceGroups/rg-b"
            $vars.TT_MCP_HTTP__SERVER_CAN_CREATE_RESOURCE_GROUP | Should -Be "false"

            Calls "role assignment create *" | Should -Be @(
                "role assignment create --assignee-object-id mi-principal --assignee-principal-type ServicePrincipal --role Contributor --scope /subscriptions/sub-1/resourceGroups/rg-a --output none",
                "role assignment create --assignee-object-id mi-principal --assignee-principal-type ServicePrincipal --role Contributor --scope /subscriptions/sub-1/resourceGroups/rg-b --output none")
            Calls "login *" | Should -HaveCount 0
        }

        It "カンマ区切りの文字列（pwsh -File で渡した場合）でも同じに扱う: <Modes>" -TestCases @(
            @{ Modes = "server,user"; Groups = "rg-a,rg-b"; Expected = "server,user" },
            @{ Modes = " User , SERVER ,user"; Groups = " rg-a , rg-b ,"; Expected = "user,server" }
        ) {
            & $script:Deploy -SubscriptionId sub-1 -ResourceGroup rg-mcp -IdentityModes $Modes -AllowedResourceGroups $Groups

            (EnvVars).TT_MCP_HTTP__IDENTITY_MODES | Should -Be $Expected
            (EnvVars).TT_MCP_HTTP__SERVER_ALLOWED_RESOURCE_GROUPS | Should -Be "/subscriptions/sub-1/resourceGroups/rg-a,/subscriptions/sub-1/resourceGroups/rg-b"
        }

        It "server 方式だけ（既定）: user 方式のための設定をしない" {
            & $script:Deploy -SubscriptionId sub-1 -ResourceGroup rg-mcp -AllowedResourceGroups rg-a -Location westus -Name team-mcp -ImageTag 1.2.3 -ClaudeCodeCallbackPort 9000

            $first = ($global:FakeAzState.Bodies | Where-Object { $_.Call -like "*applications/server-obj*" } | Select-Object -First 1).Body
            $first.requiredResourceAccess | Should -BeNullOrEmpty
            Calls "ad app federated-credential *" | Should -HaveCount 0
            Calls "ad app permission admin-consent *" | Should -HaveCount 0
            Calls "group create --name rg-mcp --location westus --output none" | Should -HaveCount 1
            Calls "ad app list --display-name TTDeployTool MCP (team-mcp) *" | Should -HaveCount 1
            @(Calls "containerapp create *")[0] | Should -BeLike "*--name team-mcp --environment team-mcp-env --image ghcr.io/yamadakou/ttdeploytool-mcp:1.2.3 *"
            (Body "*applications/client-obj*").publicClient.redirectUris | Should -Contain "http://localhost:9000/callback"
            $vars = EnvVars
            $vars.TT_MCP_HTTP__IDENTITY_MODES | Should -Be "server"
            $vars.TT_MCP_HTTP__PUBLIC_URL | Should -Be "https://team-mcp.happy-sea.japaneast.azurecontainerapps.io/mcp"
        }

        It "user 方式だけ: 許可するリソースグループとロールの割り当てを使わない" {
            & $script:Deploy -SubscriptionId sub-1 -ResourceGroup rg-mcp -IdentityModes user

            $vars = EnvVars
            $vars.TT_MCP_HTTP__IDENTITY_MODES | Should -Be "user"
            $vars.Keys | Should -Not -Contain "TT_MCP_HTTP__SERVER_ALLOWED_RESOURCE_GROUPS"
            $vars.Keys | Should -Not -Contain "TT_MCP_HTTP__SERVER_CAN_CREATE_RESOURCE_GROUP"
            Calls "role assignment *" | Should -HaveCount 0
            Calls "group create *" | Should -Be @("group create --name rg-mcp --location japaneast --output none")
            Calls "ad app federated-credential create *" | Should -HaveCount 1
        }

        It "-CanCreateResourceGroup: 作成を許す設定にし、必要な権限を警告する" {
            Mock Write-Warning { }

            & $script:Deploy -SubscriptionId sub-1 -ResourceGroup rg-mcp -AllowedResourceGroups rg-a -CanCreateResourceGroup

            (EnvVars).TT_MCP_HTTP__SERVER_CAN_CREATE_RESOURCE_GROUP | Should -Be "true"
            Should -Invoke Write-Warning -Times 1 -ParameterFilter { $Message -like "*サブスクリプションのスコープで Contributor*" }
        }

        It "サインインしていなければ az login を実行する" {
            $global:FakeAzState.LoggedIn = $false

            & $script:Deploy -SubscriptionId sub-1 -ResourceGroup rg-mcp -IdentityModes user

            Calls "login --output none" | Should -HaveCount 1
        }
    }

    Context "同じ引数での再実行" {
        BeforeEach { Set-ExistingState }

        It "作成済みのものは作らず、管理者が加えた設定を残す" {
            & $script:Deploy -SubscriptionId sub-1 -ResourceGroup rg-mcp -IdentityModes server, user -AllowedResourceGroups rg-a -ImageTag 2.0.0

            Calls "group create *" | Should -HaveCount 0
            Calls "ad app create *" | Should -HaveCount 0
            Calls "ad sp create *" | Should -HaveCount 0
            Calls "ad app federated-credential create *" | Should -HaveCount 0
            Calls "containerapp env create *" | Should -HaveCount 0
            Calls "containerapp create *" | Should -HaveCount 0
            Calls "role assignment create *" | Should -HaveCount 0
            $update = @(Calls "containerapp update *")
            $update | Should -HaveCount 1
            $update[0] | Should -BeLike "*--image ghcr.io/yamadakou/ttdeploytool-mcp:2.0.0 --min-replicas 1 --max-replicas 1 --set-env-vars *"

            # スコープとロールの ID は変えない
            $server = ServerApiBody
            $server.api.oauth2PermissionScopes[0].id | Should -Be "scope-1"
            $first = ($global:FakeAzState.Bodies | Where-Object { $_.Call -like "*applications/server-obj*" } | Select-Object -First 1).Body
            ($first.appRoles | ForEach-Object { $_.id }) | Should -Be @("role-server", "role-user")
            $first.displayName | Should -Be "TTDeployTool MCP (tt-mcp)"
            # 別のクライアント（VS Code など）の事前承認は残し、このスクリプトのクライアントだけを置き換える
            $preAuthorized = @($server.api.preAuthorizedApplications | ForEach-Object { "$($_.appId):$($_.delegatedPermissionIds -join '+')" })
            $preAuthorized | Should -Be @("vscode-app:scope-1", "client-app:scope-1")
            # 管理者が加えたアプリケーション ID の URI は残し、公開 URL を重複なしで加える
            ($global:FakeAzState.Bodies | Where-Object { $_.Call -like "*applications/server-obj*" -and $_.Body.identifierUris } | Select-Object -Last 1).Body.identifierUris |
                Should -Be @("api://server-app", "https://admin-added.example.com/mcp", "https://tt-mcp.happy-sea.japaneast.azurecontainerapps.io/mcp")
            # 管理者が加えたリダイレクト URI は残し、必要なものを重複なしで加える
            (Body "*applications/client-obj*").publicClient.redirectUris | Should -Be @(
                "https://admin-added/callback", "http://127.0.0.1:33418", "http://localhost:8765/callback")
        }

        It "以前の名前（Timetracker MCP …・Timetracker.Deploy.*）で作ったアプリ登録を引き継ぎ、表示名とロールの値を新しくする" {
            $global:FakeAzState.LegacyNames = $true
            $global:FakeAzState.ServerApp.appRoles = @(
                @{ id = "role-server"; value = "Timetracker.Deploy.Server" }, @{ id = "role-user"; value = "Timetracker.Deploy.User" })

            & $script:Deploy -SubscriptionId sub-1 -ResourceGroup rg-mcp -IdentityModes server, user -AllowedResourceGroups rg-a

            Calls "ad app create *" | Should -HaveCount 0
            $first = ($global:FakeAzState.Bodies | Where-Object { $_.Call -like "*applications/server-obj*" } | Select-Object -First 1).Body
            $first.displayName | Should -Be "TTDeployTool MCP (tt-mcp)"
            # 利用者への割り当てが外れないよう、ロールの ID はそのままにする
            ($first.appRoles | ForEach-Object { "$($_.id):$($_.value)" }) | Should -Be @("role-server:TTDeployTool.Deploy.Server", "role-user:TTDeployTool.Deploy.User")
            (Body "*applications/client-obj*").displayName | Should -Be "TTDeployTool MCP client (tt-mcp)"
        }

        It "場所の違う既存のリソースグループも、作り直そうとしない" {
            & $script:Deploy -SubscriptionId sub-1 -ResourceGroup rg-mcp -AllowedResourceGroups rg-a -Location westus

            Calls "group create *" | Should -HaveCount 0
        }
    }

    Context "誤りと失敗" {
        It "無効な方式: <Modes>" -TestCases @(@{ Modes = "admin" }, @{ Modes = "server,admin" }, @{ Modes = "," }) {
            { & $script:Deploy -SubscriptionId sub-1 -ResourceGroup rg-mcp -IdentityModes $Modes -AllowedResourceGroups rg-a } |
                Should -Throw "-IdentityModes には server、user、または server,user を指定してください。"
            $global:FakeAzState.Calls | Should -HaveCount 0
        }

        It "server 方式で、許可するリソースグループが無い: <Groups>" -TestCases @(@{ Groups = @() }, @{ Groups = @(" , ") }) {
            { & $script:Deploy -SubscriptionId sub-1 -ResourceGroup rg-mcp -AllowedResourceGroups $Groups } |
                Should -Throw "server 方式を使う場合は、-AllowedResourceGroups に*"
            $global:FakeAzState.Calls | Should -HaveCount 0
        }

        It "サインインに失敗したら止める" {
            $global:FakeAzState.LoggedIn = $false
            $global:FakeAzState.FailOn = "login"

            { & $script:Deploy -SubscriptionId sub-1 -ResourceGroup rg-mcp -IdentityModes user } | Should -Throw "Azure CLI へのサインインに失敗しました。"
        }

        It "az が失敗したら、その後の手順を行わずに止める" {
            $global:FakeAzState.FailOn = "identity create"

            { & $script:Deploy -SubscriptionId sub-1 -ResourceGroup rg-mcp -IdentityModes user } | Should -Throw "az identity create * が失敗しました。"
            Calls "ad app *" | Should -HaveCount 0
        }

        It "管理者の同意に失敗しても、警告して続ける" {
            $global:FakeAzState.ConsentFails = $true
            Mock Write-Warning { }

            & $script:Deploy -SubscriptionId sub-1 -ResourceGroup rg-mcp -IdentityModes user

            Should -Invoke Write-Warning -Times 1 -ParameterFilter { $Message -like "管理者の同意に失敗しました。実行したアカウントの分だけ同意しました。*" }
            Calls "containerapp create *" | Should -HaveCount 1
            Calls "rest --method GET --uri *oauth2PermissionGrants?`$filter=clientId eq 'sp-id' and principalId eq 'me-id'*" | Should -HaveCount 1
            $grant = Body "rest --method POST --uri https://graph.microsoft.com/v1.0/oauth2PermissionGrants *"
            $grant.clientId | Should -Be "sp-id"
            $grant.consentType | Should -Be "Principal"
            $grant.principalId | Should -Be "me-id"
            $grant.resourceId | Should -Be "arm-sp"
            $grant.scope | Should -Be "user_impersonation"
        }

        It "管理者の同意に失敗し、実行したアカウントの分の同意が既にあれば、作り直さない" {
            $global:FakeAzState.ConsentFails = $true
            $global:FakeAzState.SelfGrants = @(
                @{ resourceId = "other-sp"; scope = "user_impersonation" },
                @{ resourceId = "arm-sp"; scope = "openid user_impersonation" })
            Mock Write-Warning { }

            & $script:Deploy -SubscriptionId sub-1 -ResourceGroup rg-mcp -IdentityModes user

            Should -Invoke Write-Warning -Times 1 -ParameterFilter { $Message -like "管理者の同意に失敗しました。実行したアカウントの分だけ同意しました。*" }
            Calls "rest --method POST --uri *oauth2PermissionGrants*" | Should -HaveCount 0
        }

        It "管理者の同意に失敗し、実行したアカウントの分の同意も作れなくても、警告して続ける" {
            $global:FakeAzState.ConsentFails = $true
            $global:FakeAzState.FailOn = "rest --method POST --uri https://graph.microsoft.com/v1.0/oauth2PermissionGrants"
            Mock Write-Warning { }

            & $script:Deploy -SubscriptionId sub-1 -ResourceGroup rg-mcp -IdentityModes user

            Should -Invoke Write-Warning -Times 1 -ParameterFilter { $Message -like "管理者の同意に失敗しました。実行したアカウントの分の同意も作れませんでした。*" }
            Calls "containerapp create *" | Should -HaveCount 1
        }
    }
}

Describe "assign-mcp-role.ps1" {
    BeforeEach {
        $global:FakeAzState = New-State
        Set-ExistingState
        Mock az { Invoke-FakeAz $args }
        Mock Write-Host { }
    }

    It "ロールを割り当てる（-Role の大文字小文字を問わない）: <Role>" -TestCases @(
        @{ Role = "Server"; RoleId = "role-server" }, @{ Role = "server"; RoleId = "role-server" },
        @{ Role = "User"; RoleId = "role-user" }, @{ Role = "USER"; RoleId = "role-user" }
    ) {
        & $script:Assign -Role $Role -UserPrincipalName alice@example.com

        $body = Body "rest --method POST *"
        $body.principalId | Should -Be "user-id"
        $body.resourceId | Should -Be "sp-id"
        $body.appRoleId | Should -Be $RoleId
        Calls "rest --method POST --uri https://graph.microsoft.com/v1.0/servicePrincipals/sp-id/appRoleAssignedTo *" | Should -HaveCount 1
    }

    It "グループに割り当てる" {
        & $script:Assign -Name tt-mcp -Role User -GroupObjectId group-1

        (Body "rest --method POST *").principalId | Should -Be "group-1"
        Calls "ad user show *" | Should -HaveCount 0
    }

    It "割り当て済みなら（後のページにあっても）割り当てない" {
        $global:FakeAzState.AssignmentPages = @(
            @(@{ principalId = "someone-else"; appRoleId = "role-server" }),
            @(@{ principalId = "user-id"; appRoleId = "role-user" }),
            @(@{ principalId = "user-id"; appRoleId = "role-server" }))

        & $script:Assign -Role Server -UserPrincipalName alice@example.com

        Calls "rest --method GET *" | Should -HaveCount 3
        Calls "rest --method POST *" | Should -HaveCount 0
    }

    It "別のロールだけが割り当て済みなら、割り当てる" {
        $global:FakeAzState.AssignmentPages = @(@(@{ principalId = "user-id"; appRoleId = "role-user" }))

        & $script:Assign -Role Server -UserPrincipalName alice@example.com

        (Body "rest --method POST *").appRoleId | Should -Be "role-server"
    }

    It "利用者とグループのどちらか一方だけを指定する: <Case>" -TestCases @(
        @{ Case = "どちらも無い"; Upn = $null; Group = $null }, @{ Case = "両方"; Upn = "alice@example.com"; Group = "group-1" }
    ) {
        { & $script:Assign -Role Server -UserPrincipalName $Upn -GroupObjectId $Group } |
            Should -Throw "-UserPrincipalName か -GroupObjectId のどちらか一方を指定してください。"
        $global:FakeAzState.Calls | Should -HaveCount 0
    }

    It "アプリ登録が無ければ、先に配置するよう案内する" {
        $global:FakeAzState.ServerApp = $null

        { & $script:Assign -Role Server -UserPrincipalName alice@example.com } | Should -Throw "アプリ登録「TTDeployTool MCP (tt-mcp)」が見つかりません。*"
    }

    It "az が失敗したら止める" {
        $global:FakeAzState.FailOn = "ad user show"

        { & $script:Assign -Role Server -UserPrincipalName nobody@example.com } | Should -Throw "az ad user show * が失敗しました。"
        Calls "rest --method POST *" | Should -HaveCount 0
    }
}
