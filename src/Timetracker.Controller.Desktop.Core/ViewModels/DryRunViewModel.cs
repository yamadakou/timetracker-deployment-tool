using System.Collections.ObjectModel;
using Timetracker.Application.Compose;
using Timetracker.Controller.Desktop.Core.Mvvm;
using Timetracker.Controller.Desktop.Core.Services;
using Timetracker.Domain.Deployment;

namespace Timetracker.Controller.Desktop.Core.ViewModels;

/// <summary>
/// ローカル検証（Dry-run）の docker-compose.yml と .env の保存（CLI の --dry-run・MCP の generate_compose と同じ内容）。
/// Azure には接続しないため、サインインしなくても使える。
/// </summary>
public sealed class DryRunViewModel : ObservableObject
{
    private readonly SettingsViewModel _settings;
    private readonly IDialogService _dialogs;
    private readonly ISystemLauncher _launcher;
    private string? _savedDirectory;
    private string? _composeContent;
    private string? _error;

    public DryRunViewModel(SettingsViewModel settings, IDialogService dialogs, ISystemLauncher launcher)
    {
        _settings = settings;
        _dialogs = dialogs;
        _launcher = launcher;
        SaveCommand = new AsyncCommand(SaveAsync);
        OpenFolderCommand = new AsyncCommand(OpenFolderAsync, () => SavedDirectory != null);
    }

    public AsyncCommand SaveCommand { get; }

    public AsyncCommand OpenFolderCommand { get; }

    /// <summary>保存の前に表示する注意（.env にパスワードが平文で入る）。</summary>
    public string EnvWarning => DesktopMessages.EnvFileWarning;

    public ObservableCollection<string> Warnings { get; } = [];

    public ObservableCollection<string> NextSteps { get; } = [];

    public string? SavedDirectory
    {
        get => _savedDirectory;
        private set
        {
            if (SetProperty(ref _savedDirectory, value))
                OpenFolderCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>保存した docker-compose.yml の内容（秘密情報を含まない）。</summary>
    public string? ComposeContent
    {
        get => _composeContent;
        private set => SetProperty(ref _composeContent, value);
    }

    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    private async Task SaveAsync()
    {
        Error = null;
        var input = _settings.Build(ValidationTarget.Compose);
        if (input.Options == null)
        {
            Error = "設定に誤りがあるため、ファイルを保存しませんでした:\n- " + string.Join("\n- ", input.Errors);
            return;
        }
        if (await _dialogs.PickFolderAsync() is not { } directory)
            return;
        // .env の以前のパスワードを失わないよう、上書きは確認してから行う
        var overwrite = ComposeExporter.AnyExists(directory);
        if (overwrite && !await _dialogs.ConfirmAsync(
                "ファイルの上書き",
                $"{directory} に {ComposeExporter.ComposeFileName} または {ComposeExporter.EnvFileName} が既にあります。上書きしますか？（.env の以前のパスワードは失われます）",
                "上書きする"))
            return;
        try
        {
            var (_, compose) = ComposeExporter.Write(directory, input.Options, overwrite);
            ComposeContent = compose;
        }
        catch (ComposeExportException ex)
        {
            Error = $"{directory} に書き込めませんでした: {ex.Message}";
            return;
        }
        SavedDirectory = directory;
        Warnings.Clear();
        foreach (var warning in input.Warnings)
            Warnings.Add(warning);
        NextSteps.Clear();
        NextSteps.Add($"{directory} で docker compose up -d を実行する");
        NextSteps.Add("ブラウザーで http://localhost:8080 を開く（初回は起動に数分かかる）");
        NextSteps.Add("停止するときは docker compose down（データも消す場合は docker compose down -v）");
    }

    private Task OpenFolderAsync()
    {
        _launcher.OpenFolder(SavedDirectory!);
        return Task.CompletedTask;
    }
}
