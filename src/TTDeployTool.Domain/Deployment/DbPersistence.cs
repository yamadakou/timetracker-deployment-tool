namespace TTDeployTool.Domain.Deployment;

/// <summary>
/// DB のデータの永続化（Azure Files の SMB 共有を DB コンテナにマウントする）の規則。
/// 詳細設計: docs/design/db-persistence.md
/// </summary>
public static class DbPersistence
{
    /// <summary>Environment に登録するストレージと、DB アプリのボリュームの名前。</summary>
    public const string StorageName = "dbdata";

    /// <summary>ファイル共有のクォータ（GiB）。従量課金のため上限で、料金には関係しない。</summary>
    public const int ShareQuotaGiB = 100;

    public const string PostgresMountPath = "/var/lib/postgresql/data";

    /// <summary>
    /// PostgreSQL のデータのディレクトリ。マウント先の直下ではなく、その下のディレクトリにする
    /// （PostgreSQL の文書の推奨。マウント先の所有者と権限は変えられないため）。
    /// </summary>
    public const string PostgresPgData = PostgresMountPath + "/pgdata";

    public const string SqlServerMountPath = "/var/opt/mssql";

    // SMB ではマウント後に所有者と権限を変えられないため、DB のプロセスのユーザー（postgres:16 は 999、mssql/server:2022 は 10001）と
    // DB が求める権限（PostgreSQL はデータのディレクトリが 0700）でマウントする
    public const string PostgresMountOptions = "uid=999,gid=999,dir_mode=0700,file_mode=0600";
    public const string SqlServerMountOptions = "uid=10001,gid=0,dir_mode=0770,file_mode=0660";

    public static string MountPath(string dbType) => dbType == "postgresql" ? PostgresMountPath : SqlServerMountPath;

    public static string MountOptions(string dbType) => dbType == "postgresql" ? PostgresMountOptions : SqlServerMountOptions;

    /// <summary>
    /// 既存の DB アプリを更新するときに DB のデータが失われるかと、その警告（失われない場合は null）。
    /// 永続化を続ける場合だけ失われない（停止 → 更新 → 開始で、同じ共有を使い続ける）。
    /// </summary>
    /// <param name="dbAppName">DB アプリの名前</param>
    /// <param name="currentlyPersisted">既存の DB アプリが Azure Files を使っているか</param>
    /// <param name="persist">更新後に Azure Files を使うか</param>
    public static string? DataLossWarning(string dbAppName, bool currentlyPersisted, bool persist) => (currentlyPersisted, persist) switch
    {
        (true, true) => null,
        (false, true) =>
            $"DB アプリ '{dbAppName}' は、今は DB のデータを永続化していません。永続化に切り替えると DB コンテナが再起動し、" +
            "今の DB に保存されているデータは消えます。切り替えた後の DB は、以前に永続化していたときのデータ（初めて永続化する場合は空の DB）になります。" +
            "今のデータを残したい場合は、事前に Timetracker でエクスポートしてください。",
        (true, false) =>
            $"DB アプリ '{dbAppName}' は、今は DB のデータを Azure Files に永続化しています。永続化をやめると DB コンテナが再起動し、" +
            "Timetracker からは空の DB に見えます。これまでのデータは消えずにストレージアカウントに残り、" +
            "もう一度 --persist-db（MCP では persist_db=true）を指定してデプロイすると元に戻ります。",
        _ =>
            $"DB アプリ '{dbAppName}' は既に存在します。設定が変わると DB コンテナが再起動し、" +
            "永続ボリュームを設定していないため DB のデータは失われます。必要に応じて事前にバックアップしてください。",
    };

    /// <summary>永続化するときの案内（作るもの・料金・削除での扱い）。</summary>
    public static string StorageNotice(string storageAccountName) =>
        $"DB のデータを Azure Files（ストレージアカウント '{storageAccountName}'）に置きます。容量とトランザクションの料金がかかります" +
        "（https://azure.microsoft.com/pricing/details/storage/files/）。delete では、--keep-db-data（MCP は keep_db_data）を指定しない限り、" +
        "ストレージアカウントも DB のデータごと削除します。";

    /// <summary>永続化した既存の DB アプリを更新するときの案内。</summary>
    public static string RestartNotice(string dbAppName) =>
        $"DB アプリ '{dbAppName}' は DB のデータを Azure Files に永続化しているため、データを守るために一度停止してから更新し、開始します。" +
        "DB のデータはそのまま残りますが、その間（数分程度）は Timetracker を使えません。";
}
