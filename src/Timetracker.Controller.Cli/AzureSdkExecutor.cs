using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.AppContainers;
using Azure.ResourceManager.AppContainers.Models;
using Timetracker.Common;
using Timetracker.Domain.Deployment;

public class AzureSdkExecutor
{
    private readonly SimpleLogger _logger;
    private readonly ArmClient _arm;

    public AzureSdkExecutor(SimpleLogger logger, TokenCredential credential)
    {
        _logger = logger;
        _arm = new ArmClient(credential);
    }

    public async Task EnsureResourceGroupAsync(string subscriptionId, string rgName, string location)
    {
        var sub = _arm.GetSubscriptionResource(new ResourceIdentifier($"/subscriptions/{subscriptionId}"));
        var rgOp = await sub.GetResourceGroups().CreateOrUpdateAsync(
            WaitUntil.Completed,
            rgName,
            new ResourceGroupData(location));
        _logger.Info($"ResourceGroup ready: {rgName}");
    }

    public async Task<ContainerAppManagedEnvironmentResource> EnsureContainerAppEnvAsync(string subscriptionId, string rgName, string envName, string location)
    {
        var rg = _arm.GetResourceGroupResource(new ResourceIdentifier($"/subscriptions/{subscriptionId}/resourceGroups/{rgName}"));
        var envCollection = rg.GetContainerAppManagedEnvironments();

        var data = new ContainerAppManagedEnvironmentData(location)
        {
            // VNet 設定は呼び出し側で必要に応じて追加（例: InfrastructureSubnetId）
        };
        var envOp = await envCollection.CreateOrUpdateAsync(WaitUntil.Completed, envName, data);
        var env = envOp.Value;
        _logger.Info($"Container Apps Environment ready: {envName}");
        return env;
    }

