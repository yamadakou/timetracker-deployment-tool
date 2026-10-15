using System.Text;

namespace Timetracker.Domain.Deployment;

public static class ComposeGenerator
{
    // timetracker イメージ内のサンプルDBバックアップの格納先と、SQL Server と共有するためのボリューム名
    private const string SampleDir = "/usr/local/timetracker/sample";
    private const string SampleVolume = "ttsample";

    public static string GenerateCompose(DeployOptions o)
    {
        var ttImage = ContainerImages.Timetracker(o.TimetrackerTag);

        var sb = new StringBuilder();
        sb.AppendLine("services:");
        sb.AppendLine("  timetracker:");
        sb.AppendLine($"    image: {ttImage}");
        sb.AppendLine("    container_name: timetracker-app");
        // DB/Redis が接続可能になってから起動する（起動直後の接続失敗による再起動を防ぐ）
        sb.AppendLine("    depends_on:");
        sb.AppendLine("      db:");
        sb.AppendLine("        condition: service_healthy");
        sb.AppendLine("      redis:");
        sb.AppendLine("        condition: service_healthy");
        sb.AppendLine("    ports:");
        sb.AppendLine("      - \"8080:8080\"");
        sb.AppendLine("    environment:");
        sb.AppendLine("      ASPNETCORE_URLS: \"http://0.0.0.0:8080\"");
        // Azure Container Apps 側（ContainerAppSpecBuilder）と同じ環境変数を設定する。
        // timetracker 11.x のイメージは TT_、7.x 以前は TTNX_ 接頭辞を読むため、両方を同じ値で設定する。
        // DB_SERVER はホスト名のみ（ポートは DB_PORT。イメージ側で連結される）
        var ttVariables = new (string Name, string Value)[]
        {
            ("DB_TYPE", "${TTNX_DB_TYPE}"),
            ("DB_SERVER", "db"),
            ("DB_USER", "${DB_USER}"),
            ("DB_PASSWORD", "${DB_PASSWORD}"),
            ("DB_NAME", "${DB_NAME}"),
            ("DB_PORT", "${DB_PORT}"),
            ("DB_OPTIONS", ""),
            ("REDIS_GLOBALCACHE", "redis:6379"),
            ("REDIS_HANGFIRE", "redis:6379"),
            ("REDIS_BACKGROUNDJOB", "redis:6379"),
        };
        foreach (var prefix in new[] { ContainerImages.LegacyEnvPrefix, ContainerImages.EnvPrefix })
        {
            foreach (var (name, value) in ttVariables)
            {
                sb.AppendLine($"      {prefix}{name}: \"{value}\"");
            }
        }
        // 互換のため従来の変数も設定
        sb.AppendLine("      DB_HOST: \"db\"");
        sb.AppendLine("      DB_PORT: \"${DB_PORT}\"");
        sb.AppendLine("      DB_USER: \"${DB_USER}\"");
        sb.AppendLine("      DB_PASSWORD: \"${DB_PASSWORD}\"");
        sb.AppendLine("      DB_NAME: \"${DB_NAME}\"");
        sb.AppendLine("      REDIS_HOST: \"redis\"");
        sb.AppendLine("      REDIS_PORT: \"6379\"");
        sb.AppendLine("      APP_PASSWORD: \"${TIMETRACKER_PASSWORD}\"  # <your password>");
        if (o.DbType == "sqlserver")
        {
            // SQL Server の RESTORE は DB サーバー側のファイルを読むため、サンプルDBのバックアップを
            // 名前付きボリュームで SQL Server コンテナと共有する（空のボリュームにはイメージ内のファイルがコピーされる）
            sb.AppendLine("    volumes:");
            sb.AppendLine($"      - {SampleVolume}:{SampleDir}");
        }
        sb.AppendLine("    restart: unless-stopped");

        if (o.DbType == "postgresql")
        {
            sb.AppendLine("  db:");
            sb.AppendLine($"    image: {ContainerImages.Postgres}");
            sb.AppendLine("    container_name: timetracker-postgres");
            sb.AppendLine("    environment:");
            sb.AppendLine("      POSTGRES_USER: \"${DB_USER}\"");
            sb.AppendLine("      POSTGRES_PASSWORD: \"${DB_PASSWORD}\"");
            // POSTGRES_DB で DB を事前作成しない。事前作成すると、サンプルDB作成時にアプリが対象DBへの接続を
            // 保持したまま DB を削除・復元しようとして失敗する。DB は Timetracker のセットアップで作成される。
            sb.AppendLine("    volumes:");
            sb.AppendLine("      - pgdata:/var/lib/postgresql/data");
            // 初期化中の一時サーバーは TCP を待ち受けないため、-h 127.0.0.1 で本起動を判定する。
            // 対象DB はセットアップまで存在しないため、既定の postgres DB で確認する
            sb.AppendLine("    healthcheck:");
            sb.AppendLine("      test: [\"CMD-SHELL\", \"pg_isready -h 127.0.0.1 -U ${DB_USER} -d postgres\"]");
            sb.AppendLine("      interval: 5s");
            sb.AppendLine("      timeout: 5s");
            sb.AppendLine("      retries: 20");
            sb.AppendLine("    restart: unless-stopped");
        }
        else
        {
            sb.AppendLine("  db:");
            sb.AppendLine($"    image: {ContainerImages.SqlServer}");
            sb.AppendLine("    container_name: timetracker-sqlserver");
            sb.AppendLine("    environment:");
            sb.AppendLine("      ACCEPT_EULA: \"Y\"");
            sb.AppendLine("      MSSQL_PID: \"Developer\"");
            sb.AppendLine("      MSSQL_SA_PASSWORD: \"${DB_PASSWORD}\"");
            sb.AppendLine("    volumes:");
            sb.AppendLine("      - mssqldata:/var/opt/mssql");
            sb.AppendLine($"      - {SampleVolume}:{SampleDir}:ro");
            sb.AppendLine("    healthcheck:");
            sb.AppendLine("      test: [\"CMD-SHELL\", \"/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P \\\"$$MSSQL_SA_PASSWORD\\\" -Q \\\"SELECT 1\\\" -b -o /dev/null\"]");
            sb.AppendLine("      interval: 5s");
            sb.AppendLine("      timeout: 5s");
            sb.AppendLine("      retries: 30");
            sb.AppendLine("      start_period: 10s");
            sb.AppendLine("    restart: unless-stopped");
        }

        sb.AppendLine("  redis:");
        sb.AppendLine($"    image: {ContainerImages.Redis}");
        sb.AppendLine("    container_name: timetracker-redis");
        sb.AppendLine("    command: [\"redis-server\", \"--appendonly\", \"yes\"]");
        sb.AppendLine("    volumes:");
        sb.AppendLine("      - redisdata:/data");
        sb.AppendLine("    healthcheck:");
        sb.AppendLine("      test: [\"CMD\", \"redis-cli\", \"ping\"]");
        sb.AppendLine("      interval: 5s");
        sb.AppendLine("      timeout: 5s");
        sb.AppendLine("      retries: 20");
        sb.AppendLine("    restart: unless-stopped");

        sb.AppendLine();
        sb.AppendLine("volumes:");
        if (o.DbType == "postgresql") sb.AppendLine("  pgdata:");
        else
        {
            sb.AppendLine("  mssqldata:");
            sb.AppendLine($"  {SampleVolume}:");
        }
        sb.AppendLine("  redisdata:");

        return sb.ToString();
    }

