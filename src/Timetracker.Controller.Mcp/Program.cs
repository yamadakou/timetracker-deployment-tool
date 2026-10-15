using Microsoft.Extensions.Hosting;
using Timetracker.Controller.Mcp;

// 設定が不正なら、理由を stderr に出して終了する（stdout は JSON-RPC 専用）
if (McpServerHost.LoadSettings(Environment.GetEnvironmentVariable, Console.Error) is not { } settings)
    return 1;

await McpServerHost.Build(args, settings).RunAsync();
return 0;
