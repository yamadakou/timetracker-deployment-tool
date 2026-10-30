using System.Runtime.CompilerServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using FlaUI.UIA3.Converters;
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

    private AppSession(FlaUI.Core.Application app, UIA3Automation automation, Window window)
    {
        App = app;
        _automation = automation;
        Window = window;
    }

    public FlaUI.Core.Application App { get; }

    public Window Window { get; }

    /// <summary>テストの結果（失敗したときのスクリーンショットなど）を置くフォルダー。</summary>
    public static string ResultsDirectory =>
        Environment.GetEnvironmentVariable("TT_UITEST_RESULTS_DIRECTORY") is { Length: > 0 } dir
            ? dir
            : Path.Combine(AppContext.BaseDirectory, "TestResults");

    /// <summary>アプリを起動し、ウィンドウが開くのを待つ。</summary>
    public static AppSession Launch()
    {
        var app = FlaUI.Core.Application.LaunchStoreApp(AppUserModelId());
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
                          $"パッケージ {name} が導入されていません。Visual Studio で配置するか、署名した MSIX を導入してください（docs/design/desktop/desktop-ui-tests.md）。");
        return $"{package.Id.FamilyName}!App";
    }

    /// <summary>導入したアプリのデータのフォルダー（ApplicationData.LocalFolder。起動の前に、ジョブの記録などを置くのに使う）。</summary>
    public static string DataDirectory()
    {
        var family = AppUserModelId().Split('!')[0];
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", family, "LocalState");
    }

    /// <summary>ウィンドウが表示されているか（閉じて通知領域に残ると、隠れる）。</summary>
    public bool IsWindowShown => IsWindowVisible(Window.Properties.NativeWindowHandle.Value);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

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
    public void Select(string automationId, string itemText) => SelectWhere(automationId, name => name == itemText, $"「{itemText}」");

    /// <summary>コンボボックスの項目の名前（一覧を開いて読む）。</summary>
    public IReadOnlyList<string> ItemsOf(string automationId)
    {
        var combo = Find(automationId).AsComboBox();
        combo.Expand();
        var names = combo.Items.Select(NameOf).ToList();
        if (combo.ExpandCollapseState == ExpandCollapseState.Expanded)
            combo.Collapse();
        return names;
    }

    /// <summary>コンボボックスで選んでいる項目の名前（選んでいなければ空）。</summary>
    public string SelectedOf(string automationId) => Find(automationId).AsComboBox().SelectedItem is { } item ? NameOf(item) : "";

    /// <summary>コンボボックスで、名前が条件を満たす最初の項目を選ぶ。</summary>
    public void SelectWhere(string automationId, Func<string, bool> condition, string description, TimeSpan? timeout = null)
    {
        var combo = Find(automationId).AsComboBox();
        combo.Expand();
        var item = Retry.WhileNull(
            () => combo.Items.FirstOrDefault(i => condition(NameOf(i))),
            timeout ?? Timeout, Interval, throwOnTimeout: true, ignoreException: true,
            timeoutMessage: $"{automationId} に{description}がありませんでした。").Result!;
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

    /// <summary>表（WinUI.TableView。行は DataItem）の行を選びに加える（<paramref name="index"/> は上から 0 始まり。行が現れるまで待つ）。</summary>
    public void SelectRow(string automationId, int index = 0, TimeSpan? timeout = null)
    {
        var rows = Retry.WhileEmpty(
            () => RowsOf(automationId),
            timeout ?? Timeout, Interval, throwOnTimeout: true, ignoreException: true,
            timeoutMessage: $"表 {automationId} に行が出ませんでした。").Result!;
        rows[index].Patterns.SelectionItem.Pattern.AddToSelection();
    }

    /// <summary>
    /// 表の行のうち、列 <paramref name="cellId"/> の文字が <paramref name="text"/> の行（現れるまで待つ）。UI Automation の行の順は、画面の行の順と
    /// 一致するとは限らないため（WinUI.TableView。Desktop System Test、2026-10-29）、行の位置ではなく中身で探す。
    /// </summary>
    public AutomationElement RowWith(string tableId, string cellId, string text, TimeSpan? timeout = null) =>
        RowWhere(tableId, r => TextIn(r, cellId) == text, $"{cellId} が「{text}」", timeout);

    /// <summary>表の行のうち、条件に合う行（現れるまで待つ）。</summary>
    public AutomationElement RowWhere(string tableId, Func<AutomationElement, bool> match, string description, TimeSpan? timeout = null) =>
        Retry.WhileNull(
            () => RowsOf(tableId).FirstOrDefault(match),
            timeout ?? Timeout, Interval, throwOnTimeout: true, ignoreException: true,
            timeoutMessage: $"表 {tableId} に、{description}の行が出ませんでした（今の行: {RowsText(tableId)}）。").Result!;

    /// <summary>表の行のうち、列 <paramref name="cellId"/> の文字が <paramref name="text"/> の行を選びに加える。</summary>
    public void SelectRowWith(string tableId, string cellId, string text) =>
        RowWith(tableId, cellId, text).Patterns.SelectionItem.Pattern.AddToSelection();

    /// <summary>行の中の列 <paramref name="cellId"/> の文字（無ければ空）。</summary>
    public static string TextIn(AutomationElement row, string cellId) =>
        row.FindFirstDescendant(cf => cf.ByAutomationId(cellId)) is { } cell ? NameOf(cell) : "";

    /// <summary>
    /// 表の行のうち、列 <paramref name="keyId"/> の文字が <paramref name="key"/> の行の、列 <paramref name="cellId"/> の文字が条件を満たすまで待ち、
    /// その文字を返す（行を作り直しても、そのたびに探し直す）。
    /// </summary>
    public string WaitForRowText(string tableId, string keyId, string key, string cellId, Func<string, bool> condition, string description, TimeSpan? timeout = null)
    {
        var last = "";
        if (!TryWait(() => condition(last = TextIn(RowWith(tableId, keyId, key), cellId)), timeout))
            throw new TimeoutException($"{tableId} の {key} の {cellId}: {description}（最後の表示: {last}。今の行: {RowsText(tableId)}）");
        return last;
    }

    /// <summary>表の行の文字（UI Automation の行の順。失敗したときの記録に使う）。</summary>
    public string RowsText(string tableId) =>
        string.Join(" | ", RowsOf(tableId).Select(r => string.Join(" / ", r.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Select(NameOf).Where(t => t.Length > 0))));

    /// <summary>
    /// 表の列の見出し（HeaderItem）。WinUI.TableView の見出しは UI Automation の木に出ないため、1 行目のセルの TableItem パターンからたどる
    /// （行の無い表では見つからない。docs/design/desktop/desktop-tables.md の 9）。見出しの名前には、並べ替え・絞り込みの状態が「, 昇順」のように足される。
    /// </summary>
    public AutomationElement ColumnHeader(string tableId, string header) =>
        Retry.WhileNull(
            () =>
            {
                var grid = Find(tableId).Patterns.Grid.Pattern;
                return Enumerable.Range(0, grid.ColumnCount.Value)
                    .Select(c => grid.GetItem(0, c))
                    .Where(c => c != null)
                    .SelectMany(HeaderItems)
                    .FirstOrDefault(h => NameOf(h) == header || NameOf(h).StartsWith($"{header}, ", StringComparison.Ordinal));
            },
            Timeout, Interval, throwOnTimeout: true, ignoreException: true,
            timeoutMessage: $"表 {tableId} に、見出し「{header}」が見つかりませんでした。").Result!;

    /// <summary>
    /// 見出し <paramref name="header"/> を 3 回押し、そのたびの見出しの名前を返す（昇順 → 降順 → 既定の順。名前に並べ替えの状態が足され、3 回目で消える）。
    /// 向きは Core の並べ替えに合わせて出すため、Core で並べ替えられたことも分かる。
    /// </summary>
    public IReadOnlyList<string> SortByHeaderThreeTimes(string tableId, string header)
    {
        var names = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            ColumnHeader(tableId, header).Patterns.Invoke.Pattern.Invoke();
            var before = names.LastOrDefault() ?? header;
            WaitUntil(() => NameOf(ColumnHeader(tableId, header)) != before, $"見出し「{header}」を {i + 1} 回押すと、並べ替えの状態が変わる");
            names.Add(NameOf(ColumnHeader(tableId, header)));
        }
        return names;
    }

    /// <summary>
    /// 点を押す（<paramref name="right"/> は右クリック、<paramref name="twice"/> はダブルクリック）。モックの UI テストだけが使う、マウスの入力の合成
    /// （UI Automation のパターンで操作できないもの。docs/design/desktop/desktop-ui-tests.md の 6.3）。
    /// </summary>
    public static void Click(System.Drawing.Point point, bool right = false, bool twice = false)
    {
        FlaUI.Core.Input.Mouse.MoveTo(point);
        if (twice)
            FlaUI.Core.Input.Mouse.DoubleClick(point);
        else if (right)
            FlaUI.Core.Input.Mouse.RightClick(point);
        else
            FlaUI.Core.Input.Mouse.LeftClick(point);
        Thread.Sleep(300);
    }

    /// <summary>
    /// マウスで <paramref name="from"/> から <paramref name="to"/> までドラッグする。カーソルの位置を置き換える移動（SetCursorPos）は、ボタンを押したままの
    /// 移動として表の部品に届かなかった（CI の UI テスト、run 41075835148）ため、移動も Windows の入力（SendInput）で送る。少し手前から境目に近づけて
    /// 止め（表の部品は、境目に入ったときにつかめる形〔カーソル〕にする）、押してから少しずつ動かす。<paramref name="during"/> は、離す前に呼ぶ。
    /// </summary>
    public static void Drag(System.Drawing.Point from, System.Drawing.Point to, Action? during = null)
    {
        for (var x = from.X - 10; x <= from.X; x++)
        {
            SendMouse(new System.Drawing.Point(x, from.Y), MouseMove);
            Thread.Sleep(20);
        }
        Thread.Sleep(300);
        SendMouse(from, MouseLeftDown);
        Thread.Sleep(300);
        for (var i = 1; i <= 30; i++)
        {
            SendMouse(new System.Drawing.Point(from.X + (to.X - from.X) * i / 30, from.Y + (to.Y - from.Y) * i / 30), MouseMove);
            Thread.Sleep(30);
        }
        // 見出しの順序の変更は、動かし終えて止まってから離すと、離した後に落とす処理が走る
        for (var i = 0; i < 5; i++)
        {
            SendMouse(new System.Drawing.Point(to.X + (i % 2 == 0 ? 1 : -1), to.Y), MouseMove);
            Thread.Sleep(60);
        }
        SendMouse(to, MouseMove);
        Thread.Sleep(500);
        during?.Invoke();
        SendMouse(to, MouseLeftUp);
        Thread.Sleep(1000);
    }

    private const uint MouseMove = 0x0001;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseAbsolute = 0x8000;
    private const uint MouseVirtualDesk = 0x4000;

    // 画面の点（ピクセル）へ、絶対の位置（0〜65535）の移動とボタンを送る
    private static void SendMouse(System.Drawing.Point point, uint flags)
    {
        var left = GetSystemMetrics(76);
        var top = GetSystemMetrics(77);
        var width = Math.Max(1, GetSystemMetrics(78) - 1);
        var height = Math.Max(1, GetSystemMetrics(79) - 1);
        var input = new Input
        {
            Type = 0,
            Mouse = new MouseInput
            {
                X = (point.X - left) * 65535 / width,
                Y = (point.Y - top) * 65535 / height,
                Flags = flags | MouseMove | MouseAbsolute | MouseVirtualDesk,
            },
        };
        SendInput(1, [input], System.Runtime.InteropServices.Marshal.SizeOf<Input>());
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public MouseInput Mouse;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    /// <summary>キーボードで、修飾キーと一緒にキーを押す（Ctrl+C など）。</summary>
    public static void Press(params FlaUI.Core.WindowsAPI.VirtualKeyShort[] keys)
    {
        FlaUI.Core.Input.Keyboard.TypeSimultaneously(keys);
        Thread.Sleep(300);
    }

    /// <summary>クリップボードの文字（テストの側で読む。PowerShell の Get-Clipboard）。</summary>
    public static string Clipboard()
    {
        var start = new System.Diagnostics.ProcessStartInfo("pwsh", "-NoProfile -Command \"[Console]::OutputEncoding = [Text.Encoding]::UTF8; Get-Clipboard -Raw\"")
        {
            RedirectStandardOutput = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = System.Diagnostics.Process.Start(start)!;
        var text = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return text.TrimEnd('\r', '\n');
    }

    /// <summary>ウィンドウの中の要素（ポップアップ・ダイアログを含む）を AutomationId で探す。現れるまで待つ。</summary>
    public AutomationElement FindAnywhere(string automationId) =>
        Retry.WhileNull(() => Window.FindFirstDescendant(cf => cf.ByAutomationId(automationId)), Timeout, Interval, throwOnTimeout: true, ignoreException: true,
            timeoutMessage: $"要素 {automationId} が見つかりませんでした。").Result!;

    /// <summary>
    /// 表の行の数（今のページの行）。表は見えている行の分しか行の要素を作らない（仮想化）ため、子の数ではなく Grid パターンの行の数で読む
    /// （CI の UI テスト、run 41066697432: 20 行のうち 11 行しか子に無かった）。
    /// </summary>
    public int RowCount(string tableId) => Find(tableId).Patterns.Grid.Pattern.RowCount.Value;

    /// <summary>行を画面に入るまでスクロールする（画面の外の行の要素は、大きさが 0 になる）。</summary>
    public static void ScrollIntoView(AutomationElement row)
    {
        if (row.Patterns.ScrollItem.TryGetPattern(out var scroll))
            scroll.ScrollIntoView();
    }

    // FlaUI の ColumnHeaderItems のプロパティは XAML の UI Automation の橋渡しで「対応しない」になるため、TableItem パターン（10013）のメソッドで読む
    private static AutomationElement[] HeaderItems(AutomationElement cell) =>
        AutomationElementConverter.NativeArrayToManaged(cell.Automation,
            ((Interop.UIAutomationClient.IUIAutomationTableItemPattern)AutomationElementConverter.ToNative(cell).GetCurrentPattern(10013)).GetCurrentColumnHeaderItems());

    private AutomationElement[] RowsOf(string tableId) =>
        Find(tableId).FindAllChildren(cf => cf.ByControlType(ControlType.ListItem).Or(cf.ByControlType(ControlType.DataItem)));

    /// <summary>入力欄（TextBox）の値。</summary>
    public string ValueOf(string automationId) => Find(automationId).Patterns.Value.Pattern.Value.Value ?? "";

    /// <summary>要素とその中の文字列（TextBlock の表示、ItemsControl の各行、InfoBar のメッセージ）をつなげて返す。</summary>
    public string TextOf(string automationId)
    {
        var element = Find(automationId);
        var texts = new List<string>();
        if (element.Patterns.Value.TryGetPattern(out var value))
            texts.Add(value.Value.Value ?? "");
        texts.Add(NameOf(element));
        texts.AddRange(element.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Select(NameOf));
        return string.Join("\n", texts.Where(t => t.Length > 0).Distinct());
    }

    /// <summary>要素の文字列が条件を満たすまで待ち、その文字列を返す。</summary>
    public string WaitForText(string automationId, Func<string, bool> condition, string description, TimeSpan? timeout = null)
    {
        var last = "";
        if (!TryWait(() => condition(last = TextOf(automationId)), timeout))
            throw new TimeoutException($"{automationId}: {description}（最後の表示: {last}）");
        return last;
    }

    /// <summary>
    /// ウィンドウの中の文字列（TextBlock）をすべてつなげて返す。文字列の一覧（ItemsControl）は UI Automation の要素にならないため、
    /// 一覧の内容はこれで確かめる（各行は MainWindow.xaml の LineTemplate の TextBlock）。
    /// </summary>
    public string ScreenText() =>
        string.Join("\n", Window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Select(NameOf).Where(t => t.Length > 0));

    // 名前を持たない要素（表示の更新の途中など）は空として読む
    private static string NameOf(AutomationElement element) => element.Properties.Name.ValueOrDefault ?? "";

    /// <summary>ウィンドウの中の文字列が条件を満たすまで待つ。</summary>
    public void WaitForScreenText(Func<string, bool> condition, string description, TimeSpan? timeout = null)
    {
        var last = "";
        if (!TryWait(() => condition(last = ScreenText()), timeout))
            throw new TimeoutException($"{description}（最後の表示: {last}）");
    }

    /// <summary>条件を満たすまで待つ（満たさなければ失敗にする）。</summary>
    public static void WaitUntil(Func<bool> condition, string description, TimeSpan? timeout = null)
    {
        if (!TryWait(condition, timeout))
            throw new TimeoutException(description);
    }

    // 条件を満たすまで待つ（満たしたか）。待っている間の例外（要素の作り直しなど）は、満たしていないとして続ける
    internal static bool TryWait(Func<bool> condition, TimeSpan? timeout) =>
        Retry.WhileFalse(condition, timeout ?? Timeout, Interval, throwOnTimeout: false, ignoreException: true).Success;

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
