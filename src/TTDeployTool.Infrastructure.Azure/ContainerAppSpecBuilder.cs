using System.Globalization;
using Azure.Core;
using Azure.ResourceManager.AppContainers;
using Azure.ResourceManager.AppContainers.Models;
using Azure.ResourceManager.Storage;
using Azure.ResourceManager.Storage.Models;
using TTDeployTool.Domain.Deployment;

namespace TTDeployTool.Infrastructure.Azure;

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

    // Container Apps Environment（VNet 設定は必要に応じて追加。例: InfrastructureSubnetId）
    public static ContainerAppManagedEnvironmentData BuildManagedEnvironment(string location, string appName)
    {
        var data = new ContainerAppManagedEnvironmentData(location)
        {
            // Azure.ResourceManager.AppContainers 1.6.0 以降は properties が空だとボディから省かれ、
            // API が 400 (Must contain 'properties') を返すため、既定値（ゾーン冗長なし）を明示して properties を送る
            IsZoneRedundant = false,
        };
        foreach (var (key, value) in ManagedTags.For(appName))
        {
            data.Tags[key] = value;
        }
        return data;
    }

    /// <summary>
    /// Container App の定義に管理タグを付ける。更新時は既存のタグを引き継ぐ
    /// （PUT はタグを置き換えるため、送らないと利用者が付けたタグが消える）。
    /// <paramref name="lifecycleTags"/>（利用可能期限・デプロイした時刻）があれば、それで上書きする。
    /// </summary>
    public static ContainerAppData WithManagedTags(
        ContainerAppData data, string appName, IReadOnlyDictionary<string, string>? existingTags, IReadOnlyDictionary<string, string>? lifecycleTags = null)
    {
        data.Tags.Clear();
        foreach (var (key, value) in ManagedTags.Merge(existingTags, appName))
        {
            data.Tags[key] = value;
        }
        foreach (var (key, value) in lifecycleTags ?? new Dictionary<string, string>())
        {
            data.Tags[key] = value;
        }
        return data;
    }

    /// <summary>
    /// DB のデータを置くストレージアカウント（StorageV2 / Standard_LRS / ホット）。SMB のマウントはアカウントキーで行うため、
    /// 共有キーによるアクセスを明示的に許可する。Blob の匿名アクセスは使わないため禁止する。
    /// </summary>
    public static StorageAccountCreateOrUpdateContent BuildStorageAccount(string location, string appName)
    {
        var content = new StorageAccountCreateOrUpdateContent(new StorageSku(StorageSkuName.StandardLrs), StorageKind.StorageV2, new AzureLocation(location))
        {
            AccessTier = StorageAccountAccessTier.Hot,
            MinimumTlsVersion = StorageMinimumTlsVersion.Tls1_2,
            EnableHttpsTrafficOnly = true,
            AllowBlobPublicAccess = false,
            AllowSharedKeyAccess = true,
        };
        foreach (var (key, value) in ManagedTags.For(appName))
        {
            content.Tags[key] = value;
        }
        return content;
    }

    /// <summary>DB のデータを置くファイル共有（SMB）。</summary>
    public static FileShareData BuildDbFileShare() => new()
    {
        ShareQuota = DbPersistence.ShareQuotaGiB,
        EnabledProtocol = FileShareEnabledProtocol.Smb,
    };

    /// <summary>Environment に登録するストレージ（Azure Files の SMB 共有。読み書き）。アカウントキーを含むため、ログに出さない。</summary>
    public static ContainerAppManagedEnvironmentStorageData BuildEnvironmentStorage(string accountName, string accountKey, string shareName) => new()
    {
        ManagedEnvironmentStorageAzureFile = new ContainerAppAzureFileProperties
        {
            AccountName = accountName,
            AccountKey = accountKey,
            ShareName = shareName,
            AccessMode = ContainerAppAccessMode.ReadWrite,
        },
    };

    // DB のデータのディレクトリに Azure Files をマウントする（詳細設計 docs/design/db-persistence.md の 4）
    private static void AddDbStorage(ContainerAppTemplate template, ContainerAppContainer container, string dbType)
    {
        template.Volumes.Add(new ContainerAppVolume
        {
            Name = DbPersistence.StorageName,
            StorageType = ContainerAppStorageType.AzureFile,
            StorageName = DbPersistence.StorageName,
            MountOptions = DbPersistence.MountOptions(dbType),
        });
        container.VolumeMounts.Add(new ContainerAppVolumeMount
        {
            VolumeName = DbPersistence.StorageName,
            MountPath = DbPersistence.MountPath(dbType),
        });
        if (dbType == "postgresql")
            container.Env.Add(Env("PGDATA", DbPersistence.PostgresPgData));
    }

    // 1) DB 用 Container App（Internal Ingress: TCP + ExposedPort）。PersistDb が true なら Azure Files をマウントする
    public static ContainerAppData BuildDbApp(DeployOptions opts, string location, ResourceIdentifier envId)
    {
        var dbPort = GetDbPort(opts);
        var dbContainer = opts.DbType == "postgresql"
            ? new ContainerAppContainer()
            {
                Name = "timetracker-db",
                Image = ContainerImages.Postgres,
                Env =
                {
                    Env("POSTGRES_USER", GetDbUser(opts)),
                    SecretEnv("POSTGRES_PASSWORD", DbPasswordSecretName),
                    // POSTGRES_DB で DB を事前作成しない（サンプルDB作成が失敗するため。DB は Timetracker のセットアップで作成される）
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
                Image = ContainerImages.SqlServer,
                Env =
                {
                    Env("ACCEPT_EULA", "Y"),
                    Env("MSSQL_PID", "Developer"),
                    SecretEnv("MSSQL_SA_PASSWORD", DbPasswordSecretName), // SA_PASSWORD は非推奨
                },
                Resources = new AppContainerResources()
                {
                    Cpu = opts.DbCpu,
                    Memory = Gi(opts.DbMemoryGi)
                }
            };

        var template = SingleReplicaTemplate(dbContainer);
        if (opts.PersistDb == true)
            AddDbStorage(template, dbContainer, opts.DbType);

        return new ContainerAppData(location)
        {
            EnvironmentId = envId,
            Template = template,
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
            Image = ContainerImages.Redis,
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
            Image = ContainerImages.Timetracker(opts.TimetrackerTag),
            Env =
            {
                Env("ASPNETCORE_URLS", "http://0.0.0.0:8080"),
                Env("TTNX_DB_TYPE", opts.DbType),
                // DB はホスト名のみ。ポートは TTNX_DB_PORT で別指定する
                // （イメージの entrypoint.sh が SQL Server では "Data Source=サーバー,ポート" に連結するため、ここにポートを含めると接続できない）
                Env("TTNX_DB_SERVER", dbEndpointHost),
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

        // timetracker 11.x のイメージは TT_、7.x 以前は TTNX_ 接頭辞の環境変数を読むため、
        // どちらのタグでも動くよう TTNX_ の各変数と同じ値で TT_ の変数も設定する
        foreach (var e in timetrackerContainer.Env.Where(e => e.Name.StartsWith(ContainerImages.LegacyEnvPrefix)).ToList())
        {
            timetrackerContainer.Env.Add(new ContainerAppEnvironmentVariable
            {
                Name = ContainerImages.EnvPrefix + e.Name[ContainerImages.LegacyEnvPrefix.Length..],
                Value = e.Value,
                SecretRef = e.SecretRef,
            });
        }

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
