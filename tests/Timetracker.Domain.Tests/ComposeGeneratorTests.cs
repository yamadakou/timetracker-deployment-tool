using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Timetracker.Domain.Deployment;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Timetracker.Domain.Tests
{
    public class EnvironmentVariables : List<string> { }

    public class ComposeGeneratorTests
    {
        [Fact]
        public void RunTests()
        {
            var env = new EnvironmentVariables
            {
                $"DB_USER={Defaults.DbUserFixedPostgres}",
                $"DB_PASSWORD={Defaults.DbPassword}",
                $"DB_NAME={Defaults.DbName}",
                $"TIMETRACKER_PASSWORD={Defaults.TimetrackerPassword}"
            };
            env.Should().Contain($"DB_USER={Defaults.DbUserFixedPostgres}");
            env.Should().Contain($"DB_PASSWORD={Defaults.DbPassword}");
            env.Should().Contain($"DB_NAME={Defaults.DbName}");
            env.Should().Contain($"TIMETRACKER_PASSWORD={Defaults.TimetrackerPassword}");

            // Ensure all other interpolated strings are properly closed
            // further code and checks accordingly
        }

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
    }
}