using System.Runtime.CompilerServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using Windows.Management.Deployment;

namespace TTDeployTool.Controller.Desktop.UITests;

/// <summary>
/// 導入したデスクトップアプリを 1 回起動し、UI Automation で操作する（詳細設計 4）。テストごとに起動し直す。
/// 操作は UI Automation のパターン（Invoke・Value・SelectionItem・Toggle・ExpandCollapse）で行い、マウスやキーボードの入力は合成しない。
/// 要素は AutomationId（MainWindow.xaml の AutomationProperties.AutomationId）で探す。
/// </summary>
public sealed class AppSession : IDisposable
{
    /// <summary>要素を待つ上限（WinUI の初回の描画や、画面の切り替えを待つ）。</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(200);

    private readonly UIA3Automation _automation;

    private AppSession(Application app, UIA3Automation automation, Window window)
    {
        App = app;
        _automation = automation;
        Window = window;
    }

    public Application App { get; }

    public Window Window { get; }

    /// <summary>テストの結果（失敗したときのスクリーンショットなど）を置くフォルダー。</summary>
    public static string ResultsDirectory =>
        Environment.GetEnvironmentVariable("TT_UITEST_RESULTS_DIRECTORY") is { Length: > 0 } dir
            ? dir
            : Path.Combine(AppContext.BaseDirectory, "TestResults");

