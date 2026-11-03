using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Domain.Deployment;

namespace TTDeployTool.Controller.Desktop.Core.Services;

/// <summary>
/// 名前を付けて保存した「デプロイの設定」（docs/design/desktop/desktop-settings.md の 2.3）。パスワードは含めない。
/// 数値は画面の入力のまま（文字列）。<see cref="ExpiryDate"/> は yyyy-MM-dd（<see cref="Expiry"/> が Date のときだけ）。
/// <see cref="Group"/> はグループ名（無ければ null。docs/design/common/deployment-groups.md）。
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
    string? ExpiryDate,
    string? Group = null)
{
    /// <summary>
    /// デプロイの設定（<paramref name="options"/>）から作る入力の写し（デプロイのジョブの記録に残し、「この設定で新しくデプロイ」で入力に戻す。パスワードは含めない）。
    /// イメージタグが DB の種類の既定なら空（既定）にし、期限は yyyy-MM-dd なら日付、none なら期限なし、無ければ既定にする。
    /// </summary>
    public static DeploymentTemplate From(DeployOptions options)
    {
        var expiry = options.ExpiresOn switch
        {
            null => ExpiryMode.Default,
            var none when string.Equals(none, DeploymentExpiry.None, StringComparison.OrdinalIgnoreCase) => ExpiryMode.None,
            _ => ExpiryMode.Date,
        };
        return new DeploymentTemplate(
            "", options.Subscription, options.ResourceGroup, options.AppName, options.Location, options.DbType, options.DbName,
            options.TimetrackerTag == Defaults.GetDefaultTimetrackerTag(options.DbType) ? "" : options.TimetrackerTag,
            Format(options.TimetrackerCpu), Format(options.TimetrackerMemoryGi), Format(options.DbCpu), Format(options.DbMemoryGi),
            Format(options.RedisCpu), Format(options.RedisMemoryGi), options.PersistDb, expiry, expiry == ExpiryMode.Date ? options.ExpiresOn : null, options.Group);
    }

    private static string Format(double value) => value.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture);
}

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

    /// <summary>表ごとの列の幅と順序（表の名前ごと。docs/design/desktop/desktop-tables.md の 10.1）。</summary>
    IReadOnlyDictionary<string, IReadOnlyList<TableColumnLayout>> TableLayouts { get; set; }
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

    public IReadOnlyDictionary<string, IReadOnlyList<TableColumnLayout>> TableLayouts { get; set; } = new Dictionary<string, IReadOnlyList<TableColumnLayout>>();
}
