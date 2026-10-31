using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TTDeployTool.Controller.Desktop.Blazor;
using TTDeployTool.Controller.Desktop.Blazor.Shell;
using TTDeployTool.Controller.Desktop.Blazor.Tables;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Controller.Desktop.Fixtures;

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

/// <summary>予定を残すときの暗号化の代わり（文字を逆に並べる）。</summary>
public sealed class ReversedSecrets : ISecretProtector
{
    public string Protect(string value) => new([.. value.Reverse()]);

    public string Unprotect(string protectedValue) => new([.. protectedValue.Reverse()]);
}

/// <summary>
/// bUnit の画面（Fixtures のモックの MainViewModel〔デプロイ・終わったジョブの記録〕・確かめるダイアログ・DesktopSession と、表の JavaScript の代わり）。時刻は 2026-10-30 に止めた <see cref="FakeTimeProvider"/>。
/// </summary>
public sealed class TestApp : IDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 10, 30, 3, 0, 0, TimeSpan.Zero);

    public TestApp(int deployments = 45, int records = 30, Func<FixtureData, FixtureData>? data = null, bool secrets = false,
        Func<IReadOnlyList<JobRecord>, IReadOnlyList<JobRecord>>? history = null)
    {
        Directory = Path.Combine(Path.GetTempPath(), "ttdeploytool-blazor-tests", Guid.NewGuid().ToString("N"));
        Context.JSInterop.Mode = JSRuntimeMode.Loose;
        Module = Context.JSInterop.SetupModule(JsModule.Path);
        Module.Mode = JSRuntimeMode.Loose;
        Context.Services.AddSingleton<TimeProvider>(Time);
        System.IO.Directory.CreateDirectory(Directory);
        var recorded = FixtureSamples.JobRecords(records, Now);
        new FileJobHistory(Path.Combine(Directory, "job-history.json")).Records = history?.Invoke(recorded) ?? recorded;
        Dialogs = new BlazorDialogService(() => Task.FromResult(Folder));
        ViewModel = FixtureApp.CreateViewModel(Directory, Dialogs, Clipboard, Time, Fixture(deployments, data), secrets: secrets ? new ReversedSecrets() : null);
        Session = new DesktopSession(ViewModel, Dialogs, Clipboard);
        Context.Services.AddTTDeployToolDesktop(Session);
    }

    // 書き込みは待たない（モックの Azure は本物の時刻で待つため）
    private static FixtureData Fixture(int deployments, Func<FixtureData, FixtureData>? data)
    {
        var fixture = FixtureSamples.Data(deployments, Now) with { WriteDelaySeconds = 0 };
        return data?.Invoke(fixture) ?? fixture;
    }

    /// <summary>フォルダーの選択で返すもの（null なら選ばなかった）。</summary>
    public string? Folder { get; set; }

    public BlazorDialogService Dialogs { get; }

    public DesktopSession Session { get; }

    public BunitContext Context { get; } = new();

    public BunitJSModuleInterop Module { get; }

    public FakeTimeProvider Time { get; } = new(Now);

    public RecordingClipboard Clipboard { get; } = new();

    public MainViewModel ViewModel { get; }

    public string Directory { get; }

    /// <summary>モックのアカウントでサインインし、デプロイの一覧を読む。</summary>
    public async Task SignInAsync()
    {
        await ViewModel.SignIn.SignInCommand.ExecuteAsync();
        await ViewModel.Deployments.LoadCommand.ExecuteAsync();
    }

    public void Dispose()
    {
        Context.Dispose();
        ViewModel.Dispose();
        if (System.IO.Directory.Exists(Directory))
            System.IO.Directory.Delete(Directory, recursive: true);
    }
}
