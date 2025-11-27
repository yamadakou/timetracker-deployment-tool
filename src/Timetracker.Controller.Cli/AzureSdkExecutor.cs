using Azure;
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

    public AzureSdkExecutor(SimpleLogger logger)
    {
        _logger = logger;
        var cred = new DefaultAzureCredential();
        _arm = new ArmClient(cred);
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
            // 必要に応じて Log Analytics / VNet 等の設定
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

        var dbUserFixed = opts.DbType == "postgres" ? Defaults.DbUserFixedPostgres : Defaults.DbUserFixedSqlServer;
        var dbPort = opts.DbType == "postgres" ? 5432 : 1433;

        var ttImage = $"densocreate/timetracker:{opts.TimetrackerTag}";

        var timetrackerContainer = new ContainerAppContainer()
        {
            Name = "timetracker",
            Image = ttImage,
            Env =
            {
                new EnvironmentVar("DB_HOST", "db"),
                new EnvironmentVar("DB_PORT", dbPort.ToString()),
                new EnvironmentVar("DB_USER", dbUserFixed),
                new EnvironmentVar("DB_PASSWORD", opts.DbPassword),
                new EnvironmentVar("DB_NAME", opts.DbName),
                new EnvironmentVar("REDIS_HOST", "redis"),
                new EnvironmentVar("REDIS_PORT", "6379"),
                new EnvironmentVar("APP_PASSWORD", opts.TrackerPassword),
            },
            Resources = new ContainerResources()
            {
                Cpu = opts.TimetrackerCpu,
                Memory = $"{opts.TimetrackerMemoryGi}Gi"
            }
        };

        var dbContainer = opts.DbType == "postgres"
            ? new ContainerAppContainer()
            {
                Name = "db",
                Image = "postgres:16-alpine",
                Env =
                {
                    new EnvironmentVar("POSTGRES_USER", dbUserFixed),
                    new EnvironmentVar("POSTGRES_PASSWORD", opts.DbPassword),
                    new EnvironmentVar("POSTGRES_DB", opts.DbName),
                },
                Resources = new ContainerResources()
                {
                    Cpu = opts.DbCpu,
                    Memory = $"{opts.DbMemoryGi}Gi"
                }
            }
            : new ContainerAppContainer()
            {
                Name = "db",
                Image = "mcr.microsoft.com/mssql/server:2022-latest",
                Env =
                {
                    new EnvironmentVar("ACCEPT_EULA", "Y"),
                    new EnvironmentVar("MSSQL_PID", "Developer"),
                    new EnvironmentVar("SA_PASSWORD", opts.DbPassword),
                },
                Resources = new ContainerResources()
                {
                    Cpu = opts.DbCpu,
                    Memory = $"{opts.DbMemoryGi}Gi"
                }
            };

        var redisContainer = new ContainerAppContainer()
        {
            Name = "redis",
            Image = "redis:7-alpine",
            Args = { "redis-server", "--appendonly", "yes" },
            Resources = new ContainerResources()
            {
                Cpu = opts.RedisCpu,
                Memory = $"{opts.RedisMemoryGi}Gi"
            }
        };

        var template = new ContainerAppTemplate()
        {
            Containers = { timetrackerContainer, dbContainer, redisContainer },
            // スケール構成不要：各コンテナとも 1 台
            Scale = new ContainerAppScale()
            {
                MinReplicas = 1,
                MaxReplicas = 1
            }
        };

        var data = new ContainerAppData(location)
        {
            ManagedEnvironmentId = env.Id,
            Template = template,
            Configuration = new ContainerAppConfiguration()
            {
                Ingress = new ContainerAppIngress()
                {
                    External = true,
                    TargetPort = 8080,
                }
            }
        };

        var appOp = await apps.CreateOrUpdateAsync(WaitUntil.Completed, appName, data);
        var app = appOp.Value;
        _logger.Success($"Container App ready: {appName}");
    }
}