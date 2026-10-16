using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Moq;
using Timetracker.Common;
using Timetracker.Domain.Azure;
using Timetracker.Infrastructure.Azure;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using Azure.ResourceManager.AppContainers;
using Timetracker.Domain.Deployment;
using Xunit;

namespace Timetracker.Controller.Mcp.Tests.Http;

/// <summary>
/// HTTP のサーバー（BuildHttp）を TestServer で起動し、テスト用の署名鍵で作ったアクセストークンで、SDK のクライアントから接続して確かめる。
/// 発行者のメタデータは取得せず、検証鍵だけを差し替える（ほかの検証の設定は本番と同じ）。
/// </summary>
public sealed class HttpServerTests : IAsyncLifetime
{
    private static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048)) { KeyId = "test-key" };

    private readonly Mock<IAzureDeploymentGateway> _gateway = new();
    private readonly LogCollector _logs = new();
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _gateway.Setup(g => g.GetResourceGroupAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(new ResourceGroupInfo("rg", "japaneast"));
        _gateway.Setup(g => g.ListContainerAppsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _gateway.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync([new PermissionSet(["*"], [])]);
        _gateway.Setup(g => g.ListContainerAppDetailsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _gateway.Setup(g => g.EnsureContainerAppEnvAsync("sub", "rg", "demo", "japaneast", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<ContainerAppManagedEnvironmentResource>());
        _gateway.Setup(g => g.CreateOrUpdateContainerAppAsync("sub", "rg", "demo", "japaneast", It.IsAny<DeployOptions>(),
                It.IsAny<ContainerAppManagedEnvironmentResource>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerAppsDeployment("demo-tt", "demo-db", "demo-redis", "https://demo-tt.example.io"));

        _app = McpServerHost.BuildHttp([], HttpTestSupport.Settings(), builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Logging.AddProvider(_logs);
            builder.Services.AddSingleton<Func<IDeploymentLogger, IAzureDeploymentGateway>>(_ => _ => _gateway.Object);
            builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                o.Configuration = new OpenIdConnectConfiguration { Issuer = HttpTestSupport.Http().Issuer };
                o.Configuration.SigningKeys.Add(SigningKey);
            });
        });
        await _app.StartAsync(Ct);
        _http = _app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
    }

    private static string Token(
        string oid = "user-a", string[]? roles = null, string? audience = null, string? issuer = null, SecurityKey? key = null)
    {
        var claims = new Dictionary<string, object>
        {
            ["tid"] = HttpTestSupport.TenantId, ["oid"] = oid, ["name"] = oid, ["scp"] = "Mcp.Access",
        };
        if (oid == "user-a")
            claims["preferred_username"] = $"{oid}@example.com";
        if (roles != null)
            claims["roles"] = roles;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer ?? HttpTestSupport.Http().Issuer,
            Audience = audience ?? $"api://{HttpTestSupport.ClientId}",
            Claims = claims,
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(key ?? SigningKey, SecurityAlgorithms.RsaSha256),
        });
    }

    private async Task<McpClient> ConnectAsync(string token)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(_http.BaseAddress!, McpServerHost.McpPath),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
            },
            _http, NullLoggerFactory.Instance, ownsHttpClient: false);
        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    private static JsonElement Structured(CallToolResult result) => result.StructuredContent!.Value;

    private static string Text(CallToolResult result) => result.Content.OfType<TextContentBlock>().Single().Text;

    private Task<HttpResponseMessage> PostInitializeAsync(string? token, bool forwarded = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, McpServerHost.McpPath)
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""",
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        if (forwarded)
        {
            // Container Apps のイングレスが付けるヘッダー
            request.Headers.Add("X-Forwarded-Proto", "https");
            request.Headers.Add("X-Forwarded-Host", "mcp.example.com");
        }
        if (token != null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _http.SendAsync(request, Ct);
    }

    [Fact]
    public async Task Health_Should_Not_Require_A_Token()
    {
        var response = await _http.GetAsync(McpServerHost.HealthPath, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Be("ok");
    }

    [Fact]
    public async Task Mcp_Should_Return_401_With_The_Resource_Metadata_Without_A_Valid_Token()
    {
        var invalidTokens = new string?[]
        {
            null,
            Token(audience: "api://another-app"),
            Token(issuer: "https://login.microsoftonline.com/another-tenant/v2.0"),
            Token(key: new RsaSecurityKey(RSA.Create(2048)) { KeyId = "test-key" }),
        };
        foreach (var token in invalidTokens)
        {
            var response = await PostInitializeAsync(token);

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            response.Headers.WwwAuthenticate.ToString().Should()
                .Contain("Bearer").And.Contain("resource_metadata=\"https://mcp.example.com/.well-known/oauth-protected-resource\"");
        }

        var direct = await PostInitializeAsync(null, forwarded: false);
        direct.Headers.WwwAuthenticate.ToString().Should().Contain("resource_metadata=\"http://localhost/.well-known/oauth-protected-resource\"");
    }

    [Fact]
    public async Task Protected_Resource_Metadata_Should_Point_To_Entra_Id()
    {
        var response = await _http.GetAsync(McpServerHost.ResourceMetadataPath, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        json.RootElement.GetProperty("resource").GetString().Should().Be(HttpTestSupport.PublicUrl);
        json.RootElement.GetProperty("authorization_servers").EnumerateArray().Select(e => e.GetString())
            .Should().Equal($"https://login.microsoftonline.com/{HttpTestSupport.TenantId}/v2.0");
        json.RootElement.GetProperty("scopes_supported").EnumerateArray().Select(e => e.GetString())
            .Should().Equal($"api://{HttpTestSupport.ClientId}/Mcp.Access");
    }

    [Fact]
    public async Task Tools_Without_Azure_Should_Work_Without_A_Role()
    {
        await using var client = await ConnectAsync(Token(audience: HttpTestSupport.ClientId));

        client.ServerInstructions.Should().Contain("server 方式");
        var tools = await client.ListToolsAsync(cancellationToken: Ct);
        tools.Should().HaveCount(11);

        var options = Structured(await client.CallToolAsync("get_deployment_options", cancellationToken: Ct)).GetProperty("server");
        options.GetProperty("transport").GetString().Should().Be("http");
        options.GetProperty("auth_mode").GetString().Should().Be("server");
        options.GetProperty("allowed_resource_groups").EnumerateArray().Select(e => e.GetString()).Should().Equal(HttpTestSupport.AllowedGroup);
        options.GetProperty("can_create_resource_group").GetBoolean().Should().BeFalse();

        var compose = Structured(await client.CallToolAsync("generate_compose", new Dictionary<string, object?> { ["db_type"] = "sqlserver" }, cancellationToken: Ct));
        compose.GetProperty("env_content").GetString().Should().Contain("DB_PASSWORD=\"<DB パスワードを設定>\"");

        // Azure を読むツールはロールが必要
        var denied = await client.CallToolAsync("list_subscriptions", cancellationToken: Ct);
        denied.IsError.Should().BeTrue();
        Text(denied).Should().Contain(ServerAccessPolicy.ServerRole);
    }

    [Fact]
    public async Task Plans_Should_Be_Limited_To_The_Allowed_Resource_Groups_And_To_The_Caller()
    {
        await using var userA = await ConnectAsync(Token("user-a", [ServerAccessPolicy.ServerRole]));
        await using var userB = await ConnectAsync(Token("user-b", [ServerAccessPolicy.ServerRole]));
        var passwords = new Dictionary<string, object?> { ["app_name"] = "demo", ["db_password"] = "DbPassw0rd!x", ["tracker_password"] = "TrackerPass!y" };

        var outOfScope = Structured(await userA.CallToolAsync("plan_deployment",
            new Dictionary<string, object?>(passwords) { ["resource_group"] = "other" }, cancellationToken: Ct));
        outOfScope.GetProperty("plan_id").ValueKind.Should().Be(JsonValueKind.Null);
        outOfScope.GetProperty("blockers")[0].GetString().Should().Contain("操作できる範囲にありません");
        _gateway.Verify(g => g.GetResourceGroupAsync("sub", "other", It.IsAny<CancellationToken>()), Times.Never);

        var plan = Structured(await userA.CallToolAsync("plan_deployment", passwords, cancellationToken: Ct));
        plan.GetProperty("identity").GetProperty("auth_mode").GetString().Should().Be("server");
        var planId = plan.GetProperty("plan_id").GetString();
        planId.Should().NotBeNull();

        // ほかの利用者の計画は見えない
        var other = await userB.CallToolAsync("start_deployment", new Dictionary<string, object?> { ["plan_id"] = planId }, cancellationToken: Ct);
        other.IsError.Should().BeTrue();
        Text(other).Should().Contain("が見つかりません");
        Structured(await userB.CallToolAsync("list_jobs", cancellationToken: Ct)).GetProperty("jobs").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Jobs_Should_Be_Followed_By_The_Caller_That_Started_Them()
    {
        await using var userA = await ConnectAsync(Token("user-a", [ServerAccessPolicy.ServerRole]));
        await using var userB = await ConnectAsync(Token("user-b", [ServerAccessPolicy.ServerRole]));
        var plan = Structured(await userA.CallToolAsync("plan_deployment", new Dictionary<string, object?>
        {
            ["app_name"] = "demo", ["db_password"] = "DbPassw0rd!x", ["tracker_password"] = "TrackerPass!y",
        }, cancellationToken: Ct));

        var started = Structured(await userA.CallToolAsync("start_deployment",
            new Dictionary<string, object?> { ["plan_id"] = plan.GetProperty("plan_id").GetString() }, cancellationToken: Ct));
        var jobId = started.GetProperty("job_id").GetString();

        // 同じ利用者は、別のリクエスト（ステートレス）でもジョブを追える
        JsonElement status;
        do
        {
            status = Structured(await userA.CallToolAsync("get_job_status", new Dictionary<string, object?> { ["job_id"] = jobId }, cancellationToken: Ct));
        } while (status.GetProperty("status").GetString() == "running" && !Ct.IsCancellationRequested);
        status.GetProperty("status").GetString().Should().Be("succeeded", status.ToString());
        status.GetProperty("result").GetProperty("endpoint").GetString().Should().Be("https://demo-tt.example.io");
        Structured(await userA.CallToolAsync("list_jobs", cancellationToken: Ct)).GetProperty("jobs").GetArrayLength().Should().Be(1);

        // ほかの利用者からは見えない
        var other = await userB.CallToolAsync("get_job_status", new Dictionary<string, object?> { ["job_id"] = jobId }, cancellationToken: Ct);
        other.IsError.Should().BeTrue();
        Text(other).Should().Contain($"ジョブ '{jobId}' が見つかりません");
    }

    [Fact]
    public async Task Deployment_Resource_Should_Require_The_Role_And_The_Allowed_Resource_Group()
    {
        await using var withRole = await ConnectAsync(Token("user-a", [ServerAccessPolicy.ServerRole]));
        await using var withoutRole = await ConnectAsync(Token("user-b"));

        var allowed = await withRole.ReadResourceAsync("timetracker://deployments/sub/rg/demo", cancellationToken: Ct);
        var outOfScope = () => withRole.ReadResourceAsync("timetracker://deployments/sub/other/demo", cancellationToken: Ct);
        var noRole = () => withoutRole.ReadResourceAsync("timetracker://deployments/sub/rg/demo", cancellationToken: Ct);

        allowed.Contents.OfType<TextResourceContents>().Single().Text.Should().Contain("\"app_name\":\"demo\"");
        (await outOfScope.Should().ThrowAsync<McpException>()).Which.Message.Should().Contain("操作できる範囲にありません");
        (await noRole.Should().ThrowAsync<McpException>()).Which.Message.Should().Contain(ServerAccessPolicy.ServerRole);
        _gateway.Verify(g => g.GetResourceGroupAsync("sub", "other", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Requests_Should_Be_Logged_With_The_Caller()
    {
        await using (await ConnectAsync(Token("user-a")))
        await using (await ConnectAsync(Token("user-b")))
        {
        }
        await _http.GetAsync(McpServerHost.HealthPath, Ct);

        // preferred_username があれば表示名として、無くても tid/oid で、だれの操作かを追える
        var callers = _logs.Messages.Where(m => m.Contains(McpServerHost.McpPath + ":")).ToList();
        callers.Should().Contain(m => m.Contains($"user-a@example.com（{HttpTestSupport.TenantId}/user-a）"))
            .And.Contain(m => m.Contains($"（{HttpTestSupport.TenantId}/user-b）") && !m.Contains("user-b@"));
        // 認証のないリクエスト（/healthz）は記録しない
        _logs.Messages.Should().NotContain(m => m.Contains(McpServerHost.HealthPath + ":"));
    }

    // サーバーのログを集める
    private sealed class LogCollector : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _messages = new();

        public IReadOnlyList<string> Messages => _messages.ToList();

        public ILogger CreateLogger(string categoryName) => new Collector(_messages);

        public void Dispose()
        {
        }

        private sealed class Collector(System.Collections.Concurrent.ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception));
        }
    }
}
