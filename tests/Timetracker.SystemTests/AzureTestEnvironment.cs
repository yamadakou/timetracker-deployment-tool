using System.Globalization;
using System.Security.Cryptography;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.AppContainers;
using Azure.ResourceManager.Resources;
using Timetracker.Domain.Deployment;

namespace Timetracker.SystemTests;

/// <summary>
/// テスト結果を Azure から直接確かめるための操作（ツールとは別に ArmClient で読む）と、テスト用リソースの作成・掃除。
/// 資格情報は Azure CLI のログイン（az login / CI では azure/login）を使う。
/// </summary>
public sealed class AzureTestEnvironment
{
    /// <summary>テスト用リソースグループの接頭辞。掃除はこの接頭辞とタグの両方で対象を絞る。</summary>
    public const string ResourceGroupPrefix = "rg-ttst-";
    public const string CreatedAtTag = "ttst-created-at";
    public const string AppNamePrefix = "ttst";

    // 中断されたテストが残したリソースグループを消すまでの時間
    private static readonly TimeSpan LeftoverAge = TimeSpan.FromHours(3);

    public AzureTestEnvironment(string subscriptionId)
    {
        SubscriptionId = subscriptionId;
        Arm = new ArmClient(new AzureCliCredential(), subscriptionId);
    }

    public string SubscriptionId { get; }
    public ArmClient Arm { get; }

    private SubscriptionResource Subscription => Arm.GetSubscriptionResource(SubscriptionResource.CreateResourceIdentifier(SubscriptionId));

    public ResourceGroupResource ResourceGroup(string name) =>
        Arm.GetResourceGroupResource(ResourceGroupResource.CreateResourceIdentifier(SubscriptionId, name));

    public static string NewAppName() => AppNamePrefix + RandomSuffix(6);

    public static string NewResourceGroupName() =>
        $"{ResourceGroupPrefix}{DateTime.UtcNow:yyyyMMddHHmm}-{RandomSuffix(4)}";

    /// <summary>デプロイ・削除に使う、DB の要件を満たすパスワード。</summary>
    public static string NewPassword() => "Tt" + RandomSuffix(14) + "!9";

    private static string RandomSuffix(int length) =>
        new(Enumerable.Range(0, length).Select(_ => "abcdefghijklmnopqrstuvwxyz0123456789"[RandomNumberGenerator.GetInt32(36)]).ToArray());

    /// <summary>テスト用のリソースグループを作る。作成日時のタグを付け、掃除の対象にする。</summary>
    public async Task CreateResourceGroupAsync(string name, string location, CancellationToken cancellationToken)
    {
        var data = new ResourceGroupData(location);
        data.Tags[CreatedAtTag] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        data.Tags["purpose"] = "timetracker-deployment-tool-systemtest";
        await Subscription.GetResourceGroups().CreateOrUpdateAsync(WaitUntil.Completed, name, data, cancellationToken);
    }

    public async Task<bool> ResourceGroupExistsAsync(string name, CancellationToken cancellationToken) =>
        (await Subscription.GetResourceGroups().ExistsAsync(name, cancellationToken)).Value;

    /// <summary>リソースグループの削除を開始する（完了は待たない。Environment を含むと十数分かかるため）。</summary>
    public async Task BeginDeleteResourceGroupAsync(string name, CancellationToken cancellationToken)
    {
        if (await ResourceGroupExistsAsync(name, cancellationToken))
            await ResourceGroup(name).DeleteAsync(WaitUntil.Started, cancellationToken: cancellationToken);
    }

    /// <summary>中断されたテストが残したリソースグループ（接頭辞と作成日時のタグが付いたもの）を削除する。</summary>
    public async Task<IReadOnlyList<string>> SweepLeftoverResourceGroupsAsync(CancellationToken cancellationToken)
    {
        var swept = new List<string>();
        await foreach (var rg in Subscription.GetResourceGroups().GetAllAsync(cancellationToken: cancellationToken))
        {
            if (!rg.Data.Name.StartsWith(ResourceGroupPrefix, StringComparison.OrdinalIgnoreCase)
                || !rg.Data.Tags.TryGetValue(CreatedAtTag, out var createdAt)
                || !DateTimeOffset.TryParse(createdAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created)
                || DateTimeOffset.UtcNow - created < LeftoverAge)
                continue;

            await rg.DeleteAsync(WaitUntil.Started, cancellationToken: cancellationToken);
            swept.Add(rg.Data.Name);
        }
        return swept;
    }

