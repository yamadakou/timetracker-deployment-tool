using System.Collections.ObjectModel;
using TTDeployTool.Controller.Desktop.Core.Jobs;
using TTDeployTool.Controller.Desktop.Core.Mvvm;

namespace TTDeployTool.Controller.Desktop.Core.ViewModels;

/// <summary>デプロイのウィザードの画面（docs/design/desktop-wizard.md の 3）。</summary>
public enum WizardStep
{
    /// <summary>① 対象（テンプレート・サブスクリプション・リソースグループ・アプリ名・リージョン）。</summary>
    Target = 1,

    /// <summary>② DB（種別・DB 名・イメージのタグ・永続化）。</summary>
    Db,

    /// <summary>③ パスワード。</summary>
    Passwords,

    /// <summary>④ スペックと利用可能期限。</summary>
    Spec,

    /// <summary>⑤ 確認と実行。</summary>
    Confirm,
}

/// <summary>
/// デプロイのウィザード。入力は <see cref="SettingsViewModel"/>（と対象の <see cref="TargetViewModel"/>）、計画と実行は
/// <see cref="DeploymentViewModel"/> のまま使い、画面の切り替えと「次へ」の条件だけを持つ。誤りは、その項目のある画面に出し、
/// その画面と前の画面に誤りがあれば次へ進めない。⑤ を開いたときに計画を作る。
/// 「今すぐ実行」「予定に追加」で登録すると、画面を移らずに完了の表示にし（進み具合と結果を出す）、「続けてデプロイ」か「新規デプロイ」を押すまで
/// そのままにする（docs/design/desktop-wizard.md の 3、Q4・Q13・No.70）。
/// </summary>
public sealed class DeployWizardViewModel : ObservableObject
{
    public static readonly IReadOnlyList<string> Titles = ["① 対象", "② DB", "③ パスワード", "④ スペックと利用可能期限", "⑤ 確認と実行"];

    // メッセージの先頭の項目名（CLI のオプション名）で、誤り・警告の画面を決める（DeploymentValidator・DeploymentAdvisor・SettingsViewModel のメッセージ）
    private static readonly (string Prefix, WizardStep Step)[] Prefixes =
    [
        ("subscription", WizardStep.Target), ("resource-group", WizardStep.Target), ("アプリ名", WizardStep.Target),
        ("db-type", WizardStep.Db), ("tt-tag", WizardStep.Db), ("不適切な組み合わせ", WizardStep.Db), ("Azure 上の SQL Server", WizardStep.Db),
        ("persist-db", WizardStep.Db),
        ("tracker-password", WizardStep.Passwords), ("db-password", WizardStep.Passwords),
        ("tt-cpu", WizardStep.Spec), ("tt-memory", WizardStep.Spec), ("db-cpu", WizardStep.Spec), ("db-memory", WizardStep.Spec),
        ("redis-cpu", WizardStep.Spec), ("redis-memory", WizardStep.Spec), ("expires-on", WizardStep.Spec), ("利用可能期限日", WizardStep.Spec),
    ];

    private readonly SettingsViewModel _settings;
    private readonly DeploymentViewModel _deployment;
    private readonly TargetViewModel _target;
    private readonly JobQueue _queue;
    private WizardStep _step = WizardStep.Target;
    private JobViewModel? _registered;

