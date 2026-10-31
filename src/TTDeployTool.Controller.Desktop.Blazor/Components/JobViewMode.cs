namespace TTDeployTool.Controller.Desktop.Blazor.Components;

/// <summary>ジョブの表示の形。</summary>
public enum JobViewMode
{
    /// <summary>ウィザードの完了の表示（状態・進み具合・結果だけ）。</summary>
    Summary,

    /// <summary>実行状況の画面（進み具合・ログ・結果と、取り消し・一時停止・再開・停止・予定の変更）。</summary>
    Row,
}
