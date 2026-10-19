using TTDeployTool.Domain.Deployment;

namespace TTDeployTool.Application.Compose;

/// <summary>Dry-run で書き込むファイルのパス。</summary>
public sealed record ComposeFiles(string Directory, string ComposePath, string EnvPath);

public enum ComposeExportFailure
{
    /// <summary>docker-compose.yml か .env が既にあり、上書きを許していない。</summary>
    AlreadyExists,

    /// <summary>書き込めなかった（<see cref="ComposeExportException.Detail"/> に理由）。</summary>
    WriteFailed,
}

public sealed class ComposeExportException : Exception
{
    public ComposeExportException(ComposeExportFailure failure, string directory, string? detail = null, Exception? inner = null)
        : base(detail ?? failure.ToString(), inner)
    {
        Failure = failure;
        Directory = directory;
        Detail = detail;
    }

    public ComposeExportFailure Failure { get; }

    public string Directory { get; }

    public string? Detail { get; }
}

/// <summary>
/// Dry-run の docker-compose.yml と .env を、利用者が選んだディレクトリに書き込む（MCP の stdio の generate_compose とデスクトップアプリで共通）。
/// .env はパスワードを平文で含むため、既存のファイルは上書きを許した場合だけ上書きする（以前の .env を失わないように）。
/// </summary>
public static class ComposeExporter
{
    public const string ComposeFileName = "docker-compose.yml";
    public const string EnvFileName = ".env";

    public static ComposeFiles PathsFor(string directory) =>
        new(directory, Path.Combine(directory, ComposeFileName), Path.Combine(directory, EnvFileName));

    /// <summary>docker-compose.yml か .env のどちらかが既にあるか。</summary>
    public static bool AnyExists(string directory)
    {
        var files = PathsFor(directory);
        return File.Exists(files.ComposePath) || File.Exists(files.EnvPath);
    }

    /// <summary>ディレクトリ（無ければ作る）に書き込み、docker-compose.yml の内容（秘密情報を含まない）を返す。</summary>
    /// <param name="options">検証済みの設定</param>
    /// <exception cref="ComposeExportException">既存のファイルがあり上書きを許していない、または書き込めなかった場合</exception>
    public static (ComposeFiles Files, string ComposeContent) Write(string directory, DeployOptions options, bool overwrite)
    {
        var files = PathsFor(directory);
        if (!overwrite && AnyExists(directory))
            throw new ComposeExportException(ComposeExportFailure.AlreadyExists, directory);

        var compose = ComposeGenerator.GenerateCompose(options);
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            File.WriteAllText(files.ComposePath, compose);
            File.WriteAllText(files.EnvPath, ComposeGenerator.GenerateEnv(options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ComposeExportException(ComposeExportFailure.WriteFailed, directory, ex.Message, ex);
        }
        return (files, compose);
    }
}
