using TTDeployTool.Controller.Desktop.Core.ViewModels;

namespace TTDeployTool.Controller.Desktop.Core.Services;

/// <summary>
/// 名前を付けて保存した「デプロイの設定」（docs/design/desktop-settings.md の 2.3）。パスワードは含めない。
/// 数値は画面の入力のまま（文字列）。<see cref="ExpiryDate"/> は yyyy-MM-dd（<see cref="Expiry"/> が Date のときだけ）。
/// </summary>
public sealed record DeploymentTemplate(
    string Name,
    string? SubscriptionId,
    string ResourceGroup,
    string AppName,
    string Location,
    string DbType,
    string DbName,
    string TtTag,
    string TtCpu,
    string TtMemoryGi,
    string DbCpu,
    string DbMemoryGi,
    string RedisCpu,
    string RedisMemoryGi,
    bool? PersistDb,
    ExpiryMode Expiry,
    string? ExpiryDate);

/// <summary>アプリの設定のうち、保存するもの（秘密の情報は置かない）。画面の側でアプリのデータに保存する。</summary>
public interface IAppPreferences
{
    /// <summary>同時に実行するジョブの数（未設定なら null）。</summary>
    int? MaxConcurrentJobs { get; set; }

    /// <summary>デプロイの設定のテンプレート（名前の順）。</summary>
    IReadOnlyList<DeploymentTemplate> Templates { get; set; }
}

/// <summary>保存しない設定（テストと、保存先が無い場合）。</summary>
public sealed class InMemoryAppPreferences : IAppPreferences
{
    public int? MaxConcurrentJobs { get; set; }

    public IReadOnlyList<DeploymentTemplate> Templates { get; set; } = [];
}
