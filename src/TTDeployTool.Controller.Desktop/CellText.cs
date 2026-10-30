using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TTDeployTool.Controller.Desktop;

/// <summary>
/// 表のセルの文字（6 行までで「…」で切る。docs/design/desktop/desktop-tables.md の 10）で、切れたときだけ、マウスを置くと全体を出す。
/// </summary>
public static class CellText
{
    public static readonly DependencyProperty ShowFullTextProperty =
        DependencyProperty.RegisterAttached("ShowFullText", typeof(bool), typeof(CellText), new PropertyMetadata(false, OnShowFullTextChanged));

    public static bool GetShowFullText(TextBlock element) => (bool)element.GetValue(ShowFullTextProperty);

    public static void SetShowFullText(TextBlock element, bool value) => element.SetValue(ShowFullTextProperty, value);

    private static void OnShowFullTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock text || e.NewValue is not true)
            return;
        // 行は使い回されるため、文字が変わったときも合わせる
        text.IsTextTrimmedChanged += (_, _) => Update(text);
        text.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => Update(text));
    }

    private static void Update(TextBlock text) => ToolTipService.SetToolTip(text, text.IsTextTrimmed ? text.Text : null);
}
