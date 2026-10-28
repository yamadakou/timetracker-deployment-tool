using System.ClientModel.Primitives;
using System.Text.Json;
using Azure.Core;
using Azure.ResourceManager.AppContainers.Models;
using Azure.ResourceManager.Storage.Models;
using FluentAssertions;
using TTDeployTool.Domain.Deployment;
using Xunit;

namespace TTDeployTool.Infrastructure.Azure.Tests;

/// <summary>DB のデータの永続化（Azure Files）の定義（詳細設計 docs/design/common/db-persistence.md の 4）。</summary>
public class DbStorageSpecTests
{
    private static readonly ResourceIdentifier EnvId =
        new("/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.App/managedEnvironments/app-env");

    private static DeployOptions Options(string dbType, bool? persistDb) => new()
    {
        Subscription = "sub",
        ResourceGroup = "rg",
        DbType = dbType,
        DbPassword = "DbSecretP@ss1",
        TrackerPassword = "AppSecretP@ss1",
        PersistDb = persistDb,
    };

    [Theory]
    [InlineData("postgresql", "/var/lib/postgresql/data", "uid=999,gid=999,dir_mode=0700,file_mode=0600")]
    [InlineData("sqlserver", "/var/opt/mssql", "uid=10001,gid=0,dir_mode=0770,file_mode=0660")]
    public void DbApp_Should_Mount_The_Azure_Files_Storage_When_Persisting(string dbType, string mountPath, string mountOptions)
    {
        var data = ContainerAppSpecBuilder.BuildDbApp(Options(dbType, true), "japaneast", EnvId);

        var volume = data.Template.Volumes.Should().ContainSingle().Subject;
        (volume.Name, volume.StorageType, volume.StorageName, volume.MountOptions)
            .Should().Be(("dbdata", ContainerAppStorageType.AzureFile, "dbdata", mountOptions));
        var mount = data.Template.Containers.Single().VolumeMounts.Should().ContainSingle().Subject;
        (mount.VolumeName, mount.MountPath, mount.SubPath).Should().Be(("dbdata", mountPath, null));
    }

    [Fact]
    public void Postgres_DbApp_Should_Put_PGDATA_Under_The_Mount_Point()
    {
        // マウント先の所有者と権限は変えられないため、その下のディレクトリをデータのディレクトリにする
        var env = ContainerAppSpecBuilder.BuildDbApp(Options("postgresql", true), "japaneast", EnvId).Template.Containers.Single().Env;

        env.Should().ContainSingle(e => e.Name == "PGDATA").Which.Value.Should().Be("/var/lib/postgresql/data/pgdata");
    }

    [Fact]
    public void SqlServer_DbApp_Should_Not_Set_PGDATA()
    {
        var env = ContainerAppSpecBuilder.BuildDbApp(Options("sqlserver", true), "japaneast", EnvId).Template.Containers.Single().Env;

        env.Should().NotContain(e => e.Name == "PGDATA");
    }

    [Theory]
    [InlineData("postgresql", null)]
    [InlineData("postgresql", false)]
    [InlineData("sqlserver", false)]
    public void DbApp_Should_Not_Mount_Anything_Without_Persistence(string dbType, bool? persistDb)
    {
        var data = ContainerAppSpecBuilder.BuildDbApp(Options(dbType, persistDb), "japaneast", EnvId);

        data.Template.Volumes.Should().BeEmpty();
        data.Template.Containers.Single().VolumeMounts.Should().BeEmpty();
        data.Template.Containers.Single().Env.Should().NotContain(e => e.Name == "PGDATA");
    }

    [Fact]
    public void Persisted_DbApp_Should_Serialize_The_Volume_In_The_Template()
    {
        var data = ContainerAppSpecBuilder.BuildDbApp(Options("postgresql", true), "japaneast", EnvId);

        using var doc = JsonDocument.Parse(ModelReaderWriter.Write(data).ToString());
        var template = doc.RootElement.GetProperty("properties").GetProperty("template");
        template.GetProperty("volumes")[0].GetProperty("storageType").GetString().Should().Be("AzureFile");
        template.GetProperty("containers")[0].GetProperty("volumeMounts")[0].GetProperty("mountPath").GetString().Should().Be("/var/lib/postgresql/data");
    }

    [Fact]
    public void StorageAccount_Should_Be_Standard_Lrs_With_Shared_Key_And_Managed_Tags()
    {
        var content = ContainerAppSpecBuilder.BuildStorageAccount("japaneast", "app");

        content.Sku.Name.Should().Be(StorageSkuName.StandardLrs);
        content.Kind.Should().Be(StorageKind.StorageV2);
        content.Location.Should().Be(AzureLocation.JapanEast);
        content.AccessTier.Should().Be(StorageAccountAccessTier.Hot);
        content.MinimumTlsVersion.Should().Be(StorageMinimumTlsVersion.Tls1_2);
        content.EnableHttpsTrafficOnly.Should().BeTrue();
        content.AllowBlobPublicAccess.Should().BeFalse();
        // SMB のマウントはアカウントキーで行う
        content.AllowSharedKeyAccess.Should().BeTrue();
        content.Tags.Should().BeEquivalentTo(ManagedTags.For("app"));
    }

    [Fact]
    public void FileShare_Should_Be_Smb_With_The_Quota()
    {
        var share = ContainerAppSpecBuilder.BuildDbFileShare();

        share.EnabledProtocol.Should().Be(FileShareEnabledProtocol.Smb);
        share.ShareQuota.Should().Be(DbPersistence.ShareQuotaGiB);
    }

    [Fact]
    public void EnvironmentStorage_Should_Be_A_ReadWrite_Azure_File_With_The_Key()
    {
        var data = ContainerAppSpecBuilder.BuildEnvironmentStorage("ttapp12345678", "secret-key", "app-db");

        using var doc = JsonDocument.Parse(ModelReaderWriter.Write(data).ToString());
        var file = doc.RootElement.GetProperty("properties").GetProperty("azureFile");
        file.GetProperty("accountName").GetString().Should().Be("ttapp12345678");
        file.GetProperty("accountKey").GetString().Should().Be("secret-key");
        file.GetProperty("shareName").GetString().Should().Be("app-db");
        file.GetProperty("accessMode").GetString().Should().Be("ReadWrite");
    }
}
