using System.Globalization;
using System.Text.Json;
using Azure.ResourceManager;
using Azure.ResourceManager.ResourceGraph;
using Azure.ResourceManager.ResourceGraph.Models;
using TTDeployTool.Domain.Deployment;

namespace TTDeployTool.Infrastructure.Azure;

/// <summary>
/// 本ツールの管理タグが付いたリソース 1 件（Resource Graph の結果）。<see cref="Type"/> は小文字の種類（例: microsoft.app/containerapps）。
/// <see cref="App"/> は Container App の場合だけ。
/// </summary>
public sealed record ManagedResourceRecord(
    string Id, string Name, string Type, string SubscriptionId, string ResourceGroup, string Location,
    IReadOnlyDictionary<string, string> Tags, ContainerAppSnapshot? App = null)
{
    public const string ContainerAppType = "microsoft.app/containerapps";
    public const string EnvironmentType = "microsoft.app/managedenvironments";
    public const string StorageAccountType = "microsoft.storage/storageaccounts";
}

/// <summary>
/// Container App の状態とスペック（Resource Graph の結果。取得できない項目は null）。<see cref="Image"/>・<see cref="Cpu"/>・<see cref="Memory"/> は最初のコンテナの値。
/// <see cref="StorageNames"/> は、ボリュームで使っている Environment のストレージ（Azure Files）の名前。
/// </summary>
public sealed record ContainerAppSnapshot(
    string? ProvisioningState, string? RunningStatus, string? Image, double? Cpu, string? Memory, string? Fqdn, IReadOnlyList<string> StorageNames)
{
    /// <summary>DB のデータを置くストレージ（<see cref="DbPersistence.StorageName"/>）を使っているか。</summary>
    public bool UsesDbStorage => DbStorage.IsUsed(StorageNames);
}

/// <summary>本ツールで作ったリソースを、複数のサブスクリプションからまとめて読む（デプロイの一覧。docs/design/desktop/desktop-jobs.md の 6.3・6.4）。</summary>
public interface IDeploymentDirectory
{
    /// <summary>指定したサブスクリプション（同じテナントのもの）の、管理タグの付いた Container App・Environment・ストレージアカウント。</summary>
    Task<IReadOnlyList<ManagedResourceRecord>> ListAsync(IReadOnlyList<string> subscriptionIds, CancellationToken cancellationToken = default);
}

/// <summary>Azure Resource Graph で読む（読み取りの権限のあるサブスクリプションの結果だけが返る）。</summary>
public sealed class ResourceGraphDeploymentDirectory : IDeploymentDirectory
{
    /// <summary>1 回の応答の件数（Resource Graph の上限）。</summary>
    public const int PageSize = 1000;

    // id（文字列）を含めて並べる（すべての列が dynamic だと、続きのページの $skipToken が返らない）
    internal static readonly string Query =
        "resources" +
        $" | where type in~ ('{ManagedResourceRecord.ContainerAppType}', '{ManagedResourceRecord.EnvironmentType}', '{ManagedResourceRecord.StorageAccountType}')" +
        $" | where tostring(tags['{ManagedTags.ManagedByKey}']) in~ ('{ManagedTags.ManagedByValue}', '{ManagedTags.LegacyManagedByValue}')" +
        " | project id, name, type = tolower(type), subscriptionId, resourceGroup, location, tags," +
        " provisioningState = tostring(properties.provisioningState), runningStatus = tostring(properties.runningStatus)," +
        " image = tostring(properties.template.containers[0].image), cpu = todouble(properties.template.containers[0].resources.cpu)," +
        " memory = tostring(properties.template.containers[0].resources.memory), fqdn = tostring(properties.configuration.ingress.fqdn)," +
        " volumes = properties.template.volumes" +
        " | order by id asc";

    private readonly ArmClient _arm;

    public ResourceGraphDeploymentDirectory(ArmClient arm)
    {
        _arm = arm;
    }

    public async Task<IReadOnlyList<ManagedResourceRecord>> ListAsync(IReadOnlyList<string> subscriptionIds, CancellationToken cancellationToken = default)
    {
        var result = new List<ManagedResourceRecord>();
        if (subscriptionIds.Count == 0)
            return result;
        // 問い合わせはテナントの範囲の API（パスにテナントを含まない）。資格情報のテナントの最初の TenantResource を使う
        var tenant = await _arm.GetTenants().GetAllAsync(cancellationToken).FirstAsync(cancellationToken);
        string? skipToken = null;
        do
        {
            var content = new ResourceQueryContent(Query)
            {
                Options = new ResourceQueryRequestOptions { ResultFormat = ResultFormat.ObjectArray, Top = PageSize, SkipToken = skipToken },
            };
            foreach (var id in subscriptionIds)
                content.Subscriptions.Add(id);
            var response = await tenant.GetResourcesAsync(content, cancellationToken);
            result.AddRange(Parse(response.Value.Data));
            skipToken = response.Value.SkipToken;
        }
        while (skipToken != null);
        return result;
    }

    internal static IEnumerable<ManagedResourceRecord> Parse(BinaryData data)
    {
        using var doc = JsonDocument.Parse(data);
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            var type = Text(row, "type") ?? "";
            yield return new ManagedResourceRecord(
                Text(row, "id") ?? "", Text(row, "name") ?? "", type, Text(row, "subscriptionId") ?? "", Text(row, "resourceGroup") ?? "",
                Text(row, "location") ?? "", Tags(row),
                type == ManagedResourceRecord.ContainerAppType
                    ? new ContainerAppSnapshot(
                        Text(row, "provisioningState"), Text(row, "runningStatus"), Text(row, "image"), Number(row, "cpu"),
                        Text(row, "memory"), Text(row, "fqdn"), Volumes(row))
                    : null);
        }
    }

    // tostring で取り出した値は、無ければ空の文字列になる
    private static string? Text(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private static double? Number(JsonElement row, string name) => row.TryGetProperty(name, out var value) switch
    {
        true when value.ValueKind == JsonValueKind.Number => value.GetDouble(),
        true when value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) => n,
        _ => null,
    };

    private static IReadOnlyDictionary<string, string> Tags(JsonElement row)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (row.TryGetProperty("tags", out var value) && value.ValueKind == JsonValueKind.Object)
        {
            foreach (var tag in value.EnumerateObject())
            {
                if (tag.Value.ValueKind == JsonValueKind.String)
                    tags[tag.Name] = tag.Value.GetString()!;
            }
        }
        return tags;
    }

    private static IReadOnlyList<string> Volumes(JsonElement row) =>
        row.TryGetProperty("volumes", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Select(v => v.ValueKind == JsonValueKind.Object && v.TryGetProperty("storageName", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null)
                .OfType<string>().ToList()
            : [];
}
