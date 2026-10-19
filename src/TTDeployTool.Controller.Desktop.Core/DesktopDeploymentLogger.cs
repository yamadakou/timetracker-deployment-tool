using Microsoft.Extensions.Logging;
using TTDeployTool.Common;

namespace TTDeployTool.Controller.Desktop.Core;

/// <summary>
/// 共有層のログ（<see cref="IDeploymentLogger"/>）を、アプリの診断用のログ（<see cref="ILogger"/>）に渡す。
/// 計画や状態の確認で Azure を読むときに使う（ジョブのログは、ジョブの画面に表示する）。
/// </summary>
public sealed class DesktopDeploymentLogger : IDeploymentLogger
{
    private readonly ILogger _logger;

    public DesktopDeploymentLogger(ILogger logger)
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
