using System.Globalization;
using System.Security.Cryptography;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.AppContainers;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Storage;
using Azure.Storage;
using Azure.Storage.Files.Shares;
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

    /// <summary>中断されたテストが残したものとみなすまでの時間（実行中の別のテストのリソースを消さないため）。</summary>
    public static readonly TimeSpan LeftoverAge = TimeSpan.FromHours(3);

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
        var now = DateTimeOffset.UtcNow;
        await foreach (var rg in Subscription.GetResourceGroups().GetAllAsync(cancellationToken: cancellationToken))
        {
            if (!IsLeftoverResourceGroup(rg.Data.Name, rg.Data.Tags, now))
                continue;

            await rg.DeleteAsync(WaitUntil.Started, cancellationToken: cancellationToken);
            swept.Add(rg.Data.Name);
        }
        return swept;
    }

    /// <summary>
    /// 中断されたテストが残したリソースグループか（接頭辞が rg-ttst- で、作成日時のタグが <see cref="LeftoverAge"/> 以上前のもの）。
    /// 作成日時のタグが無い・読めないものは、テストが作ったものと確かめられないため対象にしない。
    /// </summary>
    public static bool IsLeftoverResourceGroup(string name, IDictionary<string, string> tags, DateTimeOffset now) =>
        name.StartsWith(ResourceGroupPrefix, StringComparison.OrdinalIgnoreCase)
        && tags.TryGetValue(CreatedAtTag, out var createdAt)
        && DateTimeOffset.TryParse(createdAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created)
        && now - created >= LeftoverAge;

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

    /// <summary>
    /// リソースグループ内の、中断されたテストが残したアプリ名（接頭辞 ttst の管理タグが付き、作成から <see cref="LeftoverAge"/> 以上たったもの）。
    /// 作成から間もないものは、同時に実行中の別のテスト（ローカルと CI など）のものかもしれないため含めない。
    /// </summary>
    public async Task<IReadOnlyList<string>> ListLeftoverTestAppNamesAsync(string rgName, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var names = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var app in ResourceGroup(rgName).GetContainerApps().GetAllAsync(cancellationToken))
        {
            if (LeftoverTestAppName(app.Data.Tags, app.Data.SystemData?.CreatedOn, now) is { } appName)
                names.Add(appName);
        }
        await foreach (var env in ResourceGroup(rgName).GetContainerAppManagedEnvironments().GetAllAsync(cancellationToken))
        {
            if (LeftoverTestAppName(env.Data.Tags, env.Data.SystemData?.CreatedOn, now) is { } appName)
                names.Add(appName);
        }
        return names.ToList();
    }

    /// <summary>
    /// 中断されたテストが残したリソースであれば、そのアプリ名（tt-app-name の値）。そうでなければ null。
    /// 作成日時が取得できないものは、古いものとみなす（残り続けて料金が発生しないように）。
    /// </summary>
    public static string? LeftoverTestAppName(IDictionary<string, string> tags, DateTimeOffset? createdOn, DateTimeOffset now) =>
        tags.TryGetValue(ManagedTags.AppNameKey, out var appName)
        && appName.StartsWith(AppNamePrefix, StringComparison.Ordinal)
        && (createdOn == null || now - createdOn.Value >= LeftoverAge)
            ? appName
            : null;

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

    /// <summary>Container App のボリュームで使っている、Environment のストレージ（Azure Files）の名前。</summary>
    public async Task<IReadOnlyList<string>> GetContainerAppStorageNamesAsync(string rgName, string name, CancellationToken cancellationToken)
    {
        var app = await ResourceGroup(rgName).GetContainerAppAsync(name, cancellationToken);
        return app.Value.Data.Template?.Volumes.Where(v => v.StorageName != null).Select(v => v.StorageName).ToList() ?? [];
    }

    /// <summary>ストレージアカウントのタグ（存在しなければ null）。</summary>
    public async Task<IReadOnlyDictionary<string, string>?> GetStorageAccountTagsAsync(string rgName, string name, CancellationToken cancellationToken)
    {
        var account = await ResourceGroup(rgName).GetStorageAccounts().GetIfExistsAsync(name, cancellationToken: cancellationToken);
        return account.HasValue ? new Dictionary<string, string>(account.Value!.Data.Tags) : null;
    }

    /// <summary>
    /// ファイル共有の中のファイルの最終更新日時（ファイルが無ければ null）。アカウントキーで共有を読む
    /// （DB のデータを置く共有は、本ツールがアカウントキーで Environment に登録しているため、キーによるアクセスが許可されている）。
    /// </summary>
    public async Task<DateTimeOffset?> GetShareFileLastModifiedAsync(
        string rgName, string accountName, string shareName, string path, CancellationToken cancellationToken)
    {
        var account = (await ResourceGroup(rgName).GetStorageAccountAsync(accountName, cancellationToken: cancellationToken)).Value;
        var keys = await account.GetKeysAsync(cancellationToken: cancellationToken);
        var credential = new StorageSharedKeyCredential(accountName, keys.Value.Keys[0].Value);
        var file = new ShareClient(new Uri($"https://{accountName}.file.core.windows.net/{shareName}"), credential)
            .GetRootDirectoryClient().GetFileClient(path);
        if (!(await file.ExistsAsync(cancellationToken)).Value)
            return null;
        return (await file.GetPropertiesAsync(cancellationToken: cancellationToken)).Value.LastModified;
    }

    /// <summary>ファイル共有の中のファイルの最終更新日時が条件を満たすまで待ち、その日時を返す（時間内に満たさなければ最後に読んだ値）。</summary>
    public async Task<DateTimeOffset?> WaitForShareFileAsync(
        string rgName, string accountName, string shareName, string path, Func<DateTimeOffset?, bool> condition, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var lastModified = await GetShareFileLastModifiedAsync(rgName, accountName, shareName, path, cancellationToken);
            if (condition(lastModified) || DateTime.UtcNow > deadline)
                return lastModified;
            await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
        }
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
