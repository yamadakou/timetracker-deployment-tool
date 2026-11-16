using System.Text.RegularExpressions;

namespace TTDeployTool.Controller.Desktop.Core;

/// <summary>
/// CLI・MCP と共通の誤りと警告の文（Domain・Application。CLI のオプション名や MCP の引数名で書く）を、デスクトップアプリの画面の名前に置き換える
/// （オーナーの依頼、2026-11-16: 画面やメニューに無い名前を使わない）。CLI・MCP の文は変えない。当てはまらない文は、そのまま返す。
/// </summary>
public static partial class DesktopTerms
{
    /// <summary>README の「必要な RBAC ロールと権限」（権限が足りないときの案内から開く）。</summary>
    public const string RbacDocUrl = "https://github.com/yamadakou/timetracker-deployment-tool/blob/main/README.md#必要な-rbac-ロールと権限";

    private const string RbacReadme = "README の「必要な RBAC ロールと権限」を参照し、ロールの割り当てを確認してください。";

    /// <summary>権限が足りないときの案内（必要なロールをその場で書き、資料のリンクを添える）。</summary>
    public const string RbacGuide =
        "サブスクリプションかリソースグループに「共同作成者（Contributor）」か「所有者（Owner）」のロールの割り当てが必要です（「Container Apps 共同作成者」だけでは足りません）。" +
        "Azure の管理者に依頼してください。詳しくは " + RbacDocUrl;

    private static readonly Dictionary<string, string> Apps = new() { ["tt"] = "TimeTracker", ["db"] = "DB", ["redis"] = "Redis" };

    // 「TimeTracker の CPU には」「TimeTracker のメモリには」
    private static readonly Dictionary<string, string> Specs = new() { ["cpu"] = " CPU ", ["memory"] = "メモリ" };

    // 文の頭の置き換え（Domain の DeploymentValidator・DeploymentAdvisor・DeploymentExpiry・DeploymentGroup と、SettingsViewModel の数値の誤り）
    private static readonly (Regex Pattern, Func<Match, string> Replace)[] Rules =
    [
        (Exact("subscription は必須です。"), _ => "サブスクリプションを選んでください。"),
        (Exact("resource-group は必須です。"), _ => "リソースグループ名を入力してください。"),
        (Exact("db-type は postgresql または sqlserver を指定してください。"), _ => "DB 種別を PostgreSQL か SQL Server から選んでください。"),
        (Exact("tracker-password が不足または短すぎます。"), _ => "TimeTracker のパスワードを 8 文字以上で入力してください。"),
        (Exact("db-password が不足または短すぎます。"), _ => "DB のパスワードを 8 文字以上で入力してください。"),
        (Head("db-password が SQL Server のパスワード要件"), m => "DB のパスワードが SQL Server のパスワード要件" + m.Groups["rest"].Value),
        (Head("tt-tag（timetracker イメージタグ）が空です。"), m => "TimeTracker のイメージのタグが空です。" + m.Groups["rest"].Value),
        (new Regex("^不適切な組み合わせ: db-type='(?<db>[^']*)' と tt-tag='(?<tag>[^']*)' は対応していません。(?<rest>.*)$", RegexOptions.Singleline),
            m => $"DB 種別（{m.Groups["db"].Value}）と TimeTracker のイメージのタグ（{m.Groups["tag"].Value}）の組み合わせが合いません。{m.Groups["rest"].Value}"),
        (new Regex("^(?<app>tt|db|redis)-(?<spec>cpu|memory) には正の数値を指定してください: (?<rest>.*)$", RegexOptions.Singleline),
            m => $"{Apps[m.Groups["app"].Value]} の{Specs[m.Groups["spec"].Value]}には、0 より大きい数値を入れてください: {m.Groups["rest"].Value}"),
        (new Regex("^(?<app>tt|db|redis)-(?<spec>cpu|memory) には数値を指定してください（小数点は「.」）: (?<rest>.*)$", RegexOptions.Singleline),
            m => $"{Apps[m.Groups["app"].Value]} の{Specs[m.Groups["spec"].Value]}には、数値を入れてください（小数点は「.」）: {m.Groups["rest"].Value}"),
        (new Regex("^(?<app>tt|db|redis)-cpu / (tt|db|redis)-memory（[^）]*）が (?<rest>.*)$", RegexOptions.Singleline),
            m => $"{Apps[m.Groups["app"].Value]} の CPU・メモリが {m.Groups["rest"].Value}"),
        (Head("expires-on には yyyy-MM-dd の日付か none を指定してください: "), m => "利用可能期限日を、正しい日付にしてください: " + m.Groups["rest"].Value),
        (Head("group（グループ名）"), m => "グループ名" + m.Groups["rest"].Value),
        (new Regex("^tt-tag '(?<tag>[^']*)' は既定のタグ(?<rest>.*)$", RegexOptions.Singleline),
            m => $"TimeTracker のイメージのタグ「{m.Groups["tag"].Value}」は既定のタグ{m.Groups["rest"].Value}"),
        (Head("persist-db は Azure 向けの設定のため、ローカル（Dry-run）では使いません"),
            m => "「DB のデータ」の「Azure Files に永続化する」は Azure 向けの設定のため、ローカル検証（Dry-run）では使いません" + m.Groups["rest"].Value),
    ];

    // 文の途中の置き換え（Domain の DbPersistence・Application の計画の理由と警告）
    private static readonly (string From, string To)[] Phrases =
    [
        ("もう一度 --persist-db（MCP では persist_db=true）を指定してデプロイすると元に戻ります。",
            "もう一度、手順「DB」の「DB のデータ」で「Azure Files に永続化する」を選んでデプロイすると元に戻ります。"),
        ("delete では、--keep-db-data（MCP は keep_db_data）を指定しない限り、", "Azure から削除するときは、「DB のデータを残す」にチェックを入れない限り、"),
        ("（--include-untagged でも対象にしません）", "（「管理タグの無いものも対象にする」でも対象にしません）"),
        (RbacReadme, RbacGuide),
    ];

    // リージョンの食い違い（Application の DeploymentPlanner）
    private static readonly Regex LocationMismatch = new(
        @"location に (?<location>\S+) を指定するか、別の app_name を指定してください。", RegexOptions.Singleline);

    /// <summary>誤りと警告の文を、画面の名前で書き直す。</summary>
    public static string ForScreen(string message)
    {
        foreach (var (pattern, replace) in Rules)
        {
            if (pattern.Match(message) is { Success: true } match)
            {
                message = replace(match);
                break;
            }
        }
        foreach (var (from, to) in Phrases)
            message = message.Replace(from, to, StringComparison.Ordinal);
        message = LocationMismatch.Replace(message, m => $"手順「対象」のリージョンを {m.Groups["location"].Value} にするか、別のアプリ名にしてください。");
        return message.Replace(") は location (", ") は、選んだリージョン (", StringComparison.Ordinal)
            .Replace(") が location (", ") が、選んだリージョン (", StringComparison.Ordinal);
    }

    /// <summary>まとめて書き直す。</summary>
    public static IReadOnlyList<string> ForScreen(IEnumerable<string> messages) => [.. messages.Select(ForScreen)];

    private static Regex Exact(string text) => new($"^{Regex.Escape(text)}$");

    private static Regex Head(string text) => new($"^{Regex.Escape(text)}(?<rest>.*)$", RegexOptions.Singleline);
}
