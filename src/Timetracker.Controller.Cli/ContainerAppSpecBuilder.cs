using System.Globalization;
using Azure.Core;
using Azure.ResourceManager.AppContainers;
using Azure.ResourceManager.AppContainers.Models;
using Timetracker.Domain.Deployment;

namespace Timetracker.Controller.Cli;

/// <summary>
/// Timetracker / DB / Redis の各 Container App の定義（ContainerAppData）を組み立てる。
/// Azure への送信は行わないため、単体テストで内容を検証できる。
/// </summary>
public static class ContainerAppSpecBuilder
{
    // Container Apps の secret 名（英小文字・数字・ハイフンのみ）
    public const string DbPasswordSecretName = "db-password";
    public const string AppPasswordSecretName = "app-password";

    public const int RedisPort = 6379;

    public static int GetDbPort(DeployOptions opts) => opts.DbType == "postgresql" ? 5432 : 1433;

    public static string GetDbUser(DeployOptions opts) =>
        opts.DbType == "postgresql" ? Defaults.DbUserFixedPostgres : Defaults.DbUserFixedSqlServer;

    // ロケールに依存せず "1.5Gi" 形式で出力する
    private static string Gi(double memory) => memory.ToString(CultureInfo.InvariantCulture) + "Gi";

    private static ContainerAppEnvironmentVariable Env(string name, string value) => new() { Name = name, Value = value };

    // パスワード等の機密値は平文の Value ではなく secret を参照させる（ポータル/ARM 上で値が見えないようにする）
    private static ContainerAppEnvironmentVariable SecretEnv(string name, string secretName) => new() { Name = name, SecretRef = secretName };

    private static ContainerAppTemplate SingleReplicaTemplate(ContainerAppContainer container) => new()
    {
        Containers = { container },
        Scale = new ContainerAppScale()
        {
            MinReplicas = 1,
            MaxReplicas = 1
        }
    };

    // 1) DB 用 Container App（Internal Ingress: TCP + ExposedPort）
    public static ContainerAppData BuildDbApp(DeployOptions opts, string location, ResourceIdentifier envId)
    {
        var dbPort = GetDbPort(opts);
        var dbContainer = opts.DbType == "postgresql"
            ? new ContainerAppContainer()
            {
                Name = "timetracker-db",
                Image = "postgres:16", // Functions と合わせる
                Env =
                {
                    Env("POSTGRES_USER", GetDbUser(opts)),
                    SecretEnv("POSTGRES_PASSWORD", DbPasswordSecretName),
                    Env("POSTGRES_DB", opts.DbName),
                },
                Resources = new AppContainerResources()
                {
                    Cpu = opts.DbCpu,
                    Memory = Gi(opts.DbMemoryGi)
                }
            }
            : new ContainerAppContainer()
            {
                Name = "timetracker-db",
                Image = "mcr.microsoft.com/mssql/server:2022-latest",
                Env =
                {
                    Env("ACCEPT_EULA", "Y"),
                    Env("MSSQL_PID", "Developer"),
                    SecretEnv("SA_PASSWORD", DbPasswordSecretName),
                },
                Resources = new AppContainerResources()
                {
                    Cpu = opts.DbCpu,
                    Memory = Gi(opts.DbMemoryGi)
                }
            };

        return new ContainerAppData(location)
        {
            EnvironmentId = envId,
            Template = SingleReplicaTemplate(dbContainer),
            Configuration = new ContainerAppConfiguration()
            {
                Secrets =
                {
                    new ContainerAppWritableSecret() { Name = DbPasswordSecretName, Value = opts.DbPassword },
                },
                Ingress = new ContainerAppIngressConfiguration()
                {
                    External = false,
                    TargetPort = dbPort,
                    Transport = ContainerAppIngressTransportMethod.Tcp,
                    ExposedPort = dbPort
                }
            }
        };
    }

