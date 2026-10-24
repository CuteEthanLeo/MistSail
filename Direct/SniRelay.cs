using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MistSail.Direct;

internal enum RelayEvidence { ServerHello, Alert, NonTls, Rst, Eof, Timeout, TcpFail, Other }

// Loopback TLS pass-through for the hosts that the hosts file points at RelayListenAddress.
// The browser keeps end-to-end TLS with the real server (real certificate, its own fingerprint,
// native HTTP/2 and WebSocket). This relay only picks the target IP from the SNI and re-frames
// the first ClientHello according to a HelloStrategy; every later byte is copied unchanged.
// While the browser has not received a single byte, a failed target (RST, EOF, silence) is
// retried on another IP or with the fallback strategy inside the same browser connection.
internal sealed class SniRelay : IDisposable
{
    internal const string LogFile = "relay.log";
    private const int MaxRecordLength = 18437;
    private const int MaxAttempts = 3;
    // One browser connection must never spend the whole page timeout on silent targets: three
    // attempts of HandshakeTimeoutSeconds each stay well below the old 24-40 s hang.
    private static readonly TimeSpan AttemptBudget = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FailureRefreshThrottle = TimeSpan.FromMinutes(5);
    private const int MaxRefreshedAddresses = 32;
    internal static readonly TimeSpan UnreachableLifetime = TimeSpan.FromHours(12);
    private static readonly TimeSpan TimeoutPenalty = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StrategyFailureHorizon = TimeSpan.FromMinutes(3);

    private readonly DirectOptions options;
    private readonly int targetPort;
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<int, TcpClient> connections = new();
    private readonly SemaphoreSlim slots;
    private readonly DohResolver resolver;
    private readonly bool ipv6Available;
    private int nextId;
    private int disposed;

    // Targets that swallow every ClientHello (blackholed IPs): excluded from the connect race while
    // any other candidate exists. Finder results are long-lived; a relay timeout is a short penalty.
    private readonly ConcurrentDictionary<IPAddress, DateTime> unreachable = new();
    // Targets the finder confirmed with a ServerHello: tried first.
    private readonly ConcurrentDictionary<IPAddress, DateTime> good = new();
    // (IP, strategy) pairs that were reset or closed after the ClientHello: a strategy problem on
    // that path, not a dead IP. Short horizon, never persisted.
    private readonly ConcurrentDictionary<(IPAddress Ip, string Strategy), (int Failures, DateTime Last)> strategyFailures = new();
    // Candidates learned from DoH after every configured address for a host failed. Kept per host,
    // bounded, and refreshed on demand; never written back into direct-config.json.
    private readonly ConcurrentDictionary<string, IPAddress[]> refreshedTargets = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> refreshQueued = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (int Count, DateTime Last)> hostFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private volatile HelloStrategy strategy;
    private readonly ConcurrentDictionary<string, HelloStrategy> hostStrategies = new(StringComparer.OrdinalIgnoreCase);

    // Set by MainWin when a WinDivert desync strategy is armed: the wire layer then controls framing,
    // so the relay sends each ClientHello as one plain record and does not shape it in user space.
    internal volatile bool ForcePlain;

    // The desync strategy in force (set by MainWin after the finder), carried into the saved state.
    internal string? DesyncName { get; set; }

