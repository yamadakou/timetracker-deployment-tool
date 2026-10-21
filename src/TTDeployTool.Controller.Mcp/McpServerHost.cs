using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Azure.Core;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Protocol;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Common;
using TTDeployTool.Controller.Mcp.Prompts;
using TTDeployTool.Controller.Mcp.Resources;
using TTDeployTool.Controller.Mcp.Tools;
using TTDeployTool.Infrastructure.Azure;

namespace TTDeployTool.Controller.Mcp;

/// <summary>
/// MCP サーバーの組み立て（stdio と HTTP）。Program から分け、ツールの登録や設定をテストできるようにしている。
/// ツール・Resources・Prompts と計画・ジョブの登録は共通で、トランスポートごとに呼び出し元、Azure の操作の範囲、generate_compose を差し替える。
/// </summary>
public static class McpServerHost
{
    public const string ServerName = "ttdeploytool";
    public const string McpPath = "/mcp";
    public const string HealthPath = "/healthz";

    /// <summary>保護リソースのメタデータ（RFC 9728）のパス。</summary>
    public const string ResourceMetadataPath = "/.well-known/oauth-protected-resource";

    /// <summary>HTTP の既定の待ち受け（ASPNETCORE_URLS / ASPNETCORE_HTTP_PORTS が無い場合）。</summary>
    public const string DefaultHttpUrl = "http://+:8080";

    /// <summary>ツールの結果の JSON。プロパティ名を snake_case にし、日本語をエスケープせずに返す（テキストで読めるように）。</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    /// <summary>環境変数と起動引数から設定を読む。不正な値があれば理由を <paramref name="error"/> に書いて null を返す。</summary>
    public static McpServerSettings? LoadSettings(Func<string, string?> getEnvironmentVariable, TextWriter error, IReadOnlyList<string>? args = null)
    {
        try
        {
            return McpServerSettings.FromEnvironment(getEnvironmentVariable, args);
        }
        catch (ArgumentException ex)
        {
            error.WriteLine($"[ERROR] {ex.Message}");
            return null;
        }
    }

    /// <summary>設定のトランスポートに応じたサーバーを作る。</summary>
    public static IHost Create(string[] args, McpServerSettings settings) =>
        settings.Http == null ? Build(args, settings) : BuildHttp(args, settings);

    /// <summary>stdio のサーバー（利用者の PC で、MCP クライアントが子プロセスとして起動する）。</summary>
    public static IHost Build(string[] args, McpServerSettings settings)
    {
        // 空のビルダーを使う（クライアントの作業ディレクトリにある appsettings.json などを読まないように）
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { Args = args });

        // stdout は JSON-RPC 専用のため、ログはすべて stderr に出す
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(settings.Verbose ? LogLevel.Debug : LogLevel.Information);

        AddServices(builder.Services, settings);
        builder.Services.AddSingleton<ICallerContext, LocalCallerContext>();
        builder.Services.AddSingleton<IAzureAccessPolicy, UnrestrictedAccessPolicy>();

        AddMcp(builder.Services, settings, ServerInstructions.Text)
            .WithStdioServerTransport()
            .WithTools<ComposeFileTools>(JsonOptions);

