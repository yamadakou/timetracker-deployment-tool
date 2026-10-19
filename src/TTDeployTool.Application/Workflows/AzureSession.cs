using TTDeployTool.Common;
using TTDeployTool.Infrastructure.Azure;

namespace TTDeployTool.Application.Workflows;

/// <summary>
/// だれの権限で、どの範囲の Azure を操作するか（フロントエンドが用意する）。MCP の HTTP では方式（server / user）ごとに違い、
/// デスクトップアプリではサインインした利用者の資格情報で、範囲を制限しない。
/// </summary>
public interface IAzureSession
{
    /// <summary>渡したロガーにログを書き、この資格情報で操作する Azure の操作を作る。</summary>
    IAzureDeploymentGateway CreateGateway(IDeploymentLogger logger);

    /// <summary>操作してよい範囲に無いリソースグループなら、その理由（Azure を読まずにブロッカーにする）。範囲内なら null。</summary>
    string? ScopeBlocker(string subscription, string resourceGroup);

    /// <summary>リソースグループを作成する計画を実行できない場合の理由。作成できるなら null。</summary>
    string? ResourceGroupCreationBlocker(string resourceGroup);
}

/// <summary>範囲を制限せず、リソースグループも作成できる接続（CLI・MCP の stdio・デスクトップアプリ）。</summary>
public sealed class UnrestrictedAzureSession : IAzureSession
{
    private readonly Func<IDeploymentLogger, IAzureDeploymentGateway> _gatewayFactory;

    public UnrestrictedAzureSession(Func<IDeploymentLogger, IAzureDeploymentGateway> gatewayFactory)
    {
        _gatewayFactory = gatewayFactory;
    }

    public IAzureDeploymentGateway CreateGateway(IDeploymentLogger logger) => _gatewayFactory(logger);

    public string? ScopeBlocker(string subscription, string resourceGroup) => null;

    public string? ResourceGroupCreationBlocker(string resourceGroup) => null;
}
