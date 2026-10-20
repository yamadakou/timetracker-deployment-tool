namespace TTDeployTool.Controller.Desktop.Core.Mvvm;

/// <summary>
/// 処理中の表示（詳細設計 desktop-jobs.md の 3.3）。処理中であることと、今している段階（「リソースグループを確認しています…」など）を画面に出す。
/// 段階は <see cref="IProgress{T}"/> で受け取る（呼んだスレッドで、すぐに表示を変える）。
/// </summary>
public sealed class BusyState : ObservableObject, IProgress<string>
{
    private bool _isBusy;
    private string _text = "";

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    /// <summary>今している段階（処理中でなければ空）。</summary>
    public string Text
    {
        get => _text;
        private set => SetProperty(ref _text, value);
    }

    /// <summary>処理中にして処理を行い、終わったら（失敗しても）戻す。</summary>
    public async Task RunAsync(string text, Func<Task> work)
    {
        Report(text);
        IsBusy = true;
        try
        {
            await work();
        }
        finally
        {
            IsBusy = false;
            Text = "";
        }
    }

    public void Report(string value) => Text = value + "…";
}
