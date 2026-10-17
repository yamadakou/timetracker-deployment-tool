using System.Windows.Input;

namespace Timetracker.Controller.Desktop.Core.Mvvm;

/// <summary>
/// ボタンから非同期の処理を呼ぶコマンド。実行中は押せない（二重に実行しない）。
/// 処理は例外を投げないこと（ViewModel の中でエラーの表示にする）。
/// </summary>
public sealed class AsyncCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool> _canExecute;
    private bool _running;

    public AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute ?? (() => true);
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning => _running;

    public bool CanExecute(object? parameter) => !_running && _canExecute();

    public async void Execute(object? parameter) => await ExecuteAsync();

    /// <summary>実行する（押せない状態なら何もしない）。テストと、ほかの ViewModel からの呼び出しに使う。</summary>
    public async Task ExecuteAsync()
    {
        if (!CanExecute(null))
            return;
        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute();
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    /// <summary>押せるかどうかが変わったことを画面に知らせる。</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
