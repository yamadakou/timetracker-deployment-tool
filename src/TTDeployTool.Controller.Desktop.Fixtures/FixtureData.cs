using System.Text.Json;
using System.Text.Json.Serialization;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Infrastructure.Azure;

namespace TTDeployTool.Controller.Desktop.Fixtures;

/// <summary>
/// UI テストのモックのデータ（docs/design/desktop/desktop-ui-tests.md の 6.2）。テストが起動の前に、アプリのデータのフォルダーに
/// <see cref="FileName"/> を置く。形は本物の型（サインインのアカウント・テナント・サブスクリプション・Resource Graph の結果）をそのまま JSON にしたもの。
/// <see cref="WriteDelaySeconds"/> は、Azure への書き込み 1 回にかける時間（ジョブを実行中のまま見せる長さ。停止は手順の境目で効くため、停止までの時間にもなる）。
/// </summary>
public sealed record FixtureData(
    SignedInAccount Account,
    IReadOnlyList<TenantInfo> Tenants,
    IReadOnlyList<SubscriptionInfo> Subscriptions,
    IReadOnlyList<ManagedResourceRecord> Resources,
    IReadOnlyList<FixtureResourceGroup>? ResourceGroups = null,
    IReadOnlyList<FixtureSecret>? Secrets = null,
    double WriteDelaySeconds = 2)
{
    /// <summary>アプリのデータのフォルダーに置くファイルの名前。</summary>
    public const string FileName = "uitest-fixture.json";

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };

    /// <summary>モックのデータを読む。ファイルが無ければ null（読めない JSON は例外。テストの誤りに気付けるようにする）。</summary>
    public static FixtureData? Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<FixtureData>(File.ReadAllText(path), Options) : null;

    /// <summary>テストがファイルを書くとき（読むときと同じ形）。</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Options);
}

/// <summary>リソースグループ（タグを持たせるとき。無ければ、リソースのあるリソースグループを、タグなしで扱う）。</summary>
public sealed record FixtureResourceGroup(string SubscriptionId, string Name, string Location, IReadOnlyDictionary<string, string>? Tags = null);

/// <summary>Container App のシークレット（パスワードの表示で読む）。</summary>
public sealed record FixtureSecret(string SubscriptionId, string ResourceGroup, string ContainerApp, IReadOnlyDictionary<string, string> Values);
