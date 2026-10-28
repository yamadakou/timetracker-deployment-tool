using System.Globalization;

namespace TTDeployTool.Controller.Cli;

public enum CliCommand
{
    Deploy,
    Delete,
}

/// <summary>
/// コマンドライン引数の解析。
/// 書式: [deploy|delete] --option value | --option=value | --flag [true|false]
/// </summary>
public sealed class CliArgs
{
    // サブコマンドごとに使えるオプション（タイプミスや、別のサブコマンドのオプションの指定をエラーにするため）
    private static readonly IReadOnlySet<string> DeployValueOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "--subscription", "--resource-group", "--location", "--app-name", "--db-type", "--db-password", "--db-name",
        "--tracker-password", "--tt-tag", "--auth-mode", "--expires-on", "--group",
        "--tt-cpu", "--tt-memory", "--db-cpu", "--db-memory", "--redis-cpu", "--redis-memory",
    };

    private static readonly IReadOnlySet<string> DeployBoolFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "--dry-run", "--verbose", "--persist-db",
    };

    private static readonly IReadOnlySet<string> DeleteValueOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "--subscription", "--resource-group", "--app-name", "--auth-mode",
    };

    private static readonly IReadOnlySet<string> DeleteBoolFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "--dry-run", "--verbose", "--include-untagged", "--yes", "--keep-db-data", "--delete-resource-group",
    };

    private static readonly IReadOnlyDictionary<string, CliCommand> Commands = new Dictionary<string, CliCommand>(StringComparer.OrdinalIgnoreCase)
    {
        ["deploy"] = CliCommand.Deploy,
        ["delete"] = CliCommand.Delete,
    };

    /// <summary>いずれかのサブコマンドで値を取るオプション。</summary>
    public static readonly IReadOnlySet<string> ValueOptions =
        new HashSet<string>(DeployValueOptions.Concat(DeleteValueOptions), StringComparer.OrdinalIgnoreCase);

    /// <summary>いずれかのサブコマンドの true/false のフラグ。</summary>
    public static readonly IReadOnlySet<string> BoolFlags =
        new HashSet<string>(DeployBoolFlags.Concat(DeleteBoolFlags), StringComparer.OrdinalIgnoreCase);

    public const string HelpText = """
        使い方:
          ttdeploytool-cli [deploy] [オプション]  Timetracker を Azure Container Apps にデプロイ（--dry-run でローカル用のファイルを生成）
          ttdeploytool-cli delete [オプション]    deploy で作成したリソースを削除

        deploy の必須:
          --subscription <ID>         Azure サブスクリプション ID（--dry-run 時は不要）
          --resource-group <名前>     リソースグループ名（存在しなければ作成。--dry-run 時は不要）
          --db-password <値>          DB パスワード
          --tracker-password <値>     Timetracker アプリパスワード

        deploy の任意:
          --location <リージョン>     既定: japaneast
          --app-name <名前>           既定: timetracker
          --db-type <種別>            postgresql | sqlserver（既定: postgresql）
          --db-name <名前>            既定: timetracker
          --tt-tag <タグ>             timetracker イメージタグ（既定: postgresql→11.0.1-linux-postgres / sqlserver→11.0.1-linux-mssql）
          --auth-mode <モード>        default | azure-cli | sp-env | device-code | managed-identity（既定: default）
          --tt-cpu / --tt-memory      Timetracker の vCPU / メモリ(Gi)（既定: 1.5 / 3.0）
          --db-cpu / --db-memory      DB の vCPU / メモリ(Gi)（既定: 1.0 / 2.0）
          --redis-cpu / --redis-memory Redis の vCPU / メモリ(Gi)（既定: 0.5 / 1.0）
          --persist-db [true|false]   DB のデータを Azure Files に置き、DB コンテナの再起動の後も残す（ストレージの料金がかかる）。
                                      省略時は既存の DB アプリの今の状態を引き継ぐ（新規は永続化しない）。やめるには false を指定
          --expires-on <yyyy-MM-dd|none> 利用可能期限日（期限日の終わり＝この PC の時刻帯の 23:59:59）。none は期限なし。
                                      省略時は既存のデプロイの今の期限を引き継ぎ、無ければ今日から 32 日後。期限を過ぎると、
                                      TTDeployTool のデスクトップアプリが自動で削除する（DB のデータも消える。CLI は削除しない）
          --group <名前>              グループ名（任意。用途・利用者ごとに分ける。Azure のタグ tt-group に記録。64 文字まで）。
                                      省略時は既存のデプロイの今のグループを引き継ぐ（新規はグループなし）
          --dry-run [true|false]      Azure へ反映せず docker-compose.yml と .env を生成
          --verbose [true|false]      詳細ログを出力
          -h, --help                  このヘルプを表示

        delete のオプション:
          --subscription <ID>         Azure サブスクリプション ID（必須）
          --resource-group <名前>     リソースグループ名（必須。--delete-resource-group を付けなければ、リソースグループ自体は削除しません）
          --app-name <名前>           既定: timetracker
          --auth-mode <モード>        deploy と同じ
          --include-untagged [true|false] 管理タグの無いリソースも削除の対象にする
          --keep-db-data [true|false] --persist-db で DB のデータを置いたストレージアカウントを残す（既定では削除する）
          --delete-resource-group [true|false] 削除した後にリソースグループが空になる場合、リソースグループも削除する
                                      （ほかのリソースが残る場合は、何も削除せずに終了する。元に戻せません）
          --dry-run [true|false]      削除計画を表示するだけで、削除しない
          --yes [true|false]          確認の入力（アプリ名）を省略する
          --verbose [true|false]      詳細ログを出力

        値が "--" で始まる場合は --option=value 形式で指定してください。
        """;

    private readonly Dictionary<string, string> _values;

    public bool IsHelp { get; }

    public CliCommand Command { get; }

    private CliArgs(Dictionary<string, string> values, bool isHelp, CliCommand command)
    {
        _values = values;
        IsHelp = isHelp;
        Command = command;
    }

    /// <exception cref="ArgumentException">未知のオプション・値の不足・不正な値の場合</exception>
    public static CliArgs Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var isHelp = false;
        var command = CliCommand.Deploy;

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
                // サブコマンドは先頭のみ（省略時は deploy）
                if (i == 0 && Commands.TryGetValue(a, out command))
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

            var (valueOptions, boolFlags) = command == CliCommand.Delete
                ? (DeleteValueOptions, DeleteBoolFlags)
                : (DeployValueOptions, DeployBoolFlags);

            if (boolFlags.Contains(name))
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

            if (!valueOptions.Contains(name))
            {
                if (ValueOptions.Contains(name) || BoolFlags.Contains(name))
                    throw new ArgumentException(
                        $"オプション '{name}' は {command.ToString().ToLowerInvariant()} では使えません。--help で使い方を確認してください。");
                throw new ArgumentException($"不明なオプションです: '{name}'。--help で使い方を確認してください。");
            }

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

        return new CliArgs(values, isHelp, command);
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

    /// <summary>true/false のフラグを取得する。指定が無い場合は null。</summary>
    public bool? GetBoolOrNull(string name) => _values.TryGetValue(name, out var v) ? v == "true" : null;

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
