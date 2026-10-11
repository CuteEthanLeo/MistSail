using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MistSail.Direct;

// A loopback HTTP CONNECT transport, with direct TCP egress. No remote proxy,
// TLS interception, certificate installation, system proxy changes or TUN route.
internal sealed class DirectRelay : IDisposable
{
    private readonly DirectOptions options;
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<int, TcpClient> connections = new();
    private readonly SemaphoreSlim slots;
    private readonly DohResolver resolver;
    private int nextId;
    private int disposed;
    internal int Port { get; private set; }
    internal event Action<string>? Status;

    internal DirectRelay(DirectOptions options)
    {
        this.options = options;
        listener = new TcpListener(IPAddress.Loopback, options.ListenPort);
        slots = new SemaphoreSlim(options.MaxConnections);
        resolver = new DohResolver(options, stop.Token);
    }

    internal void Start()
    {
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync();
        Report($"本机转发器 127.0.0.1:{Port} 已启动，出口为本机网络；握手模式 {options.FragmentMode}");
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
            if (!stop.IsCancellationRequested) Report($"监听失败：{ex.GetType().Name}；请停止后重新启动。");
        }
    }

    private async Task HandleAsync(int id, TcpClient browser)
    {
        string host = "unknown";
        bool tunnelEstablished = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        try
        {
            var incoming = browser.GetStream();
            string header = await ReadHeaderAsync(incoming, deadline.Token);
            string[] lines = header.Split("\r\n", StringSplitOptions.None);
            string[] first = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (first.Length != 3 || first[2] is not ("HTTP/1.0" or "HTTP/1.1"))
                throw new IOException("HTTP 请求格式无效。");
            bool connect = first[0] == "CONNECT";
            if (!Uri.TryCreate(connect ? "https://" + first[1] : first[1], UriKind.Absolute, out var destination) ||
                (!connect && destination.Scheme != "http") || !string.IsNullOrEmpty(destination.UserInfo) ||
                string.IsNullOrWhiteSpace(destination.Host) || destination.Port is < 1 or > 65535 ||
                (connect && (destination.AbsolutePath != "/" || destination.Query.Length > 0 || destination.Fragment.Length > 0)))
                throw new IOException("目标地址无效。");
            host = destination.IdnHost.Trim('[', ']');
            IPAddress[] addresses = await resolver.ResolveAsync(host, deadline.Token);
            using var remote = await DohResolver.ConnectAsync(addresses, destination.Port, options.ConnectTimeoutSeconds, deadline.Token);
            var outgoing = remote.GetStream();
            if (connect)
            {
                await incoming.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), deadline.Token);
                tunnelEstablished = true;
            }
            else
            {
                // One plain HTTP request per TCP connection. Keep payload bytes intact.
                var forwarded = new StringBuilder($"{first[0]} {destination.PathAndQuery} {first[2]}\r\n");
                foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
                    if (!line.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase) &&
                        !line.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase))
                        forwarded.Append(line).Append("\r\n");
                forwarded.Append("Connection: close\r\n\r\n");
                await outgoing.WriteAsync(Encoding.ASCII.GetBytes(forwarded.ToString()), deadline.Token);
                tunnelEstablished = true;
            }
            Report($"TCP 已连接 {host}:{destination.Port} -> {remote.Client.RemoteEndPoint}；等待目标响应");
            // Allow long video streams. Cancellation of the relay closes both directions.
            deadline.CancelAfter(Timeout.InfiniteTimeSpan);
            using var session = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            using var fragmented = new FragmentingStream(outgoing, options, connect && options.ShouldFragment(host), leaveOpen: true);
            var upload = UploadAsync(incoming, fragmented, remote, session.Token);
            var download = DownloadAsync(outgoing, incoming, host, session.Token);
            var firstDone = await Task.WhenAny(upload, download);
            if (firstDone == upload && upload.IsCompletedSuccessfully)
            {
                // Preserve half-close: a server can still send data after client EOF.
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
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or ArgumentException)
        {
            if (!stop.IsCancellationRequested)
            {
                Report($"连接未完成 {host}：{ex.Message}");
                if (!tunnelEstablished)
                    try
                    {
                        using var replyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                        await browser.GetStream().WriteAsync("HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), replyTimeout.Token);
                    }
                    catch (Exception replyEx) when (replyEx is IOException or SocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException) { }
            }
        }
        finally
        {
            browser.Dispose();
            connections.TryRemove(id, out _);
            slots.Release();
        }
    }

    private static async Task UploadAsync(Stream source, FragmentingStream destination, TcpClient remote, CancellationToken token)
    {
        await source.CopyToAsync(destination, 65536, token);
        await destination.FlushPendingAsync(token);
        try { remote.Client.Shutdown(SocketShutdown.Send); } catch (SocketException) { }
    }

    private async Task DownloadAsync(Stream source, Stream destination, string host, CancellationToken token)
    {
        byte[] buffer = new byte[65536];
        int count = await source.ReadAsync(buffer, token);
        if (count == 0) return;
        // Server bytes are transport evidence only; this does not assert TLS or site success.
        Report($"收到目标响应 {host}；TLS 和页面是否成功需在浏览器中确认");
        await destination.WriteAsync(buffer.AsMemory(0, count), token);
        await source.CopyToAsync(destination, 65536, token);
    }

    private static async Task<string> ReadHeaderAsync(Stream stream, CancellationToken token)
    {
        using var bytes = new MemoryStream();
        byte[] one = new byte[1];
        int suffix = 0;
        while (bytes.Length < 16384)
        {
            if (await stream.ReadAsync(one, token) == 0) throw new IOException("客户端关闭连接。");
            bytes.WriteByte(one[0]);
            suffix = (suffix << 8) | one[0];
            if (suffix == 0x0d0a0d0a) return Encoding.ASCII.GetString(bytes.ToArray());
        }
        throw new IOException("HTTP 请求头过大。");
    }

    private void Report(string text)
    {
        DirectLog.Write(text);
        Status?.Invoke(text);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel();
        listener.Stop();
        foreach (var client in connections.Values) client.Dispose();
        resolver.Dispose();
        DirectLog.Write("本机转发器已停止。");
        // Do not dispose stop/slots until pending tasks have released them.
    }
}
