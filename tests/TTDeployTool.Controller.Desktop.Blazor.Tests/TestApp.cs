using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TTDeployTool.Controller.Desktop.Blazor;
using TTDeployTool.Controller.Desktop.Blazor.Tables;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;

namespace TTDeployTool.Controller.Desktop.Blazor.Tests;

/// <summary>コピーした文字を残すクリップボード（<see cref="Fail"/> で、開けないクリップボードの代わり）。</summary>
public sealed class RecordingClipboard : IClipboardService
{
    public List<string> Texts { get; } = [];

    public bool Fail { get; set; }

    public void CopyText(string text)
    {
        if (Fail)
            throw new InvalidOperationException("クリップボードを開けません。");
        Texts.Add(text);
    }
}

/// <summary>
/// bUnit の試作の画面（Fixtures のモックの MainViewModel と、表の JavaScript の代わり）。時刻は 2026-10-30 に止めた <see cref="FakeTimeProvider"/>。
/// </summary>
public sealed class TestApp : IDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 10, 30, 3, 0, 0, TimeSpan.Zero);

    public TestApp(int deployments = 45, int records = 30)
    {
        Directory = Path.Combine(Path.GetTempPath(), "ttdeploytool-blazor-tests", Guid.NewGuid().ToString("N"));
        Context.JSInterop.Mode = JSRuntimeMode.Loose;
        Module = Context.JSInterop.SetupModule(JsModule.Path);
        Module.Mode = JSRuntimeMode.Loose;
        Context.Services.AddSingleton<TimeProvider>(Time);
        ViewModel = PrototypeSession.CreateViewModel(Directory, Clipboard, Time, deployments, records);
        Context.Services.AddSingleton(new PrototypeSession(ViewModel, Clipboard));
    }

    public BunitContext Context { get; } = new();

    public BunitJSModuleInterop Module { get; }

    public FakeTimeProvider Time { get; } = new(Now);

    public RecordingClipboard Clipboard { get; } = new();

    public MainViewModel ViewModel { get; }

    public string Directory { get; }

    /// <summary>モックのアカウントでサインインし、デプロイの一覧を読む。</summary>
    public async Task SignInAsync() => await Context.Services.GetRequiredService<PrototypeSession>().StartAsync();

    public void Dispose()
    {
        Context.Dispose();
        ViewModel.Dispose();
        if (System.IO.Directory.Exists(Directory))
            System.IO.Directory.Delete(Directory, recursive: true);
    }
}
