using Azure.Core;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Infrastructure.Azure;
using Xunit;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>常駐と通知（詳細設計 desktop-jobs.md の 5）と、ジョブのサインインの時間切れ（4.5）。</summary>
public class NotificationTests
{
    private static readonly JobTarget Other = new("sub", "rg", "other");

    // 始まらないまま待つジョブ（予定）
    private static JobViewModel Scheduled(MainViewModel main) =>
        main.Queue.Register(JobKind.Deploy, Other, DateTimeOffset.UtcNow.AddHours(1), () => new TaskCompletionSource<Job>().Task);

    [Fact]
    public async Task Closing_Should_Exit_Without_Jobs_And_Hide_With_Jobs_Notifying_Once()
    {
        var h = await new Harness().ReadyAsync();

        h.Main.RequestClose().Should().Be(CloseAction.Exit);
        Scheduled(h.Main);
        h.Main.RequestClose().Should().Be(CloseAction.Hide);
        h.Main.RequestClose().Should().Be(CloseAction.Hide);

        h.Notifications.Shown.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Title = "バックグラウンドで実行を続けています",
            JobId = (string?)null,
        });
        h.Notifications.Shown[0].Message.Should().StartWith("ジョブ（予定 1）があるため、通知領域に残ります。");
    }

    [Fact]
    public async Task Finished_Deployment_Should_Be_Notified_With_The_Url_And_Opened_From_The_Notice()
    {
        var h = await new Harness().ReadyAsync();
        await h.Main.Deployment.PlanCommand.ExecuteAsync();

        await h.Main.Deployment.StartCommand.ExecuteAsync();
        await h.Main.Queue.WhenIdleAsync();

        var job = h.Main.CurrentDeployment!;
        h.Notifications.Shown.Should().Equal(new AppNotice("デプロイ（rg / demo）: 完了", "https://demo-tt.example.io/", job.Id));
        h.Main.FindJob(job.Id).Should().BeSameAs(job);
        h.Main.FindJob("job-999").Should().BeNull();
        job.Id.Should().MatchRegex("^job-[0-9]+$");
    }

    [Fact]
    public async Task A_Job_Without_Details_Should_Say_It_Finished()
    {
        var h = await new Harness().ReadyAsync();
        h.Azure.DeployedUrl = null;
        await h.Main.Deployment.PlanCommand.ExecuteAsync();

        await h.Main.Deployment.StartCommand.ExecuteAsync();
        await h.Main.Queue.WhenIdleAsync();

        h.Notifications.Shown.Single().Message.Should().Be("ジョブが終わりました。");
    }

    [Fact]
    public async Task Failed_And_Not_Started_Jobs_Should_Be_Notified_But_Not_Canceled_Ones()
    {
        var h = await new Harness().ReadyAsync();
        var canceled = Scheduled(h.Main);
        await canceled.CancelCommand.ExecuteAsync();

        var failed = h.Main.Queue.Register(JobKind.Delete, new JobTarget("sub", "rg", "broken"), null,
            () => Task.FromException<Job>(new InvalidOperationException("壊れています")));
        await failed.Finished;

        h.Notifications.Shown.Should().ContainSingle().Which.Should().Be(
            new AppNotice("削除（rg / broken）: 失敗", "実行しませんでした: エラーが発生しました: 壊れています", failed.Id));
    }

    [Fact]
    public async Task Without_A_Notification_Service_Closing_And_Finishing_Should_Still_Work()
    {
        using var main = new MainViewModel(new FakeSignIn(), new FakeAzure(), new FakeDialogs(), new FakeLauncher(), TimeProvider.System,
            NullLoggerFactory.Instance);
        Scheduled(main);
        var failed = main.Queue.Register(JobKind.Delete, new JobTarget("sub", "rg", "broken"), null,
            () => Task.FromException<Job>(new InvalidOperationException("壊れています")));
        await failed.Finished;

        main.RequestClose().Should().Be(CloseAction.Hide);
    }

    [Fact]
    public async Task Job_Sessions_Should_Limit_The_Time_To_Get_A_Token()
    {
        var h = await new Harness().ReadyAsync();
        await h.Main.Deployment.PlanCommand.ExecuteAsync();

        await h.Main.Deployment.StartCommand.ExecuteAsync();
        await h.Main.Queue.WhenIdleAsync();

        h.Azure.Credentials.Last().Should().BeOfType<TimeLimitedCredential>().Which.Inner.Should().BeOfType<TenantScopedCredential>()
            .Which.TenantId.Should().Be("t1");
    }
}

public class TimeLimitedCredentialTests
{
    private static readonly TokenRequestContext Request = new(["https://management.azure.com/.default"]);
    private static readonly AccessToken Token = new("token", DateTimeOffset.MaxValue);

    private readonly FakeTimeProvider _time = new();
    private readonly Mock<TokenCredential> _inner = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Tokens_Obtained_In_Time_Should_Be_Returned()
    {
        _inner.Setup(c => c.GetTokenAsync(Request, It.IsAny<CancellationToken>())).ReturnsAsync(Token);
        _inner.Setup(c => c.GetToken(Request, Ct)).Returns(Token);
        var credential = new TimeLimitedCredential(_inner.Object, _time);

        (await credential.GetTokenAsync(Request, Ct)).Should().Be(Token);
        credential.GetToken(Request, Ct).Should().Be(Token);
        credential.Timeout.Should().Be(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task Waiting_Too_Long_Should_Fail_As_A_Sign_In_Timeout_And_Cancel_The_Request()
    {
        var requested = CancellationToken.None;
        _inner.Setup(c => c.GetTokenAsync(Request, It.IsAny<CancellationToken>()))
            .Returns((TokenRequestContext _, CancellationToken token) =>
            {
                requested = token;
                return new ValueTask<AccessToken>(new TaskCompletionSource<AccessToken>().Task);
            });
        var credential = new TimeLimitedCredential(_inner.Object, _time, TimeSpan.FromMinutes(3));

        var pending = credential.GetTokenAsync(Request, Ct).AsTask();
        _time.Advance(TimeSpan.FromMinutes(3));
        var error = (await pending.Invoking(t => t).Should().ThrowAsync<SignInTimeoutException>()).Which;

        error.Timeout.Should().Be(TimeSpan.FromMinutes(3));
        error.Message.Should().Be("サインインの画面で 3 分以内にサインインされなかったため、ジョブを止めました。");
        requested.IsCancellationRequested.Should().BeTrue("待っていた要求も取り消す");
        DesktopMessages.ForAzure(error).Should().Be(error.Message + DesktopMessages.SignInAgain);
    }

    [Fact]
    public async Task Canceling_Should_Not_Be_Reported_As_A_Timeout()
    {
        _inner.Setup(c => c.GetTokenAsync(Request, It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<AccessToken>(new TaskCompletionSource<AccessToken>().Task));
        using var cancel = new CancellationTokenSource();
        var credential = new TimeLimitedCredential(_inner.Object, _time);

        var pending = credential.GetTokenAsync(Request, cancel.Token).AsTask();
        await cancel.CancelAsync();

        await pending.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
    }
}
