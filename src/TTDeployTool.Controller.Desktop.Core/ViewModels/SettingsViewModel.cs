using System.Collections.ObjectModel;
using System.Globalization;
using TTDeployTool.Controller.Desktop.Core.Mvvm;
using TTDeployTool.Domain.Deployment;

namespace TTDeployTool.Controller.Desktop.Core.ViewModels;

/// <summary>DB のデータの永続化の選択肢（<see cref="Value"/> は null で今の状態を引き継ぐ）。</summary>
public sealed record PersistDbOption(bool? Value, string Label);

/// <summary>設定から作ったデプロイの設定と、誤り・警告。<see cref="Options"/> は誤りがあれば null。</summary>
public sealed record DeploymentInput(DeployOptions? Options, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

/// <summary>
/// デプロイの設定（DB 種別・タグ・パスワード・スペック）。誤りと警告は、入力のたびに CLI・MCP と同じ規則
/// （<see cref="DeploymentValidator"/> / <see cref="DeploymentAdvisor"/>）で表示する。パスワードは保存しない（詳細設計 6）。
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    public static readonly IReadOnlyList<string> DbTypes = ["postgresql", "sqlserver"];

    /// <summary>永続化の選択肢（CLI の --persist-db を指定しない / true / false に対応する）。</summary>
    public static readonly IReadOnlyList<PersistDbOption> PersistDbOptions =
    [
        new(null, "今の状態を引き継ぐ（新しく作る場合は永続化しない）"),
        new(true, "Azure Files に永続化する（料金がかかる）"),
        new(false, "永続化しない（DB コンテナの再起動でデータが失われる）"),
    ];

    private readonly TargetViewModel _target;
    private string _dbType = DeployOptionsResolver.DefaultDbType;
    private string _dbName = Defaults.DbName;
    private string _ttTag = "";
    private string _dbPassword = "";
    private string _trackerPassword = "";
    private string _ttCpu = Format(Defaults.TimetrackerCpu);
    private string _ttMemory = Format(Defaults.TimetrackerMemoryGi);
    private string _dbCpu = Format(Defaults.DbCpu);
    private string _dbMemory = Format(Defaults.DbMemoryGi);
    private string _redisCpu = Format(Defaults.RedisCpu);
    private string _redisMemory = Format(Defaults.RedisMemoryGi);
    private PersistDbOption _persistDb = PersistDbOptions[0];

    public SettingsViewModel(TargetViewModel target)
    {
        _target = target;
        _target.Changed += (_, _) => Validate();
        Validate();
    }

    public string DbType { get => _dbType; set => Set(ref _dbType, value); }

    public string DbName { get => _dbName; set => Set(ref _dbName, value.Trim()); }

    /// <summary>timetracker イメージのタグ。空なら DB 種別に応じた既定のタグ（<see cref="DefaultTag"/>）。</summary>
    public string TtTag { get => _ttTag; set => Set(ref _ttTag, value.Trim()); }

    public string DefaultTag => Defaults.GetDefaultTimetrackerTag(DbType);

    public string DbPassword { get => _dbPassword; set => Set(ref _dbPassword, value); }

    public string TrackerPassword { get => _trackerPassword; set => Set(ref _trackerPassword, value); }

    public string TtCpu { get => _ttCpu; set => Set(ref _ttCpu, value); }

    public string TtMemoryGi { get => _ttMemory; set => Set(ref _ttMemory, value); }

    public string DbCpu { get => _dbCpu; set => Set(ref _dbCpu, value); }

    public string DbMemoryGi { get => _dbMemory; set => Set(ref _dbMemory, value); }

    public string RedisCpu { get => _redisCpu; set => Set(ref _redisCpu, value); }

    public string RedisMemoryGi { get => _redisMemory; set => Set(ref _redisMemory, value); }

    /// <summary>DB のデータを Azure Files に置くか（詳細設計 docs/design/db-persistence.md）。</summary>
    public PersistDbOption PersistDb
    {
        get => _persistDb;
        set
        {
            if (SetProperty(ref _persistDb, value))
                Validate();
        }
    }

    /// <summary>Azure へのデプロイの設定の誤り（入力のたびに更新する）。</summary>
    public ObservableCollection<string> Errors { get; } = [];

    /// <summary>Azure へのデプロイの設定の警告（入力のたびに更新する）。</summary>
    public ObservableCollection<string> Warnings { get; } = [];

    /// <summary>入力から設定を作り、誤りと警告を返す。数値として読めない項目は誤りにする（カルチャに依存せず、小数点は「.」）。</summary>
    public DeploymentInput Build(ValidationTarget target)
    {
        var errors = new List<string>();
        var options = DeployOptionsResolver.Resolve(new DeployInput
        {
            Subscription        = target == ValidationTarget.Azure ? _target.Subscription : null,
            ResourceGroup       = target == ValidationTarget.Azure ? _target.ResourceGroup : null,
            Location            = _target.Location,
            AppName             = _target.AppName,
            DbType              = DbType,
            DbName              = DbName,
            TimetrackerTag      = TtTag,
            DbPassword          = DbPassword,
            TrackerPassword     = TrackerPassword,
            TimetrackerCpu      = Parse(TtCpu, "tt-cpu", errors),
            TimetrackerMemoryGi = Parse(TtMemoryGi, "tt-memory", errors),
            DbCpu               = Parse(DbCpu, "db-cpu", errors),
            DbMemoryGi          = Parse(DbMemoryGi, "db-memory", errors),
            RedisCpu            = Parse(RedisCpu, "redis-cpu", errors),
            RedisMemoryGi       = Parse(RedisMemoryGi, "redis-memory", errors),
            PersistDb           = PersistDb.Value,
        });
        errors.AddRange(DeploymentValidator.GetErrors(options, target));
        var warnings = DeploymentAdvisor.GetWarnings(options, target);
        return new DeploymentInput(errors.Count == 0 ? options : null, errors, warnings);
    }

    private void Validate()
    {
        var input = Build(ValidationTarget.Azure);
        Replace(Errors, input.Errors);
        Replace(Warnings, input.Warnings);
    }

    private static void Replace(ObservableCollection<string> items, IReadOnlyList<string> values)
    {
        items.Clear();
        foreach (var value in values)
            items.Add(value);
    }

    // 空なら既定値（DeployOptionsResolver が補う）。読めない場合は誤りにし、既定値で検証を続ける
    private static double? Parse(string text, string name, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return value;
        errors.Add($"{name} には数値を指定してください（小数点は「.」）: '{text}'");
        return null;
    }

    private static string Format(double value) => value.ToString("0.0##", CultureInfo.InvariantCulture);

    private void Set(ref string field, string value, [System.Runtime.CompilerServices.CallerMemberName] string propertyName = "")
    {
        if (!SetProperty(ref field, value, propertyName))
            return;
        if (propertyName == nameof(DbType))
            OnPropertyChanged(nameof(DefaultTag));
        Validate();
    }
}
