using FluentAssertions;
using TTDeployTool.Domain.Deployment;
using Xunit;

namespace TTDeployTool.Domain.Tests;

public class DeploymentValidatorTests
{
    private static DeployOptions Valid(string dbType = "postgresql") => new()
    {
        Subscription = "sub",
        ResourceGroup = "rg",
        DbType = dbType,
        DbPassword = "Passw0rd!x",
        TrackerPassword = "Passw0rd!y",
        TimetrackerTag = Defaults.GetDefaultTimetrackerTag(dbType),
    };

    [Theory]
    [InlineData("postgresql", ValidationTarget.Azure)]
    [InlineData("sqlserver", ValidationTarget.Compose)]
    public void Should_Return_No_Errors_For_Valid_Options(string dbType, ValidationTarget target)
    {
        DeploymentValidator.GetErrors(Valid(dbType), target).Should().BeEmpty();
    }

    [Fact]
    public void Should_List_All_Errors_In_The_Same_Order_As_The_Cli()
    {
        var o = Valid();
        o.Subscription = "";
        o.ResourceGroup = " ";
        o.AppName = "Bad";
        o.TimetrackerCpu = 0;
        o.TimetrackerMemoryGi = -1;
        o.DbCpu = double.NaN;
        o.DbMemoryGi = double.PositiveInfinity;
        o.RedisCpu = 0;
        o.RedisMemoryGi = 0;
        o.TrackerPassword = "short";
        o.DbPassword = "";
        o.TimetrackerTag = "11.0.1-linux-mssql";

        var errors = DeploymentValidator.GetErrors(o, ValidationTarget.Azure);

        errors.Should().HaveCount(12);
        errors[0].Should().Be("subscription は必須です。");
        errors[1].Should().Be("resource-group は必須です。");
        errors[2].Should().Contain("アプリ名 'Bad'");
        errors.Skip(3).Take(6).Should().Equal(
            "tt-cpu には正の数値を指定してください: 0",
            "tt-memory には正の数値を指定してください: -1",
            "db-cpu には正の数値を指定してください: NaN",
            "db-memory には正の数値を指定してください: Infinity",
            "redis-cpu には正の数値を指定してください: 0",
            "redis-memory には正の数値を指定してください: 0");
        errors[9].Should().Be("tracker-password が不足または短すぎます。");
        errors[10].Should().Be("db-password が不足または短すぎます。");
        errors[11].Should().StartWith("不適切な組み合わせ: db-type='postgresql' と tt-tag='11.0.1-linux-mssql'");
        // 最初のエラーは CLI（ComposeGenerator.Validate）が表示するものと同じ
        var act = () => ComposeGenerator.Validate(o);
        act.Should().Throw<ArgumentException>().WithMessage(errors[0]);
    }

    [Fact]
    public void Should_Not_Require_Azure_Settings_For_Compose()
    {
        var o = Valid();
        o.Subscription = "";
        o.ResourceGroup = "";

        DeploymentValidator.GetErrors(o, ValidationTarget.Compose).Should().BeEmpty();
        DeploymentValidator.GetErrors(o, ValidationTarget.Azure).Should().Equal("subscription は必須です。", "resource-group は必須です。");
    }

    [Fact]
    public void Should_Skip_Checks_That_Depend_On_An_Invalid_Db_Type()
    {
        var o = Valid();
        o.DbType = "mysql";
        o.DbPassword = "weakpass";

        DeploymentValidator.GetErrors(o, ValidationTarget.Compose).Should().Equal("db-type は postgresql または sqlserver を指定してください。");
    }

    [Fact]
    public void Should_Check_Sql_Server_Password_Policy_Only_When_The_Password_Is_Present()
    {
        var missing = Valid("sqlserver");
        missing.DbPassword = "";
        var weak = Valid("sqlserver");
        weak.DbPassword = "password";

        DeploymentValidator.GetErrors(missing, ValidationTarget.Compose).Should().Equal("db-password が不足または短すぎます。");
        DeploymentValidator.GetErrors(weak, ValidationTarget.Compose).Should().ContainSingle().Which.Should().Contain("SQL Server のパスワード要件");
    }

    [Fact]
    public void Should_Not_Check_The_Combination_When_The_Tag_Is_Empty()
    {
        var o = Valid();
        o.TimetrackerTag = " ";

        DeploymentValidator.GetErrors(o, ValidationTarget.Compose).Should().ContainSingle().Which.Should().StartWith("tt-tag（timetracker イメージタグ）が空です。");
    }
}

