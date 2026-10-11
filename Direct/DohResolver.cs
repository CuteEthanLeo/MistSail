using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Sheas_Cealer.Direct;

internal sealed class DohResolver : IDisposable
{
    private sealed record Answer(IPAddress[] Addresses, DateTimeOffset Expires);
    private readonly DirectOptions options;
    private readonly CancellationToken lifetime;
    private readonly HttpClient[] clients;
    private readonly ConcurrentDictionary<string, Answer> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<Answer>>> inflight = new(StringComparer.OrdinalIgnoreCase);

    internal DohResolver(DirectOptions options, CancellationToken lifetime)
    {
        this.options = options;
        this.lifetime = lifetime;
        clients = options.DohServers.Select(server =>
        {
            var handler = new SocketsHttpHandler
            {
                UseProxy = false, UseCookies = false, AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(options.ConnectTimeoutSeconds),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5), MaxConnectionsPerServer = 16,
                ConnectCallback = async (_, token) =>
                {
                    // Bootstrap without the machine's potentially poisoned DNS or system proxy.
                    var tcp = await ConnectAsync(server.BootstrapAddresses.Select(IPAddress.Parse).ToArray(), 443, options.ConnectTimeoutSeconds, token);
                    return new FragmentingStream(tcp.GetStream(), options,
                        options.ShouldFragment(new Uri(server.Url).Host));
                }
            };
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(options.DnsTimeoutSeconds) };
        }).ToArray();
    }

    internal async Task<IPAddress[]> ResolveAsync(string host, CancellationToken token)
    {
        if (IPAddress.TryParse(host, out var literal))
            return [literal];
        host = new IdnMapping().GetAscii(host.TrimEnd('.')).ToLowerInvariant();
        if (options.HostOverrides.TryGetValue(host, out var configured))
            return configured.Select(IPAddress.Parse).ToArray();
        if (cache.TryGetValue(host, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
            return cached.Addresses;
        var lazy = inflight.GetOrAdd(host, key => new Lazy<Task<Answer>>(() => QueryHostAsync(key), LazyThreadSafetyMode.ExecutionAndPublication));
        var task = lazy.Value;
        _ = task.ContinueWith(_ => inflight.TryRemove(new KeyValuePair<string, Lazy<Task<Answer>>>(host, lazy)),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return (await task.WaitAsync(token)).Addresses;
    }

    private async Task<Answer> QueryHostAsync(string host)
    {
        var ipv4 = QueryTypeOrEmptyAsync(host, 1);
        var ipv6 = options.EnableIPv6 ? QueryTypeOrEmptyAsync(host, 28) : Task.FromResult((Array.Empty<IPAddress>(), 300u));
        var results = await Task.WhenAll(ipv4, ipv6);
        var addresses = results.SelectMany(r => r.Item1).Distinct().ToArray();
        if (addresses.Length == 0)
            throw new IOException($"DoH 未取得 {host} 的可用地址；未回退到系统 DNS。");
        uint ttl = results.Where(r => r.Item1.Length > 0).Min(r => r.Item2);
        var answer = new Answer(addresses, DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(ttl, 1u, 600u)));
        if (cache.Count > 4096) cache.Clear();
        cache[host] = answer;
        return answer;
    }

    private async Task<(IPAddress[], uint)> QueryTypeOrEmptyAsync(string host, ushort type)
    {
        try { return await QueryTypeAsync(host, type); }
        catch (IOException ex)
        {
            DirectLog.Write($"DNS 解析失败 {host} type={type}：{ex.Message}");
            return ([], 30);
        }
    }

    private async Task<(IPAddress[], uint)> QueryTypeAsync(string host, ushort type)
    {
        using var race = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        var tasks = clients.Select((client, index) => QueryServerAsync(client, options.DohServers[index].Url, host, type, race.Token)).ToList();
        (IPAddress[], uint)? empty = null;
        var errors = new List<string>();
        while (tasks.Count > 0)
        {
            var completed = await Task.WhenAny(tasks);
            tasks.Remove(completed);
            try
            {
                var result = await completed;
                if (result.Item1.Length > 0)
                {
                    race.Cancel();
                    // Observe the losing tasks so exceptions never become unobserved.
                    foreach (var remaining in tasks) _ = ObserveAsync(remaining);
                    return result;
                }
                empty = result;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or SocketException)
            {
                errors.Add(ex.GetType().Name);
            }
        }
        lifetime.ThrowIfCancellationRequested();
        if (empty.HasValue) return empty.Value;
        throw new IOException($"全部 DoH 请求失败 ({string.Join(",", errors.Distinct())})。");
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task; } catch (Exception) { }
    }

    private static async Task<(IPAddress[], uint)> QueryServerAsync(HttpClient client, string endpoint, string host, ushort type, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(client.Timeout);
        token = timeout.Token;
        byte[] query = BuildQuery(host, type);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Version = HttpVersion.Version11,
            Content = new ByteArrayContent(query)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType != "application/dns-message")
            throw new IOException("DoH 返回了非 DNS 内容。");
        await using var content = await response.Content.ReadAsStreamAsync(token);
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[4096];
        int read;
        while ((read = await content.ReadAsync(buffer, token)) > 0)
        {
            if (bytes.Length + read > 65535) throw new IOException("DoH 响应过大。");
            bytes.Write(buffer, 0, read);
        }
        return ParseAnswer(bytes.ToArray(), query, host, type);
    }

    internal static byte[] BuildQuery(string host, ushort type)
    {
        using var stream = new MemoryStream();
        byte[] header = new byte[12];
        RandomNumberGenerator.Fill(header.AsSpan(0, 2));
        header[2] = 1; // Recursion desired.
        header[5] = 1;
        stream.Write(header);
        foreach (string label in host.Split('.'))
        {
            byte[] encoded = Encoding.ASCII.GetBytes(label);
            if (encoded.Length is < 1 or > 63) throw new IOException("DNS 域名标签无效。");
            stream.WriteByte((byte)encoded.Length);
            stream.Write(encoded);
        }
        stream.WriteByte(0);
        stream.WriteByte((byte)(type >> 8));
        stream.WriteByte((byte)type);
        stream.WriteByte(0);
        stream.WriteByte(1);
        if (stream.Length > 271) throw new IOException("DNS 域名过长。");
        return stream.ToArray();
    }

    internal static (IPAddress[], uint) ParseAnswer(byte[] data, byte[] query, string host, ushort type)
    {
        if (data.Length < 12 || data[0] != query[0] || data[1] != query[1] || (data[2] & 0x80) == 0 ||
            (data[2] & 0x7a) != 0 || (data[3] & 15) is not (0 or 3) || BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4, 2)) != 1)
            throw new IOException("DNS 响应头无效。");
        int position = 12;
        string question = ReadName(data, ref position);
        if (!question.Equals(host, StringComparison.OrdinalIgnoreCase) || position + 4 > data.Length ||
            BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(position, 2)) != type ||
            BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(position + 2, 2)) != 1)
            throw new IOException("DNS 响应问题不匹配。");
        position += 4;
        int count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(6, 2));
        var records = new List<(string Owner, ushort Type, uint Ttl, IPAddress? Address, string? Alias)>();
        for (int i = 0; i < count; i++)
        {
            string owner = ReadName(data, ref position);
            if (position + 10 > data.Length) throw new IOException("DNS 记录被截断。");
            ushort recordType = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(position, 2));
            ushort recordClass = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(position + 2, 2));
            uint ttl = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position + 4, 4));
            int length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(position + 8, 2));
            position += 10;
            if (position + length > data.Length) throw new IOException("DNS 内容被截断。");
            IPAddress? address = null;
            string? alias = null;
            if (recordClass == 1)
            {
                if ((recordType == 1 && length == 4) || (recordType == 28 && length == 16))
                    address = new IPAddress(data.AsSpan(position, length));
                else if (recordType == 5)
                {
                    int aliasPosition = position;
                    alias = ReadName(data, ref aliasPosition);
                    if (aliasPosition != position + length) throw new IOException("DNS CNAME 长度无效。");
                }
                records.Add((owner, recordType, ttl, address, alias));
            }
            position += length;
        }
        if ((data[3] & 15) == 3) return ([], 30);
        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { host };
        uint minimumTtl = 600;
        for (int hop = 0; hop < 16; hop++)
        {
            bool added = false;
            foreach (var record in records)
                if (record.Alias is not null && reachable.Contains(record.Owner))
                {
                    added |= reachable.Add(record.Alias);
                    minimumTtl = Math.Min(minimumTtl, record.Ttl);
                }
            if (!added) break;
        }
        var matches = records.Where(record => record.Type == type && record.Address is not null && reachable.Contains(record.Owner)).ToArray();
        foreach (var match in matches) minimumTtl = Math.Min(minimumTtl, match.Ttl);
        return (matches.Select(record => record.Address!).Distinct().ToArray(), minimumTtl);
    }

    private static string ReadName(byte[] data, ref int position)
    {
        int cursor = position;
        bool jumped = false;
        var labels = new List<string>();
        for (int hops = 0; hops < 128; hops++)
        {
            if (cursor >= data.Length) throw new IOException("DNS 名称被截断。");
            int size = data[cursor++];
            if (size == 0)
            {
                if (!jumped) position = cursor;
                return string.Join('.', labels);
            }
            if ((size & 0xc0) == 0xc0)
            {
                if (cursor >= data.Length) throw new IOException("DNS 指针被截断。");
                int pointer = ((size & 63) << 8) | data[cursor++];
                if (!jumped) position = cursor;
                jumped = true;
                cursor = pointer;
            }
            else
            {
                if (size > 63 || cursor + size > data.Length) throw new IOException("DNS 标签无效。");
                labels.Add(Encoding.ASCII.GetString(data, cursor, size));
                if (labels.Sum(label => label.Length + 1) > 254) throw new IOException("DNS 名称过长。");
                cursor += size;
            }
        }
        throw new IOException("DNS 压缩指针循环。");
    }

    internal static async Task<TcpClient> ConnectAsync(IPAddress[] addresses, int port, int timeoutSeconds, CancellationToken token)
    {
        using var race = CancellationTokenSource.CreateLinkedTokenSource(token);
        var tasks = addresses.Distinct().Take(16).Select((address, index) => AttemptAsync(address, index, race.Token)).ToList();
        while (tasks.Count > 0)
        {
            var completed = await Task.WhenAny(tasks);
            tasks.Remove(completed);
            try
            {
                var connected = await completed;
                race.Cancel();
                foreach (var task in tasks) _ = CloseLoserAsync(task);
                return connected;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException) { }
        }
        token.ThrowIfCancellationRequested();
        throw new IOException("所有目标 IP 均无法建立 TCP 连接；可能存在 IP 封锁或路由故障。");

        async Task<TcpClient> AttemptAsync(IPAddress address, int index, CancellationToken cancellation)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            var client = new TcpClient(address.AddressFamily) { NoDelay = true };
            try
            {
                if (index > 0) await Task.Delay(index * 200, timeout.Token);
                await client.ConnectAsync(address, port, timeout.Token);
                return client;
            }
            catch { client.Dispose(); throw; }
        }
    }

    private static async Task CloseLoserAsync(Task<TcpClient> task)
    {
        try { (await task).Dispose(); } catch (Exception) { }
    }

    public void Dispose()
    {
        foreach (var client in clients) client.Dispose();
    }
}
