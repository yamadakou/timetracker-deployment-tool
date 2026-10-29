using TTDeployTool.Controller.Desktop.Core.Services;

namespace TTDeployTool.Controller.Desktop.Core.ViewModels;

/// <summary>表の列の配置（列の名前と幅。並びの順が列の順）。</summary>
public sealed record TableColumnLayout(string Key, double Width);

/// <summary>
/// 表の列の幅と順序（docs/design/desktop/desktop-tables.md の 10.1）。表ごとにアプリの設定に保存し、次の起動でも同じにする。
/// 保存した配置に無い列（新しい版で足した列）は初期値の位置に入れ、知らない列（無くなった列）は捨てる。
/// </summary>
public sealed class TableLayout(string name, IReadOnlyList<TableColumnLayout> defaults, IAppPreferences preferences)
{
    public IReadOnlyList<TableColumnLayout> Defaults => defaults;

    /// <summary>今の配置（保存したもの。無ければ初期値）。</summary>
    public IReadOnlyList<TableColumnLayout> Current
    {
        get
        {
            if (!preferences.TableLayouts.TryGetValue(name, out var saved))
                return defaults;
            var known = saved.Where(s => defaults.Any(d => d.Key == s.Key)).DistinctBy(s => s.Key).ToList();
            foreach (var (column, index) in defaults.Select((d, i) => (d, i)).Where(x => known.All(k => k.Key != x.d.Key)))
                known.Insert(Math.Min(index, known.Count), column);
            return known;
        }
    }

    /// <summary>画面で変えた配置を保存する（初期値と同じなら、保存を消す）。</summary>
    public void Save(IReadOnlyList<TableColumnLayout> layout)
    {
        var layouts = preferences.TableLayouts.Where(l => l.Key != name).ToDictionary();
        if (!layout.SequenceEqual(defaults))
            layouts[name] = layout;
        preferences.TableLayouts = layouts;
    }

    /// <summary>初期値に戻す。</summary>
    public IReadOnlyList<TableColumnLayout> Reset()
    {
        Save(defaults);
        return defaults;
    }
}
