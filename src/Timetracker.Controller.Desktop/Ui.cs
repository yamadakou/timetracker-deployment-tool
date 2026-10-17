using Microsoft.UI.Xaml;

namespace Timetracker.Controller.Desktop;

/// <summary>画面の表示の切り替え（x:Bind の関数バインディングで使う）。</summary>
internal static class Ui
{
    public static Visibility IfText(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility IfTrue(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IfNotNull(object? value) => value == null ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility IfAny(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static string Time(DateTimeOffset? value) => value is { } t ? $"計画の有効期限: {t.ToLocalTime():HH:mm}" : "";
}
