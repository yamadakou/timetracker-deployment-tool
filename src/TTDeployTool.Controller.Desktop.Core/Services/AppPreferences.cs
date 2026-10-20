namespace TTDeployTool.Controller.Desktop.Core.Services;

/// <summary>アプリの設定のうち、保存するもの（秘密の情報は置かない）。画面の側でアプリのデータに保存する。</summary>
public interface IAppPreferences
{
    /// <summary>同時に実行するジョブの数（未設定なら null）。</summary>
    int? MaxConcurrentJobs { get; set; }
}

/// <summary>保存しない設定（テストと、保存先が無い場合）。</summary>
public sealed class InMemoryAppPreferences : IAppPreferences
{
    public int? MaxConcurrentJobs { get; set; }
}
