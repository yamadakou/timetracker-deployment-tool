using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using Xunit;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>
/// 画面（MainWindow.xaml）と ViewModel・UI テストの取り決めを、XAML を読んで確かめる（Windows が無くても確かめられるもの）。
/// AutomationId の重複・UI テストが使う AutomationId・画面の名前・x:Bind の名前・イベントの処理の名前。
/// Desktop System Test で、絞り込みの「閉じる」に AutomationId が無いことが分かった（2026-10-26）ため加えた。
/// </summary>
public partial class XamlContractTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private const string AutomationId = "AutomationProperties.AutomationId";

    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string DesktopDirectory = Path.Combine(RepoRoot, "src", "TTDeployTool.Controller.Desktop");
    private static readonly XDocument Xaml = XDocument.Load(Path.Combine(DesktopDirectory, "MainWindow.xaml"));

    // XAML の名前空間の接頭辞と、型を探す名前空間（Core の型だけを確かめる）
    private static readonly IReadOnlyDictionary<string, string> Prefixes = new Dictionary<string, string>
    {
        ["vm"] = "TTDeployTool.Controller.Desktop.Core.ViewModels",
        ["jobs"] = "TTDeployTool.Controller.Desktop.Core.Jobs",
    };

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "TTDeployTool.sln")))
            directory = directory.Parent ?? throw new InvalidOperationException("リポジトリのルート（TTDeployTool.sln）が見つかりません。");
        return directory.FullName;
    }

    private static IEnumerable<XElement> All => Xaml.Descendants();

    // 要素が入っている DataTemplate（無ければ null。ウィンドウの直下）
    private static XElement? TemplateOf(XElement element) => element.Ancestors().FirstOrDefault(a => a.Name.LocalName == "DataTemplate");

    [Fact]
    public void Automation_Ids_Should_Be_Unique_Within_The_Window_And_Each_Template()
    {
        var duplicates = All.Where(e => e.Attribute(AutomationId) != null)
            .GroupBy(e => (Template: TemplateOf(e), Id: e.Attribute(AutomationId)!.Value))
            .Where(g => g.Count() > 1)
            .Select(g => g.Key.Id)
            .ToList();

        duplicates.Should().BeEmpty();
    }

    [Fact]
    public void Automation_Ids_Used_By_The_UI_Tests_Should_Exist()
    {
        var ids = All.Select(e => e.Attribute(AutomationId)?.Value).OfType<string>().ToHashSet();
        var used = new SortedSet<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(RepoRoot, "tests", "TTDeployTool.Controller.Desktop.UITests"), "*.cs"))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in UsedId().Matches(text))
                used.Add(m.Groups[1].Value);
            // Show("X") はメニューの Nav.X を選び、画面の Panel.X を待つ
            foreach (Match m in ShownPage().Matches(text))
            {
                used.Add($"Nav.{m.Groups[1].Value}");
                used.Add($"Panel.{m.Groups[1].Value}");
            }
        }

        used.Should().NotBeEmpty();
        used.Where(id => !ids.Contains(id)).Should().BeEmpty("UI テストが使う AutomationId は、MainWindow.xaml にあること");
    }

    [Fact]
    public void Pages_Should_Match_The_Names_The_View_Model_Opens()
    {
        var host = All.Single(e => e.Attribute(X + "Name")?.Value == "PagesHost");
        host.Elements().Select(e => e.Attribute("Tag")?.Value).Should().BeEquivalentTo(Pages.All, "画面ごとに 1 つ、Tag が画面の名前");
        var menu = All.Where(e => e.Name.LocalName == "NavigationViewItem").Select(e => e.Attribute("Tag")?.Value).ToList();
        menu.Should().NotBeEmpty().And.OnlyContain(tag => tag != null && Pages.All.Contains(tag), "メニューの項目は画面の名前");
    }

    [Fact]
    public void Bindings_Should_Name_Members_That_Exist()
    {
        var bindings = All.SelectMany(e => e.Attributes().Select(a => (Element: e, a.Value)))
            .Select(x => (x.Element, Match: XBind().Match(x.Value)))
            .Where(x => x.Match.Success)
            .ToList();
        var missing = new List<string>();
        foreach (var (element, match) in bindings)
        {
            var expression = match.Groups[1].Value.Trim();
            // 最初の「,」より前がパス（関数の引数の「,」は括弧の中）
            var path = SplitTopLevel(expression).FirstOrDefault()?.Trim() ?? "";
            if (path.Length == 0 || path.Contains('='))
                continue;
            var context = ContextOf(element);
            foreach (var p in PathsIn(path))
            {
                if (Resolve(p, context) == null)
                    missing.Add($"{p}（{context?.Name ?? "?"}）");
            }
        }

        bindings.Should().HaveCountGreaterThan(200);
        missing.Should().BeEmpty("x:Bind の名前は ViewModel にあること");
    }

    [Fact]
    public void Event_Handlers_Should_Exist_In_The_Code_Behind()
    {
        var code = File.ReadAllText(Path.Combine(DesktopDirectory, "MainWindow.xaml.cs"));
        var handlers = All.SelectMany(e => e.Attributes())
            .Where(a => a.Name.LocalName is "Click" or "SelectionChanged" or "DoubleTapped")
            .Select(a => a.Value)
            .Distinct()
            .ToList();

        handlers.Should().NotBeEmpty();
        handlers.Where(h => !code.Contains($"void {h}(", StringComparison.Ordinal)).Should().BeEmpty("イベントの処理は MainWindow.xaml.cs にあること");
    }

    [Fact]
    public void The_Ui_Helpers_Used_By_Bindings_Should_Exist()
    {
        var ui = File.ReadAllText(Path.Combine(DesktopDirectory, "Ui.cs"));
        var used = All.SelectMany(e => e.Attributes()).SelectMany(a => UiHelper().Matches(a.Value)).Select(m => m.Groups[1].Value).Distinct().ToList();

        used.Should().NotBeEmpty();
        used.Where(name => !Regex.IsMatch(ui, $@"\b{name}\(")).Should().BeEmpty();
    }

    // x:Bind の文脈の型（DataTemplate の x:DataType。ウィンドウの直下は MainWindow の ViewModel を持つ型として MainWindowContext）
    private static Type? ContextOf(XElement element)
    {
        if (TemplateOf(element) is not { } template)
            return typeof(MainWindowContext);
        var dataType = template.Attribute(X + "DataType")?.Value;
        return dataType == null || dataType.StartsWith("x:", StringComparison.Ordinal) ? null : TypeOf(dataType);
    }

    private static Type? TypeOf(string qualified)
    {
        var parts = qualified.Split(':');
        return parts.Length == 2 && Prefixes.TryGetValue(parts[0], out var ns) ? typeof(MainViewModel).Assembly.GetType($"{ns}.{parts[1]}") : null;
    }

    // 関数の呼び出し（local:Ui.Not(ViewModel.X)）は引数のパスを、ほかはそのパスを返す。local: の関数は The_Ui_Helpers_Used_By_Bindings_Should_Exist で確かめる
    private static IEnumerable<string> PathsIn(string path)
    {
        var open = path.IndexOf('(');
        if (open < 0)
            return [path];
        var inner = path[(open + 1)..path.LastIndexOf(')')];
        return SplitTopLevel(inner).Select(a => a.Trim()).Where(a => a.Length > 0).SelectMany(PathsIn);
    }

    private static IEnumerable<string> SplitTopLevel(string text)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '(')
                depth++;
            else if (text[i] == ')')
                depth--;
            else if (text[i] == ',' && depth == 0)
            {
                yield return text[start..i];
                start = i + 1;
            }
        }
        yield return text[start..];
    }

    // パスの各段のプロパティ・フィールドを順にたどる（vm:Type.Member は静的なメンバー）。たどれなければ null
    private static Type? Resolve(string path, Type? context)
    {
        var segments = path.Split('.');
        var type = context;
        var start = 0;
        if (segments[0].Contains(':'))
        {
            type = TypeOf(segments[0]);
            start = 1;
            if (type == null)
                return typeof(object);
        }
        if (type == null)
            return typeof(object);
        for (var i = start; i < segments.Length; i++)
        {
            var member = type!.GetMember(segments[i], BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).FirstOrDefault();
            type = member switch
            {
                PropertyInfo p => p.PropertyType,
                FieldInfo f => f.FieldType,
                _ => null,
            };
            if (type == null)
                return null;
            type = Nullable.GetUnderlyingType(type) ?? type;
        }
        return type;
    }

    // ウィンドウの直下の x:Bind は MainWindow の ViewModel から始まる
    private sealed class MainWindowContext
    {
        public MainViewModel ViewModel { get; } = null!;
    }

    [GeneratedRegex(@"\.(?:Find|Invoke|TextOf|IsEnabled|Exists|SetText|Select|SelectWhere|ItemsOf|SelectedOf|SetChecked|ValueOf|WaitForText|SelectRow)\(""([A-Za-z]+\.[A-Za-z.]+)""")]
    private static partial Regex UsedId();

    [GeneratedRegex(@"\.Show\(""(\w+)""\)")]
    private static partial Regex ShownPage();

    [GeneratedRegex(@"^\{x:Bind(?:\s+(.*))?\}$")]
    private static partial Regex XBind();

    [GeneratedRegex(@"local:Ui\.(\w+)\(")]
    private static partial Regex UiHelper();
}
