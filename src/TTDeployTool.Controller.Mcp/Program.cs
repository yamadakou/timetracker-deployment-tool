using Microsoft.Extensions.Hosting;
using TTDeployTool.Controller.Mcp;

// 設定が不正なら、理由を stderr に出して終了する（stdio では stdout は JSON-RPC 専用）
if (McpServerHost.LoadSettings(Environment.GetEnvironmentVariable, Console.Error, args) is not { } settings)
    return 1;

await McpServerHost.Create(args, settings).RunAsync();
return 0;
