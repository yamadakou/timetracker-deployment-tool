using System.Collections.ObjectModel;
using System.Globalization;
using TTDeployTool.Controller.Desktop.Core.Mvvm;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Domain.Deployment;

namespace TTDeployTool.Controller.Desktop.Core.ViewModels;

/// <summary>DB のデータの永続化の選択肢（<see cref="Value"/> は null で今の状態を引き継ぐ）。</summary>
public sealed record PersistDbOption(bool? Value, string Label);

/// <summary>利用可能期限日の決め方。</summary>
public enum ExpiryMode
{
    /// <summary>既定（新しくデプロイする場合は今日から 32 日後、再デプロイは今の期限を引き継ぐ）。</summary>
    Default,

    /// <summary>日付を指定する。</summary>
    Date,

    /// <summary>期限なし。</summary>
    None,
}

/// <summary>利用可能期限日の選択肢。</summary>
public sealed record ExpiryOption(ExpiryMode Mode, string Label);

/// <summary>設定から作ったデプロイの設定と、誤り・警告。<see cref="Options"/> は誤りがあれば null。</summary>
public sealed record DeploymentInput(DeployOptions? Options, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

/// <summary>
/// デプロイの設定（DB 種別・タグ・パスワード・スペック）。誤りと警告は、入力のたびに CLI・MCP と同じ規則
/// （<see cref="DeploymentValidator"/> / <see cref="DeploymentAdvisor"/>）で表示する。パスワードは保存しない（詳細設計 6）。
/// 利用可能期限日は、CLI の --expires-on と同じ規則（<see cref="DeploymentExpiry"/>。docs/design/desktop-jobs.md の 6.1）。
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

    /// <summary>利用可能期限日の選択肢（CLI の --expires-on を指定しない / 日付 / none に対応する）。</summary>
    public static readonly IReadOnlyList<ExpiryOption> ExpiryOptions =
    [
        new(ExpiryMode.Default, $"既定（新しくデプロイする場合は今日から {DeploymentExpiry.DefaultDays} 日後、再デプロイは今の期限を引き継ぐ）"),
        new(ExpiryMode.Date, "日付を指定する"),
        new(ExpiryMode.None, "期限なし"),
    ];

    private readonly TargetViewModel _target;
    private readonly TimeProvider _time;
    private readonly IAppPreferences _preferences;
    private readonly IDialogService? _dialogs;
    private readonly IClipboardService? _clipboard;
    private bool _includePasswordsInCopy;
    private string _templateName = "";
    private DeploymentTemplate? _selectedTemplate;
    private string? _message;
    private ExpiryOption _expiry = ExpiryOptions[0];
    private DateTimeOffset _expiryDate;
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

    /// <param name="time">利用可能期限日の既定と、今日より前かを決める時刻と時刻帯（省略時はシステムの時刻）</param>
    /// <param name="preferences">テンプレートの保存先（省略時は保存しない）</param>
    /// <param name="dialogs">上書き・初期値に戻す前の確認（省略時は確認せずに行う）</param>
    /// <param name="clipboard">Markdown のコピー先（省略時はコピーできない）</param>
    public SettingsViewModel(
        TargetViewModel target, TimeProvider? time = null, IAppPreferences? preferences = null, IDialogService? dialogs = null, IClipboardService? clipboard = null)
    {
        _target = target;
        _time = time ?? TimeProvider.System;
        _preferences = preferences ?? new InMemoryAppPreferences();
        _dialogs = dialogs;
        _clipboard = clipboard;
        _expiryDate = DefaultExpiryDate();
        foreach (var template in _preferences.Templates.OrderBy(t => t.Name, StringComparer.CurrentCulture))
            Templates.Add(template);
        CopyMarkdownCommand = new AsyncCommand(CopyMarkdownAsync, () => _clipboard != null);
        SaveTemplateCommand = new AsyncCommand(SaveTemplateAsync, () => TemplateName.Length > 0);
        ApplyTemplateCommand = new AsyncCommand(ApplyTemplateAsync, () => SelectedTemplate != null);
        DeleteTemplateCommand = new AsyncCommand(DeleteTemplateAsync, () => SelectedTemplate != null);
        ResetCommand = new AsyncCommand(ResetAsync);
        _target.Changed += (_, _) => Validate();
        Validate();
    }

    /// <summary>
    /// デプロイの入力が変わったとき（計画を消す）。テンプレートの名前・選択、コピー、メッセージなど、入力でないものの変更では知らせない
    /// （確認の画面でテンプレートを保存すると計画が消え、「今すぐ実行」が無くなっていた。Desktop System Test、2026-10-22）。
    /// </summary>
    public event EventHandler? InputChanged;

    /// <summary>設定を Markdown の表にしてコピーする（パスワードは <see cref="IncludePasswordsInCopy"/> のときだけ値を出す）。</summary>
    public AsyncCommand CopyMarkdownCommand { get; }

    /// <summary>今の入力を、<see cref="TemplateName"/> の名前でテンプレートとして保存する（同じ名前があれば確認して上書きする）。</summary>
    public AsyncCommand SaveTemplateCommand { get; }

    /// <summary>選んだテンプレートを入力に入れる（パスワードは変えない）。</summary>
    public AsyncCommand ApplyTemplateCommand { get; }

    public AsyncCommand DeleteTemplateCommand { get; }

    /// <summary>確認してから、対象（サブスクリプションの選択を除く）と設定を起動したときの値に戻す。</summary>
    public AsyncCommand ResetCommand { get; }

    /// <summary>Markdown のコピーにパスワードの値を含めるか（既定は伏せ字）。</summary>
    public bool IncludePasswordsInCopy { get => _includePasswordsInCopy; set => SetProperty(ref _includePasswordsInCopy, value); }

    /// <summary>保存したテンプレート（名前の順）。</summary>
    public ObservableCollection<DeploymentTemplate> Templates { get; } = [];

    /// <summary>保存するテンプレートの名前（前後の空白は除く）。</summary>
    public string TemplateName
    {
        get => _templateName;
        set
        {
            if (SetProperty(ref _templateName, value.Trim()))
                SaveTemplateCommand.RaiseCanExecuteChanged();
        }
    }

    public DeploymentTemplate? SelectedTemplate
    {
        get => _selectedTemplate;
        set
        {
            if (!SetProperty(ref _selectedTemplate, value))
                return;
            ApplyTemplateCommand.RaiseCanExecuteChanged();
            DeleteTemplateCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>コピー・テンプレート・初期値に戻すの結果。</summary>
    public string? Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
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

    /// <summary>利用可能期限日の決め方。</summary>
    public ExpiryOption Expiry
    {
        get => _expiry;
        set
        {
            if (!SetProperty(ref _expiry, value, new Dependent(nameof(IsExpiryDate))))
                return;
            Validate();
        }
    }

    /// <summary>期限日の入力を表示するか。</summary>
    public bool IsExpiryDate => Expiry.Mode == ExpiryMode.Date;

    /// <summary>指定する期限日（画面の日付の入力。既定は今日から 32 日後）。</summary>
    public DateTimeOffset ExpiryDate
    {
        get => _expiryDate;
        set
        {
            if (SetProperty(ref _expiryDate, value))
                Validate();
        }
    }

    /// <summary>既定で決まる期限と、期限を過ぎたときの動き。</summary>
    public string ExpiryNotice =>
        $"既定では、新しくデプロイする場合は {DeploymentExpiry.Today(_time.GetUtcNow(), _time.LocalTimeZone).AddDays(DeploymentExpiry.DefaultDays):yyyy-MM-dd}" +
        $"（今日から {DeploymentExpiry.DefaultDays} 日後）、再デプロイは今の期限を引き継ぎます。期限は期限日の終わり（23:59:59）です。" +
        "期限を過ぎると、このアプリが自動で削除します（次の版から。DB のデータも消えます）。";

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
            ExpiresOn           = Expiry.Mode switch
            {
                ExpiryMode.Date => ExpiryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ExpiryMode.None => DeploymentExpiry.None,
                _ => null,
            },
        });
        errors.AddRange(DeploymentValidator.GetErrors(options, target));
        if (target == ValidationTarget.Azure && DeploymentExpiry.PastDateError(options.ExpiresOn, _time.GetUtcNow(), _time.LocalTimeZone) is { } pastDate)
            errors.Add(pastDate);
        var warnings = DeploymentAdvisor.GetWarnings(options, target);
        return new DeploymentInput(errors.Count == 0 ? options : null, errors, warnings);
    }

    /// <summary>設定の Markdown（パスワードは <paramref name="includePasswords"/> のときだけ値を出し、ほかは伏せ字）。</summary>
    public string ToMarkdown(bool includePasswords)
    {
        string Password(string value) => value.Length == 0 ? "（未入力）" : includePasswords ? value : "********（入力済み）";
        string? expiry = Expiry.Mode switch
        {
            ExpiryMode.Date => ExpiryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "（期限日の終わり）",
            ExpiryMode.None => "期限なし",
            _ => Expiry.Label,
        };
        var appName = _target.AppName.ToLowerInvariant();
        var rows = new List<IReadOnlyList<string?>>
        {
            new[] { "サブスクリプション", _target.SelectedSubscription?.Display ?? "（未選択）" },
            new[] { "リソースグループ", _target.ResourceGroup.Length == 0 ? "（未入力）" : _target.ResourceGroup },
            new[] { "アプリ名", _target.AppName },
            new[] { "リージョン", _target.Location },
            new[] { "DB 種別", DbType },
            new[] { "DB 名", DbName },
            new[] { "Timetracker のイメージのタグ", TtTag.Length == 0 ? $"{DefaultTag}（既定）" : TtTag },
            new[] { "DB のパスワード", Password(DbPassword) },
            new[] { "Timetracker のパスワード", Password(TrackerPassword) },
            new[] { "Timetracker の CPU / メモリ", $"{TtCpu} vCPU / {TtMemoryGi} Gi" },
            new[] { "DB の CPU / メモリ", $"{DbCpu} vCPU / {DbMemoryGi} Gi" },
            new[] { "Redis の CPU / メモリ", $"{RedisCpu} vCPU / {RedisMemoryGi} Gi" },
            new[] { "DB のデータの永続化", PersistDb.Label },
            new[] { "利用可能期限日", expiry },
            new[] { "作るリソース", string.Join("、", ResourceNaming.Environment(appName), ResourceNaming.TimetrackerApp(appName), ResourceNaming.DbApp(appName), ResourceNaming.RedisApp(appName)) },
        };
        return MarkdownText.Document("TTDeployTool のデプロイの設定",
            (null, MarkdownText.Table(["項目", "値"], rows)),
            ("誤り", MarkdownText.Bullets(Errors)),
            ("警告", MarkdownText.Bullets(Warnings)));
    }

    // 今の入力のテンプレート（パスワードは含めない）
    private DeploymentTemplate Snapshot(string name) => new(
        name, _target.SelectedSubscription?.SubscriptionId, _target.ResourceGroup, _target.AppName, _target.Location,
        DbType, DbName, TtTag, TtCpu, TtMemoryGi, DbCpu, DbMemoryGi, RedisCpu, RedisMemoryGi, PersistDb.Value, Expiry.Mode,
        Expiry.Mode == ExpiryMode.Date ? ExpiryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null);

    private Task CopyMarkdownAsync()
    {
        _clipboard!.CopyText(ToMarkdown(IncludePasswordsInCopy));
        Message = IncludePasswordsInCopy
            ? "設定を Markdown でコピーしました（パスワードを含みます。クリップボードの履歴には残しません）。"
            : "設定を Markdown でコピーしました（パスワードは伏せ字です）。";
        return Task.CompletedTask;
    }

    private async Task SaveTemplateAsync()
    {
        var name = TemplateName;
        var existing = Templates.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (existing != null && _dialogs != null
            && !await _dialogs.ConfirmAsync("テンプレートを上書きしますか？", $"テンプレート「{existing.Name}」を、今の入力で上書きします。", "上書きする"))
            return;
        var template = Snapshot(name);
        var all = Templates.Where(t => t != existing).Append(template).OrderBy(t => t.Name, StringComparer.CurrentCulture).ToList();
        _preferences.Templates = all;
        Templates.Clear();
        foreach (var t in all)
            Templates.Add(t);
        SelectedTemplate = template;
        Message = $"テンプレート「{name}」を保存しました（パスワードは保存しません）。";
    }

    private Task ApplyTemplateAsync()
    {
        var t = SelectedTemplate!;
        var notes = new List<string>();
        if (t.SubscriptionId != null && !string.Equals(t.SubscriptionId, _target.SelectedSubscription?.SubscriptionId, StringComparison.OrdinalIgnoreCase)
            && !_target.Select(t.SubscriptionId, t.ResourceGroup, t.AppName))
            notes.Add($"サブスクリプション {t.SubscriptionId} が一覧に無いため、サブスクリプションは変えていません（サインインし、サブスクリプションを読んでください）。");
        _target.ResourceGroup = t.ResourceGroup;
        _target.AppName = t.AppName;
        _target.Location = t.Location;
        DbType = t.DbType;
        DbName = t.DbName;
        TtTag = t.TtTag;
        TtCpu = t.TtCpu;
        TtMemoryGi = t.TtMemoryGi;
        DbCpu = t.DbCpu;
        DbMemoryGi = t.DbMemoryGi;
        RedisCpu = t.RedisCpu;
        RedisMemoryGi = t.RedisMemoryGi;
        PersistDb = PersistDbOptions.First(o => o.Value == t.PersistDb);
        Expiry = ExpiryOptions.First(o => o.Mode == t.Expiry);
        if (t.ExpiryDate != null && DateOnly.TryParseExact(t.ExpiryDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            ExpiryDate = Midnight(date);
        Message = string.Join(" ", new[] { $"テンプレート「{t.Name}」を入力しました（パスワードはそのままです）。" }.Concat(notes));
        return Task.CompletedTask;
    }

    private async Task DeleteTemplateAsync()
    {
        var t = SelectedTemplate!;
        if (_dialogs != null && !await _dialogs.ConfirmAsync("テンプレートを削除しますか？", $"テンプレート「{t.Name}」を削除します。", "削除する"))
            return;
        Templates.Remove(t);
        _preferences.Templates = Templates.ToList();
        SelectedTemplate = null;
        Message = $"テンプレート「{t.Name}」を削除しました。";
    }

    private async Task ResetAsync()
    {
        if (_dialogs != null && !await _dialogs.ConfirmAsync(
                "初期値に戻しますか？", "リソースグループ・アプリ名・リージョン・パスワードを含め、入力を起動したときの値に戻します（サブスクリプションの選択は変えません）。", "初期値に戻す"))
            return;
        _target.ResourceGroup = "";
        _target.AppName = Defaults.DefaultAppName;
        _target.Location = Defaults.DefaultLocation;
        DbType = DeployOptionsResolver.DefaultDbType;
        DbName = Defaults.DbName;
        TtTag = "";
        DbPassword = "";
        TrackerPassword = "";
        TtCpu = Format(Defaults.TimetrackerCpu);
        TtMemoryGi = Format(Defaults.TimetrackerMemoryGi);
        DbCpu = Format(Defaults.DbCpu);
        DbMemoryGi = Format(Defaults.DbMemoryGi);
        RedisCpu = Format(Defaults.RedisCpu);
        RedisMemoryGi = Format(Defaults.RedisMemoryGi);
        PersistDb = PersistDbOptions[0];
        Expiry = ExpiryOptions[0];
        ExpiryDate = DefaultExpiryDate();
        IncludePasswordsInCopy = false;
        Message = "初期値に戻しました。";
    }

    private void Validate()
    {
        var input = Build(ValidationTarget.Azure);
        Replace(Errors, input.Errors);
        Replace(Warnings, input.Warnings);
        InputChanged?.Invoke(this, EventArgs.Empty);
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

    // 既定の期限日（今日から 32 日後）を、日付の入力の初期値にする
    private DateTimeOffset DefaultExpiryDate() => Midnight(DeploymentExpiry.Today(_time.GetUtcNow(), _time.LocalTimeZone).AddDays(DeploymentExpiry.DefaultDays));

    private DateTimeOffset Midnight(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, _time.LocalTimeZone.GetUtcOffset(local));
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
