using TTDeployTool.Application.Jobs;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Domain.Deployment;
using TTDeployTool.Infrastructure.Azure;

namespace TTDeployTool.Controller.Desktop.Fixtures;

/// <summary>
/// UI テストのモックのデータの見本（desktop-ui-tests.md の 6.2）。並べ替え・絞り込み・ページング（20 件ずつで複数のページ）・6 行の表示を確かめられるよう、
/// サブスクリプション・グループ・期限・状態を混ぜたデプロイと、どの終わった状態もある・長い文字を含むジョブの記録を作る。
/// </summary>
public static class FixtureSamples
{
    public const string TenantId = "11111111-1111-1111-1111-111111111111";

    public const string SubscriptionA = "00000000-0000-0000-0000-00000000000a";

    public const string SubscriptionB = "00000000-0000-0000-0000-00000000000b";

    public const string SubscriptionNameA = "モックの開発用サブスクリプション";

    public const string SubscriptionNameB = "モックの検証用サブスクリプション";

    public const string Location = "japaneast";

    /// <summary>6 行で切れる長い文字（ジョブの記録の誤り・結果）。</summary>
    public static readonly string LongText = string.Join(" ", Enumerable.Range(1, 40).Select(i => $"長い結果の文字の {i} 番目の文です。"));

    /// <summary>グループ（3 件ごとに 研修・営業・なし）。</summary>
    public static string? Group(int index) => (index % 3) switch { 0 => "研修", 1 => "営業", _ => null };

    public static string ResourceGroup(int index) => $"rg-mock-{index:00}";

    public static string AppName(int index) => $"app{index:00}";

    public static string Subscription(int index) => index % 2 == 0 ? SubscriptionA : SubscriptionB;

    /// <summary>
    /// <paramref name="deployments"/> 件のデプロイ（デプロイごとに、Environment と 3 つの Container App）。期限は 4 件ごとに なし・3 日後（近い）・40 日後・90 日後。
    /// 5 件ごとに 1 件は止めた状態。期限を過ぎたものは作らない（期限での自動の削除が、テストの知らないジョブを始めるため）。
    /// </summary>
    public static FixtureData Data(int deployments, DateTimeOffset now)
    {
        var resources = new List<ManagedResourceRecord>();
        var groups = new List<FixtureResourceGroup>();
        var secrets = new List<FixtureSecret>();
        for (var i = 0; i < deployments; i++)
        {
            var subscription = Subscription(i);
            var rg = ResourceGroup(i);
            var app = AppName(i);
            var expiry = (i % 4) switch
            {
                0 => DeploymentExpiry.None,
                1 => DeploymentExpiry.FormatTag(now.AddDays(3)),
                2 => DeploymentExpiry.FormatTag(now.AddDays(40)),
                _ => DeploymentExpiry.FormatTag(now.AddDays(90)),
            };
            var tags = new Dictionary<string, string>
            {
                [ManagedTags.ManagedByKey] = ManagedTags.ManagedByValue,
                [ManagedTags.AppNameKey] = app,
                [DeploymentExpiry.ExpiresAtKey] = expiry,
                [DeploymentExpiry.DeployedAtKey] = DeploymentExpiry.FormatTag(now.AddDays(-i - 1)),
            };
            if (Group(i) is { } group)
                tags[DeploymentGroup.Key] = group;
            var running = i % 5 == 4 ? "Stopped" : "Running";
            groups.Add(new FixtureResourceGroup(subscription, rg, Location, new Dictionary<string, string> { [ManagedTags.ManagedByKey] = ManagedTags.ManagedByValue }));
            resources.Add(Record(subscription, rg, ResourceNaming.Environment(app), ManagedResourceRecord.EnvironmentType, tags, null));
            foreach (var (name, image, fqdn) in new[]
            {
                (ResourceNaming.TimetrackerApp(app), "densocreate/timetracker:11.0.1-linux-postgres", $"{ResourceNaming.TimetrackerApp(app)}.mock.japaneast.azurecontainerapps.io"),
                (ResourceNaming.DbApp(app), "postgres:16", (string?)null),
                (ResourceNaming.RedisApp(app), "redis:7", null),
            })
            {
                resources.Add(Record(subscription, rg, name, ManagedResourceRecord.ContainerAppType, tags,
                    new ContainerAppSnapshot("Succeeded", running, image, 0.75, "1.5Gi", fqdn, [])));
            }
            secrets.Add(new FixtureSecret(subscription, rg, ResourceNaming.TimetrackerApp(app), new Dictionary<string, string>
            {
                [ContainerAppSpecBuilder.DbPasswordSecretName] = $"Db-Pass-{i:00}",
                [ContainerAppSpecBuilder.AppPasswordSecretName] = $"App-Pass-{i:00}",
            }));
        }
        return new FixtureData(
            new SignedInAccount("mock-user@example.com", TenantId),
            [new TenantInfo(TenantId, "モックのテナント", "mock.example.com")],
            [new SubscriptionInfo(SubscriptionA, SubscriptionNameA, "Enabled", TenantId), new SubscriptionInfo(SubscriptionB, SubscriptionNameB, "Enabled", TenantId)],
            resources, groups, secrets);
    }

    private static ManagedResourceRecord Record(string subscription, string rg, string name, string type, IReadOnlyDictionary<string, string> tags, ContainerAppSnapshot? app) =>
        new($"/subscriptions/{subscription}/resourceGroups/{rg}/providers/{type}/{name}", name, type, subscription, rg, Location, tags, app);

    /// <summary>
    /// 終わったジョブの記録 <paramref name="count"/> 件（新しいものほど後ろ。1 時間ずつ前に終わった）。成功・失敗・取り消し・停止を順に繰り返し、
    /// いちばん新しい 2 件（一覧の先頭）の誤り（結果の列）は 6 行で切れる長い文字。対象は <see cref="ResourceGroup"/>・<see cref="AppName"/>（デプロイの一覧の 100 番から。重ならない）。
    /// </summary>
    public static IReadOnlyList<JobRecord> JobRecords(int count, DateTimeOffset now) =>
    [
        .. Enumerable.Range(0, count).Select(i =>
        {
            var finished = now.AddHours(-(count - i));
            var state = (i % 4) switch { 0 => DesktopJobState.Succeeded, 1 => DesktopJobState.Failed, 2 => DesktopJobState.Canceled, _ => DesktopJobState.Stopped };
            var error = i >= count - 2 ? LongText : state == DesktopJobState.Failed ? "権限がありません。" : null;
            return new JobRecord(i % 2 == 0 ? JobKind.Deploy : JobKind.Delete, Subscription(i), ResourceGroup(100 + i), AppName(100 + i), false, null, finished, state, error,
                null, null, null, null, null, [], finished.AddMinutes(-10), finished, finished.AddMinutes(-5));
        }),
    ];
}
