namespace TTDeployTool.Controller.Desktop.Core.ViewModels;

/// <summary>
/// 画面の名前（<see cref="MainViewModel.NavigationRequested"/> で開く画面。MainWindow.xaml の各画面の Tag とメニューの項目の Tag と同じ。
/// XAML との対応は単体テスト（XamlContractTests）で確かめる）。
/// </summary>
public static class Pages
{
    public const string Target = "target";
    public const string Deployments = "deployments";
    public const string Wizard = "wizard";
    public const string Status = "status";
    public const string Delete = "delete";
    public const string BatchDelete = "batchdelete";
    public const string DryRun = "dryrun";
    public const string Jobs = "jobs";
    public const string Settings = "settings";

    /// <summary>すべての画面。</summary>
    public static readonly IReadOnlyList<string> All = [Target, Deployments, Wizard, Status, Delete, BatchDelete, DryRun, Jobs, Settings];
}
