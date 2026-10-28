using Azure.Core;
using System.Text.Json;
using Azure;
using Azure.Identity;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Moq;
using TTDeployTool.Controller.Mcp.Tools;
using TTDeployTool.Domain.Deployment;
using TTDeployTool.Infrastructure.Azure;
using TTDeployTool.Controller.Mcp.Tests.Http;
using Xunit;

namespace TTDeployTool.Controller.Mcp.Tests;

public sealed class DeploymentToolsTests : IDisposable
{
    private const string DbPassword = "DbPassw0rd!x";
    private const string TrackerPassword = "TrackerPass!y";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ttmcp-tests", Guid.NewGuid().ToString("N"));
    private readonly Mock<ISubscriptionDirectory> _directory = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private DeploymentTools Tools(McpServerSettings? settings = null, Func<TokenCredential, ISubscriptionDirectory>? directory = null, IAzureAccessPolicy? policy = null)
    {
        settings ??= new McpServerSettings();
        return new(settings, policy ?? new UnrestrictedAccessPolicy(settings), directory ?? (_ => _directory.Object), NullLogger<DeploymentTools>.Instance);
    }

    private static ComposeFileTools FileTools(McpServerSettings? settings = null) =>
        new(settings ?? new McpServerSettings(), NullLogger<ComposeFileTools>.Instance);

    private static string Json(object value) => JsonSerializer.Serialize(value, McpServerHost.JsonOptions);

    // ---- get_deployment_options

    [Fact]
    public void GetDeploymentOptions_Should_Return_Defaults_Rules_And_Constraints()
    {
        var result = Tools().GetDeploymentOptions();

        result.DbTypes.Should().Equal("postgresql", "sqlserver");
        result.DefaultTtTags.Should().Equal(new Dictionary<string, string>
        {
            ["postgresql"] = Defaults.TimetrackerTagPostgres, ["sqlserver"] = Defaults.TimetrackerTagSqlServer,
        });
        result.Defaults.Should().Be(new DefaultValues("japaneast", "timetracker", "postgresql", "timetracker", 1.5, 3.0, 1.0, 2.0, 0.5, 1.0));
        result.AppNameRule.Should().Be(new AppNameRule(2, 26, @"^[a-z]([a-z0-9-]*[a-z0-9])?$", true));
        result.ResourceSuffixes.Should().Equal("-env", "-db", "-redis", "-tt");
        result.Constraints.Should().HaveCount(5);
        result.Constraints.Should().Contain(c => c.Contains("expires_on") && c.Contains(DeploymentTools.ExpiryNote));
        result.TagListUrl.Should().Be("https://hub.docker.com/r/densocreate/timetracker/tags");
        result.Server.Should().Be(new ServerStatus("stdio", "azure-cli", new PasswordEnvStatus(false, false), false, false, null));
    }

    [Fact]
    public void GetDeploymentOptions_Should_Tell_Which_Settings_Exist_Without_Values()
    {
        var settings = new McpServerSettings
        {
            AuthMode = "sp-env", DefaultSubscription = "sub", DefaultResourceGroup = "rg", DbPassword = DbPassword, OutputRoot = _root,
        };

        var result = Tools(settings).GetDeploymentOptions();

        result.Server.Should().Be(new ServerStatus("stdio", "sp-env", new PasswordEnvStatus(true, false), true, true, _root));
        Json(result).Should().NotContain(DbPassword);
    }

    [Fact]
    public void Results_Should_Use_Snake_Case_And_Readable_Japanese()
    {
        var json = Json(Tools().GetDeploymentOptions());

        json.Should().Contain("\"default_tt_tags\"").And.Contain("\"tt_memory_gi\"").And.Contain("\"password_env\"").And.Contain("検証環境向け");
    }

    // ---- validate_deployment

