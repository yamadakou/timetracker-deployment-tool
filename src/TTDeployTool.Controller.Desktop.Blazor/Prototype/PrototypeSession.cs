using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Controller.Desktop.Fixtures;

namespace TTDeployTool.Controller.Desktop.Blazor;

/// <summary>
/// 試作の画面の状態（desktop-blazor-hybrid.md の 1・7）。WinUI 3 のアプリと同じ <see cref="MainViewModel"/> を、Fixtures のモック
/// （サインイン・Azure・通知・既定のアプリで開く・サインイン時の起動）で組み立てる。本物の Azure には届かない。
/// <see cref="StartAsync"/> で、モックのアカウントでサインインし、デプロイの一覧を読む（何度呼んでも 1 回だけ）。
/// </summary>
public sealed class PrototypeSession
{
    private readonly Func<MainViewModel, Task> _startCore;
    private Task? _start;

    /// <param name="start">始めの処理（既定はモックのサインインとデプロイの一覧の読み込み。テストが、終わるまでの表示を確かめるときに替える）</param>
    public PrototypeSession(MainViewModel viewModel, IClipboardService? clipboard, Func<MainViewModel, Task>? start = null)
    {
        ViewModel = viewModel;
        Clipboard = clipboard;
        _startCore = start ?? SignInAndLoadAsync;
    }

    public MainViewModel ViewModel { get; }

    public IClipboardService? Clipboard { get; }

    public Task StartAsync() => _start ??= _startCore(ViewModel);

    private static async Task SignInAndLoadAsync(MainViewModel viewModel)
    {
        await viewModel.SignIn.SignInCommand.ExecuteAsync();
        await viewModel.Deployments.LoadCommand.ExecuteAsync();
    }

    /// <summary>
    /// モックの <see cref="MainViewModel"/>（<paramref name="deployments"/> 件のデプロイと、<paramref name="records"/> 件の終わったジョブの記録。
    /// 列の配置などの設定は <paramref name="dataDirectory"/> の preferences.json に残し、モックの通知などの記録も同じフォルダーに書く）。
    /// </summary>
    public static MainViewModel CreateViewModel(string dataDirectory, IClipboardService? clipboard, TimeProvider time, int deployments = 45, int records = 30)
    {
        Directory.CreateDirectory(dataDirectory);
        var now = time.GetUtcNow();
        var data = FixtureSamples.Data(deployments, now);
        var events = new FixtureEvents(Path.Combine(dataDirectory, FixtureEvents.FileName));
        return new MainViewModel(new FixtureSignInService(() => data), new FixtureAzureClients(data), new PrototypeDialogs(), new FixtureLauncher(events), time,
            NullLoggerFactory.Instance,
            preferences: new FilePreferences(Path.Combine(dataDirectory, "preferences.json")),
            notifications: new FixtureNotifications(events, data.Notifications),
            clipboard: clipboard,
            startupTask: new FixtureStartupTask(events, data.StartupTask),
            history: new InMemoryJobHistory { Records = FixtureSamples.JobRecords(records, now) });
    }
}

/// <summary>試作では確かめの画面を出さない（記録から削除などの操作は作らないため、確かめたら「やめる」と答える）。</summary>
public sealed class PrototypeDialogs : IDialogService
{
    public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);

    public Task<bool> ConfirmAsync(string title, string message, string confirmText) => Task.FromResult(false);
}

public static class PrototypeServices
{
    /// <summary>
    /// 試作の画面のサービス（画面ごと〔WebView・回線ごと〕に 1 つの <see cref="MainViewModel"/>）。<paramref name="clipboard"/> はホストのクリップボード。
    /// </summary>
    public static IServiceCollection AddTTDeployToolPrototype(this IServiceCollection services, string dataDirectory, Func<IServiceProvider, IClipboardService?> clipboard)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped(sp => CreateViewModelFor(sp, dataDirectory, clipboard(sp)));
        services.AddScoped(sp => new PrototypeSession(sp.GetRequiredService<MainViewModel>(), clipboard(sp)));
        return services;
    }

    private static MainViewModel CreateViewModelFor(IServiceProvider services, string dataDirectory, IClipboardService? clipboard) =>
        PrototypeSession.CreateViewModel(dataDirectory, clipboard, services.GetRequiredService<TimeProvider>());
}
