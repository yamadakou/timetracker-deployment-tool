namespace TTDeployTool.Controller.Desktop.Core.Jobs;

/// <summary>画面の日付と時刻の入力から、予定の時刻を作る。</summary>
public static class ScheduleTime
{
    /// <summary>日付（DatePicker）の日と、時刻（TimePicker）を合わせる。時刻帯は日付の値のもの（画面では PC の時刻帯）。</summary>
    public static DateTimeOffset Combine(DateTimeOffset date, TimeSpan time) =>
        new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, date.Offset).Add(time);
}
