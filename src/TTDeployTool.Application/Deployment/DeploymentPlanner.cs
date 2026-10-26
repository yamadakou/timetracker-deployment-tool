using Azure.Core;
using TTDeployTool.Application.Permissions;
using TTDeployTool.Domain.Azure;
using TTDeployTool.Domain.Deployment;
using TTDeployTool.Infrastructure.Azure;

namespace TTDeployTool.Application.Deployment;

/// <summary>デプロイ前の確認（読み取りのみ）。</summary>
public sealed class DeploymentPlanner
{
    private readonly IAzureDeploymentGateway _gateway;
    private readonly TimeProvider _time;

    /// <param name="time">利用可能期限を決める時刻と時刻帯（省略時はシステムの時刻）</param>
    public DeploymentPlanner(IAzureDeploymentGateway gateway, TimeProvider? time = null)
    {
        _gateway = gateway;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// 今のデプロイの利用可能期限（3 つの Container App のタグを <see cref="DeploymentExpiry.Combine"/> でまとめたもの。無ければ null）。
    /// 再デプロイで期限を省略したときに引き継ぐ。
    /// </summary>
    public static IReadOnlyDictionary<string, string>? CurrentLifecycleTags(IReadOnlyList<ContainerAppInfo> apps, string appName) =>
        DeploymentExpiry.Combine(new[] { ResourceNaming.TimetrackerApp(appName), ResourceNaming.DbApp(appName), ResourceNaming.RedisApp(appName) }
                .Select(name => apps.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))?.Tags)
                .Select(tags => tags?.FirstOrDefault(t => string.Equals(t.Key, DeploymentExpiry.ExpiresAtKey, StringComparison.OrdinalIgnoreCase)).Value)) is { } value
            ? new Dictionary<string, string> { [DeploymentExpiry.ExpiresAtKey] = value }
            : null;

    /// <summary>利用可能期限を決める（今日より前の日付を指定した場合は、その誤りも返す）。</summary>
    public (ExpiryDecision Decision, string? Error) ResolveExpiry(DeployOptions opts, IReadOnlyList<ContainerAppInfo> existingApps)
    {
        var now = _time.GetUtcNow();
        return (DeploymentExpiry.Resolve(opts.ExpiresOn, CurrentLifecycleTags(existingApps, opts.AppName), now, _time.LocalTimeZone),
            DeploymentExpiry.PastDateError(opts.ExpiresOn, now, _time.LocalTimeZone));
    }

    // 永続化する場合の、DB のストレージの今の状態
    private sealed record StorageState(string AccountName, bool AccountExists, bool ShareExists, bool EnvironmentStorageExists, bool AccountManaged);

    /// <summary>既存の DB アプリが、DB のデータを置くストレージ（Azure Files）を使っているか（リソースグループや DB アプリが無ければ false）。</summary>
    public static async Task<bool> UsesDbStorageAsync(IAzureDeploymentGateway gateway, DeployOptions opts, CancellationToken cancellationToken = default)
    {
        if (await gateway.GetResourceGroupAsync(opts.Subscription, opts.ResourceGroup, cancellationToken) == null)
            return false;
        var apps = await gateway.ListContainerAppsAsync(opts.Subscription, opts.ResourceGroup, cancellationToken);
        return FindDbApp(apps, opts)?.UsesDbStorage == true;
    }

    /// <summary>デプロイに必要な権限があるかを確認する。</summary>
    public async Task<PermissionCheckResult> CheckPermissionsAsync(DeployOptions opts, CancellationToken cancellationToken = default)
    {
        var rg = await _gateway.GetResourceGroupAsync(opts.Subscription, opts.ResourceGroup, cancellationToken);
        if (rg == null)
            return ResourceGroupMissing(opts);
        var existingApps = await _gateway.ListContainerAppsAsync(opts.Subscription, opts.ResourceGroup, cancellationToken);
        var env = await _gateway.GetEnvironmentAsync(opts.Subscription, opts.ResourceGroup, ResourceNaming.Environment(opts.AppName), cancellationToken);
        var persist = opts.PersistDb ?? FindDbApp(existingApps, opts)?.UsesDbStorage == true;
        var storage = persist ? await ReadStorageStateAsync(opts, env != null, cancellationToken) : null;
        return await CheckPermissionsAsync(opts, existingApps, env, storage, cancellationToken);
    }

    /// <summary>
    /// 既存のリソースを読み取り、デプロイで何が起きるかの計画を作る（Azure は変更しない）。
    /// 判定は実際のデプロイ（AzureSdkExecutor）と同じ: リソースグループのリージョン違いは警告、Environment のリージョン違いは実行できない。
    /// 永続化の指定が無ければ（PersistDb が null）、既存の DB アプリの今の状態を引き継ぐ。計画の Options には実際の値を入れる。
    /// </summary>
    /// <param name="opts">検証済みの設定</param>
    public async Task<DeploymentPlan> PlanAsync(DeployOptions opts, CancellationToken cancellationToken = default)
    {
        var operations = new List<PlannedOperation>();
        var blockers = new List<string>();
        var warnings = new List<string>();
        var location = new AzureLocation(opts.Location);
        var envName = ResourceNaming.Environment(opts.AppName);
        var dbAppName = ResourceNaming.DbApp(opts.AppName);
        var appNames = new[] { dbAppName, ResourceNaming.RedisApp(opts.AppName), ResourceNaming.TimetrackerApp(opts.AppName) };

        var rg = await _gateway.GetResourceGroupAsync(opts.Subscription, opts.ResourceGroup, cancellationToken);
        if (rg == null)
        {
            // リソースグループが無ければ、その中のリソースも無い
            var persistNew = opts.PersistDb == true;
            operations.Add(new PlannedOperation(PlannedResourceKind.ResourceGroup, opts.ResourceGroup, PlannedAction.Create));
            operations.Add(new PlannedOperation(PlannedResourceKind.Environment, envName, PlannedAction.Create));
            if (persistNew)
                await AddStorageOperationsAsync(opts, new StorageState(StorageAccountName(opts), false, false, false, false), operations, blockers, warnings, cancellationToken);
            operations.AddRange(appNames.Select(n => new PlannedOperation(PlannedResourceKind.ContainerApp, n, PlannedAction.Create)));
            var notChecked = ResourceGroupMissing(opts);
            warnings.Add(notChecked.Note!);
            var newExpiry = AddExpiryBlocker(opts, [], blockers, warnings);
            return new DeploymentPlan(opts.WithPersistDb(persistNew), operations, blockers, warnings, notChecked, newExpiry);
        }

        operations.Add(new PlannedOperation(PlannedResourceKind.ResourceGroup, rg.Name, PlannedAction.UseExisting));
        if (new AzureLocation(rg.Location) != location)
            warnings.Add($"既存のリソースグループ '{rg.Name}' のリージョン ({rg.Location}) は location ({opts.Location}) と異なります。Container Apps は {opts.Location} に作成されます。");

        var env = await _gateway.GetEnvironmentAsync(opts.Subscription, opts.ResourceGroup, envName, cancellationToken);
        if (env == null)
        {
            operations.Add(new PlannedOperation(PlannedResourceKind.Environment, envName, PlannedAction.Create));
        }
        else
        {
            operations.Add(new PlannedOperation(PlannedResourceKind.Environment, envName, PlannedAction.UseExisting));
            // Container App は Environment と同じリージョンに作る必要があるため、実行しても途中で失敗する
            if (new AzureLocation(env.Location) != location)
                blockers.Add(
                    $"既存の Container Apps Environment '{envName}' のリージョン ({env.Location}) が location ({opts.Location}) と異なります。" +
                    $"location に {env.Location} を指定するか、別の app_name を指定してください。");
        }

        var existingApps = await _gateway.ListContainerAppsAsync(opts.Subscription, opts.ResourceGroup, cancellationToken);
        var currentlyPersisted = FindDbApp(existingApps, opts)?.UsesDbStorage == true;
        var persist = opts.PersistDb ?? currentlyPersisted;
        StorageState? storage = null;
        if (persist)
        {
            storage = await ReadStorageStateAsync(opts, env != null, cancellationToken);
            await AddStorageOperationsAsync(opts, storage, operations, blockers, warnings, cancellationToken);
        }

        foreach (var name in appNames)
        {
            var exists = existingApps.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            string? dataLossWarning = null;
            if (exists && name == dbAppName)
            {
                dataLossWarning = DbPersistence.DataLossWarning(name, currentlyPersisted, persist);
                warnings.Add(dataLossWarning ?? DbPersistence.RestartNotice(name));
            }
            operations.Add(new PlannedOperation(
                PlannedResourceKind.ContainerApp, name, exists ? PlannedAction.Update : PlannedAction.Create, dataLossWarning != null));
        }

        var permissions = await CheckPermissionsAsync(opts.WithPersistDb(persist), existingApps, env, storage, cancellationToken);
        if (permissions.Status == PermissionCheckStatus.Missing)
            blockers.Add($"権限が不足しています: {string.Join(", ", permissions.MissingActions)}。" +
                         "README の「必要な RBAC ロールと権限」を参照し、ロールの割り当てを確認してください。");
        else if (permissions.Status == PermissionCheckStatus.NotChecked)
            warnings.Add($"権限を事前に確認できませんでした: {permissions.Note}");

        var expiry = AddExpiryBlocker(opts, existingApps, blockers, warnings);
        return new DeploymentPlan(opts.WithPersistDb(persist), operations, blockers, warnings, permissions, expiry);
    }

    // 今日より前の日付は実行できない。引き継ぐ期限が既に過ぎていれば警告する
    private ExpiryDecision AddExpiryBlocker(DeployOptions opts, IReadOnlyList<ContainerAppInfo> existingApps, List<string> blockers, List<string> warnings)
    {
        var (decision, error) = ResolveExpiry(opts, existingApps);
        if (error != null)
            blockers.Add(error);
        if (decision is { Source: ExpirySource.Inherited, ExpiresAt: { } at } && at <= _time.GetUtcNow())
            warnings.Add($"今の利用可能期限（{decision.Describe(_time.LocalTimeZone)}）を過ぎています。期限日を指定し直してください。");
        return decision;
    }

    private static ContainerAppInfo? FindDbApp(IReadOnlyList<ContainerAppInfo> apps, DeployOptions opts) =>
        apps.FirstOrDefault(a => string.Equals(a.Name, ResourceNaming.DbApp(opts.AppName), StringComparison.OrdinalIgnoreCase));

    private static string StorageAccountName(DeployOptions opts) =>
        ResourceNaming.StorageAccount(opts.Subscription, opts.ResourceGroup, opts.AppName);

    private async Task<StorageState> ReadStorageStateAsync(DeployOptions opts, bool envExists, CancellationToken cancellationToken)
    {
        var accountName = StorageAccountName(opts);
        var account = await _gateway.GetStorageAccountAsync(opts.Subscription, opts.ResourceGroup, accountName, cancellationToken);
        var shareExists = account != null &&
                          await _gateway.FileShareExistsAsync(opts.Subscription, opts.ResourceGroup, accountName, ResourceNaming.DbFileShare(opts.AppName), cancellationToken);
        var envStorageExists = envExists &&
                               await _gateway.EnvironmentStorageExistsAsync(
                                   opts.Subscription, opts.ResourceGroup, ResourceNaming.Environment(opts.AppName), DbPersistence.StorageName, cancellationToken);
        return new StorageState(accountName, account != null, shareExists, envStorageExists, ManagedTags.IsManaged(account?.Tags, opts.AppName));
    }

    // ストレージアカウント → ファイル共有 → Environment のストレージ（キーを登録し直すため、既存なら更新）
    private async Task AddStorageOperationsAsync(
        DeployOptions opts, StorageState storage, List<PlannedOperation> operations, List<string> blockers, List<string> warnings,
        CancellationToken cancellationToken)
    {
        // 名前は Azure 全体で一意のため、このリソースグループに無い名前が他で使われていれば作成できない
        if (!storage.AccountExists && !await _gateway.IsStorageAccountNameAvailableAsync(opts.Subscription, storage.AccountName, cancellationToken))
            blockers.Add(
                $"ストレージアカウント名 '{storage.AccountName}' は、ほかのリソースグループかサブスクリプションで使われているため作成できません。" +
                "別の app_name を指定してください。");
        operations.Add(new PlannedOperation(
            PlannedResourceKind.StorageAccount, storage.AccountName, storage.AccountExists ? PlannedAction.UseExisting : PlannedAction.Create));
        operations.Add(new PlannedOperation(
            PlannedResourceKind.FileShare, ResourceNaming.DbFileShare(opts.AppName), storage.ShareExists ? PlannedAction.UseExisting : PlannedAction.Create));
        operations.Add(new PlannedOperation(
            PlannedResourceKind.EnvironmentStorage, DbPersistence.StorageName, storage.EnvironmentStorageExists ? PlannedAction.Update : PlannedAction.Create));
        warnings.Add(DbPersistence.StorageNotice(storage.AccountName));
    }

    // 権限の一覧は、存在するリソースグループかリソースでしか取得できない
    private static PermissionCheckResult ResourceGroupMissing(DeployOptions opts) =>
        PermissionCheckResult.NotChecked(
            $"リソースグループ '{opts.ResourceGroup}' がまだ無いため、権限を事前に確認できません" +
            "（作成する権限が無い場合は最初の手順で失敗し、リソースは作成されません）。");

    private async Task<PermissionCheckResult> CheckPermissionsAsync(
        DeployOptions opts, IReadOnlyList<ContainerAppInfo> existingApps, ManagedEnvironmentInfo? env, StorageState? storage, CancellationToken cancellationToken)
    {
        // 既存のリソースへの操作は、リソースに割り当てられたロールでも許可される。作成するものはリソースグループでの権限が必要
        var requirements = new List<PermissionRequirement>();
        foreach (var name in new[] { ResourceNaming.DbApp(opts.AppName), ResourceNaming.RedisApp(opts.AppName), ResourceNaming.TimetrackerApp(opts.AppName) })
        {
            var resource = existingApps.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
                ? TargetResource.ContainerApp(name)
                : null;
            requirements.Add(new PermissionRequirement(AzurePermissions.ContainerAppRead, resource));
            requirements.Add(new PermissionRequirement(AzurePermissions.ContainerAppWrite, resource));
        }

        var envName = ResourceNaming.Environment(opts.AppName);
        var envResource = env != null ? TargetResource.Environment(envName) : null;
        requirements.Add(new PermissionRequirement(AzurePermissions.EnvironmentRead, envResource));
        requirements.Add(new PermissionRequirement(AzurePermissions.EnvironmentJoin, envResource));
        // Environment は無ければ作る（利用可能期限のタグは作成の要求に含め、既存のものには書かない）
        if (env == null)
            requirements.Add(new PermissionRequirement(AzurePermissions.EnvironmentWrite, envResource));

        if (storage != null)
        {
            var account = storage.AccountExists ? TargetResource.StorageAccount(storage.AccountName) : null;
            requirements.Add(new PermissionRequirement(AzurePermissions.StorageAccountRead, account));
            // 作るストレージアカウントと、本ツールで作った既存のものには、利用可能期限のタグを書く
            if (!storage.AccountExists || storage.AccountManaged)
                requirements.Add(new PermissionRequirement(AzurePermissions.StorageAccountWrite, account));
            requirements.Add(new PermissionRequirement(AzurePermissions.StorageAccountListKeys, account));
            requirements.Add(new PermissionRequirement(AzurePermissions.FileShareRead, account));
            if (!storage.ShareExists)
                requirements.Add(new PermissionRequirement(AzurePermissions.FileShareWrite, account));
            requirements.Add(new PermissionRequirement(AzurePermissions.EnvironmentStorageRead, envResource));
            requirements.Add(new PermissionRequirement(AzurePermissions.EnvironmentStorageWrite, envResource));

            // 永続化を続ける既存の DB アプリは、停止してから更新し、開始する
            if (FindDbApp(existingApps, opts)?.UsesDbStorage == true)
            {
                var db = TargetResource.ContainerApp(ResourceNaming.DbApp(opts.AppName));
                requirements.Add(new PermissionRequirement(AzurePermissions.ContainerAppStop, db));
                requirements.Add(new PermissionRequirement(AzurePermissions.ContainerAppStart, db));
            }
        }

        return await new PermissionChecker(_gateway).CheckAsync(opts.Subscription, opts.ResourceGroup, requirements, cancellationToken);
    }
}
