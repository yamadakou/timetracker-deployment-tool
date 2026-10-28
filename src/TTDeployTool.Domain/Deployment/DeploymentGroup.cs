namespace TTDeployTool.Domain.Deployment;

/// <summary>デプロイで記録するグループ（<see cref="Current"/> は今のデプロイのグループ。新しいデプロイと、付いていなければ null）。</summary>
public sealed record GroupDecision(string? Current, string? Planned)
{
    /// <summary>グループを変えるか（付ける・別の名前にする。大文字と小文字の違いも変える）。</summary>
    public bool Changes => Planned != null && !string.Equals(Current, Planned, StringComparison.Ordinal);

    /// <summary>グループを指定せず、今のグループを引き継ぐか。</summary>
    public bool Inherited => Current != null && string.Equals(Current, Planned, StringComparison.Ordinal);

    /// <summary>計画の表示（例: 「グループ: 営業（今の 評価 から変えます）」）。</summary>
    public string Describe() => Planned == null ? "グループ: なし"
        : Inherited ? $"グループ: {Planned}（今のグループを引き継ぎます）"
        : Current == null ? $"グループ: {Planned}"
        : $"グループ: {Planned}（今の {Current} から変えます）";
}

/// <summary>
/// デプロイのグループ（用途・利用者ごとの管理。docs/design/common/deployment-groups.md）。グループ名は、利用可能期限と同じく、本ツールで作る
/// リソースの Azure のタグ <see cref="Key"/> に記録する。名前は前後の空白を除いて 1〜<see cref="MaxLength"/> 文字で、制御文字は使えない。
/// 大文字と小文字は区別して保存し、一覧の絞り込み・並べ替えと入力の候補では区別せずに束ねる（<see cref="Comparer"/>）。
/// </summary>
public static class DeploymentGroup
{
    /// <summary>グループ名のタグ。</summary>
    public const string Key = "tt-group";

    /// <summary>グループ名の長さの上限（Azure のタグの値の上限は 256 文字。表で読める長さにする）。</summary>
    public const int MaxLength = 64;

    /// <summary>グループ名を同じグループとして束ねるときの比べ方（大文字と小文字を区別しない）。</summary>
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>入力のグループ名（前後の空白を除く）。空なら null（指定しない）。</summary>
    public static string? Normalize(string? input) => string.IsNullOrWhiteSpace(input) ? null : input.Trim();

    /// <summary>入力のグループ名の誤り（無ければ null。空は誤りではなく「指定しない」）。</summary>
    public static string? FormatError(string? input) => Normalize(input) switch
    {
        null => null,
        { Length: > MaxLength } name => $"group（グループ名）は {MaxLength} 文字までにしてください（{name.Length} 文字）。",
        var name when name.Any(char.IsControl) => "group（グループ名）に、改行やタブなどの制御文字は使えません。",
        _ => null,
    };

    /// <summary>デプロイで記録するグループを決める（指定が無ければ、今のグループを引き継ぐ。Q2）。</summary>
    public static GroupDecision Resolve(string? input, string? current) => new(Normalize(current), Normalize(input) ?? Normalize(current));

    /// <summary>リソースのタグから、グループ名を読む（タグの名前は大文字と小文字を区別しない。無ければ null）。</summary>
    public static string? Read(IReadOnlyDictionary<string, string>? tags) =>
        Normalize(tags?.FirstOrDefault(t => string.Equals(t.Key, Key, StringComparison.OrdinalIgnoreCase)).Value);

    /// <summary>複数のリソースのグループ名をまとめる（先に渡したものを優先する。Timetracker の Container App を先に渡す）。どれにも無ければ null。</summary>
    public static string? Combine(IEnumerable<string?> values) => values.Select(Normalize).FirstOrDefault(v => v != null);

    /// <summary>リソースごとのグループ名が食い違っているか（付いていないリソースがあるものを含む。変更の途中の失敗、ポータルでの書き換えなど）。</summary>
    public static bool Differ(IEnumerable<string?> values) => values.Select(v => Normalize(v)).Distinct(StringComparer.Ordinal).Count() > 1;
}
