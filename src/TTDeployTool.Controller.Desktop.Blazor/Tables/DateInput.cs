using System.Globalization;

namespace TTDeployTool.Controller.Desktop.Blazor.Tables;

/// <summary>日付の入力欄（input type="date"。値は yyyy-MM-dd）と、Core の日付のフィルタ（日付だけを使う）の間の変換。</summary>
public static class DateInput
{
    public static string? Format(DateTimeOffset? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>空・読めない値は null（フィルタを外す）。</summary>
    public static DateTimeOffset? Parse(object? value) =>
        DateOnly.TryParseExact(value as string, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            : null;
}