    internal IPEndPoint EndPoint { get; private set; }
    internal event Action<string>? Status;
    // Fired after a whole browser connection failed all candidates. MainWin debounces this and runs
    // the line finder, so a user who opens the browser before the connectivity check still gets a
    // diagnosis instead of repeated failures.
    internal event Action<string, int>? HostFailure;
    // Latest transport evidence per SNI host, e.g. "收到 ServerHello [record-misalign]（104.16.123.96，230 ms）".
    internal ConcurrentDictionary<string, string> LastResultByHost { get; } = new(StringComparer.OrdinalIgnoreCase);
    // The browser's own ClientHello per host, so the line finder can probe with the real layout.
    internal ConcurrentDictionary<string, byte[]> LastClientHelloByHost { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal SniRelay(DirectOptions options, int listenPort = 443, int targetPort = 443, DirectState? state = null, bool forcePlain = false)
    {
        this.options = options;
        this.targetPort = targetPort;
        ForcePlain = forcePlain;
        DesyncName = state?.Desync;
        EndPoint = new IPEndPoint(IPAddress.Parse(options.RelayListenAddress), listenPort);
        listener = new TcpListener(EndPoint);
        slots = new SemaphoreSlim(options.MaxConnections);
        resolver = new DohResolver(options, stop.Token);
        ipv6Available = options.EnableIPv6 && DirectState.HasGlobalIpv6();

        HelloStrategy? configured = options.ConfiguredStrategy;
        strategy = configured ?? (state?.Strategy is not null && HelloStrategy.TryParse(state.Strategy, out var learned) ? options.WithGap(learned) : options.WithGap(HelloStrategy.Default));
        if (state is not null)
        {
            if (configured is null)
                foreach (var pair in state.HostStrategies)
                    if (HelloStrategy.TryParse(pair.Value, out var hostStrategy))
                        hostStrategies[pair.Key] = options.WithGap(hostStrategy);
            foreach (var pair in state.DeadIps)
                if (IPAddress.TryParse(pair.Key, out var ip) && pair.Value > DateTime.UtcNow)
                    unreachable[ip] = pair.Value;
            foreach (string text in state.GoodIps)
                if (IPAddress.TryParse(text, out var ip))
                    good[ip] = DateTime.UtcNow;
        }
    }

    internal HelloStrategy CurrentStrategy => strategy;
    internal bool IsPinnedByConfig => options.ConfiguredStrategy is not null;

    internal HelloStrategy StrategyFor(string host) =>
        ForcePlain ? HelloStrategy.Plain : hostStrategies.TryGetValue(host, out var specific) ? specific : strategy;

    // Hot-swap: connections already in flight keep the strategy they captured.
    internal void ApplyStrategy(HelloStrategy value, string? host = null)
    {
        HelloStrategy applied = options.WithGap(value);
        if (host is null)
        {
            strategy = applied;
            hostStrategies.Clear();
            Report($"中继切换为策略 {applied.Name}（{applied.Summary}）");
        }
        else
            hostStrategies[host] = applied;
    }

    internal void MarkUnreachable(IPAddress ip, DateTime until)
    {
        unreachable[ip] = until;
        good.TryRemove(ip, out _);
    }

    internal void MarkGood(IPAddress ip)
    {
        good[ip] = DateTime.UtcNow;
        unreachable.TryRemove(ip, out _);
    }

    internal IPAddress[] DynamicCandidatesFor(string host) =>
        refreshedTargets.TryGetValue(host, out IPAddress[]? addresses) ? addresses : [];

    // Refreshes the candidate pool from DoH after the configured addresses were exhausted, and on
    // demand from the line finder. force bypasses the resolver cache; failures are logged, never
    // thrown, because a blocked DoH endpoint must not turn a page load into a crash.
    internal async Task<int> RefreshCandidatesAsync(IEnumerable<string> hosts, CancellationToken token)
    {
        if (!options.RefreshCandidatesFromDoh)
            return 0;
        string[] requested = hosts
            .Where(host => !string.IsNullOrWhiteSpace(host) && !host.Contains('*') && !IPAddress.TryParse(host, out _))
            .Select(host => host.TrimEnd('.').ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
        if (requested.Length == 0)
            return 0;
        if (!await refreshGate.WaitAsync(0, token))
            return 0;
        try
        {
            int added = 0;
            int failed = 0;
            foreach (string host in requested)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    IPAddress[] resolved = await resolver.ResolveAsync(host, token, force: true);
                    int count = MergeRefreshed(host, resolved.Where(IsUsableAddress).ToArray());
                    if (count > 0)
                    {
                        added += count;
                        Report($"候选 IP 已刷新 {host} +{count}（{string.Join(",", refreshedTargets[host].Select(ip => ip.ToString()))}）");
                    }
                    else
                        DirectLog.Write(LogFile, $"候选 IP 刷新 {host}：DoH 未给出新地址");
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ArgumentException or InvalidOperationException)
                {
                    failed++;
                    DirectLog.Write(LogFile, $"候选 IP 刷新失败 {host}：{Describe(ex)}");
                }
            }
            if (failed > 0 && added == 0)
                Report($"候选 IP 刷新完成：{failed} 个域名未取得新地址");
            return added;
        }
        finally { refreshGate.Release(); }
    }

    private int MergeRefreshed(string host, IPAddress[] addresses)
    {
        if (addresses.Length == 0)
            return 0;
        IPAddress[] old = refreshedTargets.TryGetValue(host, out IPAddress[]? current) ? current : [];
        // Prefer the freshly resolved edges so a full pool can still rotate away from a dead range.
        IPAddress[] merged = addresses.Concat(old).Distinct().Take(MaxRefreshedAddresses).ToArray();
        refreshedTargets[host] = merged;
        return Math.Min(addresses.Except(old).Count(), merged.Length);
    }

