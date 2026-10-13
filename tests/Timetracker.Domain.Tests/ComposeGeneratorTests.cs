using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Timetracker.Domain.Deployment;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Timetracker.Domain.Tests
{
    public class ComposeGeneratorTests
    {
        [Fact]
        public void GenerateCompose_Should_Include_ASPNETCORE_URLS_Environment_Variable()
        {
            // Arrange
            var options = new DeployOptions
            {
                DbType = "postgresql",
                DbPassword = "TestPassword123!",
                TrackerPassword = "TrackerPassword123!",
                TimetrackerTag = "7.0-linux-postgres",
                Subscription = "test-subscription",
                ResourceGroup = "test-rg"
            };

            // Act
            var composeYml = ComposeGenerator.GenerateCompose(options);

            // Assert
            EnvironmentOf(composeYml, "timetracker")["ASPNETCORE_URLS"].Should().Be("http://0.0.0.0:8080");
        }

        [Fact]
        public void GenerateCompose_Should_Include_TTNX_DB_TYPE_Environment_Variable()
        {
            // Arrange
            var options = new DeployOptions
            {
                DbType = "postgresql",
                DbPassword = "TestPassword123!",
                TrackerPassword = "TrackerPassword123!",
                TimetrackerTag = "7.0-linux-postgres",
                Subscription = "test-subscription",
                ResourceGroup = "test-rg"
            };

            // Act
            var composeYml = ComposeGenerator.GenerateCompose(options);

            // Assert
            EnvironmentOf(composeYml, "timetracker")["TTNX_DB_TYPE"].Should().Be("${TTNX_DB_TYPE}");
        }

        [Fact]
        public void GenerateEnv_Should_Include_TTNX_DB_TYPE_For_Postgresql()
        {
            // Arrange
            var options = new DeployOptions
            {
                DbType = "postgresql",
                DbPassword = "TestPassword123!",
                TrackerPassword = "TrackerPassword123!",
                TimetrackerTag = "7.0-linux-postgres",
                Subscription = "test-subscription",
                ResourceGroup = "test-rg"
            };

            // Act
            var env = ComposeGenerator.GenerateEnv(options);

            // Assert
            env.Should().Contain("TTNX_DB_TYPE=postgresql");
        }

        [Fact]
        public void GenerateEnv_Should_Include_TTNX_DB_TYPE_For_SqlServer()
        {
            // Arrange
            var options = new DeployOptions
            {
                DbType = "sqlserver",
                DbPassword = "TestPassword123!",
                TrackerPassword = "TrackerPassword123!",
                TimetrackerTag = "7.0-linux-mssql",
                Subscription = "test-subscription",
                ResourceGroup = "test-rg"
            };

            // Act
            var env = ComposeGenerator.GenerateEnv(options);

            // Assert
            env.Should().Contain("TTNX_DB_TYPE=sqlserver");
        }

        private static DeployOptions CreateOptions(string dbType) => new()
        {
            DbType = dbType,
            DbPassword = "TestPassword123!",
            TrackerPassword = "TrackerPassword123!",
            TimetrackerTag = dbType == "postgresql" ? "7.0-linux-postgres" : "7.0-linux-mssql",
            Subscription = "test-subscription",
            ResourceGroup = "test-rg"
        };

        private static YamlMappingNode ParseCompose(string composeYml)
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(composeYml));
            return (YamlMappingNode)stream.Documents[0].RootNode;
        }

        private static Dictionary<string, string> EnvironmentOf(string composeYml, string service)
        {
            var root = ParseCompose(composeYml);
            var services = (YamlMappingNode)root["services"];
            var svc = (YamlMappingNode)services[service];
            // docker compose は environment にマップまたはリストのみ受け付ける
            svc["environment"].Should().BeOfType<YamlMappingNode>();
            return ((YamlMappingNode)svc["environment"]).Children
                .ToDictionary(kv => ((YamlScalarNode)kv.Key).Value!, kv => ((YamlScalarNode)kv.Value).Value!);
        }

        [Theory]
        [InlineData("postgresql")]
        [InlineData("sqlserver")]
        public void GenerateCompose_Should_Produce_Valid_Yaml_With_Mapping_Environments(string dbType)
        {
            var composeYml = ComposeGenerator.GenerateCompose(CreateOptions(dbType));

            var tt = EnvironmentOf(composeYml, "timetracker");
            tt["APP_PASSWORD"].Should().Be("${TIMETRACKER_PASSWORD}");
            tt["DB_HOST"].Should().Be("db");

            var db = EnvironmentOf(composeYml, "db");
            if (dbType == "postgresql")
                db["POSTGRES_PASSWORD"].Should().Be("${DB_PASSWORD}");
            else
                db["ACCEPT_EULA"].Should().Be("Y");
        }
    
        [Fact]
        public void GenerateCompose_SqlServer_Should_Use_MSSQL_SA_PASSWORD_And_Not_Publish_Db_Port()
        {
            var composeYml = ComposeGenerator.GenerateCompose(CreateOptions("sqlserver"));

            var db = EnvironmentOf(composeYml, "db");
            db["MSSQL_SA_PASSWORD"].Should().Be("${DB_PASSWORD}");
            db.Should().NotContainKey("SA_PASSWORD"); // 非推奨

            var services = (YamlMappingNode)ParseCompose(composeYml)["services"];
            ((YamlMappingNode)services["db"]).Children.Keys.Select(k => ((YamlScalarNode)k).Value)
                .Should().NotContain("ports"); // DB をホストに公開しない
        }
    
        [Theory]
        [InlineData("postgresql")]
        [InlineData("sqlserver")]
        public void GenerateCompose_TTNX_DB_SERVER_Should_Be_Host_Only(string dbType)
        {
            // ポートは TTNX_DB_PORT で別指定（イメージ側で SQL Server は "server,port" に連結される）
            var tt = EnvironmentOf(ComposeGenerator.GenerateCompose(CreateOptions(dbType)), "timetracker");
            tt["TTNX_DB_SERVER"].Should().Be("db");
            tt["TTNX_DB_PORT"].Should().Be("${DB_PORT}");
        }
    
        [Theory]
        [InlineData("postgresql")]
        [InlineData("sqlserver")]
        public void GenerateCompose_Timetracker_Should_Wait_For_Healthy_Db_And_Redis(string dbType)
        {
            // DB の起動完了前にアプリが接続して終了・再起動するのを防ぐ
            var services = (YamlMappingNode)ParseCompose(ComposeGenerator.GenerateCompose(CreateOptions(dbType)))["services"];
            var dependsOn = (YamlMappingNode)((YamlMappingNode)services["timetracker"])["depends_on"];
            foreach (var dep in new[] { "db", "redis" })
            {
                ((YamlScalarNode)((YamlMappingNode)dependsOn[dep])["condition"]).Value.Should().Be("service_healthy");
                ((YamlMappingNode)services[dep]).Children.Keys.Select(k => ((YamlScalarNode)k).Value).Should().Contain("healthcheck");
            }
        }
    
        [Fact]
        public void GenerateCompose_Postgres_Should_Not_PreCreate_Database()
        {
            // POSTGRES_DB で DB を事前作成すると、Timetracker のサンプルDB作成時にアプリが対象DBへの接続を保持したまま
            // DB を削除・復元しようとして失敗する（Exception while writing to stream）。DB はアプリのセットアップで作成させる。
            var composeYml = ComposeGenerator.GenerateCompose(CreateOptions("postgresql"));
            EnvironmentOf(composeYml, "db").Should().NotContainKey("POSTGRES_DB");

            var services = (YamlMappingNode)ParseCompose(composeYml)["services"];
            var test = string.Join(" ", ((YamlSequenceNode)((YamlMappingNode)((YamlMappingNode)services["db"])["healthcheck"])["test"])
                .Select(n => ((YamlScalarNode)n).Value));
            test.Should().Contain("-d postgres"); // 未作成の対象DBではなく既定DBで疎通確認する
        }
    
        private static List<string> VolumesOf(string composeYml, string service)
        {
            var svc = (YamlMappingNode)((YamlMappingNode)ParseCompose(composeYml)["services"])[service];
            return ((YamlSequenceNode)svc["volumes"]).Select(n => ((YamlScalarNode)n).Value!).ToList();
        }

        [Fact]
        public void GenerateCompose_SqlServer_Should_Share_Sample_Backup_With_Db_Container()
        {
            // SQL Server の RESTORE は DB サーバー側のファイルを読むため、アプリイメージ内のサンプルバックアップを
            // 同じパスで SQL Server コンテナからも参照できるようにする
            var composeYml = ComposeGenerator.GenerateCompose(CreateOptions("sqlserver"));

            VolumesOf(composeYml, "timetracker").Should().Contain("ttsample:/usr/local/timetracker/sample");
            VolumesOf(composeYml, "db").Should().Contain("ttsample:/usr/local/timetracker/sample:ro");
            ((YamlMappingNode)ParseCompose(composeYml)["volumes"]).Children.Keys
                .Select(k => ((YamlScalarNode)k).Value).Should().Contain("ttsample");
        }

        [Fact]
        public void GenerateCompose_Postgres_Should_Not_Need_Sample_Volume()
        {
            // PostgreSQL はアプリが SQL を接続経由で送るため共有不要
            ComposeGenerator.GenerateCompose(CreateOptions("postgresql")).Should().NotContain("ttsample");
        }
    }
}
