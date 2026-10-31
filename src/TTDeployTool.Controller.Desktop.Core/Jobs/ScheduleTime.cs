namespace TTDeployTool.Controller.Desktop.Core.Jobs;

/// <summary>画面の日付と時刻の入力から、予定の時刻を作る。</summary>
public static class ScheduleTime
{
    /// <summary>日付（DatePicker）の日と、時刻（TimePicker）を合わせる。時刻帯は日付の値のもの（画面では PC の時刻帯）。</summary>
    public static DateTimeOffset Combine(DateTimeOffset date, TimeSpan time) =>
        new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, date.Offset).Add(time);

    /// <summary>
    /// 予定の時刻の誤り（今と同じか今より前なら、その旨。誤りが無ければ null）。予定に追加・ジョブの一覧での変更・ジョブ 1 件での変更で同じ決まりを使う
    /// （ジョブ 1 件の変更は、今より前を受け付けてすぐ実行になっていた。オーナーの判断で断る。2026-10-31）。
    /// </summary>
    public static string? PastError(DateTimeOffset at, DateTimeOffset now) =>
        at <= now ? $"予定の時刻（{at.ToLocalTime():MM/dd HH:mm}）は、今より後にしてください。" : null;
}