    /// <param name="showJob">完了の表示の「実行状況（ログ）を見る」「ジョブの一覧を見る」</param>
    public DeployWizardViewModel(
        SettingsViewModel settings, DeploymentViewModel deployment, TargetViewModel target, JobQueue queue, Func<JobViewModel, Task> showJob)
    {
        _settings = settings;
        _deployment = deployment;
        _target = target;
        _queue = queue;
        NextCommand = new AsyncCommand(() => GoToAsync(_step + 1), () => CanGoNext);
        BackCommand = new AsyncCommand(() => GoToAsync(_step - 1), () => _step > WizardStep.Target);
        // 登録した入力は使い終わっているため、確かめずに初期値に戻す（残したい設定は、⑤ でテンプレートとして保存できる）
        NewDeploymentCommand = new AsyncCommand(async () =>
        {
            await _settings.ResetToDefaultsAsync(confirm: false);
            await StartOverAsync();
        });
        ContinueCommand = new AsyncCommand(StartOverAsync);
        ShowJobCommand = new AsyncCommand(() => showJob(_registered!), () => _registered != null);
        _settings.Errors.CollectionChanged += (_, _) => Refresh();
        _settings.Warnings.CollectionChanged += (_, _) => Refresh();
        // ① の「同じ対象のジョブがあります」を、対象とジョブの変化で出し直す
        _target.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TargetViewModel.Subscription) or nameof(TargetViewModel.ResourceGroup) or nameof(TargetViewModel.AppName))
                RaiseActiveJobNotice();
        };
        _queue.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(JobQueue.Summary))
                RaiseActiveJobNotice();
        };
        Refresh();
    }

    public AsyncCommand NextCommand { get; }

    public AsyncCommand BackCommand { get; }

    /// <summary>完了の表示から、入力を初期値に戻して ① から始める（確かめない。No.70）。</summary>
    public AsyncCommand NewDeploymentCommand { get; }

    /// <summary>完了の表示から、今の入力のまま ① から始める（設定を変えて続けてデプロイする。No.70）。</summary>
    public AsyncCommand ContinueCommand { get; }

    /// <summary>登録したジョブの実行状況（今すぐ実行したもの）か、ジョブの一覧（予定に追加したもの）を開く。</summary>
    public AsyncCommand ShowJobCommand { get; }

    /// <summary>このウィザードで登録したジョブ（完了の表示。「続けてデプロイ」「新規デプロイ」を押すまで）。</summary>
    public JobViewModel? RegisteredJob => _registered;

    /// <summary>完了の表示か（登録した後）。</summary>
    public bool IsCompleted => _registered != null;

    /// <summary>完了の表示の見出し。</summary>
    public string? CompletedText => _registered switch
    {
        null => null,
        { ScheduledAt: { } at } => $"{_registered.Title}を予定に追加しました（{at.ToLocalTime():MM/dd HH:mm} に実行します）。",
        _ => $"{_registered.Title}を実行しました。",
    };

    /// <summary>「実行状況（ログ）を見る」か「ジョブの一覧を見る」。</summary>
    public string ShowJobText => _registered?.ScheduledAt == null ? "実行状況（ログ）を見る" : "ジョブの一覧を見る";

    /// <summary>① で、同じ対象の終わっていないジョブがあること（あると ⑤ で計画できない。No.70）。無ければ null。</summary>
    public string? ActiveJobNotice => _queue.ActiveFor(_target.Subscription, _target.ResourceGroup, _target.AppName.ToLowerInvariant()) is { } job
        ? $"この対象（{_target.ResourceGroup} / {_target.AppName}）には「{job.Title}: {job.StatusText}」があります。終わるまでは計画できないため、アプリ名かリソースグループを変えてください。"
        : null;

    public bool HasActiveJobNotice => ActiveJobNotice != null;

    public WizardStep Step => _step;

    /// <summary>画面の題名と、何番目か。</summary>
    public string StepText => IsCompleted ? "完了" : $"{Titles[(int)_step - 1]}（{(int)_step} / {Titles.Count}）";

    public bool IsTarget => !IsCompleted && _step == WizardStep.Target;

    public bool IsDb => !IsCompleted && _step == WizardStep.Db;

    public bool IsPasswords => !IsCompleted && _step == WizardStep.Passwords;

    public bool IsSpec => !IsCompleted && _step == WizardStep.Spec;

    public bool IsConfirm => !IsCompleted && _step == WizardStep.Confirm;

    /// <summary>画面の切り替え（戻る・次へ・誤りと警告）を出すか（完了の表示では出さない）。</summary>
    public bool IsEditing => !IsCompleted;

    /// <summary>今の画面の誤り（⑤ ではすべて）。</summary>
    public ObservableCollection<string> StepErrors { get; } = [];

    /// <summary>今の画面の警告（⑤ ではすべて）。</summary>
    public ObservableCollection<string> StepWarnings { get; } = [];

    /// <summary>次へ進めるか（今の画面と前の画面に誤りが無い）。</summary>
    public bool CanGoNext => _step < WizardStep.Confirm && !_settings.Errors.Any(e => StepOf(e) <= _step);

    /// <summary>次へ進めない理由（進めれば null）。</summary>
    public string? BlockedText => _step == WizardStep.Confirm || CanGoNext
        ? null
        : StepErrors.Count > 0
            ? $"誤りが {StepErrors.Count} 件あるため、次へ進めません。"
            : "前の画面に誤りがあるため、次へ進めません。「戻る」で直してください。";

    /// <summary>誤り・警告のメッセージを出す画面（項目名が分からないものは ⑤）。</summary>
    public static WizardStep StepOf(string message) =>
        Prefixes.FirstOrDefault(p => message.StartsWith(p.Prefix, StringComparison.Ordinal)) is (not null, var step) ? step : WizardStep.Confirm;

    /// <summary>最初の画面に戻す（一覧の「対象に入れる」・完了の表示の「続けて」「新規」）。完了の表示もやめる。</summary>
    public Task StartOverAsync()
    {
        SetRegistered(null);
        return GoToAsync(WizardStep.Target);
    }

    /// <summary>このウィザードの「今すぐ実行」「予定に追加」でジョブを登録したとき。画面を移らずに完了の表示にする。</summary>
    internal void Registered(JobViewModel job) => SetRegistered(job);

    private void RaiseActiveJobNotice()
    {
        OnPropertyChanged(nameof(ActiveJobNotice));
        OnPropertyChanged(nameof(HasActiveJobNotice));
    }

    private void SetRegistered(JobViewModel? job)
    {
        if (!SetProperty(ref _registered, job, nameof(RegisteredJob)))
            return;
        foreach (var name in new[] { nameof(IsCompleted), nameof(IsEditing), nameof(CompletedText), nameof(ShowJobText), nameof(StepText),
                     nameof(IsTarget), nameof(IsDb), nameof(IsPasswords), nameof(IsSpec), nameof(IsConfirm) })
            OnPropertyChanged(name);
        ShowJobCommand.RaiseCanExecuteChanged();
    }

    private async Task GoToAsync(WizardStep step)
    {
        _step = step;
        OnPropertyChanged(nameof(Step));
        OnPropertyChanged(nameof(StepText));
        OnPropertyChanged(nameof(IsTarget));
        OnPropertyChanged(nameof(IsDb));
        OnPropertyChanged(nameof(IsPasswords));
        OnPropertyChanged(nameof(IsSpec));
        OnPropertyChanged(nameof(IsConfirm));
        BackCommand.RaiseCanExecuteChanged();
        Refresh();
        // 確認の画面を開いたら、今の入力で計画を作る（入力を変えると計画は消える）
        if (step == WizardStep.Confirm)
            await _deployment.PlanCommand.ExecuteAsync();
    }

    private void Refresh()
    {
        Replace(StepErrors, _settings.Errors.Where(e => _step == WizardStep.Confirm || StepOf(e) == _step));
        Replace(StepWarnings, _settings.Warnings.Where(w => _step == WizardStep.Confirm || StepOf(w) == _step));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(BlockedText));
        NextCommand.RaiseCanExecuteChanged();
    }

    private static void Replace(ObservableCollection<string> items, IEnumerable<string> values)
    {
        var list = values.ToList();
        if (items.SequenceEqual(list))
            return;
        items.Clear();
        foreach (var value in list)
            items.Add(value);
    }
}
