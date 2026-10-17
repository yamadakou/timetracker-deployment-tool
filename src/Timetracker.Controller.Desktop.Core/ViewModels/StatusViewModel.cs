using System.Collections.ObjectModel;
using System.Globalization;
using Timetracker.Application.Deployment;
using Timetracker.Common;
using Timetracker.Controller.Desktop.Core.Mvvm;
using Timetracker.Domain.Deployment;
using Timetracker.Infrastructure.Azure;

namespace Timetracker.Controller.Desktop.Core.ViewModels;

/// <summary>デプロイ済みの Container App 1 件（画面の表の 1 行）。</summary>
public sealed record AppStateRow(string Role, string Name, string State, string? Image, string Resources, string? Revision, bool Managed);

/// <summary>デプロイ済みの環境の状態（MCP の get_deployment_info と同じ内容。Azure は変更しない）。</summary>
public sealed class StatusViewModel : ObservableObject
{
    private readonly TargetViewModel _target;
    private readonly IDeploymentLogger _logger;
    private DeploymentInfo? _info;
    private string? _error;

    public StatusViewModel(TargetViewModel target, IDeploymentLogger logger)
    {
        _target = target;
        _logger = logger;
        RefreshCommand = new AsyncCommand(RefreshAsync);
        _target.Changed += (_, _) => Info = null;
    }

    public AsyncCommand RefreshCommand { get; }

    public ObservableCollection<AppStateRow> Apps { get; } = [];

    public string? Summary => _info switch
    {
        null => null,
        { Exists: false } => "デプロイされた環境はありません。",
        { Environment: { } env } => $"Container Apps Environment {env.Name}（{env.Location}、{env.ProvisioningState}）",
        _ => "Container Apps Environment はありません。",
    };

    public Uri? Endpoint => _info?.Endpoint is { } url ? new Uri(url) : null;

    /// <summary>DB のデータの永続化の状態。</summary>
    public string? PersistenceText => _info switch
    {
        null => null,
        { DbPersisted: true } i => $"DB のデータ: Azure Files に永続化しています（ストレージアカウント {ResourceNaming.StorageAccount(i.Subscription, i.ResourceGroup, i.AppName)}）",
        { StorageAccount: { } account } => $"DB のデータ: 永続化していません（以前の DB のデータがストレージアカウント {account.Name} に残っています）",
        _ => "DB のデータ: 永続化していません（DB コンテナの再起動でデータが失われます）",
    };

    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

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
            OnPropertyChanged(nameof(PersistenceText));
        }
    }

    private async Task RefreshAsync()
    {
        Error = null;
        Info = null;
        if (_target.TargetError is { } targetError)
        {
            Error = targetError;
            return;
        }
        try
        {
            var gateway = _target.CreateSession().CreateGateway(_logger);
            Info = await new DeploymentInspector(gateway).InspectAsync(_target.Subscription, _target.ResourceGroup, _target.AppName.ToLowerInvariant());
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
