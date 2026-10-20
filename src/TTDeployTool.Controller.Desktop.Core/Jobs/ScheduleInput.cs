using TTDeployTool.Controller.Desktop.Core.Mvvm;

namespace TTDeployTool.Controller.Desktop.Core.Jobs;

/// <summary>「予定に追加」の日付と時刻の入力（既定は今から 1 時間後の、分を切り捨てた時刻）。</summary>
public sealed class ScheduleInput : ObservableObject
{
    private readonly TimeProvider _time;
    private DateTimeOffset _date;
    private TimeSpan _timeOfDay;

    public ScheduleInput(TimeProvider time)
    {
        _time = time;
        var next = time.GetLocalNow().AddHours(1);
        _date = next;
        _timeOfDay = new TimeSpan(next.Hour, next.Minute, 0);
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
    public string? Error => At <= _time.GetUtcNow() ? $"予定の時刻（{At.ToLocalTime():MM/dd HH:mm}）は、今より後にしてください。" : null;
}