    public async Task CreateOrUpdateContainerAppAsync(
        string subscriptionId,
        string rgName,
        string appName,
        string location,
        DeployOptions opts,
        ContainerAppManagedEnvironmentResource env)
    {
        var rg = _arm.GetResourceGroupResource(new ResourceIdentifier($"/subscriptions/{subscriptionId}/resourceGroups/{rgName}"));
        var apps = rg.GetContainerApps();

        var dbUserFixed = opts.DbType == "postgresql" ? Defaults.DbUserFixedPostgres : Defaults.DbUserFixedSqlServer;
        var dbPort = opts.DbType == "postgresql" ? 5432 : 1433;

        var ttImage = $"densocreate/timetracker:{opts.TimetrackerTag}";

        // 1) DB 用 Container App（Internal Ingress: TCP + ExposedPort）
        var dbContainer = opts.DbType == "postgresql"
            ? new ContainerAppContainer()
            {
                Name = "timetracker-db",
                Image = "postgres:16", // Functions と合わせる
                Env =
                {
                    new ContainerAppEnvironmentVariable() { Name = "POSTGRES_USER", Value = dbUserFixed },
                    new ContainerAppEnvironmentVariable() { Name = "POSTGRES_PASSWORD", Value = opts.DbPassword },
                    new ContainerAppEnvironmentVariable() { Name = "POSTGRES_DB", Value = opts.DbName },
                },
                Resources = new AppContainerResources()
                {
                    Cpu = opts.DbCpu,
                    Memory = $"{opts.DbMemoryGi}Gi"
                }
            }
            : new ContainerAppContainer()
            {
                Name = "timetracker-db",
                Image = "mcr.microsoft.com/mssql/server:2022-latest",
                Env =
                {
                    new ContainerAppEnvironmentVariable() { Name = "ACCEPT_EULA", Value = "Y" },
                    new ContainerAppEnvironmentVariable() { Name = "MSSQL_PID", Value = "Developer" },
                    new ContainerAppEnvironmentVariable() { Name = "SA_PASSWORD", Value = opts.DbPassword },
                },
                Resources = new AppContainerResources()
                {
                    Cpu = opts.DbCpu,
                    Memory = $"{opts.DbMemoryGi}Gi"
                }
            };

        var dbTemplate = new ContainerAppTemplate()
        {
            Containers = { dbContainer },
            Scale = new ContainerAppScale()
            {
                MinReplicas = 1,
                MaxReplicas = 1
            }
        };

        var dbConfig = new ContainerAppConfiguration()
        {
            Ingress = new ContainerAppIngressConfiguration()
            {
                External = false,
                TargetPort = dbPort,
                Transport = ContainerAppIngressTransportMethod.Tcp,
                ExposedPort = dbPort
            }
        };

        var dbAppData = new ContainerAppData(location)
        {
            EnvironmentId = env.Id,
            Template = dbTemplate,
            Configuration = dbConfig
        };

        var dbAppName = $"{appName}-db";
        var dbAppOp = await apps.CreateOrUpdateAsync(WaitUntil.Completed, dbAppName, dbAppData);
        var dbApp = dbAppOp.Value;
        // 内部接続は Container App 名を使用
        var dbEndpointHost = dbAppName;
        _logger.Info($"DB app ready: {dbApp.Data.Name} (endpoint: {dbEndpointHost}:{dbPort})");

        // 2) Redis 用 Container App（Internal Ingress: TCP + ExposedPort）
        var redisContainer = new ContainerAppContainer()
        {
            Name = "timetracker-redis",
            Image = "redis:latest", // Functions と合わせる
            Args = { "redis-server", "--appendonly", "yes" },
            Resources = new AppContainerResources()
            {
                Cpu = opts.RedisCpu,
                Memory = $"{opts.RedisMemoryGi}Gi"
            }
        };

        var redisTemplate = new ContainerAppTemplate()
        {
            Containers = { redisContainer },
            Scale = new ContainerAppScale()
            {
                MinReplicas = 1,
                MaxReplicas = 1
            }
        };

        var redisConfig = new ContainerAppConfiguration()
        {
            Ingress = new ContainerAppIngressConfiguration()
            {
                External = false,
                TargetPort = 6379,
                Transport = ContainerAppIngressTransportMethod.Tcp,
                ExposedPort = 6379
            }
        };

        var redisAppData = new ContainerAppData(location)
        {
            EnvironmentId = env.Id,
            Template = redisTemplate,
            Configuration = redisConfig
        };

        var redisAppName = $"{appName}-redis";
        var redisAppOp = await apps.CreateOrUpdateAsync(WaitUntil.Completed, redisAppName, redisAppData);
        var redisApp = redisAppOp.Value;
        var redisEndpointHost = redisAppName;
        _logger.Info($"Redis app ready: {redisApp.Data.Name} (endpoint: {redisEndpointHost}:6379)");

        // 3) Timetracker 用 Container App（External Ingress: Auto）
        var timetrackerContainer = new ContainerAppContainer()
        {
            Name = "timetracker",
            Image = ttImage,
            Env =
            {
                new ContainerAppEnvironmentVariable() { Name = "ASPNETCORE_URLS", Value = "http://0.0.0.0:8080" },
                new ContainerAppEnvironmentVariable() { Name = "TTNX_DB_TYPE", Value = opts.DbType },
                // DB/Redis は Container App 名 + ポートを使用
                new ContainerAppEnvironmentVariable() { Name = "TTNX_DB_SERVER", Value = $"{dbEndpointHost}:{dbPort}" },
                new ContainerAppEnvironmentVariable() { Name = "TTNX_DB_USER", Value = dbUserFixed },
                new ContainerAppEnvironmentVariable() { Name = "TTNX_DB_PASSWORD", Value = opts.DbPassword },
                new ContainerAppEnvironmentVariable() { Name = "TTNX_DB_NAME", Value = opts.DbName },
                new ContainerAppEnvironmentVariable() { Name = "TTNX_DB_PORT", Value = dbPort.ToString() },
                // Optional: DB オプション（Functions では空）
                new ContainerAppEnvironmentVariable() { Name = "TTNX_DB_OPTIONS", Value = string.Empty },
                new ContainerAppEnvironmentVariable() { Name = "TTNX_REDIS_GLOBALCACHE", Value = $"{redisEndpointHost}:6379" },
                new ContainerAppEnvironmentVariable() { Name = "TTNX_REDIS_HANGFIRE", Value = $"{redisEndpointHost}:6379" },
                new ContainerAppEnvironmentVariable() { Name = "TTNX_REDIS_BACKGROUNDJOB", Value = $"{redisEndpointHost}:6379" },
                // 互換のため従来の変数も設定
                new ContainerAppEnvironmentVariable() { Name = "DB_HOST", Value = dbEndpointHost },
                new ContainerAppEnvironmentVariable() { Name = "DB_PORT", Value = dbPort.ToString() },
                new ContainerAppEnvironmentVariable() { Name = "DB_USER", Value = dbUserFixed },
                new ContainerAppEnvironmentVariable() { Name = "DB_PASSWORD", Value = opts.DbPassword },
                new ContainerAppEnvironmentVariable() { Name = "DB_NAME", Value = opts.DbName },
                new ContainerAppEnvironmentVariable() { Name = "REDIS_HOST", Value = redisEndpointHost },
                new ContainerAppEnvironmentVariable() { Name = "REDIS_PORT", Value = "6379" },
                new ContainerAppEnvironmentVariable() { Name = "APP_PASSWORD", Value = opts.TrackerPassword },
            },
            Resources = new AppContainerResources()
            {
                Cpu = opts.TimetrackerCpu,
                Memory = $"{opts.TimetrackerMemoryGi}Gi"
            }
        };

        var ttTemplate = new ContainerAppTemplate()
        {
            Containers = { timetrackerContainer },
            Scale = new ContainerAppScale()
            {
                MinReplicas = 1,
                MaxReplicas = 1
            }
        };

        var ttConfig = new ContainerAppConfiguration()
        {
            Ingress = new ContainerAppIngressConfiguration()
            {
                External = true,
                TargetPort = 8080,
                Transport = ContainerAppIngressTransportMethod.Auto
            }
        };

        var ttAppData = new ContainerAppData(location)
        {
            EnvironmentId = env.Id,
            Template = ttTemplate,
            Configuration = ttConfig,
        };

        var ttAppOp = await apps.CreateOrUpdateAsync(WaitUntil.Completed, $"{appName}-tt", ttAppData);
        var ttApp = ttAppOp.Value;
        var ttUrl = ttApp.Data.Configuration?.Ingress?.Fqdn != null
            ? $"https://{ttApp.Data.Configuration.Ingress.Fqdn}"
            : string.Empty;

        _logger.Success($"Container Apps ready: {ttApp.Data.Name}, {dbApp.Data.Name}, {redisApp.Data.Name}");
        if (!string.IsNullOrEmpty(ttUrl))
        {
            _logger.Success($"Timetracker endpoint: {ttUrl}");
        }
        else
        {
            _logger.Warn("Timetracker endpoint FQDN not available yet. Check Container App ingress settings.");
        }
    }
}