    [Fact]
    public void ValidateDeployment_Should_Accept_Valid_Azure_Settings_And_Mask_Passwords()
    {
        var result = Tools(new McpServerSettings { TrackerPassword = TrackerPassword })
            .ValidateDeployment(subscription: "sub", resource_group: "rg", app_name: "Demo", db_password: DbPassword);

        result.Valid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
        result.Warnings.Should().BeEmpty();
        result.Resolved.Should().Be(new ResolvedSettings(
            "sub", "rg", "japaneast", "demo", "postgresql", "timetracker", Defaults.TimetrackerTagPostgres,
            "********（引数）", "********（環境変数）", 1.5, 3.0, 1.0, 2.0, 0.5, 1.0));
        result.ResourceNames.Should().Be(new ResourceNames("demo-env", "demo-tt", "demo-db", "demo-redis"));
        Json(result).Should().NotContain(DbPassword).And.NotContain(TrackerPassword);
    }

    [Theory]
    [InlineData("2026-12-01", true)]
    [InlineData(" none ", true)]
    [InlineData("12/01", false)]
    public void ValidateDeployment_Should_Check_The_Expiry_Format(string expiresOn, bool valid)
    {
        var settings = new McpServerSettings { DbPassword = DbPassword, TrackerPassword = TrackerPassword };

        var result = Tools(settings).ValidateDeployment(subscription: "sub", resource_group: "rg", expires_on: expiresOn);

        result.Valid.Should().Be(valid);
        if (!valid)
            result.Errors.Should().Equal("expires-on には yyyy-MM-dd の日付か none を指定してください: 12/01");
    }

    [Fact]
    public void ValidateDeployment_Should_Check_The_Group()
    {
        var settings = new McpServerSettings { DbPassword = DbPassword, TrackerPassword = TrackerPassword };

        Tools(settings).ValidateDeployment(subscription: "sub", resource_group: "rg", group: " 営業 ").Resolved.Group.Should().Be("営業");
        Tools(settings).ValidateDeployment(subscription: "sub", resource_group: "rg", group: new string('x', 65)).Errors
            .Should().Equal("group（グループ名）は 64 文字までにしてください（65 文字）。");
    }

    [Fact]
    public void ValidateDeployment_Should_List_All_Errors_Warnings_And_Password_Hints()
    {
        var result = Tools().ValidateDeployment(db_type: "sqlserver", tt_memory_gi: 2, tt_cpu: 0);

        result.Valid.Should().BeFalse();
        result.Errors.Should().Equal(
            "subscription は必須です。",
            "resource-group は必須です。",
            "tt-cpu には正の数値を指定してください: 0",
            "tracker-password が不足または短すぎます。",
            "db-password が不足または短すぎます。",
            "db_password が未設定です。サーバーの環境変数 TT_MCP_DB_PASSWORD で設定するか（推奨。値が会話に残らない）、引数で指定してください。",
            "tracker_password が未設定です。サーバーの環境変数 TT_MCP_TRACKER_PASSWORD で設定するか（推奨。値が会話に残らない）、引数で指定してください。");
        result.Warnings.Should().HaveCount(2);
        (result.Resolved.DbPassword, result.Resolved.TrackerPassword).Should().Be(("（未設定）", "（未設定）"));
    }

