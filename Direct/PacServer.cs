using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MistSail.Direct;

// A tiny loopback PAC server. While a global connection is up, Windows Internet Settings points
// AutoConfigURL here, so only the configured dynamic hosts (rr*.googlevideo.com and friends) go
// through the loopback CONNECT relay; every other URL stays DIRECT and keeps using hosts + nginx.
// No DNS, certificate or TUN change is involved, and the script is served over 127.0.0.1 only.
internal sealed class PacServer : IDisposable
{
    private const int MaxRequestBytes = 8192;
    private readonly TcpListener listener;
    private readonly string script;
    private readonly string[] hosts;
    private readonly CancellationTokenSource stop = new();
    private int disposed;

    internal int Port { get; private set; }
    internal string PacUrl => $"http://127.0.0.1:{Port}/mistsail.pac";
    internal event Action<string>? Status;

    internal PacServer(IEnumerable<string> suffixes, int proxyPort)
    {
        hosts = suffixes.Select(DirectOptions.NormalizeDynamicHost)
            .Where(IsValidHost)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToArray();
        script = BuildScript(proxyPort, hosts);
        listener = new TcpListener(IPAddress.Loopback, 0);
    }

    internal void Start()
    {
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync();
        Status?.Invoke($"动态视频 PAC 已启动 {PacUrl}；仅代理 {string.Join("、", hosts)}");
    }

    internal static string BuildScript(int proxyPort, IEnumerable<string> suffixes)
    {
        if (proxyPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(proxyPort));
        string[] domains = suffixes.Select(DirectOptions.NormalizeDynamicHost)
            .Where(IsValidHost)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToArray();
        var builder = new StringBuilder();
        builder.AppendLine("// MistSail dynamic video PAC: only the listed hosts use the loopback CONNECT relay.");
        builder.AppendLine("function FindProxyForURL(url, host) {");
        builder.AppendLine("  host = host.toLowerCase();");
        builder.AppendLine("  if (host.charAt(host.length - 1) === \".\") host = host.slice(0, -1);");
        builder.AppendLine("  if (host === \"localhost\" || host === \"127.0.0.1\" || host === \"::1\") return \"DIRECT\";");
        if (domains.Length == 0)
        {
            builder.AppendLine("  return \"DIRECT\";");
        }
        else
        {
            builder.AppendLine("  var DIRECT_HOSTS = [");
            foreach (string domain in domains)
                builder.AppendLine($"    \"{domain}\",");
            builder.AppendLine("  ];");
            builder.AppendLine("  for (var i = 0; i < DIRECT_HOSTS.length; i++) {");
            builder.AppendLine("    var d = DIRECT_HOSTS[i];");
            builder.AppendLine($"    if (host === d || dnsDomainIs(host, \".\" + d)) return \"PROXY 127.0.0.1:{proxyPort}; DIRECT\";");
            builder.AppendLine("  }");
            builder.AppendLine("  return \"DIRECT\";");
        }
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static bool IsValidHost(string host)
    {
        if (host.Length is < 1 or > 253)
            return false;
        string[] labels = host.Split('.');
        return labels.Length >= 2 && labels.All(label =>
            label.Length is >= 1 and <= 63 && label[0] != '-' && label[^1] != '-' &&
            label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(stop.Token);
                _ = HandleAsync(client);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            client.NoDelay = true;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                NetworkStream stream = client.GetStream();
                string request = await ReadRequestAsync(stream, timeout.Token);
                bool found = request.StartsWith("GET /mistsail.pac ", StringComparison.Ordinal);
                byte[] body = found ? Encoding.UTF8.GetBytes(script) : "Not Found"u8.ToArray();
                string header = found
                    ? $"HTTP/1.1 200 OK\r\nContent-Type: application/x-ns-proxy-autoconfig\r\nCache-Control: no-store\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"
                    : $"HTTP/1.1 404 Not Found\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), timeout.Token);
                await stream.WriteAsync(body, timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    private static async Task<string> ReadRequestAsync(Stream stream, CancellationToken token)
    {
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[512];
        while (bytes.Length < MaxRequestBytes)
        {
            int read = await stream.ReadAsync(buffer, token);
            if (read == 0) break;
            bytes.Write(buffer, 0, read);
            string text = Encoding.ASCII.GetString(bytes.ToArray());
            int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0) return text[..end];
            int bare = text.IndexOf("\n\n", StringComparison.Ordinal);
            if (bare >= 0) return text[..bare];
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel();
        listener.Stop();
        Status?.Invoke("动态视频 PAC 已停止");
    }
}
