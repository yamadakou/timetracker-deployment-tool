using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.ResourceManager;

namespace TTDeployTool.Infrastructure.Azure;

/// <summary>
/// IPv6 と IPv4 のアドレスを、少しずつずらして並行して試し、先につながったものを使う（Happy Eyeballs。RFC 8305 の考え方）。
/// .NET の HttpClient は DNS が返したアドレスを 1 つずつ試すため、IPv6 の通信が応答なく捨てられる環境では、アドレスごとに
/// TCP の接続の時間切れ（Windows では約 21 秒）まで待つ。実機の確認で、Azure の管理 API に 21 秒、サインインの設定情報の取得に
/// 100 秒以上かかっていた（docs/REVIEW_ISSUES.md の No.54）。ブラウザーと WAM はこの方式でつなぐため、速い。
/// CLI・MCP サーバー・デスクトップアプリの Azure SDK（管理 API と Azure.Identity）の通信で使う。
/// </summary>
public static class HappyEyeballs
{
    /// <summary>前の接続を待たずに、次のアドレスを試し始めるまでの時間（RFC 8305 の推奨は 250 ms）。</summary>
    public static readonly TimeSpan AttemptDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>アドレスの種類（IPv6・IPv4）が交互になるように並べる（最初の種類は DNS の順のまま）。</summary>
    public static IReadOnlyList<IPAddress> Interleave(IReadOnlyList<IPAddress> addresses)
    {
        if (addresses.Count == 0)
            return addresses;
        var first = addresses.Where(a => a.AddressFamily == addresses[0].AddressFamily).ToList();
        var second = addresses.Where(a => a.AddressFamily != addresses[0].AddressFamily).ToList();
        var result = new List<IPAddress>(addresses.Count);
        for (var i = 0; i < Math.Max(first.Count, second.Count); i++)
        {
            if (i < first.Count)
                result.Add(first[i]);
            if (i < second.Count)
                result.Add(second[i]);
        }
        return result;
    }

    /// <summary>
    /// アドレスを順に、<see cref="AttemptDelay"/> ずつずらして（前の接続が失敗したらすぐに）試し、最初につながったものを返す。
    /// 残りの接続は取り消し、後からつながったものは閉じる。すべて失敗したら、失敗の理由を投げる。
    /// </summary>
    public static async Task<T> ConnectAsync<T>(
        IReadOnlyList<IPAddress> addresses, Func<IPAddress, CancellationToken, Task<T>> connect, TimeProvider time,
        CancellationToken cancellationToken) where T : IDisposable
    {
        if (addresses.Count == 0)
            throw new SocketException((int)SocketError.HostNotFound);
        var ordered = Interleave(addresses);
        using var attempts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = new List<Task<T>>();
        var errors = new List<Exception>();
        var next = 0;
        // 繰り返すのは、待つ時間が過ぎたか、接続が失敗したときなので、毎回、次のアドレスを試し始める
        while (pending.Count > 0 || next < ordered.Count)
        {
            if (next < ordered.Count)
                pending.Add(connect(ordered[next++], attempts.Token));
            var waiting = new List<Task>(pending);
            var delay = next < ordered.Count ? Task.Delay(AttemptDelay, time, attempts.Token) : null;
            if (delay != null)
                waiting.Add(delay);
            var done = await Task.WhenAny(waiting);
            if (done == delay)
                continue;
            var attempt = (Task<T>)done;
            pending.Remove(attempt);
            if (attempt.IsCompletedSuccessfully)
            {
                await attempts.CancelAsync();
                foreach (var late in pending)
                    _ = CloseWhenConnected(late);
                return attempt.Result;
            }
            errors.Add(attempt.Exception?.InnerException ?? new OperationCanceledException());
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (errors.Count == 1)
            ExceptionDispatchInfo.Throw(errors[0]);
        throw new AggregateException("どのアドレスにも接続できませんでした。", errors);
    }

    /// <summary>
    /// HttpClient（SocketsHttpHandler.ConnectCallback）で使う接続。名前を解決し、<see cref="ConnectAsync{T}"/> でつなぐ。
    /// </summary>
    public static async ValueTask<Stream> ConnectCallback(DnsEndPoint endPoint, TimeProvider time, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(endPoint.Host, cancellationToken);
        var socket = await ConnectAsync(addresses, async (address, token) =>
        {
            var s = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await s.ConnectAsync(new IPEndPoint(address, endPoint.Port), token);
                return s;
            }
            catch
            {
                s.Dispose();
                throw;
            }
        }, time, cancellationToken);
        return new NetworkStream(socket, ownsSocket: true);
    }

    /// <summary>IPv6 と IPv4 を並行して試して接続する HttpClient の処理。</summary>
    public static SocketsHttpHandler Handler { get; } = new()
    {
        ConnectCallback = (context, token) => ConnectCallback(context.DnsEndPoint, TimeProvider.System, token),
    };

    /// <summary>Azure SDK（管理 API と Azure.Identity）に渡す通信（アプリの中で 1 つを使い回す）。</summary>
    public static HttpClientTransport Transport { get; } = new(Handler);

    /// <summary>この通信を使う、Azure の管理 API の接続。</summary>
    public static ArmClient Arm(TokenCredential credential) => new(credential, default, new ArmClientOptions { Transport = Transport });

    private static async Task CloseWhenConnected<T>(Task<T> attempt) where T : IDisposable
    {
        try
        {
            (await attempt).Dispose();
        }
        catch (Exception)
        {
            // 取り消した・失敗した接続は、閉じるものが無い
        }
    }
}
