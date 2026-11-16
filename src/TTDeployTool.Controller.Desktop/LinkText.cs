using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace TTDeployTool.Controller.Desktop;

/// <summary>
/// 文の中の URL をリンクにして出す（権限が足りないときの案内の README など。No.92。Blazor 版の UX の見直し E と同じ）。
/// TextBlock の Text の代わりに使う（<c>local:LinkText.Text="{x:Bind}"</c>）。読み上げの名前は、もとの文のまま。
/// </summary>
public static partial class LinkText
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(LinkText), new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);

    [GeneratedRegex(@"https?://[^\s「」（）。、]+")]
    private static partial Regex Url();

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
            return;
        var text = e.NewValue as string ?? "";
        block.Inlines.Clear();
        var at = 0;
        foreach (Match match in Url().Matches(text))
        {
            if (match.Index > at)
                block.Inlines.Add(new Run { Text = text[at..match.Index] });
            var link = new Hyperlink { NavigateUri = new Uri(match.Value) };
            link.Inlines.Add(new Run { Text = match.Value });
            block.Inlines.Add(link);
            at = match.Index + match.Length;
        }
        if (at < text.Length)
            block.Inlines.Add(new Run { Text = text[at..] });
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(block, text);
    }
}