    // docker compose は .env のクォート無しの値に含まれる $VAR を展開するため、
    // ダブルクォートで囲み \ " $ をエスケープして値をそのまま渡す
    private static string EnvValue(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$") + "\"";

    public static string GenerateEnv(DeployOptions o)
    {
        var dbPort = o.DbType == "postgresql" ? 5432 : 1433;
        var dbUserFixed = o.DbType == "postgresql" ? Defaults.DbUserFixedPostgres : Defaults.DbUserFixedSqlServer;

        var lines = new List<string>
        {
            $"TTNX_DB_TYPE={EnvValue(o.DbType)}",
            $"DB_PORT={EnvValue(dbPort.ToString())}",
            $"DB_USER={EnvValue(dbUserFixed)}",
            $"DB_PASSWORD={EnvValue(o.DbPassword)}",
            $"DB_NAME={EnvValue(o.DbName)}",
            $"TIMETRACKER_PASSWORD={EnvValue(o.TrackerPassword)}"
        };

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    // SQL Server の SA パスワード要件: 8 文字以上で、英大文字・英小文字・数字・記号のうち 3 種類以上を含む
    internal static bool MeetsSqlServerPasswordPolicy(string password)
    {
        var categories = 0;
        if (password.Any(char.IsAsciiLetterUpper)) categories++;
        if (password.Any(char.IsAsciiLetterLower)) categories++;
        if (password.Any(char.IsAsciiDigit)) categories++;
        if (password.Any(c => !char.IsAsciiLetterOrDigit(c))) categories++;
        return password.Length >= 8 && categories >= 3;
    }

    // timetracker イメージのタグが DB 種別向けのものか（例: postgresql → *-postgres、sqlserver → *-mssql）
    internal static bool TagMatchesDbType(string dbType, string tag)
    {
        var tagLower = tag.ToLowerInvariant();
        return dbType switch
        {
            "postgresql" => tagLower.Contains("postgres"),
            "sqlserver" => tagLower.Contains("mssql") || tagLower.Contains("sqlserver"),
            _ => false
        };
    }

    /// <summary>Azure へのデプロイ用の検証。<see cref="ValidateForCompose"/> の内容に加え、subscription / resource-group を必須とする。</summary>
    /// <exception cref="ArgumentException">設定が不正な場合</exception>
    public static void Validate(DeployOptions o) => Validate(o, requireAzure: true);

    /// <summary>Dry-run（docker-compose.yml / .env の生成）用の検証。Azure を使わないため subscription / resource-group は不要。</summary>
    /// <exception cref="ArgumentException">設定が不正な場合</exception>
    public static void ValidateForCompose(DeployOptions o) => Validate(o, requireAzure: false);

    // 全エラーのうち最初のものを返す（CLI は従来どおり最初のエラーだけを表示する）
    private static void Validate(DeployOptions o, bool requireAzure)
    {
        var errors = DeploymentValidator.GetErrors(o, requireAzure ? ValidationTarget.Azure : ValidationTarget.Compose);
        if (errors.Count > 0)
            throw new ArgumentException(errors[0]);
    }
}
