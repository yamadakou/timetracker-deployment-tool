using FluentAssertions;
using Timetracker.Domain.Azure;
using Xunit;

namespace Timetracker.Domain.Tests;

public class PermissionEvaluatorTests
{
    private static PermissionSet Set(string[] actions, string[]? notActions = null) => new(actions, notActions ?? []);

    // 組み込みロールの定義（Microsoft Learn の JSON から本ツールに関係する部分を抜粋）
    private static readonly PermissionSet Contributor = Set(
        ["*"],
        ["Microsoft.Authorization/*/Delete", "Microsoft.Authorization/*/Write", "Microsoft.Authorization/elevateAccess/Action"]);

    private static readonly PermissionSet Reader = Set(["*/read"]);

    private static readonly PermissionSet ContainerAppsContributor = Set(
    [
        "Microsoft.Authorization/*/read", "Microsoft.Insights/alertRules/*",
        "Microsoft.App/containerApps/*/read", "Microsoft.App/containerApps/*/write", "Microsoft.App/containerApps/*/delete",
        "Microsoft.App/containerApps/*/action", "Microsoft.App/managedEnvironments/read", "Microsoft.App/managedEnvironments/*/read",
        "Microsoft.App/managedEnvironments/join/action", "Microsoft.App/managedEnvironments/checknameavailability/action",
    ]);

    [Theory]
    [InlineData("Microsoft.App/containerApps/write", "Microsoft.App/containerApps/write", true)]
    [InlineData("microsoft.app/CONTAINERAPPS/write", "Microsoft.App/containerApps/write", true)] // 大文字小文字を区別しない
    [InlineData("*", "Microsoft.App/containerApps/write", true)]
    [InlineData("Microsoft.App/*", "Microsoft.App/managedEnvironments/delete", true)]
    [InlineData("*/read", "Microsoft.App/containerApps/read", true)]
    [InlineData("*/read", "Microsoft.App/containerApps/write", false)]
    [InlineData("Microsoft.App/containerApps/read", "Microsoft.App/containerApps/write", false)]
    [InlineData("Microsoft.App/containerApps/*/write", "Microsoft.App/containerApps/write", true)] // "/*/" は 0 階層にも一致させる（許可側に倒す）
    [InlineData("Microsoft.App/containerApps/*/write", "Microsoft.App/containerApps/revisions/write", true)]
    [InlineData("Microsoft.App/containerApps/*/write", "Microsoft.App/containerApps/read", false)]
    [InlineData("Microsoft.App.Other/*", "Microsoft.App/containerApps/read", false)] // "." は文字どおりに扱う
    public void Matches_Should_Follow_Wildcard_Rules(string pattern, string action, bool expected)
    {
        PermissionEvaluator.Matches(pattern, action).Should().Be(expected);
    }

    [Fact]
    public void IsAllowed_Should_Exclude_NotActions_Of_The_Same_Set()
    {
        var set = Set(["Microsoft.App/*"], ["Microsoft.App/managedEnvironments/delete"]);

        PermissionEvaluator.IsAllowed([set], "Microsoft.App/containerApps/delete").Should().BeTrue();
        PermissionEvaluator.IsAllowed([set], "Microsoft.App/managedEnvironments/delete").Should().BeFalse();
    }

    [Fact]
    public void IsAllowed_Should_Allow_When_Any_Set_Allows()
    {
        // notActions は同じセットの actions だけを除外する（他のロールで許可されていれば許可）
        var restricted = Set(["Microsoft.App/*"], ["Microsoft.App/managedEnvironments/delete"]);
        var other = Set(["Microsoft.App/managedEnvironments/delete"]);

        PermissionEvaluator.IsAllowed([restricted, other], "Microsoft.App/managedEnvironments/delete").Should().BeTrue();
    }

    [Fact]
    public void IsAllowed_Should_Deny_When_No_Permissions()
    {
        PermissionEvaluator.IsAllowed([], "Microsoft.App/containerApps/read").Should().BeFalse();
    }

    [Fact]
    public void Missing_Should_Return_Denied_Actions_In_Order()
    {
        PermissionEvaluator.Missing([Reader], [AzurePermissions.ContainerAppWrite, AzurePermissions.ContainerAppRead, AzurePermissions.EnvironmentWrite])
            .Should().Equal(AzurePermissions.ContainerAppWrite, AzurePermissions.EnvironmentWrite);
    }

    private static readonly string[] DeployWithNewEnvironment = [.. AzurePermissions.Deploy, AzurePermissions.EnvironmentWrite];
    private static readonly string[] Delete = [AzurePermissions.ContainerAppDelete, AzurePermissions.EnvironmentDelete];

    [Fact]
    public void Contributor_Should_Allow_Deploy_And_Delete()
    {
        PermissionEvaluator.Missing([Contributor], [.. DeployWithNewEnvironment, .. Delete]).Should().BeEmpty();
    }

    [Fact]
    public void Reader_Should_Lack_Write_And_Delete()
    {
        PermissionEvaluator.Missing([Reader], [.. DeployWithNewEnvironment, .. Delete]).Should().Equal(
            AzurePermissions.ContainerAppWrite, AzurePermissions.EnvironmentJoin, AzurePermissions.EnvironmentWrite,
            AzurePermissions.ContainerAppDelete, AzurePermissions.EnvironmentDelete);
    }

    [Fact]
    public void ContainerAppsContributor_Should_Lack_Environment_Write_And_Delete()
    {
        // README の「Container Apps Contributor だけでは本ツールを使えない」に対応する（Environment を作成・削除できない）
        PermissionEvaluator.Missing([ContainerAppsContributor], [.. DeployWithNewEnvironment, .. Delete]).Should().Equal(
            AzurePermissions.EnvironmentWrite, AzurePermissions.EnvironmentDelete);
    }
}
