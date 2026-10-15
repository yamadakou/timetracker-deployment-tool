using Timetracker.Infrastructure.Azure;

namespace Timetracker.Controller.Mcp;

/// <summary>
/// サーバーの設定（MCP クライアントの設定で渡す環境変数 TT_MCP_*）。
/// 認証モードやパスワードはツールの引数にしない（LLM が会話の途中で切り替えたり、値が会話に残ったりしないように）。
/// </summary>
public sealed record McpServerSettings
{
    public const string DefaultAuthMode = "azure-cli";

    public string AuthMode { get; init; } = DefaultAuthMode;
    public string? DefaultSubscription { get; init; }
    public string? DefaultResourceGroup { get; init; }
    public string? DbPassword { get; init; }
    public string? TrackerPassword { get; init; }

    /// <summary>generate_compose の出力先を、この配下（絶対パス）に限る。null なら制限しない。</summary>
    public string? OutputRoot { get; init; }

    public bool Verbose { get; init; }

    /// <summary>環境変数から読む。空文字は未設定として扱う。</summary>
    /// <exception cref="ArgumentException">値が不正な場合（起動時にエラーにする）</exception>
    public static McpServerSettings FromEnvironment(Func<string, string?> getEnvironmentVariable)
    {
        string? Get(string name) => getEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

        var authMode = Get("TT_MCP_AUTH_MODE") ?? DefaultAuthMode;
        if (!CredentialFactory.AuthModes.Contains(authMode.ToLowerInvariant()))
            throw new ArgumentException(
                $"TT_MCP_AUTH_MODE '{authMode}' は無効です。有効な値は {string.Join(", ", CredentialFactory.AuthModes)} です。");

        var outputRoot = Get("TT_MCP_OUTPUT_ROOT");
        if (outputRoot != null && !Path.IsPathFullyQualified(outputRoot))
            throw new ArgumentException($"TT_MCP_OUTPUT_ROOT には絶対パスを指定してください: {outputRoot}");

        return new McpServerSettings
        {
            AuthMode = authMode.ToLowerInvariant(),
            DefaultSubscription = Get("TT_MCP_DEFAULT_SUBSCRIPTION"),
            DefaultResourceGroup = Get("TT_MCP_DEFAULT_RESOURCE_GROUP"),
            DbPassword = Get("TT_MCP_DB_PASSWORD"),
            TrackerPassword = Get("TT_MCP_TRACKER_PASSWORD"),
            OutputRoot = outputRoot == null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRoot)),
            Verbose = Get("TT_MCP_VERBOSE") is "true" or "1",
        };
    }
}
