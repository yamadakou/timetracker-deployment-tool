using TTDeployTool.Controller.Desktop.Blazor;
using TTDeployTool.Controller.Desktop.Blazor.TestHost;
using TTDeployTool.Controller.Desktop.Blazor.TestHost.Components;

// 引数: --urls http://127.0.0.1:<port> --data <アプリのデータのフォルダー>（列の配置などの設定を残す。同じフォルダーで起動し直すと、前の設定で始まる）
var builder = WebApplication.CreateBuilder(args);
var data = builder.Configuration["data"] ?? Path.Combine(Path.GetTempPath(), "ttdeploytool-blazor-testhost");
var clipboard = new TestClipboard();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddTTDeployToolPrototype(data, _ => clipboard);

var app = builder.Build();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapGet("/test/clipboard", () => clipboard.Last);
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