    /// <summary>
    /// リソースグループ内の Container App と Environment の名前の一覧（昇順）。
    /// ARM の汎用のリソース一覧（GetGenericResourcesAsync）は削除の反映が遅れることがあるため、各リソースプロバイダーから直接読む。
    /// </summary>
    public async Task<IReadOnlyList<string>> ListResourceNamesAsync(string rgName, CancellationToken cancellationToken)
    {
        var rg = ResourceGroup(rgName);
        var names = new List<string>();
        await foreach (var app in rg.GetContainerApps().GetAllAsync(cancellationToken))
        {
            names.Add(app.Data.Name);
        }
        await foreach (var env in rg.GetContainerAppManagedEnvironments().GetAllAsync(cancellationToken))
        {
            names.Add(env.Data.Name);
        }
        return names.Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// リソース名の一覧が条件を満たすまで待ち、最後に読んだ一覧を返す（削除の直後は反映が遅れることがあるため）。
    /// 時間内に満たさなければ、そのときの一覧を返す（判定は呼び出し側のアサーションで行う）。
    /// </summary>
    public async Task<IReadOnlyList<string>> WaitForResourceNamesAsync(
        string rgName, Func<IReadOnlyList<string>, bool> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (true)
        {
            var names = await ListResourceNamesAsync(rgName, cancellationToken);
            if (condition(names) || DateTime.UtcNow > deadline)
                return names;
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
        }
    }

    /// <summary>リソースグループ内の、テストのアプリ名（接頭辞 ttst）の管理タグが付いた Container App のアプリ名。</summary>
    public async Task<IReadOnlyList<string>> ListTestAppNamesAsync(string rgName, CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var app in ResourceGroup(rgName).GetContainerApps().GetAllAsync(cancellationToken))
        {
            if (app.Data.Tags.TryGetValue(ManagedTags.AppNameKey, out var appName) && appName.StartsWith(AppNamePrefix, StringComparison.Ordinal))
                names.Add(appName);
        }
        await foreach (var env in ResourceGroup(rgName).GetContainerAppManagedEnvironments().GetAllAsync(cancellationToken))
        {
            if (env.Data.Tags.TryGetValue(ManagedTags.AppNameKey, out var appName) && appName.StartsWith(AppNamePrefix, StringComparison.Ordinal))
                names.Add(appName);
        }
        return names.ToList();
    }

    public async Task<IReadOnlyDictionary<string, string>> GetContainerAppTagsAsync(string rgName, string name, CancellationToken cancellationToken)
    {
        var app = await ResourceGroup(rgName).GetContainerAppAsync(name, cancellationToken);
        return new Dictionary<string, string>(app.Value.Data.Tags);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetEnvironmentTagsAsync(string rgName, string name, CancellationToken cancellationToken)
    {
        var env = await ResourceGroup(rgName).GetContainerAppManagedEnvironmentAsync(name, cancellationToken);
        return new Dictionary<string, string>(env.Value.Data.Tags);
    }

    public async Task<string?> GetContainerAppFqdnAsync(string rgName, string name, CancellationToken cancellationToken)
    {
        var app = await ResourceGroup(rgName).GetContainerAppAsync(name, cancellationToken);
        return app.Value.Data.Configuration?.Ingress?.Fqdn;
    }

    public async Task AddContainerAppTagAsync(string rgName, string name, string key, string value, CancellationToken cancellationToken)
    {
        var app = await ResourceGroup(rgName).GetContainerAppAsync(name, cancellationToken);
        await app.Value.AddTagAsync(key, value, cancellationToken);
    }

    public async Task RemoveContainerAppTagAsync(string rgName, string name, string key, CancellationToken cancellationToken)
    {
        var app = await ResourceGroup(rgName).GetContainerAppAsync(name, cancellationToken);
        await app.Value.RemoveTagAsync(key, cancellationToken);
    }

    /// <summary>Timetracker が HTTP で応答するまで待つ（初回起動は時間がかかる）。応答した状態コードを返す。</summary>
    public static async Task<int> WaitForHttpResponseAsync(string url, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                var response = await http.GetAsync(url, cancellationToken);
                // 502/503 などは起動中。それ以外が返れば Timetracker が応答している
                if ((int)response.StatusCode < 500)
                    return (int)response.StatusCode;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // 起動前は接続できない
            }

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"{url} が {timeout} 以内に応答しませんでした。");
            await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
        }
    }
}
