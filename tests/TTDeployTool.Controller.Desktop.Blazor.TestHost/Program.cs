using TTDeployTool.Controller.Desktop.Blazor.Shell;
using TTDeployTool.Controller.Desktop.Blazor.TestHost;
using TTDeployTool.Controller.Desktop.Blazor.TestHost.Components;
using TTDeployTool.Controller.Desktop.Fixtures;

// 引数: --urls http://127.0.0.1:<port> --data <アプリのデータのフォルダー> [--sample] [--folder <Dry-run の保存先>]
// アプリのデータのフォルダーは、WinUI 3 版の UI テストと同じく、テストが起動の前に uitest-fixture.json（モックのデータ）と job-history.json（終わったジョブの記録）を置く。
// --sample は、どちらも無ければ見本（FixtureSamples: デプロイ 45 件・記録 30 件）を置く。同じフォルダーで起動し直すと、前の設定と記録で始まる
var builder = WebApplication.CreateBuilder(args);
var data = builder.Configuration["data"] ?? Path.Combine(Path.GetTempPath(), "ttdeploytool-blazor-testhost");
if (builder.Configuration["sample"] != null)
    SampleData.WriteIfMissing(data, DateTimeOffset.UtcNow);
var clipboard = new TestClipboard();
var folder = builder.Configuration["folder"];
var dialogs = new BlazorDialogService(() => Task.FromResult(folder));
var viewModel = FixtureApp.CreateViewModel(data, dialogs, clipboard, TimeProvider.System);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddTTDeployToolDesktop(new DesktopSession(viewModel, dialogs, clipboard));

var app = builder.Build();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapGet("/test/clipboard", () => clipboard.Last);
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
