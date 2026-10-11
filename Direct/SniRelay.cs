using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MistSail.Direct;

// Loopback TLS pass-through for the hosts that the hosts file points at RelayListenAddress.
// The browser keeps end-to-end TLS with the real server (real certificate, its own fingerprint,
// native HTTP/2 and WebSocket). This relay only picks the target IP from the SNI and re-frames
// the first ClientHello into fragments; every later byte is copied unchanged.
internal sealed class SniRelay : IDisposable
{
    internal const string LogFile = "relay.log";
    private const int MaxRecordLength = 18437;

    private readonly DirectOptions options;
    private readonly int targetPort;
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<int, TcpClient> connections = new();
    private readonly SemaphoreSlim slots;
    private readonly DohResolver resolver;
    private int nextId;
    private int disposed;
    // Recent failures per target IP: candidates are ordered by this, so an IP that connects but
    // then dies (drop/EOF/RST) stops winning the connect race and the fallbacks get their turn.
    private readonly ConcurrentDictionary<IPAddress, (int Failures, DateTime Last)> penalties = new();

    internal IPEndPoint EndPoint { get; private set; }
    internal event Action<string>? Status;
    // Latest transport evidence per SNI host, e.g. "收到 ServerHello (162.159.140.229, 180 ms)".
    internal ConcurrentDictionary<string, string> LastResultByHost { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal SniRelay(DirectOptions options, int listenPort = 443, int targetPort = 443)
    {
        this.options = options;
        this.targetPort = targetPort;
        EndPoint = new IPEndPoint(IPAddress.Parse(options.RelayListenAddress), listenPort);
        listener = new TcpListener(EndPoint);
        slots = new SemaphoreSlim(options.MaxConnections);
        resolver = new DohResolver(options, stop.Token);
    }

    internal void Start()
    {
        listener.Start();
        EndPoint = (IPEndPoint)listener.LocalEndpoint;
        _ = AcceptLoopAsync();
        Report($"X 分片中继 {EndPoint} 已启动；握手模式 {options.FragmentMode}，分片 {options.FragmentBytes} 字节，间隔 {options.FragmentDelayMs} ms，{options.RelayHosts.Count} 个域名");
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                if (stop.IsCancellationRequested || !await slots.WaitAsync(0))
                {
                    client.Dispose();
                    continue;
                }
                client.NoDelay = true;
                int id = Interlocked.Increment(ref nextId);
                connections[id] = client;
                _ = HandleAsync(id, client);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            if (!stop.IsCancellationRequested) Report($"中继监听失败：{ex.GetType().Name}；请断开后重新连接。");
        }
    }

