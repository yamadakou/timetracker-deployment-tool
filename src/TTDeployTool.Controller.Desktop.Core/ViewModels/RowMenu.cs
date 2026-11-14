namespace TTDeployTool.Controller.Desktop.Core.ViewModels;

/// <summary>
/// 一覧の行の右クリックのメニューの、コピーの後に並べる操作（オーナーの依頼、2026-11-14: 詳細・Azure から削除・変更の操作）。
/// <see cref="StartsGroup"/> は、前に区切りの線を引くか。<see cref="IsDanger"/> は、元に戻せない操作（赤で出す）。
/// 対象の行は、選んだ行（選んでいなければ、右クリックした行。<see cref="TableRules.Target"/>）。
/// </summary>
public sealed record RowMenuItem(string Id, string Label, Func<Task> Run, bool IsEnabled = true, bool IsDanger = false, bool StartsGroup = false);
