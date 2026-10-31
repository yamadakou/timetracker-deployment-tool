using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TTDeployTool.Controller.Desktop.Blazor.Shell;

public static class DesktopServices
{
    /// <summary>画面（Razor）のサービス。<paramref name="session"/> はホストが組み立てたもの（ウィンドウ・通知領域と同じ <see cref="Core.ViewModels.MainViewModel"/>）。</summary>
    public static IServiceCollection AddTTDeployToolDesktop(this IServiceCollection services, DesktopSession session)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(session);
        return services;
    }
}
