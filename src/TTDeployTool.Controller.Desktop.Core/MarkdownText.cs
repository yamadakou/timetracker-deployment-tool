namespace TTDeployTool.Controller.Desktop.Core;

/// <summary>クリップボードにコピーする Markdown（docs/design/desktop/desktop-settings.md の 2.2）。</summary>
internal static class MarkdownText
{
    /// <summary>表（1 行目が見出し）。セルの「|」と改行は、表が崩れないように置き換える。</summary>
    public static string Table(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string?>> rows)
    {
        var lines = new List<string>
        {
            Row(headers),
            "|" + string.Concat(headers.Select(_ => "---|")),
        };
        lines.AddRange(rows.Select(Row));
        return string.Join("\n", lines);
    }

    /// <summary>箇条書き（空のものは出さない）。</summary>
    public static string Bullets(IEnumerable<string?> items) =>
        string.Join("\n", items.Where(i => !string.IsNullOrEmpty(i)).Select(i => "- " + Cell(i)));

    /// <summary>見出しと本文をつなぐ（空の本文は出さない）。</summary>
    public static string Document(string title, params (string? Heading, string? Body)[] sections) =>
        string.Join("\n\n", new[] { $"# {Cell(title)}" }.Concat(sections
            .Where(s => !string.IsNullOrEmpty(s.Body))
            .Select(s => s.Heading == null ? s.Body! : $"## {s.Heading}\n\n{s.Body}"))) + "\n";

    private static string Row(IEnumerable<string?> cells) => "| " + string.Join(" | ", cells.Select(Cell)) + " |";

    private static string Cell(string? value) => (value ?? "").Replace("|", "\\|").Replace("\r\n", " ").Replace('\n', ' ');
}
