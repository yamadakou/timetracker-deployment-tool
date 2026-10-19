using FluentAssertions;
using TTDeployTool.Application.Compose;
using TTDeployTool.Domain.Deployment;
using Xunit;

namespace TTDeployTool.Application.Tests;

public sealed class ComposeExporterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tt-compose-" + Guid.NewGuid().ToString("N"));

    private static DeployOptions Opts() => new()
    {
        Subscription = "", ResourceGroup = "", AppName = "demo", DbPassword = "DbPassw0rd!", TrackerPassword = "TrackerPass!",
    };

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Write_Should_Create_The_Directory_And_Both_Files()
    {
        var directory = Path.Combine(_root, "out");

        var (files, compose) = ComposeExporter.Write(directory, Opts(), overwrite: false);

        files.Should().Be(new ComposeFiles(directory, Path.Combine(directory, "docker-compose.yml"), Path.Combine(directory, ".env")));
        File.ReadAllText(files.ComposePath).Should().Be(compose).And.Be(ComposeGenerator.GenerateCompose(Opts()));
        File.ReadAllText(files.EnvPath).Should().Be(ComposeGenerator.GenerateEnv(Opts()));
        compose.Should().NotContain("DbPassw0rd!");
        ComposeExporter.AnyExists(directory).Should().BeTrue();
    }

    [Theory]
    [InlineData("docker-compose.yml")]
    [InlineData(".env")]
    public void Write_Should_Not_Overwrite_Existing_Files_Unless_Allowed(string existing)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, existing), "old");

        var act = () => ComposeExporter.Write(_root, Opts(), overwrite: false);

        var ex = act.Should().Throw<ComposeExportException>().Which;
        ex.Failure.Should().Be(ComposeExportFailure.AlreadyExists);
        ex.Directory.Should().Be(_root);
        ex.Detail.Should().BeNull();
        ex.Message.Should().Be("AlreadyExists");
        File.ReadAllText(Path.Combine(_root, existing)).Should().Be("old");

        ComposeExporter.Write(_root, Opts(), overwrite: true);
        File.ReadAllText(Path.Combine(_root, existing)).Should().NotBe("old");
    }

    [Fact]
    public void AnyExists_Should_Be_False_For_An_Empty_Or_Missing_Directory()
    {
        ComposeExporter.AnyExists(_root).Should().BeFalse();
    }

    [Fact]
    public void Write_Should_Report_Files_That_Cannot_Be_Written()
    {
        // 出力先と同じ名前のファイルがあると、ディレクトリを作れない
        Directory.CreateDirectory(_root);
        var blocked = Path.Combine(_root, "file");
        File.WriteAllText(blocked, "");

        var act = () => ComposeExporter.Write(blocked, Opts(), overwrite: true);

        var ex = act.Should().Throw<ComposeExportException>().Which;
        ex.Failure.Should().Be(ComposeExportFailure.WriteFailed);
        ex.Detail.Should().NotBeNullOrEmpty();
        ex.InnerException.Should().BeAssignableTo<IOException>();
    }
}