    [Theory]
    [InlineData("compose")]
    [InlineData("COMPOSE")]
    public void ValidateDeployment_Should_Not_Require_Azure_Settings_For_Compose(string target)
    {
        var settings = new McpServerSettings { DbPassword = DbPassword, TrackerPassword = TrackerPassword };

        var result = Tools(settings).ValidateDeployment(target: target, db_type: "sqlserver");

        result.Valid.Should().BeTrue();
        // ローカルではサンプル DB を作れるため、SQL Server の警告は出ない
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void ValidateDeployment_Should_Return_The_Requested_Persistence_And_Warn_For_Compose()
    {
        var settings = new McpServerSettings { DbPassword = DbPassword, TrackerPassword = TrackerPassword };

        var result = Tools(settings).ValidateDeployment(target: "compose", persist_db: true);

        result.Valid.Should().BeTrue();
        result.Resolved.PersistDb.Should().BeTrue();
        result.Warnings.Should().ContainSingle().Which.Should().StartWith("persist-db は Azure 向けの設定のため");
    }

    [Fact]
    public void ValidateDeployment_Should_Reject_An_Unknown_Target()
    {
        var settings = new McpServerSettings
        {
            DefaultSubscription = "sub", DefaultResourceGroup = "rg", DbPassword = DbPassword, TrackerPassword = TrackerPassword,
        };

        var result = Tools(settings).ValidateDeployment(target: "aks");

        result.Errors.Should().Equal("target 'aks' は無効です。azure または compose を指定してください。");
    }

    // ---- generate_compose

    private static ComposeFileTools ComposeTools(string? outputRoot = null) =>
        FileTools(new McpServerSettings { DbPassword = DbPassword, TrackerPassword = TrackerPassword, OutputRoot = outputRoot });

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public void GenerateCompose_Should_Write_The_Same_Files_As_The_Cli(string dbType)
    {
        var dir = Path.Combine(_root, "new", "dir");

        var result = ComposeTools().GenerateCompose(dir, db_type: dbType, app_name: "Demo", location: "eastus",
            tt_cpu: 1, tt_memory_gi: 3, db_cpu: 1, db_memory_gi: 2, redis_cpu: 0.5, redis_memory_gi: 1, db_name: "db", tt_tag: null);

        var expected = DeployOptionsResolver.Resolve(new DeployInput
        {
            AppName = "Demo", DbType = dbType, DbName = "db", DbPassword = DbPassword, TrackerPassword = TrackerPassword,
        });
        result.ComposePath.Should().Be(Path.Combine(dir, "docker-compose.yml"));
        result.EnvPath.Should().Be(Path.Combine(dir, ".env"));
        File.ReadAllText(result.ComposePath).Should().Be(ComposeGenerator.GenerateCompose(expected)).And.Be(result.ComposeContent);
        File.ReadAllText(result.EnvPath).Should().Be(ComposeGenerator.GenerateEnv(expected));
        result.Warnings.Should().BeEmpty();
        result.NextSteps.Should().HaveCount(3).And.Contain(s => s.Contains(dir) && s.Contains("docker compose up -d"));
        // .env（パスワード）の内容は返さない
        Json(result).Should().NotContain(DbPassword).And.NotContain(TrackerPassword);
    }

    [Fact]
    public void GenerateCompose_Should_Use_Password_Arguments_And_Return_Warnings()
    {
        var dir = Path.Combine(_root, "args");

        var result = FileTools().GenerateCompose(dir, db_password: "ArgDbPass1!", tracker_password: "ArgTracker1!", tt_memory_gi: 2);

        File.ReadAllText(result.EnvPath).Should().Contain("DB_PASSWORD=\"ArgDbPass1!\"").And.Contain("TIMETRACKER_PASSWORD=\"ArgTracker1!\"");
        result.Warnings.Should().ContainSingle().Which.Should().Contain("OutOfMemory");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("relative/dir")]
    public void GenerateCompose_Should_Require_An_Absolute_Directory(string dir)
    {
        var act = () => ComposeTools().GenerateCompose(dir);

        act.Should().Throw<McpException>().WithMessage($"output_dir には出力先ディレクトリの絶対パスを指定してください: '{dir}'");
    }

    [Fact]
    public void GenerateCompose_Should_Not_Write_When_Settings_Are_Invalid()
    {
        var dir = Path.Combine(_root, "invalid");

        var act = () => FileTools().GenerateCompose(dir, db_type: "mysql");

        act.Should().Throw<McpException>().WithMessage("設定に誤りがあるため、ファイルを生成しませんでした:*db-type*db_password が未設定です*tracker_password が未設定です*");
        Directory.Exists(dir).Should().BeFalse();
    }

    [Fact]
    public void GenerateCompose_Should_Not_Overwrite_Existing_Files_Unless_Requested()
    {
        var composeOnly = Path.Combine(_root, "compose-only");
        var envOnly = Path.Combine(_root, "env-only");
        Directory.CreateDirectory(composeOnly);
        Directory.CreateDirectory(envOnly);
        File.WriteAllText(Path.Combine(composeOnly, "docker-compose.yml"), "old");
        File.WriteAllText(Path.Combine(envOnly, ".env"), "OLD=1");

        var actCompose = () => ComposeTools().GenerateCompose(composeOnly);
        var actEnv = () => ComposeTools().GenerateCompose(envOnly);

        actCompose.Should().Throw<McpException>().WithMessage("*既にあるため、上書きしませんでした*overwrite=true*");
        actEnv.Should().Throw<McpException>();
        File.ReadAllText(Path.Combine(envOnly, ".env")).Should().Be("OLD=1");

        ComposeTools().GenerateCompose(envOnly, overwrite: true);
        File.ReadAllText(Path.Combine(envOnly, ".env")).Should().Contain(DbPassword);
    }

    [Fact]
    public void GenerateCompose_Should_Report_A_Write_Failure()
    {
        // 出力先と同じ名前のファイルがあると、ディレクトリを作れない
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "a-file");
        File.WriteAllText(file, "");

        var act = () => ComposeTools().GenerateCompose(file);

        act.Should().Throw<McpException>().WithMessage($"{file} に書き込めませんでした: *");
    }

