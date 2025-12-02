using System.Text;

namespace Timetracker.Domain.Deployment;

public static class ComposeGenerator
{
    public static string GenerateCompose(DeployOptions o)
    {
        var ttImage = $"densocreate/timetracker:{o.TimetrackerTag}";

        var sb = new StringBuilder();
        sb.AppendLine("version: '3.9'");
        sb.AppendLine("services:");
        sb.AppendLine("  timetracker:");
        sb.AppendLine($"    image: {ttImage}");
        sb.AppendLine("    container_name: timetracker-app");
        sb.AppendLine("    depends_on:");
        sb.AppendLine("      - db");
        sb.AppendLine("      - redis");
        sb.AppendLine("    ports:");
        sb.AppendLine("      - \"8080:8080\"");
        sb.AppendLine("    environment:");
        sb.AppendLine("      DB_HOST=db");
        sb.AppendLine("      DB_PORT=${DB_PORT}");
        sb.AppendLine("      DB_USER=${DB_USER}");
        sb.AppendLine("      DB_PASSWORD=${DB_PASSWORD}");
        sb.AppendLine("      DB_NAME=${DB_NAME}");
        sb.AppendLine("      REDIS_HOST=redis");
        sb.AppendLine("      REDIS_PORT=6379");
        sb.AppendLine("      APP_PASSWORD=${TIMETRACKER_PASSWORD}  # <your password>");
        sb.AppendLine("    restart: unless-stopped");

        if (o.DbType == "postgres")
        {
            sb.AppendLine("  db:");
            sb.AppendLine("    image: postgres:16-alpine");
            sb.AppendLine("    container_name: timetracker-postgres");
            sb.AppendLine("    environment:");
            sb.AppendLine("      POSTGRES_USER=${DB_USER}");
            sb.AppendLine("      POSTGRES_PASSWORD=${DB_PASSWORD}");
            sb.AppendLine("      POSTGRES_DB=${DB_NAME}");
            sb.AppendLine("    volumes:");
            sb.AppendLine("      - pgdata:/var/lib/postgresql/data");
            sb.AppendLine("    restart: unless-stopped");
        }
        else
        {
            sb.AppendLine("  db:");
            sb.AppendLine("    image: mcr.microsoft.com/mssql/server:2022-latest");
            sb.AppendLine("    container_name: timetracker-sqlserver");
            sb.AppendLine("    environment:");
            sb.AppendLine("      ACCEPT_EULA: 'Y'");
            sb.AppendLine("      MSSQL_PID: 'Developer'");
            sb.AppendLine("      SA_PASSWORD=${DB_PASSWORD}");
            sb.AppendLine("    ports:");
            sb.AppendLine("      - '1433:1433'");
            sb.AppendLine("    volumes:");
            sb.AppendLine("      - mssqldata:/var/opt/mssql");
            sb.AppendLine("    restart: unless-stopped");
        }

        sb.AppendLine("  redis:");
        sb.AppendLine("    image: redis:7-alpine");
        sb.AppendLine("    container_name: timetracker-redis");
        sb.AppendLine("    command: [\"redis-server\", \"--appendonly\", \"yes\"]");
        sb.AppendLine("    volumes:");
        sb.AppendLine("      - redisdata:/data");
        sb.AppendLine("    restart: unless-stopped");

        sb.AppendLine();
        sb.AppendLine("volumes:");
        if (o.DbType == "postgres") sb.AppendLine("  pgdata:");
        else sb.AppendLine("  mssqldata:");
        sb.AppendLine("  redisdata:");

        return sb.ToString();
    }

    public static string GenerateEnv(DeployOptions o)
    {
        var dbPort = o.DbType == "postgres" ? 5432 : 1433;
        var dbUserFixed = o.DbType == "postgres" ? Defaults.DbUserFixedPostgres : Defaults.DbUserFixedSqlServer;

        var lines = new List<string>
        {
            $"DB_PORT={dbPort}",
            $"DB_USER={dbUserFixed}",
            $"DB_PASSWORD={o.DbPassword}",
            $"DB_NAME={o.DbName}",
            $"TIMETRACKER_PASSWORD={o.TrackerPassword}"
        };

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    public static void Validate(DeployOptions o)
    {
        if (o.DbType is not ("postgres" or "sqlserver"))
            throw new ArgumentException("db-type は postgres または sqlserver を指定してください。");
        if (string.IsNullOrWhiteSpace(o.Subscription))
            throw new ArgumentException("subscription は必須です。");
        if (string.IsNullOrWhiteSpace(o.ResourceGroup))
            throw new ArgumentException("resource-group は必須です。");
        if (string.IsNullOrWhiteSpace(o.TrackerPassword) || o.TrackerPassword.Length < 8)
            throw new ArgumentException("tracker-password が不足または短すぎます。");
        if (string.IsNullOrWhiteSpace(o.DbPassword) || o.DbPassword.Length < 8)
            throw new ArgumentException("db-password が不足または短すぎます。");
        if (string.IsNullOrWhiteSpace(o.TimetrackerTag))
            throw new ArgumentException("tt-tag（timetracker イメージタグ）が空です。例: 7.0-linux-postgres, 7.0-linux-mssql など。");

        // db-type と tt-tag の組み合わせ検証
        var tagLower = o.TimetrackerTag.ToLowerInvariant();
        bool tagMatchesDb = o.DbType switch
        {
            "postgres" => tagLower.Contains("postgres"),
            "sqlserver" => tagLower.Contains("mssql") || tagLower.Contains("sqlserver"),
            _ => false
        };
        if (!tagMatchesDb)
        {
            throw new ArgumentException(
                $"不適切な組み合わせ: db-type='{o.DbType}' と tt-tag='{o.TimetrackerTag}' は対応していません。" +
                " DB 種別に合致するタグを指定してください (例: postgres → 7.0-linux-postgres / sqlserver → 7.0-linux-mssql)。" +
                " タグ一覧: https://hub.docker.com/r/densocreate/timetracker/tags");
        }
    }
} 
