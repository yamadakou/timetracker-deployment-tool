namespace Timetracker.Domain.Deployment;

/// <summary>
/// DB / Redis のコンテナイメージ。Dry-run の compose と Azure Container Apps で同じものを使う。
/// </summary>
public static class ContainerImages
{
    public const string Postgres = "postgres:16";
    public const string SqlServer = "mcr.microsoft.com/mssql/server:2022-latest";
    public const string Redis = "redis:7"; // latest はメジャーバージョンが意図せず上がるため固定

    public static string Timetracker(string tag) => $"densocreate/timetracker:{tag}";

    public static string Db(string dbType) => dbType == "postgresql" ? Postgres : SqlServer;
}
