using System.Text.RegularExpressions;
using FluentAssertions;
using TTDeployTool.Application.Deletion;
using TTDeployTool.Application.Workflows;
using TTDeployTool.Domain.Deployment;
using Xunit;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>
/// CLI・MCP と共通の誤りと警告の文を、画面の名前で書き直す（オーナーの依頼、2026-11-16: 画面やメニューに無い名前を使わない）。
/// 入力は、Domain と Application が実際に作る文。
/// </summary>
public sealed class DesktopTermsTests
{
    // CLI のオプション名・MCP の引数名（画面には無い名前）
    private static readonly Regex CliNames = new(@"\b(subscription|resource-group|db-type|tracker-password|db-password|tt-tag|(tt|db|redis)-(cpu|memory)|expires-on|persist-db|keep-db-data|include-untagged|app_name|persist_db|keep_db_data|location)\b|--[a-z]|group（|README の「");

    private static DeployOptions Options(Action<DeployOptions> change)
    {
        var o = new DeployOptions
        {
            Subscription = "", ResourceGroup = "", DbType = "postgresql", DbPassword = "", TrackerPassword = "", TimetrackerTag = "",
            TimetrackerCpu = 0, TimetrackerMemoryGi = 0, DbCpu = 0, DbMemoryGi = 0, RedisCpu = 0, RedisMemoryGi = 0, ExpiresOn = "x",
            Group = new string('g', 65),
        };
        change(o);
        return o;
    }

    [Fact]
    public void Every_Validation_Error_Should_Use_The_Screen_Names()
    {
        var errors = DeploymentValidator.GetErrors(Options(_ => { }), ValidationTarget.Azure)
            .Concat(DeploymentValidator.GetErrors(Options(o => (o.DbType, o.DbPassword, o.TimetrackerTag) = ("sqlserver", "abcdefgh", "11.0.1-linux-postgres")), ValidationTarget.Azure))
            .Concat(DeploymentValidator.GetErrors(Options(o => o.DbType = "mysql"), ValidationTarget.Azure))
            .Distinct().ToList();

        var screen = DesktopTerms.ForScreen(errors);

        screen.Should().Contain([
            "サブスクリプションを選んでください。", "リソースグループ名を入力してください。", "DB 種別を PostgreSQL か SQL Server から選んでください。",
            "TimeTracker のパスワードを 8 文字以上で入力してください。", "DB のパスワードを 8 文字以上で入力してください。",
            "TimeTracker の CPU には、0 より大きい数値を入れてください: 0", "TimeTracker のメモリには、0 より大きい数値を入れてください: 0",
            "DB の CPU には、0 より大きい数値を入れてください: 0", "Redis のメモリには、0 より大きい数値を入れてください: 0",
        ]);
        screen.Should().Contain(m => m.StartsWith("DB のパスワードが SQL Server のパスワード要件", StringComparison.Ordinal));
        screen.Should().Contain(m => m.StartsWith("TimeTracker のイメージのタグが空です。例: ", StringComparison.Ordinal));
        screen.Should().Contain(m => m.StartsWith("DB 種別（sqlserver）と TimeTracker のイメージのタグ（11.0.1-linux-postgres）の組み合わせが合いません。", StringComparison.Ordinal));
        screen.Should().Contain(m => m.StartsWith("利用可能期限日を、正しい日付にしてください: x", StringComparison.Ordinal));
        screen.Should().Contain(m => m.StartsWith("グループ名は 64 文字までにしてください", StringComparison.Ordinal));
        screen.Should().NotContain(m => CliNames.IsMatch(m));
    }

    [Fact]
    public void Warnings_Notices_And_Reasons_Should_Use_The_Screen_Names()
    {
        var o = Options(o => (o.TimetrackerCpu, o.TimetrackerMemoryGi, o.DbCpu, o.DbMemoryGi, o.RedisCpu, o.RedisMemoryGi, o.TimetrackerTag, o.PersistDb) =
            (1, 2, 1, 2, 0.5, 1, "7.0.0-linux-postgres", true));
        var messages = DeploymentAdvisor.GetWarnings(o, ValidationTarget.Azure)
            .Concat(DeploymentAdvisor.GetWarnings(o, ValidationTarget.Compose))
            .Concat([
                DbPersistence.DataLossWarning("demo-db", currentlyPersisted: true, persist: false)!,
                DbPersistence.StorageNotice("ttdemo"),
                DeletionPlanner.OtherOwnerReason,
                string.Format(System.Globalization.CultureInfo.InvariantCulture, DeletionWorkflow.PermissionBlocker, "Microsoft.App/containerApps/write"),
                "既存のリソースグループ 'rg' のリージョン (westus) は location (japaneast) と異なります。Container Apps は japaneast に作成されます。",
                "既存の Container Apps Environment 'demo-env' のリージョン (westus) が location (japaneast) と異なります。location に westus を指定するか、別の app_name を指定してください。",
            ]).ToList();

        var screen = DesktopTerms.ForScreen(messages);

        screen.Should().Contain(m => m.StartsWith("TimeTracker の CPU・メモリが 1 vCPU / 2Gi です。", StringComparison.Ordinal));
        screen.Should().Contain(m => m.StartsWith("TimeTracker のイメージのタグ「7.0.0-linux-postgres」は既定のタグ", StringComparison.Ordinal));
        screen.Should().Contain(m => m.StartsWith("「DB のデータ」の「Azure Files に永続化する」は Azure 向けの設定のため、ローカル検証（Dry-run）では使いません", StringComparison.Ordinal));
        screen.Should().Contain(m => m.Contains("もう一度、手順「DB」の「DB のデータ」で「Azure Files に永続化する」を選んでデプロイすると元に戻ります。"));
        screen.Should().Contain(m => m.Contains("Azure から削除するときは、「DB のデータを残す」にチェックを入れない限り、"));
        screen.Should().Contain(m => m.Contains("（「管理タグの無いものも対象にする」でも対象にしません）"));
        screen.Should().Contain("権限が不足しています: Microsoft.App/containerApps/write。" + DesktopTerms.RbacGuide);
        screen.Should().Contain(m => m.Contains("(westus) は、選んだリージョン (japaneast) と異なります。"));
        screen.Should().Contain(m => m.EndsWith("(westus) が、選んだリージョン (japaneast) と異なります。手順「対象」のリージョンを westus にするか、別のアプリ名にしてください。", StringComparison.Ordinal));
        screen.Should().NotContain(m => CliNames.IsMatch(m));
        DesktopTerms.RbacGuide.Should().Contain("「共同作成者（Contributor）」").And.EndWith(DesktopTerms.RbacDocUrl);
    }

    [Fact]
    public void Other_Messages_Should_Be_Kept()
    {
        DesktopTerms.ForScreen("アプリ名が空です。").Should().Be("アプリ名が空です。");
        DesktopTerms.ForScreen("TimeTracker の CPU には、数値を入れてください: x").Should().Be("TimeTracker の CPU には、数値を入れてください: x");
    }
}
