using System.Globalization;
using TTDeployTool.Controller.Desktop.Core.Mvvm;
using TTDeployTool.Domain.Deployment;

namespace TTDeployTool.Controller.Desktop.Core.ViewModels;

/// <summary>
/// TimeTracker・DB・Redis のスペックのスライダー（オーナーの依頼、2026-11-13: 3 つとも案 B のスライダー）。CPU を段階（<see cref="CpuSteps"/>）から選ぶと、
/// メモリは CPU の 2 倍（Container Apps の従量課金〔ワークロード プロファイルを使わない環境〕で選べる組み合わせ）にする。値は設定の入力（文字列）に入れる。
/// </summary>
public sealed class SpecSlider : ObservableObject
{
    /// <summary>選べる CPU（vCPU）。メモリ（GiB）はその 2 倍。</summary>
    public static readonly IReadOnlyList<double> CpuSteps = [0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0];

    private readonly Func<string> _cpu;
    private readonly Action<string> _setCpu;
    private readonly Func<string> _memory;
    private readonly Action<string> _setMemory;
    private readonly double _defaultCpu;
    private readonly double _defaultMemory;

    internal SpecSlider(string name, Func<string> cpu, Action<string> setCpu, Func<string> memory, Action<string> setMemory, double defaultCpu, double defaultMemory)
    {
        Name = name;
        _cpu = cpu;
        _setCpu = setCpu;
        _memory = memory;
        _setMemory = setMemory;
        _defaultCpu = defaultCpu;
        _defaultMemory = defaultMemory;
        Minimum = DeploymentAdvisor.SampleDbMinimumOf(name);
    }

    public string Name { get; }

    // ---- サンプル DB を作れる実用上の最低スペック（オーナーの依頼、2026-11-14: 下回る範囲を赤にする）

    /// <summary>サンプル DB を作れる実用上の最低スペック。</summary>
    public SampleDbMinimum Minimum { get; }

    /// <summary>最低スペックを下回る段階の割合（0〜1。スライダーの赤の帯の幅。0 なら帯は無い）。</summary>
    public double BelowMinimumShare => (double)CpuSteps.Count(c => c < Minimum.Cpu) / MaxStep;

    /// <summary>段階が最低スペックを下回るか（目盛りの文字を赤にする）。</summary>
    public bool IsBelowMinimumStep(int step) => CpuSteps[step] < Minimum.Cpu;

    /// <summary>今のスペックが最低スペックを下回るか（空の欄は既定値）。</summary>
    public bool IsBelowMinimum => Minimum.IsBelow(Cpu ?? _defaultCpu, Memory ?? _defaultMemory);

    /// <summary>最低スペックの案内（下回っていれば注意の文）。</summary>
    public string MinimumText => IsBelowMinimum
        ? $"サンプル DB を作れる実用上の最低スペック（{Minimum.Text}）を下回っています。サンプル DB の作成ができない場合があります。"
        : $"サンプル DB を作れる実用上の最低スペック: {Minimum.Text}";

    /// <summary>スライダーの最後の段階の番号。</summary>
    public int MaxStep => CpuSteps.Count - 1;

    /// <summary>今の CPU に最も近い段階（読めなければ既定値の段階）。動かすと CPU とメモリを入れる。</summary>
    public int Step
    {
        get
        {
            var cpu = Cpu ?? _defaultCpu;
            return CpuSteps.Select((value, index) => (Distance: Math.Abs(value - cpu), index)).MinBy(x => x.Distance).index;
        }
        set
        {
            var cpu = CpuSteps[Math.Clamp(value, 0, MaxStep)];
            _setCpu(Format(cpu));
            _setMemory(Format(cpu * 2));
        }
    }

    /// <summary>段階の目盛りの文字（0.25・0.5・…・2.0）。</summary>
    public IReadOnlyList<string> Labels { get; } = [.. CpuSteps.Select(Format)];

    /// <summary>今のスペックの文字（「1.5 vCPU / 3 GiB」。空の欄は既定値）。</summary>
    public string Text => string.Create(CultureInfo.InvariantCulture, $"{Cpu ?? _defaultCpu:0.##} vCPU / {Memory ?? _defaultMemory:0.##} GiB");

    /// <summary>CPU とメモリが選べる組み合わせでなければ、その旨（テンプレート・前の入力で、ほかの値が入っている場合）。</summary>
    public string? Note
    {
        get
        {
            var cpu = Cpu ?? _defaultCpu;
            var memory = Memory ?? _defaultMemory;
            return CpuSteps.Contains(cpu) && memory == cpu * 2
                ? null
                : "CPU とメモリが Container Apps の組み合わせ（メモリは CPU の 2 倍）と違います。スライダーを動かすと、組み合わせに直します。";
        }
    }

    private double? Cpu => Parse(_cpu());

    private double? Memory => Parse(_memory());

    internal void Raise()
    {
        OnPropertyChanged(nameof(Step));
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(IsBelowMinimum));
        OnPropertyChanged(nameof(MinimumText));
    }

    private static double? Parse(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static string Format(double value) => value.ToString("0.0##", CultureInfo.InvariantCulture);
}
