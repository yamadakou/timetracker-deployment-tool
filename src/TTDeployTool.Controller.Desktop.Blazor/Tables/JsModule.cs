using Microsoft.JSInterop;

namespace TTDeployTool.Controller.Desktop.Blazor.Tables;

/// <summary>表の JavaScript（wwwroot/table.js）の読み込みと、画面を閉じた後（WebView・回線が先に無くなった）の呼び出しの扱い。</summary>
public static class JsModule
{
    public const string Path = "./_content/TTDeployTool.Controller.Desktop.Blazor/table.js";

    public static Task<IJSObjectReference> ImportAsync(IJSRuntime js) => js.InvokeAsync<IJSObjectReference>("import", Path).AsTask();

    /// <summary>呼ぶ（画面が無くなっていれば何もしない）。</summary>
    public static async Task InvokeQuietlyAsync(Task<IJSObjectReference> module, string identifier, params object?[] args)
    {
        try
        {
            await (await module).InvokeVoidAsync(identifier, args);
        }
        catch (JSDisconnectedException)
        {
        }
    }

    /// <summary>捨てる（画面が無くなっていれば何もしない）。</summary>
    public static async ValueTask DisposeQuietlyAsync(Task<IJSObjectReference> module)
    {
        try
        {
            await (await module).DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
        }
    }
}
