using System.Globalization;

namespace Timetracker.Controller.Cli;

/// <summary>
/// コマンドライン引数の解析。
/// 書式: [deploy] --option value | --option=value | --flag [true|false]
/// </summary>
public sealed class CliArgs
{
    public static readonly IReadOnlySet<string> ValueOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "--subscription", "--resource-group", "--location", "--app-name", "--db-type", "--db-password", "--db-name",
        "--tracker-password", "--tt-tag", "--auth-mode",
        "--tt-cpu", "--tt-memory", "--db-cpu", "--db-memory", "--redis-cpu", "--redis-memory",
    };

    public static readonly IReadOnlySet<string> BoolFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "--dry-run", "--verbose",
    };

    public const string HelpText = """
        使い方: timetracker-cli [deploy] [オプション]

        必須:
          --subscription <ID>         Azure サブスクリプション ID（--dry-run 時は不要）
          --resource-group <名前>     リソースグループ名（存在しなければ作成。--dry-run 時は不要）
          --db-password <値>          DB パスワード
          --tracker-password <値>     Timetracker アプリパスワード

        任意:
          --location <リージョン>     既定: japaneast
          --app-name <名前>           既定: timetracker
          --db-type <種別>            postgresql | sqlserver（既定: postgresql）
          --db-name <名前>            既定: timetracker
          --tt-tag <タグ>             timetracker イメージタグ（既定: postgresql→11.0.1-linux-postgres / sqlserver→11.0.1-linux-mssql）
          --auth-mode <モード>        default | azure-cli | sp-env | device-code | managed-identity（既定: default）
          --tt-cpu / --tt-memory      Timetracker の vCPU / メモリ(Gi)（既定: 1.5 / 3.0）
          --db-cpu / --db-memory      DB の vCPU / メモリ(Gi)（既定: 1.0 / 2.0）
          --redis-cpu / --redis-memory Redis の vCPU / メモリ(Gi)（既定: 0.5 / 1.0）
          --dry-run [true|false]      Azure へ反映せず docker-compose.yml と .env を生成
          --verbose [true|false]      詳細ログを出力
          -h, --help                  このヘルプを表示

        値が "--" で始まる場合は --option=value 形式で指定してください。
        """;

    private readonly Dictionary<string, string> _values;

    public bool IsHelp { get; }

    private CliArgs(Dictionary<string, string> values, bool isHelp)
    {
        _values = values;
        IsHelp = isHelp;
    }

    /// <exception cref="ArgumentException">未知のオプション・値の不足・不正な値の場合</exception>
    public static CliArgs Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var isHelp = false;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];

            if (a is "-h" or "--help")
            {
                isHelp = true;
                continue;
            }

            if (!a.StartsWith("--"))
            {
                // 先頭の "deploy" サブコマンドのみ許可
                if (i == 0 && a.Equals("deploy", StringComparison.OrdinalIgnoreCase))
                    continue;
                throw new ArgumentException($"不明な引数です: '{a}'。--help で使い方を確認してください。");
            }

            string name = a;
            string? inlineValue = null;
            var eq = a.IndexOf('=');
            if (eq > 0)
            {
                name = a[..eq];
                inlineValue = a[(eq + 1)..];
            }

            if (BoolFlags.Contains(name))
            {
                if (inlineValue != null)
                {
                    values[name] = ParseBool(name, inlineValue);
                }
                else if (i + 1 < args.Length && IsBoolLiteral(args[i + 1]))
                {
                    values[name] = ParseBool(name, args[++i]);
                }
                else
                {
                    values[name] = "true";
                }
                continue;
            }

            if (!ValueOptions.Contains(name))
                throw new ArgumentException($"不明なオプションです: '{name}'。--help で使い方を確認してください。");

            if (inlineValue != null)
            {
                values[name] = inlineValue;
            }
            else if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            {
                values[name] = args[++i];
            }
            else
            {
                throw new ArgumentException($"オプション '{name}' に値が指定されていません。");
            }
        }

        return new CliArgs(values, isHelp);
    }

    private static bool IsBoolLiteral(string s) =>
        s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("false", StringComparison.OrdinalIgnoreCase);

    private static string ParseBool(string name, string value) =>
        IsBoolLiteral(value)
            ? value.ToLowerInvariant()
            : throw new ArgumentException($"オプション '{name}' の値は true または false で指定してください: '{value}'");

    /// <summary>オプションの値を取得する。未指定または空白の場合は null。</summary>
    public string? Get(string name) =>
        _values.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    public string Get(string name, string defaultValue) => Get(name) ?? defaultValue;

    public bool GetBool(string name) => _values.TryGetValue(name, out var v) && v == "true";

    /// <summary>
    /// 正の数値を取得する。OS のロケールに依存せず小数点は "." として解釈する
    /// （小数点がカンマのロケールで "0.75" が 75 と解釈されるのを防ぐ）。
    /// </summary>
    /// <exception cref="ArgumentException">数値として解釈できない、または 0 以下の場合</exception>
    public double GetDouble(string name, double defaultValue) => GetDouble(name) ?? defaultValue;

    /// <summary><see cref="GetDouble(string, double)"/> と同じ。未指定または空白の場合は null。</summary>
    /// <exception cref="ArgumentException">数値として解釈できない、または 0 以下の場合</exception>
    public double? GetDouble(string name)
    {
        if (!_values.TryGetValue(name, out var v) || string.IsNullOrWhiteSpace(v))
            return null;

        if (double.TryParse(v, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d) && d > 0)
            return d;

        throw new ArgumentException($"オプション '{name}' には正の数値（小数点は '.'）を指定してください: '{v}'");
    }
}
