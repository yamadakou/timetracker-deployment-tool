using System.Collections.ObjectModel;
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
    private WizardStep _step = WizardStep.Target;

    public DeployWizardViewModel(SettingsViewModel settings, DeploymentViewModel deployment)
    {
        _settings = settings;
        _deployment = deployment;
        NextCommand = new AsyncCommand(() => GoToAsync(_step + 1), () => CanGoNext);
        BackCommand = new AsyncCommand(() => GoToAsync(_step - 1), () => _step > WizardStep.Target);
        _settings.Errors.CollectionChanged += (_, _) => Refresh();
        _settings.Warnings.CollectionChanged += (_, _) => Refresh();
        Refresh();
    }

    public AsyncCommand NextCommand { get; }

    public AsyncCommand BackCommand { get; }

    public WizardStep Step => _step;

    /// <summary>画面の題名と、何番目か。</summary>
    public string StepText => $"{Titles[(int)_step - 1]}（{(int)_step} / {Titles.Count}）";

    public bool IsTarget => _step == WizardStep.Target;

    public bool IsDb => _step == WizardStep.Db;

    public bool IsPasswords => _step == WizardStep.Passwords;

    public bool IsSpec => _step == WizardStep.Spec;

    public bool IsConfirm => _step == WizardStep.Confirm;

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

    /// <summary>最初の画面に戻す（一覧の「対象に入れる」・実行の後）。</summary>
    public Task StartOverAsync() => GoToAsync(WizardStep.Target);

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
