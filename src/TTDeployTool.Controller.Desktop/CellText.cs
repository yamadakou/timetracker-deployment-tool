using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TTDeployTool.Controller.Desktop.Core.ViewModels;

namespace TTDeployTool.Controller.Desktop;

/// <summary>
/// 表のセルの文字（6 行までで「…」で切る。docs/design/desktop/desktop-tables.md の 10）で、切れたときだけ、マウスを置くと全体を出す（UI Automation の説明にも入れる）。
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

    // ツールチップと同じ全体の文字を、UI Automation の説明（HelpText）にも入れる（読み上げで全体が分かり、UI テストで確かめられる。
    // ツールチップは HelpText にならない。CI の UI テスト、run 41070225534）
    private static void Update(TextBlock text)
    {
        var full = TableRules.ToolTip(text.IsTextTrimmed, text.Text);
        ToolTipService.SetToolTip(text, full);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(text, full ?? "");
    }
}
