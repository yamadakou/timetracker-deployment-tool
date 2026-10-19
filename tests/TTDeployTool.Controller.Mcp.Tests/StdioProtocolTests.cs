using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace TTDeployTool.Controller.Mcp.Tests;

/// <summary>
/// ビルドした ttdeploytool-mcp を子プロセスとして起動し、MCP クライアント（公式 SDK）から stdio で接続して確かめる。
/// stdout に JSON-RPC 以外が 1 行でも出るとプロトコルが壊れるため、接続とツールの呼び出しが成り立つことでも確認になる。
/// </summary>
public sealed class StdioProtocolTests : IAsyncLifetime
{
    private const string DbPassword = "DbPassw0rd!x";
    private const string TrackerPassword = "TrackerPass!y";

    private static readonly string ServerDll = Path.Combine(AppContext.BaseDirectory, "ttdeploytool-mcp.dll");

    private readonly string _outputRoot = Path.Combine(Path.GetTempPath(), "ttmcp-stdio", Guid.NewGuid().ToString("N"));
    private McpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "ttdeploytool-mcp",
            Command = "dotnet",
            Arguments = [ServerDll],
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["TT_MCP_DB_PASSWORD"] = DbPassword,
                ["TT_MCP_TRACKER_PASSWORD"] = TrackerPassword,
                ["TT_MCP_OUTPUT_ROOT"] = _outputRoot,
                ["TT_MCP_VERBOSE"] = "true",
            },
        });
        _client = await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        if (Directory.Exists(_outputRoot))
            Directory.Delete(_outputRoot, recursive: true);
    }

    private static string Text(CallToolResult result) => result.Content.OfType<TextContentBlock>().Single().Text;

    [Fact]
    public void Initialize_Should_Return_Server_Info_And_Instructions()
    {
        _client.ServerInfo.Name.Should().Be("ttdeploytool");
        _client.ServerInstructions.Should().Contain("get_deployment_options").And.Contain("検証環境向け");
    }

    [Fact]
    public async Task ListTools_Should_Return_The_Tools_With_Snake_Case_Arguments_And_Output_Schemas()
    {
        var tools = (await _client.ListToolsAsync(cancellationToken: Ct)).ToDictionary(t => t.Name, t => t.ProtocolTool);

        tools.Keys.Should().BeEquivalentTo(
            "get_deployment_options", "validate_deployment", "generate_compose", "list_subscriptions",
            "plan_deployment", "start_deployment", "get_job_status", "list_jobs", "get_deployment_info", "plan_deletion", "start_deletion");
        var validateArgs = tools["validate_deployment"].InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name);
        validateArgs.Should().Contain(["target", "resource_group", "app_name", "tt_memory_gi", "redis_cpu"]);
        tools["generate_compose"].InputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Should().Equal("output_dir");
        tools["validate_deployment"].OutputSchema!.Value.GetProperty("properties").TryGetProperty("resource_names", out _).Should().BeTrue();
        tools["list_subscriptions"].Annotations!.OpenWorldHint.Should().BeTrue();
        tools["start_deployment"].InputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Should().Equal("plan_id");
        tools["start_deployment"].Annotations!.DestructiveHint.Should().BeTrue();
        tools["start_deletion"].InputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo("plan_id", "confirm_app_name");
    }

    [Fact]
    public async Task Resources_Should_List_And_Read_The_Documents_And_Reject_Invalid_Deployment_Uris()
    {
        var resources = await _client.ListResourcesAsync(cancellationToken: Ct);
        var templates = await _client.ListResourceTemplatesAsync(cancellationToken: Ct);
        var constraints = await _client.ReadResourceAsync("ttdeploytool://docs/constraints", cancellationToken: Ct);
        var invalid = () => _client.ReadResourceAsync("ttdeploytool://deployments/sub/rg/Bad--Name", cancellationToken: Ct);

        resources.Select(r => r.Uri).Should().BeEquivalentTo("ttdeploytool://docs/constraints", "ttdeploytool://docs/rbac");
        templates.Select(t => t.UriTemplate).Should().Equal("ttdeploytool://deployments/{subscription}/{resource_group}/{app_name}");
        var text = constraints.Contents.OfType<TextResourceContents>().Single();
        text.MimeType.Should().Be("text/markdown");
        text.Text.Should().Contain("検証環境");
        // アプリ名の誤りは Azure を読まずにエラーになる
        await invalid.Should().ThrowAsync<McpException>();
    }

    [Fact]
    public async Task Prompts_Should_List_And_Render_With_Arguments()
    {
        var prompts = await _client.ListPromptsAsync(cancellationToken: Ct);
        var delete = await _client.GetPromptAsync("delete-timetracker", new Dictionary<string, object?> { ["app_name"] = "demo" }, cancellationToken: Ct);

        prompts.Select(p => p.Name).Should().BeEquivalentTo("deploy-timetracker", "local-dry-run", "delete-timetracker");
        prompts.Single(p => p.Name == "delete-timetracker").ProtocolPrompt.Arguments!.Select(a => a.Name).Should().Equal("app_name");
        var message = delete.Messages.Should().ContainSingle().Subject;
        message.Role.Should().Be(Role.User);
        ((TextContentBlock)message.Content).Text.Should().Contain("「demo」").And.Contain("confirm_app_name");
    }

    [Fact]
    public async Task ValidateDeployment_Should_Return_Structured_And_Text_Results_Without_Passwords()
    {
        var result = await _client.CallToolAsync("validate_deployment",
            new Dictionary<string, object?> { ["target"] = "compose", ["db_type"] = "sqlserver", ["app_name"] = "Demo" }, cancellationToken: Ct);

        result.IsError.Should().NotBe(true);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("valid").GetBoolean().Should().BeTrue();
        structured.GetProperty("resolved").GetProperty("db_password").GetString().Should().Be("********（環境変数）");
        structured.GetProperty("resource_names").GetProperty("tt").GetString().Should().Be("demo-tt");
        // structuredContent を使えないクライアントのために、同じ内容をテキストでも返す
        JsonDocument.Parse(Text(result)).RootElement.GetProperty("valid").GetBoolean().Should().BeTrue();
        Text(result).Should().NotContain(DbPassword).And.NotContain(TrackerPassword);
    }

    [Fact]
    public async Task GenerateCompose_Should_Write_Files_And_Return_Errors_As_Tool_Results()
    {
        var dir = Path.Combine(_outputRoot, "local");

        var ok = await _client.CallToolAsync("generate_compose", new Dictionary<string, object?> { ["output_dir"] = dir }, cancellationToken: Ct);
        var again = await _client.CallToolAsync("generate_compose", new Dictionary<string, object?> { ["output_dir"] = dir }, cancellationToken: Ct);
        var outside = await _client.CallToolAsync("generate_compose",
            new Dictionary<string, object?> { ["output_dir"] = Path.GetTempPath() }, cancellationToken: Ct);

        ok.IsError.Should().NotBe(true);
        File.Exists(Path.Combine(dir, "docker-compose.yml")).Should().BeTrue();
        File.ReadAllText(Path.Combine(dir, ".env")).Should().Contain(DbPassword);
        Text(ok).Should().NotContain(DbPassword);
        again.IsError.Should().BeTrue();
        Text(again).Should().Contain("上書きしませんでした");
        outside.IsError.Should().BeTrue();
        Text(outside).Should().Contain("TT_MCP_OUTPUT_ROOT");
    }

    [Fact]
    public void Server_Should_Exit_With_An_Error_When_Settings_Are_Invalid()
    {
        var start = new ProcessStartInfo("dotnet", [ServerDll])
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.Environment["TT_MCP_AUTH_MODE"] = "password";
        using var process = Process.Start(start)!;

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(30_000).Should().BeTrue();

        process.ExitCode.Should().Be(1);
        stderr.Should().Contain("TT_MCP_AUTH_MODE 'password' は無効です。");
        stdout.Should().BeEmpty();
    }
}