    // 2) Redis 用 Container App（Internal Ingress: TCP + ExposedPort）
    public static ContainerAppData BuildRedisApp(DeployOptions opts, string location, ResourceIdentifier envId)
    {
        var redisContainer = new ContainerAppContainer()
        {
            Name = "timetracker-redis",
            Image = "redis:latest", // Functions と合わせる
            Args = { "redis-server", "--appendonly", "yes" },
            Resources = new AppContainerResources()
            {
                Cpu = opts.RedisCpu,
                Memory = Gi(opts.RedisMemoryGi)
            }
        };

        return new ContainerAppData(location)
        {
            EnvironmentId = envId,
            Template = SingleReplicaTemplate(redisContainer),
            Configuration = new ContainerAppConfiguration()
            {
                Ingress = new ContainerAppIngressConfiguration()
                {
                    External = false,
                    TargetPort = RedisPort,
                    Transport = ContainerAppIngressTransportMethod.Tcp,
                    ExposedPort = RedisPort
                }
            }
        };
    }

    // 3) Timetracker 用 Container App（External Ingress: Auto）
    public static ContainerAppData BuildTimetrackerApp(
        DeployOptions opts, string location, ResourceIdentifier envId, string dbEndpointHost, string redisEndpointHost)
    {
        var dbPort = GetDbPort(opts);
        var dbUser = GetDbUser(opts);
        var redisEndpoint = $"{redisEndpointHost}:{RedisPort}";

        var timetrackerContainer = new ContainerAppContainer()
        {
            Name = "timetracker",
            Image = $"densocreate/timetracker:{opts.TimetrackerTag}",
            Env =
            {
                Env("ASPNETCORE_URLS", "http://0.0.0.0:8080"),
                Env("TTNX_DB_TYPE", opts.DbType),
                // DB/Redis は Container App 名 + ポートを使用
                Env("TTNX_DB_SERVER", $"{dbEndpointHost}:{dbPort}"),
                Env("TTNX_DB_USER", dbUser),
                SecretEnv("TTNX_DB_PASSWORD", DbPasswordSecretName),
                Env("TTNX_DB_NAME", opts.DbName),
                Env("TTNX_DB_PORT", dbPort.ToString()),
                // Optional: DB オプション（Functions では空）
                Env("TTNX_DB_OPTIONS", string.Empty),
                Env("TTNX_REDIS_GLOBALCACHE", redisEndpoint),
                Env("TTNX_REDIS_HANGFIRE", redisEndpoint),
                Env("TTNX_REDIS_BACKGROUNDJOB", redisEndpoint),
                // 互換のため従来の変数も設定
                Env("DB_HOST", dbEndpointHost),
                Env("DB_PORT", dbPort.ToString()),
                Env("DB_USER", dbUser),
                SecretEnv("DB_PASSWORD", DbPasswordSecretName),
                Env("DB_NAME", opts.DbName),
                Env("REDIS_HOST", redisEndpointHost),
                Env("REDIS_PORT", RedisPort.ToString()),
                SecretEnv("APP_PASSWORD", AppPasswordSecretName),
            },
            Resources = new AppContainerResources()
            {
                Cpu = opts.TimetrackerCpu,
                Memory = Gi(opts.TimetrackerMemoryGi)
            }
        };

        return new ContainerAppData(location)
        {
            EnvironmentId = envId,
            Template = SingleReplicaTemplate(timetrackerContainer),
            Configuration = new ContainerAppConfiguration()
            {
                Secrets =
                {
                    new ContainerAppWritableSecret() { Name = DbPasswordSecretName, Value = opts.DbPassword },
                    new ContainerAppWritableSecret() { Name = AppPasswordSecretName, Value = opts.TrackerPassword },
                },
                Ingress = new ContainerAppIngressConfiguration()
                {
                    External = true,
                    TargetPort = 8080,
                    Transport = ContainerAppIngressTransportMethod.Auto
                }
            }
        };
    }
}