    /// <summary>アプリを起動し、ウィンドウが開くのを待つ。</summary>
    public static AppSession Launch()
    {
        var app = Application.LaunchStoreApp(AppUserModelId());
        var automation = new UIA3Automation();
        try
        {
            var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(90))
                         ?? throw new InvalidOperationException("アプリのウィンドウが開きませんでした。");
            return new AppSession(app, automation, window);
        }
        catch
        {
            automation.Dispose();
            app.Kill();
            throw;
        }
    }

    /// <summary>
    /// 起動するアプリの AppUserModelID。TT_UITEST_APP_ID があればそれを、無ければ導入したパッケージ（名前は TT_UITEST_PACKAGE_NAME。
    /// 既定は TTDeployTool）から作る。
    /// </summary>
    public static string AppUserModelId()
    {
        if (Environment.GetEnvironmentVariable("TT_UITEST_APP_ID") is { Length: > 0 } id)
            return id;
        var name = Environment.GetEnvironmentVariable("TT_UITEST_PACKAGE_NAME") is { Length: > 0 } n ? n : "TTDeployTool";
        var package = new PackageManager().FindPackagesForUser(string.Empty).FirstOrDefault(p => p.Id.Name == name)
                      ?? throw new InvalidOperationException(
                          $"パッケージ {name} が導入されていません。Visual Studio で配置するか、署名した MSIX を導入してください（docs/design/desktop-ui-tests.md）。");
        return $"{package.Id.FamilyName}!App";
    }

    /// <summary>AutomationId の要素が出るまで待って返す。</summary>
    public AutomationElement Find(string automationId, TimeSpan? timeout = null) =>
        Retry.WhileNull(
            () => Window.FindFirstDescendant(cf => cf.ByAutomationId(automationId)),
            timeout ?? Timeout, Interval, throwOnTimeout: true, ignoreException: true,
            timeoutMessage: $"要素 {automationId} が見つかりませんでした。").Result!;

    /// <summary>AutomationId の要素が今あるか（待たない）。</summary>
    public bool Exists(string automationId) => Window.FindFirstDescendant(cf => cf.ByAutomationId(automationId)) != null;

    /// <summary>左の一覧から画面を選び、その画面が表示されるのを待つ（Nav.{page} と Panel.{page}）。</summary>
    public void Show(string page)
    {
        var item = Find($"Nav.{page}");
        if (item.Patterns.SelectionItem.TryGetPattern(out var selection))
            selection.Select();
        else
            item.Patterns.Invoke.Pattern.Invoke();
        Find($"Panel.{page}");
    }

    /// <summary>ボタンが押せるようになるのを待って押す。</summary>
    public void Invoke(string automationId)
    {
        var element = Find(automationId);
        WaitUntil(() => element.IsEnabled, $"{automationId} が押せるようになりませんでした。");
        element.Patterns.Invoke.Pattern.Invoke();
    }

    /// <summary>ボタンが押せるか（押せる・押せないの表示の確かめ）。</summary>
    public bool IsEnabled(string automationId) => Find(automationId).IsEnabled;

    /// <summary>テキストボックス・パスワードの入力欄に値を入れる。</summary>
    public void SetText(string automationId, string value) => Find(automationId).Patterns.Value.Pattern.SetValue(value);

    /// <summary>コンボボックスで、表示の文字列が一致する項目を選ぶ。</summary>
    public void Select(string automationId, string itemText)
    {
        var combo = Find(automationId).AsComboBox();
        combo.Expand();
        var item = Retry.WhileNull(
            () => combo.Items.FirstOrDefault(i => i.Name == itemText),
            Timeout, Interval, throwOnTimeout: true, ignoreException: true,
            timeoutMessage: $"{automationId} に「{itemText}」がありませんでした。").Result!;
        item.Patterns.SelectionItem.Pattern.Select();
        if (combo.ExpandCollapseState == ExpandCollapseState.Expanded)
            combo.Collapse();
    }

    /// <summary>チェックボックスの状態を決める。</summary>
    public void SetChecked(string automationId, bool value)
    {
        var toggle = Find(automationId).Patterns.Toggle.Pattern;
        if ((toggle.ToggleState == ToggleState.On) != value)
            toggle.Toggle();
    }

    /// <summary>要素とその中の文字列（TextBlock の表示、ItemsControl の各行、InfoBar のメッセージ）をつなげて返す。</summary>
    public string TextOf(string automationId)
    {
        var element = Find(automationId);
        var texts = new List<string>();
        if (element.Patterns.Value.TryGetPattern(out var value))
            texts.Add(value.Value.Value ?? "");
        texts.Add(element.Name);
        texts.AddRange(element.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Select(t => t.Name));
        return string.Join("\n", texts.Where(t => t.Length > 0).Distinct());
    }

    /// <summary>要素の文字列が条件を満たすまで待ち、その文字列を返す。</summary>
    public string WaitForText(string automationId, Func<string, bool> condition, string description, TimeSpan? timeout = null)
    {
        var last = "";
        WaitUntil(() => condition(last = TextOf(automationId)), $"{automationId}: {description}（最後の表示: {last}）", timeout);
        return last;
    }

    /// <summary>
    /// ウィンドウの中の文字列（TextBlock）をすべてつなげて返す。文字列の一覧（ItemsControl）は UI Automation の要素にならないため、
    /// 一覧の内容はこれで確かめる（各行は MainWindow.xaml の LineTemplate の TextBlock）。
    /// </summary>
    public string ScreenText() =>
        string.Join("\n", Window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Select(t => t.Name).Where(t => t.Length > 0));

    /// <summary>ウィンドウの中の文字列が条件を満たすまで待つ。</summary>
    public void WaitForScreenText(Func<string, bool> condition, string description, TimeSpan? timeout = null)
    {
        var last = "";
        WaitUntil(() => condition(last = ScreenText()), $"{description}（最後の表示: {last}）", timeout);
    }

    /// <summary>条件を満たすまで待つ（満たさなければ失敗にする）。</summary>
    public static void WaitUntil(Func<bool> condition, string description, TimeSpan? timeout = null)
    {
        var result = Retry.WhileFalse(condition, timeout ?? Timeout, Interval, throwOnTimeout: false, ignoreException: true);
        if (!result.Success)
            throw new TimeoutException(description);
    }

    /// <summary>
    /// OS のフォルダーの選択のダイアログで、フォルダーを選ぶ。ダイアログの要素は Windows の共通のダイアログの ID
    /// （フォルダー名の入力欄 1152、選択のボタン 1）で探す（表示の言語によらない）。
    /// </summary>
    public void PickFolder(string path)
    {
        var dialog = WaitForDialog();
        var box = Retry.WhileNull(
            () => dialog.FindFirstDescendant(cf => cf.ByAutomationId("1152")),
            Timeout, Interval, throwOnTimeout: true, ignoreException: true, timeoutMessage: "フォルダー名の入力欄が見つかりませんでした。").Result!;
        box.Patterns.Value.Pattern.SetValue(path);
        Button(dialog, "1").Patterns.Invoke.Pattern.Invoke();
        // 入力したフォルダーに移動しただけで閉じない場合は、移動した先（入力したフォルダー）をもう一度選ぶ
        if (!Retry.WhileFalse(() => FindDialog() == null, TimeSpan.FromSeconds(3), Interval, ignoreException: true).Success)
            FindDialog()?.FindFirstDescendant(cf => cf.ByAutomationId("1"))?.Patterns.Invoke.Pattern.Invoke();
        WaitUntil(() => FindDialog() == null, "フォルダーの選択のダイアログが閉じませんでした。");
    }

    private static AutomationElement Button(AutomationElement dialog, string id) =>
        dialog.FindFirstDescendant(cf => cf.ByAutomationId(id))
        ?? throw new InvalidOperationException($"ダイアログにボタン {id} がありませんでした。");

    private AutomationElement WaitForDialog() =>
        Retry.WhileNull(FindDialog, Timeout, Interval, throwOnTimeout: true, ignoreException: true,
            timeoutMessage: "フォルダーの選択のダイアログが開きませんでした。").Result!;

    // 共通のダイアログ（クラス名 #32770）。メインのウィンドウの下か、デスクトップの直下（同じプロセス）にある
    private AutomationElement? FindDialog() =>
        Window.FindFirstChild(cf => cf.ByClassName("#32770"))
        ?? _automation.GetDesktop().FindFirstChild(cf => cf.ByClassName("#32770").And(cf.ByProcessId(App.ProcessId)));

    /// <summary>今の画面のスクリーンショットを保存する（失敗したときの調査のため）。</summary>
    public string Screenshot(string name)
    {
        Directory.CreateDirectory(ResultsDirectory);
        var path = Path.Combine(ResultsDirectory, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        FlaUI.Core.Capturing.Capture.Screen().ToFile(path);
        return path;
    }

    /// <summary>ウィンドウを閉じ、アプリが終わるのを待つ（終わらなければ止める）。</summary>
    public bool CloseAndWait(TimeSpan? timeout = null)
    {
        Window.Close();
        var exited = Retry.WhileFalse(() => App.HasExited, timeout ?? TimeSpan.FromSeconds(15), Interval, ignoreException: true).Success;
        return exited;
    }

    public void Dispose()
    {
        try
        {
            if (!App.HasExited && !CloseAndWait())
                App.Kill();
        }
        catch (Exception)
        {
            // 後片付けの失敗で、テストの結果を変えない
        }
        App.Dispose();
        _automation.Dispose();
    }

    /// <summary>アプリを起動してテストを行い、失敗したらスクリーンショットを残す。</summary>
    public static void Run(Action<AppSession> test, [CallerMemberName] string name = "")
    {
        using var session = Launch();
        try
        {
            test(session);
        }
        catch
        {
            try
            {
                session.Screenshot(name);
            }
            catch (Exception)
            {
                // スクリーンショットを取れなくても、元の失敗を報告する
            }
            throw;
        }
    }
}