    // Fire-and-forget refresh for one failed host; throttled so a dead line cannot turn into a DoH
    // query storm. The current browser connection is never blocked on it.
    private void QueueRefresh(string host)
    {
        if (!options.RefreshCandidatesFromDoh || string.IsNullOrWhiteSpace(host) || host == "unknown" || host.Contains('*'))
            return;
        DateTime now = DateTime.UtcNow;
        if (refreshQueued.TryGetValue(host, out DateTime last) && last > now - FailureRefreshThrottle)
            return;
        refreshQueued[host] = now;
        _ = Task.Run(async () =>
        {
            try
            {
                using var refresh = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                refresh.CancelAfter(TimeSpan.FromSeconds(options.DnsTimeoutSeconds));
                await RefreshCandidatesAsync([host], refresh.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { DirectLog.Write(LogFile, $"候选 IP 刷新任务失败 {host}：{ex.GetType().Name}"); }
        });
    }

    // Three failed browser connections within two minutes are enough evidence that the current
    // candidates/strategy are wrong; MainWin then runs the finder once (with its own debounce).
    private void NoteHostResult(string host, bool success, int attempts)
    {
        if (success)
        {
            hostFailures.TryRemove(host, out _);
            return;
        }
        DateTime now = DateTime.UtcNow;
        var entry = hostFailures.AddOrUpdate(host, _ => (1, now),
            (_, old) => old.Last > now - TimeSpan.FromMinutes(2) ? (old.Count + 1, now) : (1, now));
        QueueRefresh(host);
        if (entry.Count < 3)
            return;
        hostFailures.TryRemove(host, out _);
        try { HostFailure?.Invoke(host, attempts); }
        catch (Exception ex) { DirectLog.Write(LogFile, $"HostFailure 处理失败：{ex.GetType().Name}"); }
    }

    private bool IsUsableAddress(IPAddress address) =>
        ipv6Available || address.AddressFamily != AddressFamily.InterNetworkV6;

    internal DirectState Snapshot()
    {
        DateTime now = DateTime.UtcNow;
        return new DirectState
        {
            Strategy = strategy.Name,
            Desync = DesyncName,
            HostStrategies = hostStrategies.ToDictionary(p => p.Key, p => p.Value.Name, StringComparer.OrdinalIgnoreCase),
            GoodIps = good.Keys.Select(ip => ip.ToString()).OrderBy(s => s, StringComparer.Ordinal).ToArray(),
            DeadIps = unreachable.Where(p => p.Value > now).ToDictionary(p => p.Key.ToString(), p => p.Value),
            NetworkKey = DirectState.CurrentNetworkKey(),
            SavedAt = now
        };
    }

    internal void Start()
    {
        listener.Start();
        EndPoint = (IPEndPoint)listener.LocalEndpoint;
        _ = AcceptLoopAsync();
        Report($"X 分片中继 {EndPoint} 已启动；策略 {strategy.Name}（{strategy.Summary}），{options.RelayHosts.Count} 个域名，IPv6 {(ipv6Available ? "可用" : "不可用")}");
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
        bool recorded = false;
        var watch = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        TcpClient? remote = null;
        try
        {
            var incoming = browser.GetStream();
            // Exactly one record: 0-RTT data that follows stays in `incoming` for the pump, so a
            // retry replays nothing but the ClientHello.
            byte[] hello = await ReadClientHelloAsync(incoming, deadline.Token);
            if (!FragmentingStream.TryFindServerName(hello, out int nameOffset, out int nameLength))
                throw new IOException("首个 TLS 记录中没有 SNI，无法选择目标。");
            host = Encoding.ASCII.GetString(hello, nameOffset, nameLength).TrimEnd('.').ToLowerInvariant();
            LastClientHelloByHost[host] = hello;

            IPAddress[] addresses = await ResolveTargetsAsync(host, deadline.Token);
            bool shape = options.ShouldFragment(host) && !ForcePlain;
            HelloStrategy primary = shape ? StrategyFor(host) : HelloStrategy.Plain;
            HelloStrategy fallback = shape ? Fallback(primary) : HelloStrategy.Plain;

            byte[] buffer = new byte[65536];
            int count = 0;
            NetworkStream? outgoing = null;
            var tried = new HashSet<IPAddress>();
            IPAddress? best = null;
            int attemptsMade = 0;
            // A hard wall-clock budget for the whole candidate race. It bounds a slow connect plus
            // handshake too, so one browser connection cannot spend 15 s in two long attempts and
            // another 10 s in the third.
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            budget.CancelAfter(AttemptBudget);
            for (int attempt = 0; attempt < MaxAttempts && remote is null && !budget.IsCancellationRequested; attempt++)
            {
                HelloStrategy current = attempt < 2 || fallback == primary ? primary : fallback;
                IPAddress[] candidates;
                if (attempt < 2 || fallback == primary)
                {
                    candidates = Order(addresses.Where(a => !tried.Contains(a)), current.Name);
                    if (candidates.Length == 0) candidates = Order(addresses, current.Name);
                }
                else if (best is not null && !unreachable.ContainsKey(best))
                {
                    // The first target answered with RST/EOF/an alert: retry that same edge with the
                    // backup layout before spending the last attempt on a different address.
                    candidates = new[] { best }.Concat(Order(addresses.Where(a => !a.Equals(best)), current.Name)).ToArray();
                }
                else
                    candidates = Order(addresses, current.Name);
                if (candidates.Length == 0) break;
                attemptsMade = attempt + 1;

                var connectWatch = Stopwatch.StartNew();
                TcpClient candidate;
                try { candidate = await DohResolver.ConnectAsync(candidates, targetPort, options.ConnectTimeoutSeconds, budget.Token); }
                catch (IOException)
                {
                    recorded = Record(host, null, RelayEvidence.TcpFail, current, $"TCP 连接失败，候选 IP 均不可达：{string.Join(",", candidates.Select(a => a.ToString()))}", watch, 0);
                    break;
                }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                {
                    recorded = Record(host, null, RelayEvidence.Timeout, current, "候选连接超时（总预算耗尽）", watch, 0);
                    break;
                }
                long connectMs = connectWatch.ElapsedMilliseconds;
                IPAddress target = ((IPEndPoint)candidate.Client.RemoteEndPoint!).Address;
                tried.Add(target);
                best ??= target;

                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                attemptCts.CancelAfter(TimeSpan.FromSeconds(options.HandshakeTimeoutSeconds));
                var phase = Stopwatch.StartNew();
                try
                {
                    var stream = candidate.GetStream();
                    await FragmentingStream.WriteShapedAsync(stream, hello, current, attemptCts.Token);
                    count = await stream.ReadAsync(buffer, attemptCts.Token);
                    if (count == 0)
                        recorded = Record(host, target, RelayEvidence.Eof, current, $"握手后被对端关闭 (EOF) {phase.ElapsedMilliseconds} ms", watch, connectMs);
                    else
                    {
                        remote = candidate;
                        outgoing = stream;
                        recorded = Record(host, target, buffer[0] switch { 0x16 => RelayEvidence.ServerHello, 0x15 => RelayEvidence.Alert, _ => RelayEvidence.NonTls }, current,
                            buffer[0] switch { 0x16 => "收到 ServerHello", 0x15 => "服务器返回 TLS alert", _ => $"收到非 TLS 响应 0x{buffer[0]:X2}" }, watch, connectMs);
                    }
                }
                catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset })
                {
                    long ms = phase.ElapsedMilliseconds;
                    recorded = Record(host, target, RelayEvidence.Rst, current, $"握手后被重置 (RST) {ms} ms{(ms < connectMs ? "，快于连接 RTT ⇒ 疑似注入" : "")}", watch, connectMs);
                }
                catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut })
                {
                    recorded = Record(host, target, RelayEvidence.Timeout, current, "TCP 重传超时（发出的数据未被确认，疑似被丢包）", watch, connectMs);
                }
                catch (OperationCanceledException) when (attemptCts.IsCancellationRequested && !deadline.IsCancellationRequested)
                {
                    recorded = Record(host, target, RelayEvidence.Timeout, current, $"发出 ClientHello 后 {options.HandshakeTimeoutSeconds} s 无任何响应", watch, connectMs);
                }
                if (remote is null) candidate.Dispose();
            }
            if (remote is null || outgoing is null)
            {
                NoteHostResult(host, false, Math.Max(attemptsMade, 1));
                Report($"host={host} 候选全部失败：{Math.Max(attemptsMade, 1)} 次尝试，{watch.ElapsedMilliseconds} ms");
                return;
            }
            NoteHostResult(host, true, attemptsMade);

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
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or ArgumentException or InvalidOperationException)
        {
            if (!stop.IsCancellationRequested && !recorded)
                Record(host, null, RelayEvidence.Other, null, $"连接未完成：{Describe(ex)}", watch, 0);
        }
        finally
        {
            remote?.Dispose();
            browser.Dispose();
            connections.TryRemove(id, out _);
            slots.Release();
        }
    }

    private HelloStrategy Fallback(HelloStrategy primary)
    {
        HelloStrategy? next = HelloStrategy.All.FirstOrDefault(s => s.Name != primary.Name && s.Fragments && !s.CloudflareOnly && s.GapMs < HelloStrategy.LongPauseMs);
        return next is null ? primary : options.WithGap(next);
    }

    private async Task<IPAddress[]> ResolveTargetsAsync(string host, CancellationToken token)
    {
        IPAddress[] pinned = TryGetConfiguredAddresses(host, out string[] configured) && configured.Length > 0
            ? configured.Select(IPAddress.Parse).ToArray() : [];
        IPAddress[] refreshed = refreshedTargets.TryGetValue(host, out IPAddress[]? learned) ? learned : [];
        IPAddress[] addresses = pinned.Length > 0 || refreshed.Length > 0
            ? pinned.Concat(refreshed).Distinct().ToArray()
            // No fixed address: best-effort DoH (honours HostOverrides; never the system resolver).
            : await resolver.ResolveAsync(host, token);
        return addresses.Distinct().Where(IsUsableAddress).ToArray();
    }

    // Candidates for one attempt: blackholed IPs are left out while anything else exists; confirmed
    // IPs go first; then the fewest recent failures for this strategy; ties are random so one
    // bad round cannot pin the order back to the configured list.
    private IPAddress[] Order(IEnumerable<IPAddress> addresses, string strategyName)
    {
        DateTime now = DateTime.UtcNow;
        IPAddress[] all = addresses.ToArray();
        IPAddress[] alive = all.Where(a => !unreachable.TryGetValue(a, out var until) || until <= now).ToArray();
        if (alive.Length == 0) alive = all;
        return alive
            .Select(a => (address: a,
                          preferred: good.ContainsKey(a) ? 0 : 1,
                          failures: strategyFailures.TryGetValue((a, strategyName), out var f) && f.Last > now - StrategyFailureHorizon ? f.Failures : 0,
                          tie: Random.Shared.Next()))
            .OrderBy(e => e.preferred).ThenBy(e => e.failures).ThenBy(e => e.tie)
            .Select(e => e.address).ToArray();
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

    private bool Record(string host, IPAddress? target, RelayEvidence evidence, HelloStrategy? strategy, string result, Stopwatch watch, long connectMs)
    {
        DateTime now = DateTime.UtcNow;
        if (target is not null)
            switch (evidence)
            {
                case RelayEvidence.ServerHello:
                    strategyFailures.TryRemove((target, strategy?.Name ?? string.Empty), out _);
                    unreachable.TryRemove(target, out _);
                    break;
                case RelayEvidence.Timeout:
                {
                    // A short penalty: the finder decides whether the IP is dead for good. An IP the
                    // finder confirmed gets an even shorter one, since silence there is more likely a
                    // dropped ClientHello than a dead address.
                    DateTime until = now + (good.ContainsKey(target) ? TimeSpan.FromMinutes(1) : TimeoutPenalty);
                    unreachable.AddOrUpdate(target, until, (_, old) => old > until ? old : until);
                    break;
                }
                case RelayEvidence.Rst:
                case RelayEvidence.Eof:
                case RelayEvidence.Alert:
                case RelayEvidence.NonTls:
                    strategyFailures.AddOrUpdate((target, strategy?.Name ?? string.Empty), _ => (1, now), (_, old) => (Math.Min(old.Failures + 1, 50), now));
                    break;
            }
        string ip = target?.ToString() ?? "-";
        string tag = strategy is null ? string.Empty : $" [{strategy.Name}]";
        string rtt = connectMs > 0 ? $"，连接 {connectMs} ms" : string.Empty;
        LastResultByHost[host] = $"{result}{tag}（{ip}，{watch.ElapsedMilliseconds} ms{rtt}）";
        Report($"host={host} ip={ip}{tag} {result} {watch.ElapsedMilliseconds}ms{rtt}");
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
