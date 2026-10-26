using System.Reflection;
using TTDeployTool.Infrastructure.Azure;

namespace TTDeployTool.Controller.Desktop.Core.Services;

/// <summary>
/// Azure を読む前に、何を読むかを知らせる（計画・状態の確認の処理中の表示。詳細設計 desktop-jobs.md の 3.3）。
/// Application の処理を変えずに段階を知らせるため、Azure の窓口（<see cref="IAzureDeploymentGateway"/>）を包む。
/// 作成・削除（ジョブの中で呼ぶもの）は知らせない。
/// </summary>
public class ReportingGateway : DispatchProxy
{
    /// <summary>読み取りの操作と、その段階の説明。</summary>
    internal static readonly IReadOnlyDictionary<string, string> Steps = new Dictionary<string, string>
    {
        [nameof(IAzureDeploymentGateway.GetResourceGroupAsync)] = "リソースグループを確認しています",
        [nameof(IAzureDeploymentGateway.GetEnvironmentAsync)] = "Container Apps Environment を確認しています",
        [nameof(IAzureDeploymentGateway.ListContainerAppsAsync)] = "Container Apps を読んでいます",
        [nameof(IAzureDeploymentGateway.ListContainerAppDetailsAsync)] = "Container Apps の状態を読んでいます",
        [nameof(IAzureDeploymentGateway.GetContainerAppSecretsAsync)] = "パスワード（Container App のシークレット）を読んでいます",
        [nameof(IAzureDeploymentGateway.UpdateContainerAppTagsAsync)] = "利用可能期限（タグ）を書き換えています",
        [nameof(IAzureDeploymentGateway.UpdateStorageAccountTagsAsync)] = "利用可能期限（タグ）を書き換えています",
        [nameof(IAzureDeploymentGateway.GetStorageAccountAsync)] = "ストレージアカウントを確認しています",
        [nameof(IAzureDeploymentGateway.IsStorageAccountNameAvailableAsync)] = "ストレージアカウントの名前を確認しています",
        [nameof(IAzureDeploymentGateway.FileShareExistsAsync)] = "ファイル共有を確認しています",
        [nameof(IAzureDeploymentGateway.EnvironmentStorageExistsAsync)] = "Environment のストレージを確認しています",
        [nameof(IAzureDeploymentGateway.ListResourcesAsync)] = "リソースグループの中のリソースを読んでいます",
        [nameof(IAzureDeploymentGateway.GetPermissionsAsync)] = "権限を確認しています",
        [nameof(IAzureDeploymentGateway.GetResourcePermissionsAsync)] = "権限を確認しています",
    };

    private IAzureDeploymentGateway _inner = null!;
    private IProgress<string> _progress = null!;

    public static IAzureDeploymentGateway Wrap(IAzureDeploymentGateway inner, IProgress<string> progress)
    {
        var gateway = Create<IAzureDeploymentGateway, ReportingGateway>();
        var reporting = (ReportingGateway)(object)gateway;
        reporting._inner = inner;
        reporting._progress = progress;
        return gateway;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (Steps.TryGetValue(targetMethod!.Name, out var step))
            _progress.Report(step);
        // 例外は TargetInvocationException で包まずに、そのまま呼び出し元に返す
        return targetMethod.Invoke(_inner, BindingFlags.DoNotWrapExceptions, null, args, null);
    }
}