        return builder.Build();
    }

    /// <summary>
    /// HTTP のサーバー（Streamable HTTP・ステートレス。詳細設計 Phase 4a）。/mcp は Entra ID のアクセストークンが必要で、
    /// Azure はサーバーの ID（server 方式）で、許可したリソースグループだけを操作する。
    /// </summary>
    /// <param name="configure">ビルドの前の追加の設定（テストで TestServer やトークンの検証鍵を差し替える）</param>
    public static WebApplication BuildHttp(string[] args, McpServerSettings settings, Action<WebApplicationBuilder>? configure = null)
    {
        var http = settings.Http ?? throw new ArgumentException("HTTP の設定（TT_MCP_HTTP__*）がありません。", nameof(settings));
        // コンテナ内で動かすため、appsettings.json などはアプリのディレクトリから読む
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
        if (builder.Configuration["URLS"] == null && builder.Configuration["HTTP_PORTS"] == null)
            builder.WebHost.UseUrls(DefaultHttpUrl);
        builder.Logging.SetMinimumLevel(settings.Verbose ? LogLevel.Debug : LogLevel.Information);

        AddServices(builder.Services, settings);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<ICallerContext, HttpCallerContext>();
        builder.Services.AddSingleton<IAzureAccessPolicy, HttpAccessPolicy>();

        builder.Services
            .AddAuthentication(o =>
            {
                o.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                // 401 のときに WWW-Authenticate で保護リソースのメタデータを示す（クライアントはそこから認可サーバーを知る）
                o.DefaultChallengeScheme = McpAuthenticationDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(o =>
            {
                // 署名鍵は発行者のメタデータ（OpenID Connect の discovery）から取得する
                o.Authority = http.Issuer;
                // クレーム名を変換しない（tid / oid / roles をそのまま使う）
                o.MapInboundClaims = false;
                o.TokenValidationParameters.ValidIssuer = http.Issuer;
                o.TokenValidationParameters.ValidAudiences = [http.ClientId, $"api://{http.ClientId}"];
                o.TokenValidationParameters.RoleClaimType = "roles";
                o.TokenValidationParameters.NameClaimType = "name";
            })
            .AddMcp(o =>
            {
                o.ResourceMetadataUri = new Uri(ResourceMetadataPath, UriKind.Relative);
                o.ResourceMetadata = new ProtectedResourceMetadata
                {
                    Resource = http.PublicUrl.ToString(),
                    AuthorizationServers = [http.Issuer],
                    ScopesSupported = [http.Scope],
                    ResourceName = ServerName,
                };
            });
        // 発行者・対象・署名だけでは、このアプリ登録向けの ID トークンと区別できない。委任のアクセストークンに限るため、
        // scp（スペース区切り）に Mcp.Access があることを求める（無ければ 403）
        builder.Services.AddAuthorizationBuilder()
            .SetDefaultPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireAssertion(context => McpHttpSettings.HasScope(context.User))
                .Build());
        // TLS は前段（Container Apps のイングレス）で終端するため、401 で示すメタデータの URL を、利用者から見た https の URL にする
        builder.Services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
        });

        AddMcp(builder.Services, settings, ServerInstructions.Http(http))
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools<ComposeContentTools>(JsonOptions);

        configure?.Invoke(builder);
        var app = builder.Build();
        app.UseForwardedHeaders();
        app.UseAuthentication();
        app.UseAuthorization();
        // だれの操作かをサーバーのログで追えるようにする（表示名は結果には出さない）
        app.Use(async (context, next) =>
        {
            if (HttpCallerContext.Id(context.User) is { } caller)
                app.Logger.LogInformation("{Method} {Path}: {Name}（{Caller}）",
                    context.Request.Method, context.Request.Path, context.User.FindFirst("preferred_username")?.Value, caller);
            await next(context);
        });
        app.MapGet(HealthPath, () => Results.Text("ok"));
        app.MapMcp(McpPath).RequireAuthorization();
        return app;
    }

    // 共通のサービス（設定、Azure の資格情報、計画とジョブ）
    private static void AddServices(IServiceCollection services, McpServerSettings settings)
    {
        services.AddSingleton(settings);
        // 資格情報は、ツールが方式（IAzureAccessPolicy）に応じて作って渡す
        services.AddSingleton<Func<TokenCredential, ISubscriptionDirectory>>(_ => credential => new AzureSubscriptionDirectory(HappyEyeballs.Arm(credential)));

        // 計画とジョブはプロセス内のメモリに置く（サーバーの終了で消える。HTTP ではレプリカを 1 に固定する）
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IPlanStore>(sp => new InMemoryPlanStore(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IJobStore, InMemoryJobStore>();
        services.AddSingleton<JobManager>();
        services.AddSingleton<Func<IDeploymentLogger, TokenCredential, IAzureDeploymentGateway>>(_ =>
            (logger, credential) => new AzureSdkExecutor(logger, credential));
    }

    // generate_compose 以外のツールと、Resources・Prompts
    private static IMcpServerBuilder AddMcp(IServiceCollection services, McpServerSettings settings, string instructions) =>
        services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation { Name = ServerName, Version = Version };
                o.ServerInstructions = instructions;
            })
            .WithTools<DeploymentTools>(JsonOptions)
            .WithTools<DeploymentJobTools>(JsonOptions)
            .WithTools<DeletionJobTools>(JsonOptions)
            .WithResources<TimetrackerResources>()
            .WithPrompts<TimetrackerPrompts>(JsonOptions);

    // .NET SDK がビルド時に必ず付ける属性
    internal static string Version =>
        typeof(McpServerHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
}
