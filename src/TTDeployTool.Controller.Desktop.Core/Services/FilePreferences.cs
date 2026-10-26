using System.Text.Json;

namespace TTDeployTool.Controller.Desktop.Core.Services;

/// <summary>
/// アプリの設定を JSON のファイル（アプリのデータの preferences.json）に保存する。
/// 読めない・書けない場合は既定の値で動く（設定が保存されないだけで、ジョブは実行できるため）。
/// </summary>
public sealed class FilePreferences : IAppPreferences
{
    private readonly string _path;
    private Data _data;

    public FilePreferences(string path)
    {
        _path = path;
        _data = Load(path);
    }

    public int? MaxConcurrentJobs
    {
        get => _data.MaxConcurrentJobs;
        set
        {
            _data = _data with { MaxConcurrentJobs = value };
            Save();
        }
    }

    public IReadOnlyList<DeploymentTemplate> Templates
    {
        get => _data.Templates ?? [];
        set
        {
            _data = _data with { Templates = value };
            Save();
        }
    }

    public IReadOnlyList<string> ExpiryNotices
    {
        get => _data.ExpiryNotices ?? [];
        set
        {
            _data = _data with { ExpiryNotices = value };
            Save();
        }
    }

    public bool StartupTaskAsked
    {
        get => _data.StartupTaskAsked;
        set
        {
            _data = _data with { StartupTaskAsked = value };
            Save();
        }
    }

    public IReadOnlyList<SavedJob> SavedJobs
    {
        get => _data.SavedJobs ?? [];
        set
        {
            _data = _data with { SavedJobs = value };
            Save();
        }
    }

    public StartupLaunch? LastStartupLaunch
    {
        get => _data.LastStartupLaunch;
        set
        {
            _data = _data with { LastStartupLaunch = value };
            Save();
        }
    }

    private static Data Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<Data>(File.ReadAllText(path)) ?? new Data();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Data();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_data));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 保存できなくても、このアプリを終了するまでは変えた値で動く
        }
    }

    private sealed record Data(
        int? MaxConcurrentJobs = null, IReadOnlyList<DeploymentTemplate>? Templates = null, IReadOnlyList<string>? ExpiryNotices = null, bool StartupTaskAsked = false,
        IReadOnlyList<SavedJob>? SavedJobs = null, StartupLaunch? LastStartupLaunch = null);
}
