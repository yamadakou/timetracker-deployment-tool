using Azure.Core;
using Azure.Identity;

namespace TTDeployTool.Controller.Desktop.Core.Services;

/// <summary>トークンの取得（サインインの画面での対話を含む）が、決めた時間内に終わらなかった。</summary>
public sealed class SignInTimeoutException(TimeSpan timeout)
    : AuthenticationFailedException($"サインインの画面で {timeout.TotalMinutes:0} 分以内にサインインされなかったため、ジョブを止めました。")
{
    public TimeSpan Timeout { get; } = timeout;
}

/// <summary>
/// ジョブに渡す資格情報。予定の時刻に、MFA などの対話（WAM のサインインの画面）が要ったまま誰もサインインしないと、
/// ジョブが止まったままになるため、トークンの取得が決めた時間を超えたら失敗させる（詳細設計 desktop-jobs.md の 4.5・5.1）。
/// </summary>
public sealed class TimeLimitedCredential : TokenCredential
{
    /// <summary>ジョブでトークンの取得を待つ上限。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    private readonly TokenCredential _inner;
    private readonly TimeProvider _time;

    public TimeLimitedCredential(TokenCredential inner, TimeProvider time, TimeSpan? timeout = null)
    {
        _inner = inner;
        _time = time;
        Timeout = timeout ?? DefaultTimeout;
    }

    public TimeSpan Timeout { get; }

    public TokenCredential Inner => _inner;

    // Azure SDK の非同期の呼び出しは GetTokenAsync を使う。同期の呼び出しは時間を区切れないため、そのまま渡す
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        _inner.GetToken(requestContext, cancellationToken);

    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _inner.GetTokenAsync(requestContext, timeout.Token).AsTask();
        // 対話の画面が取り消しに応じない場合もあるため、取り消しを伝えたうえで、待つのをやめる
        var expired = Task.Delay(Timeout, _time, cancellationToken);
        if (await Task.WhenAny(token, expired) == token)
            return await token;
        cancellationToken.ThrowIfCancellationRequested();
        await timeout.CancelAsync();
        throw new SignInTimeoutException(Timeout);
    }
}
