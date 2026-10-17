using FluentAssertions;
using Timetracker.Domain.Deployment;
using Xunit;

namespace Timetracker.Domain.Tests;

/// <summary>DB のデータの永続化の規則（詳細設計 docs/design/db-persistence.md）。</summary>
public class DbPersistenceTests
{
    [Fact]
    public void StorageAccount_Should_Be_Stable_And_Follow_The_Azure_Rules()
    {
        var name = ResourceNaming.StorageAccount("00000000-0000-0000-0000-000000000000", "rg-tt", "timetracker");

        name.Should().MatchRegex("^tttimetracker[0-9a-f]{8}$");
        // 同じ入力（大文字小文字は区別しない）からは同じ名前
        ResourceNaming.StorageAccount("00000000-0000-0000-0000-000000000000", "RG-TT", "TimeTracker").Should().Be(name);
    }

    [Fact]
    public void StorageAccount_Should_Differ_By_Subscription_Resource_Group_And_App_Name()
    {
        var names = new[]
        {
            ResourceNaming.StorageAccount("sub1", "rg", "app"),
            ResourceNaming.StorageAccount("sub2", "rg", "app"),
            ResourceNaming.StorageAccount("sub1", "rg2", "app"),
            ResourceNaming.StorageAccount("sub1", "rg", "app2"),
        };

        names.Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("a-b-c")]
    [InlineData("abcdefghijklmn")]                // 14 文字（切り詰めない）
    [InlineData("abcdefghijklmnopqrstuvwxyz")]    // アプリ名の上限 26 文字（切り詰める）
    [InlineData("a1-b2-c3-d4-e5-f6-g7-h8-i9-j")]
    public void StorageAccount_Should_Be_3_To_24_Lowercase_Letters_And_Digits(string appName)
    {
        var name = ResourceNaming.StorageAccount("sub", "rg", appName);

        name.Length.Should().BeInRange(3, ResourceNaming.StorageAccountNameMaxLength);
        name.Should().MatchRegex("^[a-z0-9]+$");
        name.Should().StartWith("tt" + new string(appName.Where(char.IsAsciiLetterOrDigit).Take(14).ToArray()));
    }

    [Fact]
    public void DbFileShare_Should_Be_The_Db_App_Name()
    {
        ResourceNaming.DbFileShare("demo").Should().Be("demo-db");
    }

    [Theory]
    [InlineData("postgresql", "/var/lib/postgresql/data", "uid=999,gid=999,dir_mode=0700,file_mode=0600")]
    [InlineData("sqlserver", "/var/opt/mssql", "uid=10001,gid=0,dir_mode=0770,file_mode=0660")]
    public void Mount_Should_Depend_On_The_Db_Type(string dbType, string path, string options)
    {
        DbPersistence.MountPath(dbType).Should().Be(path);
        DbPersistence.MountOptions(dbType).Should().Be(options);
    }

    [Fact]
    public void PgData_Should_Be_Under_The_Mount_Point()
    {
        DbPersistence.PostgresPgData.Should().StartWith(DbPersistence.PostgresMountPath + "/");
    }

    [Theory]
    [InlineData(false, false, "永続ボリュームを設定していないため")]
    [InlineData(false, true, "共有にあるデータから始まります")]
    [InlineData(true, false, "DB は空から始まります")]
    public void DataLossWarning_Should_Explain_Why_The_Data_Is_Lost(bool currentlyPersisted, bool persist, string expected)
    {
        DbPersistence.DataLossWarning("demo-db", currentlyPersisted, persist).Should().Contain("demo-db").And.Contain(expected);
    }

    [Fact]
    public void DataLossWarning_Should_Be_Null_When_Persistence_Continues()
    {
        DbPersistence.DataLossWarning("demo-db", true, true).Should().BeNull();
        DbPersistence.RestartNotice("demo-db").Should().Contain("demo-db").And.Contain("停止");
    }

    [Fact]
    public void StorageNotice_Should_Mention_The_Account_The_Price_And_The_Deletion()
    {
        DbPersistence.StorageNotice("ttdemo12345678").Should()
            .Contain("ttdemo12345678").And.Contain("料金").And.Contain("--keep-db-data").And.Contain("keep_db_data");
    }

    [Fact]
    public void WithPersistDb_Should_Copy_The_Options_Without_Changing_The_Original()
    {
        var original = new DeployOptions { Subscription = "sub", ResourceGroup = "rg", DbPassword = "x", TrackerPassword = "y" };

        var copy = original.WithPersistDb(true);

        copy.Should().NotBeSameAs(original);
        copy.PersistDb.Should().BeTrue();
        copy.Subscription.Should().Be("sub");
        original.PersistDb.Should().BeNull();
    }

    [Theory]
    [InlineData(true, ValidationTarget.Compose, true)]
    [InlineData(true, ValidationTarget.Azure, false)]
    [InlineData(false, ValidationTarget.Compose, false)]
    [InlineData(null, ValidationTarget.Compose, false)]
    public void Advisor_Should_Warn_That_Persist_Db_Is_Ignored_Locally(bool? persistDb, ValidationTarget target, bool warns)
    {
        var o = new DeployOptions
        {
            Subscription = "sub", ResourceGroup = "rg", DbPassword = "x", TrackerPassword = "y", PersistDb = persistDb,
        };

        DeploymentAdvisor.GetWarnings(o, target).Any(w => w.StartsWith("persist-db")).Should().Be(warns);
    }
}
