using System;
using FluentAssertions;
using Xunit;

namespace Timetracker.Domain.Tests
{
    public class ComposeGeneratorTests
    {
        [Fact]
        public void RunTests()
        {
            var env = new EnvironmentVariables();
            env.Should().Contain($"DB_USER={Defaults.DbUserFixedPostgres}");
            env.Should().Contain($"DB_PASSWORD={Defaults.DbPassword}");
            env.Should().Contain($"DB_NAME={Defaults.DbName}");
            env.Should().Contain($"TIMETRACKER_PASSWORD={Defaults.TimetrackerPassword}");

            // Ensure all other interpolated strings are properly closed
            // further code and checks accordingly
        }
    }
}