    private async Task HandleAsync(int id, TcpClient browser)
    {
        string host = "unknown";
        IPAddress? target = null;
        bool recorded = false;
        var watch = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        try
        {
            var incoming = browser.GetStream();
            byte[] hello = await ReadClientHelloAsync(incoming, deadline.Token);
            if (!FragmentingStream.TryFindServerName(hello, out int nameOffset, out int nameLength))
                throw new IOException("首个 TLS 记录中没有 SNI，无法选择目标。");
            host = Encoding.ASCII.GetString(hello, nameOffset, nameLength).TrimEnd('.').ToLowerInvariant();

            IPAddress[] addresses = await ResolveTargetsAsync(host, deadline.Token);
            TcpClient remote;
            try { remote = await DohResolver.ConnectAsync(addresses, targetPort, options.ConnectTimeoutSeconds, deadline.Token); }
            catch (IOException)
            {
                recorded = Record(host, null, $"TCP 连接失败，候选 IP 均不可达：{string.Join(",", addresses.Select(a => a.ToString()))}", watch);
                throw;
            }
            using (remote)
            {
                target = (remote.Client.RemoteEndPoint as IPEndPoint)?.Address;
                var outgoing = remote.GetStream();
                using var fragmented = new FragmentingStream(outgoing, options, options.ShouldFragment(host), leaveOpen: true);
                await fragmented.WriteAsync(hello, deadline.Token);
                await fragmented.FlushPendingAsync(deadline.Token);

                // The first server bytes decide the evidence class for the user's log.
                byte[] buffer = new byte[65536];
                int count;
                try { count = await outgoing.ReadAsync(buffer, deadline.Token); }
                catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset })
                {
                    recorded = Record(host, target, "握手后被对端重置 (RST)；疑似 SNI 封锁，当前分片参数未生效", watch);
                    throw;
                }
                if (count == 0)
                {
                    recorded = Record(host, target, "握手后被对端关闭 (EOF)；疑似 SNI 封锁或服务器拒绝该 SNI", watch);
                    return;
                }
                recorded = Record(host, target, buffer[0] switch
                {
                    0x16 => "收到 ServerHello",
                    0x15 => "服务器返回 TLS alert",
                    _ => $"收到非 TLS 响应 0x{buffer[0]:X2}"
                }, watch);
                await incoming.WriteAsync(buffer.AsMemory(0, count), deadline.Token);

                // Long-lived streams (video, WebSocket) must not hit the setup deadline.
                deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                using var session = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                var upload = PumpAsync(incoming, outgoing, remote, session.Token);
                var download = PumpAsync(outgoing, incoming, browser, session.Token);
                var firstDone = await Task.WhenAny(upload, download);
                if (firstDone == upload && upload.IsCompletedSuccessfully)
                {
                    // Preserve half-close: the server may still send after the browser's EOF.
                    session.CancelAfter(TimeSpan.FromSeconds(30));
                    await download;
                }
                else
                {
                    session.Cancel();
                    browser.Dispose();
                    remote.Dispose();
                }
                try { await Task.WhenAll(upload, download); }
                catch (OperationCanceledException) when (session.IsCancellationRequested) { }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or ArgumentException or InvalidOperationException)
        {
            if (!stop.IsCancellationRequested && !recorded)
                Record(host, target, $"连接未完成：{Describe(ex)}", watch);
        }
        finally
        {
            browser.Dispose();
            connections.TryRemove(id, out _);
            slots.Release();
        }
    }

    private async Task<IPAddress[]> ResolveTargetsAsync(string host, CancellationToken token)
    {
        IPAddress[] addresses = TryGetConfiguredAddresses(host, out string[] configured) && configured.Length > 0 ?
            configured.Select(IPAddress.Parse).ToArray() :
            // No fixed address: best-effort DoH (honours HostOverrides; never the system resolver).
            await resolver.ResolveAsync(host, token);
        DateTime horizon = DateTime.UtcNow.AddMinutes(-3);
        return addresses
            .Select((address, index) => (address, index, score: penalties.TryGetValue(address, out var p) && p.Last > horizon ? p.Failures : 0))
            .OrderBy(entry => entry.score).ThenBy(entry => entry.index)
            .Select(entry => entry.address).ToArray();
    }

    private void Penalize(IPAddress? target, bool failed)
    {
        if (target is null) return;
        if (!failed) { penalties.TryRemove(target, out _); return; }
        penalties.AddOrUpdate(target, _ => (1, DateTime.UtcNow), (_, old) => (Math.Min(old.Failures + 1, 50), DateTime.UtcNow));
    }

    internal bool TryGetConfiguredAddresses(string host, out string[] addresses)
    {
        if (options.RelayHosts.TryGetValue(host, out addresses!))
            return true;
        foreach (var pair in options.RelayHosts)
        {
            if (!pair.Key.StartsWith('*')) continue;
            string suffix = pair.Key.TrimStart('*', '.');
            if (host.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
            {
                addresses = pair.Value;
                return true;
            }
        }
        addresses = [];
        return false;
    }

    // Reads exactly one TLS record (header + body). Anything that is not a handshake record is rejected.
    private static async Task<byte[]> ReadClientHelloAsync(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[5];
        await ReadExactlyAsync(stream, header, token);
        int length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(3, 2));
        if (header[0] != 22 || header[1] != 3 || length is < 39 or > MaxRecordLength - 5)
            throw new IOException("首个数据不是 TLS 握手记录。");
        byte[] record = new byte[5 + length];
        header.CopyTo(record, 0);
        await ReadExactlyAsync(stream, record.AsMemory(5), token);
        return record;
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], token);
            if (read == 0) throw new IOException("浏览器在握手前关闭了连接。");
            offset += read;
        }
    }

    private static async Task PumpAsync(Stream source, Stream destination, TcpClient peer, CancellationToken token)
    {
        await source.CopyToAsync(destination, 65536, token);
        try { peer.Client.Shutdown(SocketShutdown.Send); } catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
    }

    private static string Describe(Exception ex) => ex switch
    {
        OperationCanceledException => "超时",
        IOException { InnerException: SocketException inner } => $"{inner.SocketErrorCode}",
        SocketException socket => $"{socket.SocketErrorCode}",
        _ => ex.Message
    };

    private bool Record(string host, IPAddress? target, string result, Stopwatch watch)
    {
        Penalize(target, !result.StartsWith("收到 ServerHello", StringComparison.Ordinal));
        string ip = target?.ToString() ?? "-";
        LastResultByHost[host] = $"{result}（{ip}，{watch.ElapsedMilliseconds} ms）";
        Report($"host={host} ip={ip} {result} {watch.ElapsedMilliseconds}ms");
        return true;
    }

    private void Report(string text)
    {
        // Domain, IP, phase and timing only. Never URLs, headers, cookies or payload.
        DirectLog.Write(LogFile, text);
        Status?.Invoke(text);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel();
        listener.Stop();
        foreach (var client in connections.Values) client.Dispose();
        resolver.Dispose();
        DirectLog.Write(LogFile, "X 分片中继已停止。");
    }
}
