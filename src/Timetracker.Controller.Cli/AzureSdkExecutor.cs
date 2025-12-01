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
                new ContainerAppEnvironmentVariable() { Name = "DB_HOST", Value = "db" },
                new ContainerAppEnvironmentVariable() { Name = "DB_PORT", Value = dbPort.ToString() },
                new ContainerAppEnvironmentVariable() { Name = "DB_USER", Value = dbUserFixed },
                new ContainerAppEnvironmentVariable() { Name = "DB_PASSWORD", Value = opts.DbPassword },
                new ContainerAppEnvironmentVariable() { Name = "DB_NAME", Value = opts.DbName },
                new ContainerAppEnvironmentVariable() { Name = "REDIS_HOST", Value = "redis" },
                new ContainerAppEnvironmentVariable() { Name = "REDIS_PORT", Value = "6379" },
                new ContainerAppEnvironmentVariable() { Name = "APP_PASSWORD", Value = opts.TrackerPassword },
            },
            Resources = new AppContainerResources()
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
                Name = "db",
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

        var redisContainer = new ContainerAppContainer()
        {
            Name = "redis",
            Image = "redis:7-alpine",
            Args = { "redis-server", "--appendonly", "yes" },
            Resources = new AppContainerResources()
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
                Ingress = new ContainerAppIngressConfiguration()
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