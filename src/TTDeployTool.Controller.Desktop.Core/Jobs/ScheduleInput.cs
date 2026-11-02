using TTDeployTool.Controller.Desktop.Core.Mvvm;

namespace TTDeployTool.Controller.Desktop.Core.Jobs;

/// <summary>
/// 「予定に追加」の日付と時刻の入力。既定は今の次の分（今より後だけを受け付けるため。10:23:40 なら 10:24）で、画面を開くたびに入れ直す
/// （<see cref="ResetToNextMinute"/>。オーナーの依頼、2026-11-02: 初期値を今の時刻に）。
/// </summary>
public sealed class ScheduleInput : ObservableObject
{
    private readonly TimeProvider _time;
    private DateTimeOffset _date;
    private TimeSpan _timeOfDay;

    public ScheduleInput(TimeProvider time)
    {
        _time = time;
        (_date, _timeOfDay) = NextMinute(time);
    }

    /// <summary>日付と時刻を、今の次の分に入れ直す（画面を開いたとき。MainViewModel.PageShownAsync）。</summary>
    public void ResetToNextMinute()
    {
        var (date, timeOfDay) = NextMinute(_time);
        Date = date;
        TimeOfDay = timeOfDay;
    }

    // 今（ローカル）の分を切り捨てて 1 分足した日時（23:59 台なら次の日の 00:00）
    private static (DateTimeOffset Date, TimeSpan TimeOfDay) NextMinute(TimeProvider time)
    {
        var now = time.GetLocalNow();
        var next = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset).AddMinutes(1);
        return (next, next.TimeOfDay);
    }

    public DateTimeOffset Date
    {
        get => _date;
        set => SetProperty(ref _date, value);
    }

    public TimeSpan TimeOfDay
    {
        get => _timeOfDay;
        set => SetProperty(ref _timeOfDay, value);
    }

    public DateTimeOffset At => ScheduleTime.Combine(Date, TimeOfDay);

    /// <summary>予定の時刻の誤り（今より前なら、その旨）。誤りが無ければ null。</summary>
    public string? Error => ErrorOf(At, _time.GetUtcNow());

    /// <summary>予定の時刻 <paramref name="at"/> の誤り（今 <paramref name="now"/> と同じか前なら、その旨）。どの画面の予定の変更もこれで断る。</summary>
    public static string? ErrorOf(DateTimeOffset at, DateTimeOffset now) =>
        at <= now ? $"予定の時刻（{at.ToLocalTime():MM/dd HH:mm}）は、今より後にしてください。" : null;
}
