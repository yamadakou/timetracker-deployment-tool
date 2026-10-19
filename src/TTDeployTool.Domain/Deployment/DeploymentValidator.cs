using System.Globalization;

namespace TTDeployTool.Domain.Deployment;

/// <summary>検証の対象。Azure へのデプロイでは subscription / resource-group も必須にする。</summary>
public enum ValidationTarget
{
    Azure,
    Compose,
}

/// <summary>
/// デプロイ設定の検証。最初のエラーで止めずに、すべてのエラーを列挙する
/// （MCP などで、利用者がまとめて修正できるようにするため）。
/// 確認の順序とメッセージは CLI（<see cref="ComposeGenerator.Validate"/>）と同じ。
/// </summary>
public static class DeploymentValidator
{
    public static IReadOnlyList<string> GetErrors(DeployOptions o, ValidationTarget target)
    {
        var errors = new List<string>();
        var dbTypeValid = o.DbType is "postgresql" or "sqlserver";
        if (!dbTypeValid)
            errors.Add("db-type は postgresql または sqlserver を指定してください。");
        if (target == ValidationTarget.Azure)
        {
            if (string.IsNullOrWhiteSpace(o.Subscription))
                errors.Add("subscription は必須です。");
            if (string.IsNullOrWhiteSpace(o.ResourceGroup))
                errors.Add("resource-group は必須です。");
        }
        // CLI は引数の解析時に確認済みだが、他のフロントエンドから呼ばれた場合も Azure に送る前に止める
        if (AppNameValidator.GetError(o.AppName) is { } appNameError)
            errors.Add(appNameError);
        AddIfNotPositive(errors, o.TimetrackerCpu, "tt-cpu");
        AddIfNotPositive(errors, o.TimetrackerMemoryGi, "tt-memory");
        AddIfNotPositive(errors, o.DbCpu, "db-cpu");
        AddIfNotPositive(errors, o.DbMemoryGi, "db-memory");
        AddIfNotPositive(errors, o.RedisCpu, "redis-cpu");
        AddIfNotPositive(errors, o.RedisMemoryGi, "redis-memory");
        if (IsMissingPassword(o.TrackerPassword))
            errors.Add("tracker-password が不足または短すぎます。");
        if (IsMissingPassword(o.DbPassword))
            errors.Add("db-password が不足または短すぎます。");
        // 前の確認でエラーになった項目に依存する確認は、意味のないエラーを重ねないよう行わない
        else if (o.DbType == "sqlserver" && !ComposeGenerator.MeetsSqlServerPasswordPolicy(o.DbPassword))
            errors.Add(
                "db-password が SQL Server のパスワード要件を満たしていません。英大文字・英小文字・数字・記号のうち 3 種類以上を含めてください" +
                "（要件を満たさない場合 SQL Server コンテナが起動しません）。");
        if (string.IsNullOrWhiteSpace(o.TimetrackerTag))
            errors.Add("tt-tag（timetracker イメージタグ）が空です。例: 11.0.1-linux-postgres, 11.0.1-linux-mssql など。");
        // db-type と tt-tag の組み合わせ検証
        else if (dbTypeValid && !ComposeGenerator.TagMatchesDbType(o.DbType, o.TimetrackerTag))
            errors.Add(
                $"不適切な組み合わせ: db-type='{o.DbType}' と tt-tag='{o.TimetrackerTag}' は対応していません。" +
                " DB 種別に合致するタグを指定してください (例: postgresql → 11.0.1-linux-postgres / sqlserver → 11.0.1-linux-mssql)。" +
                " タグ一覧: https://hub.docker.com/r/densocreate/timetracker/tags");
        return errors;
    }

    private static bool IsMissingPassword(string password) => string.IsNullOrWhiteSpace(password) || password.Length < 8;

    private static void AddIfNotPositive(List<string> errors, double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
            errors.Add($"{name} には正の数値を指定してください: {value.ToString(CultureInfo.InvariantCulture)}");
    }
}
