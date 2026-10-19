using Microsoft.Extensions.Logging;
using TTDeployTool.Controller.Desktop.Core.Mvvm;
using TTDeployTool.Controller.Desktop.Core.Services;

namespace TTDeployTool.Controller.Desktop.Core.ViewModels;

/// <summary>サインインの状態と、サインイン・サインアウトの操作。</summary>
public sealed class SignInViewModel : ObservableObject
{
    private readonly ISignInService _signIn;
    private readonly Func<bool, Task> _accountChanged;
    private readonly ILogger _logger;
    private SignedInAccount? _account;
    private string? _error;

    /// <param name="accountChanged">サインイン（true）・サインアウト（false）したときの処理（対象の一覧を読み直す・消す）</param>
    public SignInViewModel(ISignInService signIn, Func<bool, Task> accountChanged, ILogger logger)
    {
        _signIn = signIn;
        _accountChanged = accountChanged;
        _logger = logger;
        SignInCommand = new AsyncCommand(SignInAsync, () => !IsSignedIn);
        SignOutCommand = new AsyncCommand(SignOutAsync, () => IsSignedIn);
    }

    public AsyncCommand SignInCommand { get; }

    public AsyncCommand SignOutCommand { get; }

    public SignedInAccount? Account => _account;

    public bool IsSignedIn => Account != null;

    public string AccountDisplay => Account?.UserName ?? "サインインしていません（Dry-run はサインインせずに使えます）";

    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    /// <summary>起動時に、前回のサインインを対話なしで復元する（できなければサインインしていない状態のまま）。</summary>
    public async Task RestoreAsync()
    {
        try
        {
            await SetAccountAsync(await _signIn.RestoreAsync());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "前回のサインインを復元できませんでした");
            Error = DesktopMessages.ForAzure(ex);
        }
    }

    private async Task SignInAsync()
    {
        Error = null;
        try
        {
            await SetAccountAsync(await _signIn.SignInAsync());
        }
        catch (OperationCanceledException)
        {
            // 利用者がサインインの画面を閉じた
            _logger.LogInformation("サインインが取り消されました");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "サインインに失敗しました");
            Error = DesktopMessages.ForAzure(ex);
        }
    }

    private async Task SignOutAsync()
    {
        Error = null;
        try
        {
            await _signIn.SignOutAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "サインアウトに失敗しました");
            Error = DesktopMessages.ForAzure(ex);
        }
        // サインアウトに失敗しても、このアプリでは使わない（次回は対話でサインインする）
        await SetAccountAsync(null);
    }

    private async Task SetAccountAsync(SignedInAccount? account)
    {
        if (!SetProperty(ref _account, account, nameof(Account)))
            return;
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(AccountDisplay));
        SignInCommand.RaiseCanExecuteChanged();
        SignOutCommand.RaiseCanExecuteChanged();
        // アカウント名は書かない（ホームのテナントだけ）
        _logger.LogInformation("サインインの状態: {State}（ホームのテナント {TenantId}）", account != null ? "サインイン" : "サインアウト", account?.HomeTenantId);
        await _accountChanged(account != null);
    }
}
