namespace TTDeployTool.Common;

/// <summary>
/// デプロイ処理のログ出力先。CLI はコンソール（<see cref="SimpleLogger"/>）、
/// 他のフロントエンド（MCP など）は独自の出力先を実装して差し替える。
/// </summary>
public interface IDeploymentLogger
{
    void Info(string message);
    void Verbose(string message);
    void Warn(string message);
    void Success(string message);
    void Error(string message);

    /// <summary>例外をエラーとして出力する。</summary>
    void Exception(Exception ex);
}
