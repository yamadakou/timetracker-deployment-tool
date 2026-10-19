namespace TTDeployTool.Application.Deployment;

/// <summary>デプロイの手順。</summary>
public enum DeploymentStep
{
    ResourceGroup,
    Environment,
    DbStorage,
    ContainerApps,
}

/// <summary>開始した手順の通知（<see cref="Index"/> は 1 始まり）。</summary>
public sealed record DeploymentProgress(DeploymentStep Step, int Index, int Total, string Description);
