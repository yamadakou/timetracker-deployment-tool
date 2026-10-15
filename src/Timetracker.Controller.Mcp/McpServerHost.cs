using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Azure.ResourceManager;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Timetracker.Controller.Mcp.Tools;
using Timetracker.Infrastructure.Azure;

namespace Timetracker.Controller.Mcp;

/// <summary>
/// MCP サーバー（stdio）の組み立て。Program から分け、ツールの登録や設定をテストできるようにしている。
/// </summary>
public static class McpServerHost
{
    public const string ServerName = "timetracker-deploy";

    /// <summary>ツールの結果の JSON。プロパティ名を snake_case にし、日本語をエスケープせずに返す（テキストで読めるように）。</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    /// <summary>環境変数から設定を読む。不正な値があれば理由を <paramref name="error"/> に書いて null を返す。</summary>
    public static McpServerSettings? LoadSettings(Func<string, string?> getEnvironmentVariable, TextWriter error)
    {
        try
        {
            return McpServerSettings.FromEnvironment(getEnvironmentVariable);
        }
        catch (ArgumentException ex)
        {
            error.WriteLine($"[ERROR] {ex.Message}");
            return null;
        }
    }

    public static IHost Build(string[] args, McpServerSettings settings)
    {
        // 空のビルダーを使う（クライアントの作業ディレクトリにある appsettings.json などを読まないように）
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { Args = args });

        // stdout は JSON-RPC 専用のため、ログはすべて stderr に出す
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(settings.Verbose ? LogLevel.Debug : LogLevel.Information);

        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<Func<ISubscriptionDirectory>>(sp => () =>
        {
            var logger = new McpDeploymentLogger(sp.GetRequiredService<ILoggerFactory>().CreateLogger("Timetracker.Azure"));
            return new AzureSubscriptionDirectory(new ArmClient(CredentialFactory.Create(settings.AuthMode, logger)));
        });

        builder.Services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation { Name = ServerName, Version = Version };
                o.ServerInstructions = ServerInstructions.Text;
            })
            .WithStdioServerTransport()
            .WithTools<DeploymentTools>(JsonOptions);

        return builder.Build();
    }

    // .NET SDK がビルド時に必ず付ける属性
    internal static string Version =>
        typeof(McpServerHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
}
