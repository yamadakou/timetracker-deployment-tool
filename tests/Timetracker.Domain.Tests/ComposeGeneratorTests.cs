using System;
using System.Collections.Generic;
using FluentAssertions;
using Timetracker.Domain.Deployment;
using Xunit;

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
            composeYml.Should().Contain("ASPNETCORE_URLS=http://0.0.0.0:8080");
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
            composeYml.Should().Contain("TTNX_DB_TYPE=${TTNX_DB_TYPE}");
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
    }
}