using Microsoft.Extensions.Logging;
using TTDeployTool.Common;

namespace TTDeployTool.Controller.Mcp;

/// <summary>
/// 共有層のログ（<see cref="IDeploymentLogger"/>）を Generic Host のロガーに渡す。
/// stdio では stdout が JSON-RPC 専用のため、<see cref="TTDeployTool.Common.SimpleLogger"/>（stdout に出す）は使わない。
/// </summary>
internal sealed class McpDeploymentLogger : IDeploymentLogger
{
    private readonly ILogger _logger;

    public McpDeploymentLogger(ILogger logger)
    {
        _logger = logger;
    }

    public void Info(string message) => _logger.LogInformation("{Message}", message);
    public void Verbose(string message) => _logger.LogDebug("{Message}", message);
    public void Warn(string message) => _logger.LogWarning("{Message}", message);
    public void Success(string message) => _logger.LogInformation("{Message}", message);
    public void Error(string message) => _logger.LogError("{Message}", message);
    public void Exception(Exception ex) => _logger.LogError(ex, "{Message}", ex.Message);
}