public class DeploymentAdvisorTests
{
    private static DeployOptions Options(string dbType = "postgresql") => new()
    {
        Subscription = "sub", ResourceGroup = "rg", DbType = dbType, DbPassword = "Passw0rd!x", TrackerPassword = "Passw0rd!y",
        TimetrackerTag = Defaults.GetDefaultTimetrackerTag(dbType),
    };

    [Fact]
    public void Should_Return_No_Warnings_For_Defaults()
    {
        DeploymentAdvisor.GetWarnings(Options(), ValidationTarget.Azure).Should().BeEmpty();
    }

    [Fact]
    public void Should_Warn_About_Sample_Db_Only_For_Sql_Server_On_Azure()
    {
        DeploymentAdvisor.GetWarnings(Options("sqlserver"), ValidationTarget.Azure).Should().ContainSingle().Which.Should().Contain("サンプル DB を作成できません");
        DeploymentAdvisor.GetWarnings(Options("sqlserver"), ValidationTarget.Compose).Should().BeEmpty();
    }

    [Theory]
    [InlineData(1.0, 2.0, 1.0, 2.0, 0.5, 1.0, "tt")]
    [InlineData(1.25, 2.0, 1.0, 2.0, 0.5, 1.0, "tt")]
    [InlineData(1.25, 2.5, 0.25, 0.5, 0.5, 1.0, "db")]
    [InlineData(1.25, 2.5, 0.5, 0.75, 0.5, 1.0, "db")]
    [InlineData(1.25, 2.5, 0.5, 1.0, 0.25, 0.25, "redis")]
    [InlineData(1.25, 2.5, 0.5, 1.0, 0.1, 0.5, "redis")]
    [InlineData(1.25, 2.5, 0.5, 1.0, 0.25, 0.5, null)]
    public void Should_Warn_When_A_Spec_Is_Below_The_Sample_Db_Minimum(double ttCpu, double ttMemory, double dbCpu, double dbMemory, double redisCpu, double redisMemory, string? warned)
    {
        var o = Options();
        (o.TimetrackerCpu, o.TimetrackerMemoryGi, o.DbCpu, o.DbMemoryGi, o.RedisCpu, o.RedisMemoryGi) = (ttCpu, ttMemory, dbCpu, dbMemory, redisCpu, redisMemory);

        var warnings = DeploymentAdvisor.GetWarnings(o, ValidationTarget.Azure);

        if (warned == null)
            warnings.Should().BeEmpty("実用上の最低スペックちょうどは警告しない");
        else
            warnings.Should().ContainSingle().Which.Should().StartWith($"{warned}-cpu / {warned}-memory（")
                .And.EndWith("サンプル DB を作れる実用上の最低スペック（TimeTracker 1.25 vCPU / 2.5Gi、DB 0.5 vCPU / 1Gi、Redis 0.25 vCPU / 0.5Gi）を下回るため、サンプル DB の作成ができない場合があります。");
        DeploymentAdvisor.GetWarnings(o, ValidationTarget.Compose).Should().BeEmpty("docker-compose にはスペックの上限を書かない");
    }

    [Fact]
    public void The_Warning_Should_Name_The_App_And_Its_Spec()
    {
        var o = Options();
        (o.TimetrackerCpu, o.TimetrackerMemoryGi) = (1.0, 2.0);
        DeploymentAdvisor.GetWarnings(o, ValidationTarget.Azure).Single().Should().StartWith("tt-cpu / tt-memory（TimeTracker）が 1 vCPU / 2Gi です。");
        DeploymentAdvisor.SampleDbMinimumOf("DB").Should().Be(new SampleDbMinimum("DB", "db", 0.5, 1.0));
    }

    [Fact]
    public void Should_Warn_About_A_Non_Default_Tag_Only_For_Known_Db_Types()
    {
        var o = Options();
        o.TimetrackerTag = "7.0.0-linux-postgres";
        var unknownDb = Options();
        unknownDb.DbType = "mysql";
        unknownDb.TimetrackerTag = "7.0.0";

        DeploymentAdvisor.GetWarnings(o, ValidationTarget.Compose).Should().ContainSingle()
            .Which.Should().Contain("'7.0.0-linux-postgres'").And.Contain("11.0.1-linux-postgres");
        DeploymentAdvisor.GetWarnings(unknownDb, ValidationTarget.Compose).Should().BeEmpty();
    }
}
