using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;

namespace TTDeployTool.Controller.Desktop.Blazor.Tables;

/// <summary>
/// 表の画面の側（desktop-blazor-hybrid.md の 4・9）。決まりは Core（絞り込み・並べ替え・列の配置の保存・コピーの文字）と <see cref="HtmlTableRules"/> が持ち、
/// ここは描くことと入力を渡すことだけを行う。幅のつまみ・Alt+矢印・切れたセルのツールチップは、小さな JavaScript（wwwroot/table.js）が受けて、ここを呼ぶ。
/// </summary>
public partial class DataTable<TRow, TItem> : IAsyncDisposable
    where TRow : class
    where TItem : class
{
    private ElementReference _root;
    private IReadOnlyList<TableColumnLayout> _layout = [];
    private TableSource<TRow, TItem>? _watched;
    private TableRows<TItem>? _rows;
    private string? _filterKey;
    private string? _dragKey;
    private (TRow Row, string Key, double X, double Y)? _menu;
    private string? _message;
    private Task<IJSObjectReference> _module = default!;
    private DotNetObjectReference<DataTable<TRow, TItem>> _self = default!;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    [Parameter, EditorRequired]
    public TableSource<TRow, TItem> Source { get; set; } = default!;

    /// <summary>表の名前（読み上げ）。</summary>
    [Parameter]
    public string Label { get; set; } = "";

    /// <summary>コピーの先（無ければコピーしない）。</summary>
    [Parameter]
    public IClipboardService? Clipboard { get; set; }

    [Parameter]
    public EventCallback<TRow> OnRowDoubleClick { get; set; }

    /// <summary>今の列の配置（列の順と幅）。</summary>
    public IReadOnlyList<TableColumnLayout> Layout => _layout;

    private bool? AllState => HtmlTableRules.AllState(Source.Rows.Count, Source.Rows.Count(IsSelected));

    protected override void OnInitialized()
    {
        // 試作のホスト（WebView・テスト用のホスト）は先に描かない（プリレンダーしない）ため、最初から JavaScript を読める
        _self = DotNetObjectReference.Create(this);
        _module = JsModule.ImportAsync(JS);
    }

    protected override void OnParametersSet()
    {
        if (ReferenceEquals(_watched, Source))
            return;
        // 一覧が変わったとき（同じ一覧なら購読し直さない）
        _watched = Source;
        Watch(Source.Rows, Source.Filters);
        _layout = Source.Layout.Current;
        _rows = new TableRows<TItem>(Source.Filters, o => Source.ItemOf((TRow)o));
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // 行の中身（ジョブの状態など）の変更でも描き直す
        Watcher.Sync(Source.Rows);
        // 最初はつまみ・キーボード・ツールチップをつなぎ、その後は左上のチェックボックスの「一部を選んだ」の印を合わせる（HTML の属性では出せない）
        if (firstRender)
            await (await _module).InvokeVoidAsync("attach", _root, _self);
        else
            await (await _module).InvokeVoidAsync("sync", _root);
    }

    /// <summary>つまみのドラッグで幅を変えた（JavaScript から）。</summary>
    [JSInvokable]
    public void ColumnResized(string key, double width) => SaveLayout(HtmlTableRules.Resize(_layout, key, width));

    /// <summary>見出しのボタンで Alt+←/→（JavaScript から）。今の幅から <see cref="HtmlTableRules.KeyboardStep"/> ずつ変える。</summary>
    [JSInvokable]
    public void ColumnNudged(string key, int step)
    {
        var index = _layout.ToList().FindIndex(c => c.Key == key);
        if (index < 0)
            return;
        var current = HtmlTableRules.WidthOf(_layout, index, Source.FillMinWidth) ?? HtmlTableRules.LastMinWidth(_layout, Source.FillMinWidth);
        ColumnResized(key, current + step * HtmlTableRules.KeyboardStep);
    }

    /// <summary>見出しのボタンで Alt+Shift+←/→（JavaScript から）。列を 1 つ動かす。</summary>
    [JSInvokable]
    public void ColumnShifted(string key, int step) => SaveLayout(HtmlTableRules.Shift(_layout, key, step));

    // Blazor は IAsyncDisposable を持つコンポーネントの Dispose を呼ばないため、ここで購読も外す
    public async ValueTask DisposeAsync()
    {
        Dispose();
        await JsModule.DisposeQuietlyAsync(_module);
        _self.Dispose();
    }

    private static string Px(double value) => value.ToString("0.##", CultureInfo.InvariantCulture) + "px";

    private bool IsSelected(TRow row) => Source.Selected().Contains(row);

    private string AriaSort(string key) =>
        Source.SortColumn() != key ? "none" : Source.SortDescending() ? "descending" : "ascending";

    private string Arrow(string key) => AriaSort(key) switch { "ascending" => "▲", "descending" => "▼", _ => "" };

    private void SaveLayout(IReadOnlyList<TableColumnLayout> layout)
    {
        _layout = layout;
        Source.Layout.Save(layout);
        StateHasChanged();
    }

    private void ResetLayout() => _layout = Source.Layout.Reset();

    private void SortBy(string key)
    {
        var next = HtmlTableRules.Next(Source.SortColumn(), Source.SortDescending(), key);
        if (next.Key == null)
            Source.ClearSort();
        else
            Source.Sort(next.Key, next.Descending);
    }

    private void SortFromMenu(string key, bool descending)
    {
        Source.Sort(key, descending);
        _filterKey = null;
    }

    private void ClearSortFromMenu()
    {
        Source.ClearSort();
        _filterKey = null;
    }

    private void ToggleFilter(string key) => _filterKey = _filterKey == key ? null : key;

    private void CloseFilter() => _filterKey = null;

    private void Drop(string target)
    {
        if (_dragKey is { } moved)
            SaveLayout(HtmlTableRules.Move(_layout, moved, target));
        _dragKey = null;
    }

    private void Toggle(TRow row)
    {
        var selected = Source.Selected();
        Source.SetSelection(selected.Contains(row) ? selected.Where(r => r != row) : Source.Rows.Where(r => r == row || selected.Contains(r)));
    }

    private void ToggleAll() => Source.SetSelection(AllState == true ? [] : Source.Rows);

    private void OpenMenu(TRow row, string key, MouseEventArgs e) => _menu = (row, key, e.ClientX, e.ClientY);

    private void CloseMenu() => _menu = null;

    private void OnMenuKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
            CloseMenu();
    }

    private void OnKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
        {
            _filterKey = null;
            CloseMenu();
        }
        else if (e.CtrlKey && e.Key is "c" or "C" && Source.Selected().Count > 0)
        {
            Copy(_rows!.CopyText(Source.Selected(), Keys, e.ShiftKey), $"{Source.Selected().Count} 行をコピーしました。");
        }
    }

    private IEnumerable<string> Keys => _layout.Select(c => c.Key);

    // 選んだ行（選んでいなければ、右クリックした行。Core の TableRules.Target）
    private IReadOnlyList<object> MenuRows => TableRules.Target([.. Source.Selected()], _menu!.Value.Row);

    private void CopyCell()
    {
        var (row, key, _, _) = _menu!.Value;
        Copy(Source.CellText(row, key), "文字をコピーしました。");
        CloseMenu();
    }

    private void CopyRows(bool markdown)
    {
        var rows = MenuRows;
        Copy(markdown ? _rows!.CopyMarkdown(rows, Keys) : _rows!.CopyText(rows, Keys, headers: false),
            markdown ? $"{rows.Count} 行を Markdown 形式でコピーしました。" : $"{rows.Count} 行をコピーしました。");
        CloseMenu();
    }

    private void Copy(string text, string done)
    {
        if (Clipboard == null || text.Length == 0)
            return;
        // クリップボードがほかのアプリに使われていると開けないことがある。その 1 回はあきらめる（WinUI の画面と同じ）
        try
        {
            Clipboard.CopyText(text);
            _message = done;
        }
        catch (Exception)
        {
            _message = "コピーできませんでした。もう一度試してください。";
        }
    }
}
