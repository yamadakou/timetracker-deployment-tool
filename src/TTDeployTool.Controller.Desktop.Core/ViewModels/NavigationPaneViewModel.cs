using TTDeployTool.Controller.Desktop.Core.Mvvm;
using TTDeployTool.Controller.Desktop.Core.Services;

namespace TTDeployTool.Controller.Desktop.Core.ViewModels;

/// <summary>
/// 左のメニューの幅と、アイコンだけに畳むか（オーナーの依頼、2026-11-16: 幅を変えられず、長いアカウント名が読めなかった。オーナーの判断で、幅の変更・畳むの両方）。
/// 幅は右端のつまみ（ドラッグ・キーボード）で変え、アプリの設定に保存する。畳んだ後も、広げると前の幅に戻る。
/// </summary>
public sealed class NavigationPaneViewModel : ObservableObject
{
    /// <summary>既定の幅（今までの見た目の幅〔余白を含めて 236〕に近い幅）。</summary>
    public const double DefaultWidth = 240;

    /// <summary>いちばん狭い幅（メニューの文字が読める幅）。</summary>
    public const double MinWidth = 180;

    /// <summary>いちばん広い幅（画面を狭めすぎない）。</summary>
    public const double MaxWidth = 420;

    /// <summary>畳んだときの幅（アイコンだけ）。</summary>
    public const double CollapsedWidth = 60;

    /// <summary>キーボード（つまみで ←/→）で変える幅。</summary>
    public const double KeyboardStep = 10;

    private readonly IAppPreferences _preferences;
    private double _width;
    private bool _isCollapsed;

    public NavigationPaneViewModel(IAppPreferences preferences)
    {
        _preferences = preferences;
        var saved = preferences.NavigationPane;
        _width = Clamp(saved?.Width ?? DefaultWidth);
        _isCollapsed = saved?.Collapsed ?? false;
    }

    /// <summary>広げたときの幅。</summary>
    public double Width => _width;

    /// <summary>アイコンだけに畳んでいるか。</summary>
    public bool IsCollapsed => _isCollapsed;

    /// <summary>今の幅（畳んでいれば <see cref="CollapsedWidth"/>）。</summary>
    public double CurrentWidth => _isCollapsed ? CollapsedWidth : _width;

    /// <summary>畳む・広げるボタンの説明。</summary>
    public string ToggleText => _isCollapsed ? "メニューを広げる" : "メニューを畳む（アイコンだけにする）";

    /// <summary>幅を変える（ドラッグ。畳んでいれば広げる）。<see cref="MinWidth"/>〜<see cref="MaxWidth"/> に収める。</summary>
    public void Resize(double width) => Apply(Clamp(width), collapsed: false);

    /// <summary>キーボードで幅を変える（1 回で <see cref="KeyboardStep"/>）。</summary>
    public void Nudge(int step) => Resize(_width + step * KeyboardStep);

    /// <summary>既定の幅に戻す（つまみのダブルクリック）。</summary>
    public void Reset() => Resize(DefaultWidth);

    /// <summary>畳む・広げる。</summary>
    public void Toggle() => Apply(_width, !_isCollapsed);

    private void Apply(double width, bool collapsed)
    {
        if (width == _width && collapsed == _isCollapsed)
            return;
        _width = width;
        _isCollapsed = collapsed;
        _preferences.NavigationPane = new NavigationPaneLayout(width, collapsed);
        OnPropertyChanged(nameof(Width));
        OnPropertyChanged(nameof(IsCollapsed));
        OnPropertyChanged(nameof(CurrentWidth));
        OnPropertyChanged(nameof(ToggleText));
    }

    // 保存した値が壊れていれば既定の幅。整数のピクセルに丸める
    private static double Clamp(double width) => double.IsFinite(width) ? Math.Round(Math.Clamp(width, MinWidth, MaxWidth)) : DefaultWidth;
}
