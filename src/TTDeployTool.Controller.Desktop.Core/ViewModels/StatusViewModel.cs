using System.Collections.ObjectModel;
using System.Globalization;
using TTDeployTool.Application.Deployment;
using TTDeployTool.Common;
using TTDeployTool.Controller.Desktop.Core.Mvvm;
using TTDeployTool.Domain.Deployment;
using TTDeployTool.Infrastructure.Azure;

namespace TTDeployTool.Controller.Desktop.Core.ViewModels;

/// <summary>デプロイ済みの Container App 1 件（画面の表の 1 行）。</summary>
public sealed record AppStateRow(string Role, string Name, string State, string? Image, string Resources, string? Revision, bool Managed);

/// <summary>デプロイ済みの環境の状態（MCP の get_deployment_info と同じ内容。Azure は変更しない）。</summary>
public sealed class StatusViewModel : ObservableObject
{
    private readonly TargetViewModel _target;
    private readonly IDeploymentLogger _logger;
    private readonly Func<bool> _targetHasRunningJob;
    private DeploymentInfo? _info;
    private string? _error;
    private string? _note;

    /// <param name="targetHasRunningJob">対象のデプロイ・削除が実行中か（実行中なら、まだ反映されていない場合がある旨を添える）</param>
    public StatusViewModel(TargetViewModel target, IDeploymentLogger logger, Func<bool> targetHasRunningJob)
    {
        _target = target;
        _logger = logger;
        _targetHasRunningJob = targetHasRunningJob;
        RefreshCommand = new AsyncCommand(() => Busy.RunAsync("状態を読んでいます", RefreshAsync));
        _target.Changed += (_, _) => Info = null;
    }

    public AsyncCommand RefreshCommand { get; }

    /// <summary>状態の確認の処理中の表示。</summary>
    public BusyState Busy { get; } = new();

    /// <summary>実行中のデプロイ・削除がある場合の注意（無ければ null）。</summary>
    public string? Note
    {
        get => _note;
        private set => SetProperty(ref _note, value);
    }

    public ObservableCollection<AppStateRow> Apps { get; } = [];

    public string? Summary => _info switch
    {
        null => null,
        { Exists: false } => "デプロイされた環境はありません。",
        { Environment: { } env } => $"Container Apps Environment {env.Name}（{env.Location}、{env.ProvisioningState}）",
        _ => "Container Apps Environment はありません。",
    };

    public Uri? Endpoint => _info?.Endpoint is { } url ? new Uri(url) : null;

    public bool HasEndpoint => Endpoint != null;

    /// <summary>DB のデータの永続化の状態（環境が無ければ出さない）。</summary>
    public string? PersistenceText => _info switch
    {
        null or { Exists: false } => null,
        { DbPersisted: true } i => $"DB のデータ: Azure Files に永続化しています（ストレージアカウント {ResourceNaming.StorageAccount(i.Subscription, i.ResourceGroup, i.AppName)}）",
        { StorageAccount: { } account } => $"DB のデータ: 永続化していません（以前の DB のデータがストレージアカウント {account.Name} に残っています）",
        _ => "DB のデータ: 永続化していません（DB コンテナの再起動でデータが失われます）",
    };

    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value, new Dependent(nameof(HasError)));
    }

    /// <summary>エラーを表示するか（画面の表示の切り替えに使う）。</summary>
    public bool HasError => Error != null;

    private DeploymentInfo? Info
    {
        set
        {
            _info = value;
            Apps.Clear();
            if (value != null)
            {
                Add("Timetracker", value.Timetracker, value.AppName);
                Add("DB", value.Db, value.AppName);
                Add("Redis", value.Redis, value.AppName);
            }
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(Endpoint));
            OnPropertyChanged(nameof(HasEndpoint));
            OnPropertyChanged(nameof(PersistenceText));
        }
    }

    private async Task RefreshAsync()
    {
        Error = null;
        Info = null;
        Note = null;
        if (_target.TargetError is { } targetError)
        {
            Error = targetError;
            return;
        }
        try
        {
            var gateway = _target.CreateSession(Busy).CreateGateway(_logger);
            Info = await new DeploymentInspector(gateway).InspectAsync(_target.Subscription, _target.ResourceGroup, _target.AppName.ToLowerInvariant());
            if (_targetHasRunningJob())
                Note = "この対象のデプロイまたは削除を実行中のため、まだ反映されていない場合があります。完了してから、もう一度読み込んでください。";
        }
        catch (Exception ex)
        {
            Error = DesktopMessages.ForAzure(ex);
        }
    }

    private void Add(string role, ContainerAppDetails? app, string appName)
    {
        if (app != null)
            Apps.Add(new AppStateRow(
                role, app.Name, $"{app.ProvisioningState} / {app.RunningStatus}", app.Image,
                string.Create(CultureInfo.InvariantCulture, $"{app.Cpu} vCPU / {app.Memory}"),
                app.LatestRevisionName, ManagedTags.IsManaged(app.Tags, appName)));
    }
}
