using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Xunit;

namespace Timetracker.Controller.Mcp.Tests.Http;

/// <summary>
/// ビルドした timetracker-mcp を --transport http で子プロセスとして起動し、Program から HTTP のサーバーが動くこと
/// （起動引数が設定に渡ること、実際の Kestrel で待ち受けること）を確かめる。
/// </summary>
public sealed class HttpProcessTests
{
    private static readonly string ServerDll = Path.Combine(AppContext.BaseDirectory, "timetracker-mcp.dll");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ProcessStartInfo Start(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet", [ServerDll, .. args]) { RedirectStandardOutput = true, RedirectStandardError = true };
        // テストを実行している環境の設定を引き継がない
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("TT_MCP_") || k.StartsWith("ASPNETCORE_")).ToList())
            start.Environment.Remove(name);
        return start;
    }

    [Fact]
    public void Server_Should_Exit_With_An_Error_When_The_Http_Settings_Are_Missing()
    {
        using var process = Process.Start(Start("--transport", "http"))!;

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(30_000).Should().BeTrue();

        process.ExitCode.Should().Be(1);
        stderr.Should().Contain("[ERROR] HTTP で起動するには TT_MCP_HTTP__TENANT_ID が必要です。");
        stdout.Should().BeEmpty();
    }

    [Fact]
    public async Task Server_Should_Listen_Over_Http_When_Started_With_The_Transport_Argument()
    {
        var url = $"http://127.0.0.1:{FreePort()}";
        var start = Start("--transport", "http", "--urls", url);
        start.Environment["TT_MCP_HTTP__TENANT_ID"] = HttpTestSupport.TenantId;
        start.Environment["TT_MCP_HTTP__CLIENT_ID"] = HttpTestSupport.ClientId;
        start.Environment["TT_MCP_HTTP__PUBLIC_URL"] = HttpTestSupport.PublicUrl;
        start.Environment["TT_MCP_HTTP__SERVER_ALLOWED_RESOURCE_GROUPS"] = HttpTestSupport.AllowedGroup;
        using var process = Process.Start(start)!;
        // 出力を読み続けないと、バッファーが埋まってサーバーが止まる
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var http = new HttpClient { BaseAddress = new Uri(url) };
        try
        {
            (await WaitForHealthAsync(http)).Should().Be(HttpStatusCode.OK);

            var unauthorized = await http.PostAsync(McpServerHost.McpPath, new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), Ct);
            unauthorized.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            unauthorized.Headers.WwwAuthenticate.ToString().Should().Contain($"resource_metadata=\"{url}/.well-known/oauth-protected-resource\"");
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(Ct);
        }
    }

    private static async Task<HttpStatusCode?> WaitForHealthAsync(HttpClient http)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                return (await http.GetAsync(McpServerHost.HealthPath, Ct)).StatusCode;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(200, Ct); // まだ起動中
            }
        }
        return null;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
