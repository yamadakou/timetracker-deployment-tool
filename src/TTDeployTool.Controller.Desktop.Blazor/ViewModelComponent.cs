using Microsoft.AspNetCore.Components;

namespace TTDeployTool.Controller.Desktop.Blazor;

/// <summary>
/// Core の ViewModel を描くコンポーネントの基底（Q3 の A）。<see cref="Watch"/> で渡した ViewModel が変わったら、描き直す。
/// Core のジョブの定期の更新など、UI のスレッドの外からの変更もあるため、<see cref="ComponentBase.InvokeAsync(Action)"/> で描き直す。
/// </summary>
public abstract class ViewModelComponent : ComponentBase, IDisposable
{
    protected ViewModelComponent()
    {
        Watcher = new Watcher(() => _ = InvokeAsync(StateHasChanged));
    }

    protected Watcher Watcher { get; }

    /// <summary>変わったら描き直す ViewModel（いくつでも）。</summary>
    protected void Watch(params object[] sources)
    {
        foreach (var source in sources)
            Watcher.Watch(source);
    }

    public void Dispose()
    {
        Watcher.Dispose();
        OnDispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>購読を外すなど（派生のコンポーネント）。</summary>
    protected virtual void OnDispose()
    {
    }
}
