using TTDeployTool.Controller.Desktop.Core.Mvvm;
using TTDeployTool.Controller.Desktop.Core.Services;

namespace TTDeployTool.Controller.Desktop.Blazor.Shell;

/// <summary>確かめるダイアログの中身（答えると <see cref="Answer"/> が終わる）。</summary>
public sealed record ConfirmRequest(string Title, string Message, string ConfirmText)
{
    internal TaskCompletionSource<bool> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// 確かめるダイアログ（WinUI の画面の ContentDialog の代わり）。画面の <c>ConfirmDialog</c> が <see cref="Pending"/> を描き、押されたら <see cref="Answer"/> を呼ぶ。
/// フォルダーの選択は、ホストの部品（WPF の OpenFolderDialog）を使う。
/// </summary>
public sealed class BlazorDialogService(Func<Task<string?>> pickFolder) : ObservableObject, IDialogService
{
    private ConfirmRequest? _pending;

    /// <summary>答えを待っているダイアログ（無ければ null）。</summary>
    public ConfirmRequest? Pending
    {
        get => _pending;
        private set => SetProperty(ref _pending, value);
    }

    public Task<string?> PickFolderAsync() => pickFolder();

    /// <summary>確かめる（前のダイアログが残っていれば、それは「キャンセル」にする）。</summary>
    public Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        Pending?.Answer.TrySetResult(false);
        var request = new ConfirmRequest(title, message, confirmText);
        Pending = request;
        return request.Answer.Task;
    }

    /// <summary>ダイアログのボタンを押した（true: 確かめのボタン、false: キャンセル）。</summary>
    public void Answer(bool confirmed)
    {
        var request = Pending;
        Pending = null;
        request?.Answer.TrySetResult(confirmed);
    }
}
