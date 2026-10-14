using System.ClientModel.Primitives;
using System.Text.Json;
using Azure.Core;
using Azure.ResourceManager.AppContainers;
using FluentAssertions;
using Timetracker.Domain.Deployment;
using Xunit;

namespace Timetracker.Controller.Cli.Tests;

public class ContainerAppSpecBuilderTests
{
    private const string DbPassword = "DbSecretP@ss1";
    private const string AppPassword = "AppSecretP@ss1";
    private static readonly ResourceIdentifier EnvId =
        new("/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.App/managedEnvironments/app-env");

    private static DeployOptions CreateOptions(string dbType) => new()
    {
        Subscription = "sub",
        ResourceGroup = "rg",
        DbType = dbType,
        DbPassword = DbPassword,
        TrackerPassword = AppPassword,
        TimetrackerTag = dbType == "postgresql" ? "11.0.1-linux-postgres" : "11.0.1-linux-mssql",
    };

    public static IEnumerable<object[]> AllApps()
    {
        foreach (var dbType in new[] { "postgresql", "sqlserver" })
        {
            var o = CreateOptions(dbType);
            yield return new object[] { dbType, "db", ContainerAppSpecBuilder.BuildDbApp(o, "japaneast", EnvId) };
            yield return new object[] { dbType, "redis", ContainerAppSpecBuilder.BuildRedisApp(o, "japaneast", EnvId) };
            yield return new object[] { dbType, "tt", ContainerAppSpecBuilder.BuildTimetrackerApp(o, "japaneast", EnvId, "app-db", "app-redis") };
        }
    }

    [Theory]
    [MemberData(nameof(AllApps))]
    public void Passwords_Should_Not_Appear_As_Plain_Environment_Values(string dbType, string app, ContainerAppData data)
    {
        _ = (dbType, app);
        var envValues = data.Template.Containers.SelectMany(c => c.Env).Select(e => e.Value);
        envValues.Should().NotContain(DbPassword).And.NotContain(AppPassword);
    }

    [Theory]
    [MemberData(nameof(AllApps))]
    public void Every_SecretRef_Should_Have_Matching_Secret(string dbType, string app, ContainerAppData data)
    {
        _ = (dbType, app);
        var secretNames = data.Configuration.Secrets.Select(s => s.Name).ToList();
        var refs = data.Template.Containers.SelectMany(c => c.Env).Where(e => e.SecretRef != null).Select(e => e.SecretRef);
        refs.Should().OnlyContain(r => secretNames.Contains(r));
        secretNames.Should().OnlyContain(n => System.Text.RegularExpressions.Regex.IsMatch(n, "^[a-z0-9][a-z0-9-]*[a-z0-9]$"));
    }

    [Theory]
    [InlineData("postgresql", "POSTGRES_PASSWORD")]
    [InlineData("sqlserver", "MSSQL_SA_PASSWORD")]
    public void DbApp_Should_Reference_Db_Password_Secret(string dbType, string envName)
    {
        var data = ContainerAppSpecBuilder.BuildDbApp(CreateOptions(dbType), "japaneast", EnvId);

        data.Configuration.Secrets.Should().ContainSingle(s => s.Name == ContainerAppSpecBuilder.DbPasswordSecretName && s.Value == DbPassword);
        data.Template.Containers[0].Env.Should().ContainSingle(e => e.Name == envName && e.SecretRef == ContainerAppSpecBuilder.DbPasswordSecretName);
    }

    [Fact]
    public void TimetrackerApp_Should_Reference_Secrets_And_Keep_All_Environment_Variables()
    {
        var data = ContainerAppSpecBuilder.BuildTimetrackerApp(CreateOptions("postgresql"), "japaneast", EnvId, "app-db", "app-redis");
        var env = data.Template.Containers[0].Env.ToDictionary(e => e.Name);

        env.Keys.Should().BeEquivalentTo(new[]
        {
            "ASPNETCORE_URLS", "TTNX_DB_TYPE", "TTNX_DB_SERVER", "TTNX_DB_USER", "TTNX_DB_PASSWORD", "TTNX_DB_NAME",
            "TTNX_DB_PORT", "TTNX_DB_OPTIONS", "TTNX_REDIS_GLOBALCACHE", "TTNX_REDIS_HANGFIRE", "TTNX_REDIS_BACKGROUNDJOB",
            "DB_HOST", "DB_PORT", "DB_USER", "DB_PASSWORD", "DB_NAME", "REDIS_HOST", "REDIS_PORT", "APP_PASSWORD",
            "TT_DB_TYPE", "TT_DB_SERVER", "TT_DB_USER", "TT_DB_PASSWORD", "TT_DB_NAME",
            "TT_DB_PORT", "TT_DB_OPTIONS", "TT_REDIS_GLOBALCACHE", "TT_REDIS_HANGFIRE", "TT_REDIS_BACKGROUNDJOB",
        });
        env["TTNX_DB_PASSWORD"].SecretRef.Should().Be(ContainerAppSpecBuilder.DbPasswordSecretName);
        env["DB_PASSWORD"].SecretRef.Should().Be(ContainerAppSpecBuilder.DbPasswordSecretName);
        env["APP_PASSWORD"].SecretRef.Should().Be(ContainerAppSpecBuilder.AppPasswordSecretName);
        env["TTNX_DB_SERVER"].Value.Should().Be("app-db"); // ポートは TTNX_DB_PORT で別指定（イメージ側で連結される）
        env["TTNX_REDIS_GLOBALCACHE"].Value.Should().Be("app-redis:6379");
        data.Configuration.Secrets.Should().Contain(s => s.Name == ContainerAppSpecBuilder.AppPasswordSecretName && s.Value == AppPassword);
        data.Configuration.Ingress.External.Should().BeTrue();
        data.Configuration.Ingress.TargetPort.Should().Be(8080);
    }

