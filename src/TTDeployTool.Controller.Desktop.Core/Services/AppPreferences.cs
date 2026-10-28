using TTDeployTool.Controller.Desktop.Core.ViewModels;

namespace TTDeployTool.Controller.Desktop.Core.Services;

/// <summary>
/// 名前を付けて保存した「デプロイの設定」（docs/design/desktop/desktop-settings.md の 2.3）。パスワードは含めない。
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

    /// <summary>出した期限の前の通知（同じ通知を出さないため。docs/design/desktop/desktop-jobs.md の 6.5）。</summary>
    IReadOnlyList<string> ExpiryNotices { get; set; }

    /// <summary>Windows のサインイン時の起動を尋ねたか（一度だけ尋ねる。D16）。</summary>
    bool StartupTaskAsked { get; set; }

    /// <summary>アプリを終了しても残す予定のジョブ（docs/design/desktop/desktop-jobs.md の 6.6）。</summary>
    IReadOnlyList<SavedJob> SavedJobs { get; set; }

    /// <summary>前回の Windows のサインイン時の起動（まだ無ければ null）。</summary>
    StartupLaunch? LastStartupLaunch { get; set; }
}

/// <summary>保存しない設定（テストと、保存先が無い場合）。</summary>
public sealed class InMemoryAppPreferences : IAppPreferences
{
    public int? MaxConcurrentJobs { get; set; }

    public IReadOnlyList<DeploymentTemplate> Templates { get; set; } = [];

    public IReadOnlyList<string> ExpiryNotices { get; set; } = [];

    public bool StartupTaskAsked { get; set; }

    public IReadOnlyList<SavedJob> SavedJobs { get; set; } = [];

    public StartupLaunch? LastStartupLaunch { get; set; }
}
