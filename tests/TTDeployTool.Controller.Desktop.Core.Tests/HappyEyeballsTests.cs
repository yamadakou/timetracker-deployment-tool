using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using TTDeployTool.Controller.Desktop.Core.Services;
using Xunit;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>IPv6 と IPv4 を並行して試す接続（docs/REVIEW_ISSUES.md の No.54）。</summary>
public class HappyEyeballsTests
{
    private static readonly IPAddress V6a = IPAddress.Parse("2001:db8::1");
    private static readonly IPAddress V6b = IPAddress.Parse("2001:db8::2");
    private static readonly IPAddress V4a = IPAddress.Parse("192.0.2.1");
    private static readonly IPAddress V4b = IPAddress.Parse("192.0.2.2");

    private readonly FakeTimeProvider _time = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Connection(IPAddress address) : IDisposable
    {
        public IPAddress Address { get; } = address;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    /// <summary>アドレスごとの接続を、テストが終わらせる。</summary>
    private sealed class Attempts
    {
        public Dictionary<IPAddress, TaskCompletionSource<Connection>> Started { get; } = [];
        public Dictionary<IPAddress, CancellationToken> Tokens { get; } = [];

        public Task<Connection> Connect(IPAddress address, CancellationToken token)
        {
            var attempt = new TaskCompletionSource<Connection>(TaskCreationOptions.RunContinuationsAsynchronously);
            Started[address] = attempt;
            Tokens[address] = token;
            return attempt.Task;
        }

        public void Succeed(IPAddress address) => Started[address].SetResult(new Connection(address));

        public void Fail(IPAddress address) => Started[address].SetException(new SocketException((int)SocketError.ConnectionRefused));
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(5, Ct);
        condition().Should().BeTrue();
    }

    [Fact]
    public void Addresses_Should_Alternate_Between_Families_Starting_With_The_First()
    {
        HappyEyeballs.Interleave([V6a, V6b, V4a]).Should().Equal(V6a, V4a, V6b);
        HappyEyeballs.Interleave([V4a, V6a, V4b]).Should().Equal(V4a, V6a, V4b);
        HappyEyeballs.Interleave([V4a]).Should().Equal(V4a);
        HappyEyeballs.Interleave([]).Should().BeEmpty();
    }

    [Fact]
    public async Task A_Fast_First_Address_Should_Be_Used_Alone()
    {
        var attempts = new Attempts();
        var connecting = HappyEyeballs.ConnectAsync([V6a, V4a], attempts.Connect, _time, Ct);
        attempts.Succeed(V6a);

        (await connecting).Address.Should().Be(V6a);
        attempts.Started.Keys.Should().Equal(V6a);
    }

    [Fact]
    public async Task A_Silent_IPv6_Address_Should_Not_Delay_IPv4()
    {
        // IPv6 が応答なく捨てられる（つながりも失敗もしない）
        var attempts = new Attempts();
        var connecting = HappyEyeballs.ConnectAsync([V6a, V6b, V4a], attempts.Connect, _time, Ct);
        await Until(() => attempts.Started.Count == 1);

        _time.Advance(HappyEyeballs.AttemptDelay);
        await Until(() => attempts.Started.ContainsKey(V4a));
        attempts.Succeed(V4a);
        var connection = await connecting;

        connection.Address.Should().Be(V4a);
        attempts.Tokens[V6a].IsCancellationRequested.Should().BeTrue("残りの接続は取り消す");
        attempts.Started.Keys.Should().NotContain(V6b);
        // 取り消した後につながったものは閉じる
        attempts.Succeed(V6a);
        await Until(() => attempts.Started[V6a].Task.Result.Disposed);
    }

    [Fact]
    public async Task A_Failed_Address_Should_Start_The_Next_At_Once_And_Late_Failures_Should_Be_Ignored()
    {
        var attempts = new Attempts();
        var connecting = HappyEyeballs.ConnectAsync([V6a, V4a, V6b], attempts.Connect, _time, Ct);
        await Until(() => attempts.Started.Count == 1);

        attempts.Fail(V6a);
        await Until(() => attempts.Started.ContainsKey(V4a));
        _time.Advance(HappyEyeballs.AttemptDelay);
        await Until(() => attempts.Started.ContainsKey(V6b));
        attempts.Succeed(V6b);

        (await connecting).Address.Should().Be(V6b);
        attempts.Fail(V4a);
    }

    [Fact]
    public async Task A_Slow_Address_Should_Still_Win_When_The_Others_Fail()
    {
        var attempts = new Attempts();
        var connecting = HappyEyeballs.ConnectAsync([V6a, V4a], attempts.Connect, _time, Ct);
        await Until(() => attempts.Started.Count == 1);
        _time.Advance(HappyEyeballs.AttemptDelay);
        await Until(() => attempts.Started.ContainsKey(V4a));

        // すべて始めた後に 1 つが失敗しても、まだ待っている接続を待つ
        attempts.Fail(V4a);
        await Task.Delay(20, Ct);
        attempts.Succeed(V6a);

        (await connecting).Address.Should().Be(V6a);
    }

    [Fact]
    public async Task Failures_Should_Be_Reported_When_No_Address_Connects()
    {
        var one = new Attempts();
        var single = HappyEyeballs.ConnectAsync([V4a], one.Connect, _time, Ct);
        one.Fail(V4a);
        await single.Invoking(t => t).Should().ThrowAsync<SocketException>();

        // 取り消された接続も失敗として数える
        var two = HappyEyeballs.ConnectAsync<Connection>([V6a, V4a],
            (address, _) => address.Equals(V6a)
                ? Task.FromCanceled<Connection>(new CancellationToken(true))
                : Task.FromException<Connection>(new SocketException((int)SocketError.HostUnreachable)),
            _time, Ct);
        (await two.Invoking(t => t).Should().ThrowAsync<AggregateException>()).Which.InnerExceptions.Should()
            .SatisfyRespectively(e => e.Should().BeAssignableTo<OperationCanceledException>(), e => e.Should().BeOfType<SocketException>());

        var none = () => HappyEyeballs.ConnectAsync<Connection>([], (_, _) => throw new InvalidOperationException(), _time, Ct);
        await none.Should().ThrowAsync<SocketException>();
    }

    [Fact]
    public async Task Canceling_Should_Stop_Connecting()
    {
        using var cancel = new CancellationTokenSource();
        var connecting = HappyEyeballs.ConnectAsync<Connection>([V4a],
            (_, token) => Task.Delay(Timeout.Infinite, token).ContinueWith<Connection>(_ => throw new OperationCanceledException(token), TaskScheduler.Default),
            _time, cancel.Token);

        await cancel.CancelAsync();

        await connecting.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task The_Handler_Should_Connect_To_A_Real_Server_And_Report_Refused_Connections()
    {
        // 1 回だけ HTTP の応答を返すサーバー（IPv4 のループバックだけで待つ。localhost の ::1 は拒否され、すぐに IPv4 を試す）
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serving = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(Ct);
            var stream = client.GetStream();
            var buffer = new byte[4096];
            _ = await stream.ReadAsync(buffer, Ct);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"), Ct);
        }, Ct);
        using var http = new HttpClient(HappyEyeballs.Handler, disposeHandler: false);

        (await http.GetStringAsync($"http://localhost:{port}/", Ct)).Should().Be("ok");
        await serving;
        HappyEyeballs.Transport.Should().NotBeNull();

        listener.Stop();
        var refused = async () => await HappyEyeballs.ConnectCallback(new DnsEndPoint("127.0.0.1", port), _time, Ct);
        await refused.Should().ThrowAsync<SocketException>();
    }
}