    [Fact]
    public void GenerateCompose_Should_Allow_Only_Directories_Under_The_Output_Root()
    {
        var root = Path.Combine(_root, "root");
        var tools = ComposeTools(root);

        tools.GenerateCompose(root).ComposePath.Should().Be(Path.Combine(root, "docker-compose.yml"));
        tools.GenerateCompose(Path.Combine(root, "sub") + Path.DirectorySeparatorChar).ComposePath.Should().StartWith(Path.Combine(root, "sub"));

        foreach (var outside in new[] { Path.Combine(root, "..", "elsewhere"), root + "-sibling" })
        {
            var act = () => tools.GenerateCompose(outside);
            act.Should().Throw<McpException>().WithMessage($"output_dir は {root} の配下を指定してください*");
        }
        Directory.Exists(Path.Combine(_root, "elsewhere")).Should().BeFalse();
    }

    [Fact]
    public void GenerateCompose_Should_Reject_Links_That_Lead_Outside_The_Output_Root()
    {
        var root = Path.Combine(_root, "root");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        var linkedDir = Path.Combine(root, "linked");
        var realDir = Path.Combine(root, "real");
        Directory.CreateDirectory(realDir);
        var linkedEnv = Path.Combine(realDir, ".env");
        try
        {
            Directory.CreateSymbolicLink(linkedDir, outside);
            File.CreateSymbolicLink(linkedEnv, Path.Combine(outside, "stolen.env"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.Skip($"シンボリックリンクを作れない環境です: {ex.Message}");
        }
        var tools = ComposeTools(root);

        var throughDir = () => tools.GenerateCompose(Path.Combine(linkedDir, "sub"));
        var throughFile = () => tools.GenerateCompose(realDir, overwrite: true);

        throughDir.Should().Throw<McpException>().WithMessage($"{linkedDir} はシンボリックリンク*");
        throughFile.Should().Throw<McpException>().WithMessage($"{linkedEnv} はシンボリックリンク*");
        Directory.EnumerateFileSystemEntries(outside).Should().BeEmpty();
        File.Exists(Path.Combine(realDir, "docker-compose.yml")).Should().BeFalse();
    }

    [Fact]
    public void GenerateCompose_Should_Allow_Everything_Under_A_File_System_Root()
    {
        var fileSystemRoot = Path.GetPathRoot(_root)!;

        var result = ComposeTools(fileSystemRoot).GenerateCompose(Path.Combine(_root, "under-root"));

        File.Exists(result.ComposePath).Should().BeTrue();
    }

    // ---- list_subscriptions

    [Fact]
    public async Task ListSubscriptions_Should_Return_Subscriptions_And_Auth_Mode()
    {
        _directory.Setup(d => d.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new SubscriptionInfo("aaa", "開発", "Enabled", "tenant"),
            new SubscriptionInfo("bbb", "検証", null, null),
        ]);

        var result = await Tools().ListSubscriptions(cancellationToken: TestContext.Current.CancellationToken);

        result.AuthMode.Should().Be("azure-cli");
        result.Subscriptions.Should().Equal(new SubscriptionItem("aaa", "開発", "Enabled", "tenant"), new SubscriptionItem("bbb", "検証", null, null));
    }

    [Theory]
    [InlineData("azure-cli", "az login")]
    [InlineData("default", "DefaultAzureCredential")]
    [InlineData("device-code", "stderr")]
    [InlineData("managed-identity", "マネージド ID")]
    // sp-env は環境変数が無いと資格情報を作る段階で失敗するため、案内は UnrestrictedAccessPolicy のテストで確かめる
    public async Task ListSubscriptions_Should_Explain_How_To_Sign_In(string authMode, string hint)
    {
        _directory.Setup(d => d.ListAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new CredentialUnavailableException("no credential"));

        var act = () => Tools(new McpServerSettings { AuthMode = authMode }).ListSubscriptions(cancellationToken: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<McpException>()).WithMessage($"Azure の認証に失敗しました（認証モード: {authMode}）。*{hint}*");
    }

    [Fact]
    public async Task ListSubscriptions_Should_Report_Azure_Errors()
    {
        _directory.Setup(d => d.ListAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(403, "forbidden", "AuthorizationFailed", null));

        var act = () => Tools().ListSubscriptions(cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<McpException>().WithMessage("Azure がエラーを返しました（403 AuthorizationFailed）: forbidden*");
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(ArgumentException))]
    public async Task ListSubscriptions_Should_Report_Credential_Creation_Errors(Type exceptionType)
    {
        var tools = Tools(directory: _ => throw (Exception)Activator.CreateInstance(exceptionType, "sp-env モードで必要な環境変数が不足しています。")!);

        var act = () => tools.ListSubscriptions(cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<McpException>().WithMessage("sp-env モードで必要な環境変数が不足しています。");
    }

    // ---- HTTP（server 方式）

    [Fact]
    public void GetDeploymentOptions_Should_Describe_The_Http_Server()
    {
        var settings = HttpTestSupport.Settings(canCreate: true);

        var result = Tools(settings, policy: HttpTestSupport.Policy(settings)).GetDeploymentOptions();

        result.Server.Should().BeEquivalentTo(new ServerStatus(
            "http", "server", new PasswordEnvStatus(false, false), true, true, null, [HttpTestSupport.AllowedGroup], true, ["server"], ["server"]));
    }

    [Fact]
    public void ValidateDeployment_Should_Ask_For_Password_Arguments_Over_Http()
    {
        var settings = HttpTestSupport.Settings();

        var result = Tools(settings, policy: HttpTestSupport.Policy(settings)).ValidateDeployment();

        result.Errors.Should().Contain(e => e.Contains("HTTP のサーバーでは、パスワードの環境変数は使えません"));
    }

    [Fact]
    public async Task ListSubscriptions_Should_Require_The_Server_Role()
    {
        var settings = HttpTestSupport.Settings();
        _directory.Setup(d => d.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var denied = () => Tools(settings, policy: HttpTestSupport.Policy(settings, withRole: false)).ListSubscriptions(cancellationToken: TestContext.Current.CancellationToken);
        var allowed = await Tools(settings, policy: HttpTestSupport.Policy(settings)).ListSubscriptions(cancellationToken: TestContext.Current.CancellationToken);

        await denied.Should().ThrowAsync<McpException>().WithMessage("*TTDeployTool.Deploy.Server*");
        allowed.AuthMode.Should().Be("server");
    }

    [Theory]
    [InlineData("postgresql", "DB_PASSWORD に任意のパスワードを設定する")]
    [InlineData("sqlserver", "SQL Server のパスワード要件")]
    public void GenerateComposeContent_Should_Return_The_Files_With_Password_Placeholders(string dbType, string passwordRule)
    {
        var result = new ComposeContentTools(HttpTestSupport.Settings()).GenerateCompose(app_name: "Demo", db_type: dbType, db_name: "db");

        var expected = DeployOptionsResolver.Resolve(new DeployInput
        {
            AppName = "Demo", DbType = dbType, DbName = "db",
            DbPassword = ComposeContentTools.DbPasswordPlaceholder, TrackerPassword = ComposeContentTools.TrackerPasswordPlaceholder,
        });
        result.ComposeFileName.Should().Be("docker-compose.yml");
        result.EnvFileName.Should().Be(".env");
        result.ComposeContent.Should().Be(ComposeGenerator.GenerateCompose(expected));
        result.EnvContent.Should().Be(ComposeGenerator.GenerateEnv(expected))
            .And.Contain("DB_PASSWORD=\"<DB パスワードを設定>\"").And.Contain("TIMETRACKER_PASSWORD=\"<Timetracker のパスワードを設定>\"");
        result.Warnings.Should().BeEmpty();
        result.NextSteps.Should().Contain(s => s.Contains(passwordRule));
        Json(result).Should().NotContain("Placeholder-Passw0rd");
    }

    [Fact]
    public void GenerateComposeContent_Should_Reject_Invalid_Settings()
    {
        var act = () => new ComposeContentTools(HttpTestSupport.Settings()).GenerateCompose(db_type: "mysql");

        act.Should().Throw<McpException>().WithMessage("設定に誤りがあるため、生成しませんでした:*");
    }

    // ---- HTTP（user 方式。詳細設計 Phase 4b）

    [Fact]
    public async Task ListSubscriptions_Should_List_With_The_Callers_Own_Permissions_In_The_User_Mode()
    {
        var settings = HttpTestSupport.Settings(modes: "server,user");
        var credentials = new List<TokenCredential>();
        _directory.Setup(d => d.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var tools = Tools(settings, credential => { credentials.Add(credential); return _directory.Object; },
            HttpTestSupport.PolicyWithToken(settings, roles: HttpAccessPolicy.UserRole));

        var result = await tools.ListSubscriptions(cancellationToken: TestContext.Current.CancellationToken);

        result.AuthMode.Should().Be("user");
        credentials.Should().ContainSingle().Which.Should().BeOfType<OnBehalfOfCredential>();
    }

    [Fact]
    public void GetDeploymentOptions_Should_Show_The_Modes_The_Caller_Can_Use()
    {
        var both = HttpTestSupport.Settings(modes: "server,user");
        var userOnly = HttpTestSupport.Settings(modes: "user");

        var server = Tools(both, policy: HttpTestSupport.PolicyWithToken(both, roles: HttpAccessPolicy.UserRole)).GetDeploymentOptions().Server;
        var user = Tools(userOnly, policy: HttpTestSupport.PolicyWithToken(userOnly, roles: HttpAccessPolicy.UserRole)).GetDeploymentOptions().Server;

        server.AuthMode.Should().Be("user");
        server.IdentityModes.Should().Equal("server", "user");
        server.CallerIdentityModes.Should().Equal("user");
        server.AllowedResourceGroups.Should().Equal(HttpTestSupport.AllowedGroup);
        // server 方式が無効なら、server 方式の範囲は示さない
        user.AllowedResourceGroups.Should().BeNull();
        user.CanCreateResourceGroup.Should().BeNull();
        user.IdentityModes.Should().Equal("user");
    }
}
