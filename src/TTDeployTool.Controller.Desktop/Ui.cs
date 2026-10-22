using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using TTDeployTool.Controller.Desktop.Core.ViewModels;

namespace TTDeployTool.Controller.Desktop;

/// <summary>
/// 画面の表示の切り替え（x:Bind の関数バインディングで使う）。null になる値を引数にしない
/// （null に戻ったときに表示が切り替わらなかったため、ViewModel の bool で切り替える。詳細設計 desktop-jobs.md の 3.1）。
/// </summary>
internal static class Ui
{
    public static Visibility IfTrue(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IfAny(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static bool Not(bool value) => !value;

    /// <summary>利用可能期限の近さの色（過ぎた → 赤、近い → 黄、ほかは文字の色）。</summary>
    public static Brush ExpiryBrush(ExpiryLevel level) => (Brush)Microsoft.UI.Xaml.Application.Current.Resources[level switch
    {
        ExpiryLevel.Expired => "SystemFillColorCriticalBrush",
        ExpiryLevel.Soon => "SystemFillColorCautionBrush",
        _ => "TextFillColorPrimaryBrush",
    }];

    public static string Time(DateTimeOffset? value) => value is { } t ? $"計画の有効期限: {t.ToLocalTime():HH:mm}" : "";
}