    [Fact]
    public void Serialized_Arm_Payload_Should_Use_SecretRef_In_Template()
    {
        var data = ContainerAppSpecBuilder.BuildTimetrackerApp(CreateOptions("postgresql"), "japaneast", EnvId, "app-db", "app-redis");
        using var doc = JsonDocument.Parse(ModelReaderWriter.Write(data).ToString());
        var template = doc.RootElement.GetProperty("properties").GetProperty("template").GetRawText();

        template.Should().Contain("\"secretRef\":\"app-password\"");
        template.Should().NotContain(AppPassword).And.NotContain(DbPassword);
    }

    [Fact]
    public void Memory_Should_Be_Formatted_With_Dot_Decimal_Regardless_Of_Culture()
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var o = CreateOptions("postgresql");
            ContainerAppSpecBuilder.BuildTimetrackerApp(o, "japaneast", EnvId, "app-db", "app-redis")
                .Template.Containers[0].Resources.Memory.Should().Be("1.5Gi");
            ContainerAppSpecBuilder.BuildDbApp(o, "japaneast", EnvId).Template.Containers[0].Resources.Memory.Should().Be("2Gi");
            ContainerAppSpecBuilder.BuildRedisApp(o, "japaneast", EnvId).Template.Containers[0].Resources.Memory.Should().Be("1Gi");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData("postgresql", "5432")]
    [InlineData("sqlserver", "1433")]
    public void TTNX_DB_SERVER_Should_Be_Host_Only_And_Port_Separate(string dbType, string port)
    {
        // イメージの entrypoint.sh は SQL Server の場合 "Data Source=${TTNX_DB_SERVER},${TTNX_DB_PORT}" と連結するため、
        // TTNX_DB_SERVER にポートを含めると "app-db:1433,1433" となり接続できない
        var env = ContainerAppSpecBuilder.BuildTimetrackerApp(CreateOptions(dbType), "japaneast", EnvId, "app-db", "app-redis")
            .Template.Containers[0].Env.ToDictionary(e => e.Name);
        env["TTNX_DB_SERVER"].Value.Should().Be("app-db");
        env["TTNX_DB_PORT"].Value.Should().Be(port);
    }

    [Fact]
    public void Postgres_DbApp_Should_Not_PreCreate_Database()
    {
        // compose と同様、DB は Timetracker のセットアップで作成させる（サンプルDB作成の失敗を防ぐ）
        ContainerAppSpecBuilder.BuildDbApp(CreateOptions("postgresql"), "japaneast", EnvId)
            .Template.Containers[0].Env.Select(e => e.Name).Should().NotContain("POSTGRES_DB");
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public void TimetrackerApp_Should_Set_Both_TTNX_And_TT_Prefixed_Variables(string dbType)
    {
        // timetracker 11.x のイメージは TT_ 接頭辞、7.x 以前は TTNX_ 接頭辞の環境変数を読むため、両方を同じ値で設定する
        var env = ContainerAppSpecBuilder.BuildTimetrackerApp(CreateOptions(dbType), "japaneast", EnvId, "app-db", "app-redis")
            .Template.Containers[0].Env.ToDictionary(e => e.Name);
        var ttnx = env.Keys.Where(k => k.StartsWith("TTNX_")).ToList();
        ttnx.Should().NotBeEmpty();
        foreach (var k in ttnx)
        {
            var newKey = "TT_" + k["TTNX_".Length..];
            env.Should().ContainKey(newKey);
            env[newKey].Value.Should().Be(env[k].Value);
            env[newKey].SecretRef.Should().Be(env[k].SecretRef);
        }
    }

    [Fact]
    public void ManagedEnvironment_Should_Serialize_Properties()
    {
        // properties が無いと Container Apps API が 400 (Must contain 'properties') を返す
        var data = ContainerAppSpecBuilder.BuildManagedEnvironment("japaneast");
        using var doc = JsonDocument.Parse(ModelReaderWriter.Write(data).ToString());
        doc.RootElement.TryGetProperty("properties", out var props).Should().BeTrue();
        props.ValueKind.Should().Be(JsonValueKind.Object);
    }
}
