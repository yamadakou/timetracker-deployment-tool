using Azure;
using Azure.Core;
using Azure.Identity;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Application.Workflows;
using TTDeployTool.Controller.Desktop.Core.Mvvm;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Infrastructure.Azure;
using Xunit;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

public class MvvmTests
{
    private sealed class Sample : ObservableObject
    {
        private int _value;

        public int Value
        {
            get => _value;
            set => SetProperty(ref _value, value);
        }

        public bool Set(int value) => SetProperty(ref _value, value, nameof(Value));
    }

    [Fact]
    public void SetProperty_Should_Notify_Only_When_The_Value_Changes()
    {
        var sample = new Sample();
        // 購読していなくても通知できる
        sample.Set(1).Should().BeTrue();
        var changes = new ChangeRecorder(sample);

        sample.Set(1).Should().BeFalse();
        sample.Value = 2;
        sample.Value.Should().Be(2);

        changes.Names.Should().Equal("Value");
    }

    [Fact]
    public async Task AsyncCommand_Should_Not_Run_Twice_At_The_Same_Time()
    {
        var release = new TaskCompletionSource();
        var runs = 0;
        var command = new AsyncCommand(async () =>
        {
            runs++;
            await release.Task;
        });
        // 購読していなくても通知できる
        command.RaiseCanExecuteChanged();
        var changed = 0;
        command.CanExecuteChanged += (_, _) => changed++;

        command.Execute(null);
        command.IsRunning.Should().BeTrue();
        command.CanExecute(null).Should().BeFalse();
        await command.ExecuteAsync();
        release.SetResult();
        await Task.Yield();

        runs.Should().Be(1);
        changed.Should().Be(2);
        command.IsRunning.Should().BeFalse();
        command.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task AsyncCommand_Should_Not_Run_When_It_Cannot_Execute()
    {
        var runs = 0;
        var command = new AsyncCommand(() =>
        {
            runs++;
            return Task.CompletedTask;
        }, () => false);

        await command.ExecuteAsync();

        runs.Should().Be(0);
        command.CanExecute(null).Should().BeFalse();
    }
}

public class DesktopMessagesTests
{
    [Theory]
    [InlineData(WorkflowFailure.PlanNotFound, DesktopMessages.PlanNotFound)]
    [InlineData(WorkflowFailure.PlanExpired, DesktopMessages.PlanExpired)]
    [InlineData(WorkflowFailure.DataLossNotAcknowledged, DesktopMessages.DataLossNotAcknowledged)]
    [InlineData(WorkflowFailure.AppNameMismatch, DesktopMessages.AppNameMismatch)]
    [InlineData(WorkflowFailure.Changed, DesktopMessages.Changed)]
    public void Workflow_Failures_Should_Guide_To_The_Next_Step(WorkflowFailure failure, string expected)
    {
        DesktopMessages.For(new WorkflowException(failure)).Should().Be(expected);
    }

    [Theory]
    [InlineData(WorkflowFailure.OutOfScope)]
    [InlineData(WorkflowFailure.Blocked)]
    public void Blockers_Should_Be_Listed(WorkflowFailure failure)
    {
        DesktopMessages.For(new WorkflowException(failure, ["a", "b"])).Should().Be("実行の直前の確認で、実行できない状態になっていました:\n- a\n- b");
    }

    [Fact]
    public void Conflicts_Should_Name_The_Target()
    {
        DesktopMessages.For(new WorkflowException(WorkflowFailure.Conflict, target: new JobTarget("sub", "rg", "demo"), conflictingJobId: "job_1"))
            .Should().StartWith("同じ対象（rg / demo）のデプロイまたは削除が実行中です。");
    }

    [Fact]
    public void Azure_Failures_Should_Be_Explained()
    {
        DesktopMessages.ForAzure(new WorkflowException(WorkflowFailure.Changed)).Should().Be(DesktopMessages.Changed);
        DesktopMessages.ForAzure(new AuthenticationRequiredException("x", new TokenRequestContext([]))).Should().StartWith("サインインが必要です。");
        DesktopMessages.ForAzure(new CredentialUnavailableException("無い")).Should().StartWith("サインインできませんでした。").And.EndWith("（無い）");
        DesktopMessages.ForAzure(new AuthenticationFailedException("AADSTS50079: x")).Should().Be(DesktopMessages.MfaRegistrationRequired);
        DesktopMessages.ForAzure(new AuthenticationFailedException("AADSTS50076: x")).Should().Be(DesktopMessages.MfaRequired);
        DesktopMessages.ForAzure(new AuthenticationFailedException("失敗")).Should().StartWith("Azure の認証に失敗しました。").And.EndWith("（失敗）");
        DesktopMessages.ForAzure(new RequestFailedException(403, "拒否", "AuthorizationFailed", null)).Should().Be("Azure がエラーを返しました（403 AuthorizationFailed）: 拒否");
        DesktopMessages.ForAzure(new InvalidOperationException("何か")).Should().Be("エラーが発生しました: 何か");
    }
}

public class ServicesTests
{
    [Fact]
    public void AzureClients_Should_Create_Azure_Clients_For_The_Credential()
    {
        var credential = Mock.Of<TokenCredential>();
        var clients = new AzureClients();

        clients.Tenants(credential).Should().BeOfType<AzureTenantDirectory>();
        clients.Subscriptions(credential).Should().BeOfType<AzureSubscriptionDirectory>();
        clients.Gateway(new DesktopDeploymentLogger(Mock.Of<ILogger>()), credential).Should().BeOfType<AzureSdkExecutor>();
        clients.Deployments(credential).Should().BeOfType<ResourceGraphDeploymentDirectory>();
    }

    [Fact]
    public void DesktopDeploymentLogger_Should_Write_Each_Level()
    {
        var logger = new Mock<ILogger>();
        var levels = new List<LogLevel>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        logger.Setup(l => l.Log(It.IsAny<LogLevel>(), It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(new InvocationAction(i => levels.Add((LogLevel)i.Arguments[0]!)));
        var target = new DesktopDeploymentLogger(logger.Object);

        target.Info("i");
        target.Verbose("v");
        target.Warn("w");
        target.Success("s");
        target.Error("e");
        target.Exception(new InvalidOperationException("x"));

        levels.Should().Equal(LogLevel.Information, LogLevel.Debug, LogLevel.Warning, LogLevel.Information, LogLevel.Error, LogLevel.Error);
    }
}
