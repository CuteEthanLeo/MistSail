using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace MistSail.Direct;

// The line finder. Phase 1 finds edge IPs that still answer a ClientHello at all (control SNI).
// Phase 2 sends the blocked SNI with each HelloStrategy on those IPs and keeps the first layout
// that gets a ServerHello twice (on another IP, with a differently laid out ClientHello). Only
// handshake openings are sent; nothing is completed and no page is requested.
internal static class LineProbe
{
    internal enum ProbeResult { ServerHello, Alert, Rst, Eof, Timeout, TcpFail, Other }

    internal sealed record Step(string Name, IPAddress Ip, string Sni, HelloStrategy? Strategy);

    internal sealed record Outcome(Step Step, ProbeResult Result, string Text, long Milliseconds, long ConnectMs)
    {
        internal bool Ok => Result == ProbeResult.ServerHello;
        internal bool Reachable => Result is ProbeResult.ServerHello or ProbeResult.Alert;
        // A reset that arrives in less than one connect RTT after the ClientHello cannot come from the server.
        internal bool Injected => Result == ProbeResult.Rst && ConnectMs > 0 && Milliseconds - ConnectMs < ConnectMs;
    }

    internal sealed record Report(bool Valid, string Text, HelloStrategy? Winner, IPAddress? WinnerIp, HelloStrategy? FastlyWinner,
        IPAddress[] Reachable, IPAddress[] Dead, IPAddress[] Good, List<Outcome> Outcomes, DesyncStrategy? DesyncWinner = null, IPAddress? DesyncWinnerIp = null);

    internal static readonly string[] GenericCloudflare = ["104.16.123.96", "104.16.124.96", "104.18.32.47", "104.17.209.9"];
    internal static readonly string[] GenericFastly = ["151.101.0.159", "151.101.64.159", "151.101.128.159", "151.101.192.159"];
    private const string CloudflareControlSni = "www.cloudflare.com";
    private const string FastlyControlSni = "www.python.org";
    private static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan StrategyTimeout = TimeSpan.FromSeconds(8);

    // helloSource: the browser's own ClientHello for a host when the relay has seen one.
    // onDead: called as soon as an IP proves blackholed so the live relay stops using it.
    internal static async Task<Report> RunAsync(DirectOptions options, Func<string, byte[]?> helloSource, Action<string> report,
        Action<IPAddress, DateTime>? onDead, WinDivertDesync? desync, string? desyncError, CancellationToken token,
        Func<string, IReadOnlyCollection<IPAddress>>? extraCandidates = null)
    {
        var outcomes = new List<Outcome>();
        DesyncStrategy? desyncWinner = null;
        IPAddress? desyncWinnerIp = null;
        void Log(Outcome o)
        {
            outcomes.Add(o);
            report($"诊断 {o.Step.Name} [{o.Step.Ip} sni={o.Step.Sni} strategy={o.Step.Strategy?.Name ?? "-"}] -> {o.Text}{(o.Injected ? "（快于 RTT，注入）" : string.Empty)} {o.Milliseconds}ms（连接 {o.ConnectMs}ms）");
        }
        Report Finish(string text, HelloStrategy? winner, IPAddress? winnerIp, HelloStrategy? fastlyWinner, IEnumerable<IPAddress> reachable, IEnumerable<IPAddress> dead, IEnumerable<IPAddress> goodIps, bool valid = true)
        {
            report("诊断结论：" + text);
            return new Report(valid, text, winner, winnerIp, fastlyWinner, reachable.ToArray(), dead.ToArray(), goodIps.ToArray(), outcomes, desyncWinner, desyncWinnerIp);
        }

        string[] tun = DirectState.TunAdapters();
        if (tun.Length > 0)
            return Finish($"检测到 TUN/代理网卡 {string.Join("、", tun)}：流量会被它接管，本轮诊断无效；请先关闭 Clash 等的 TUN 模式再重试。", null, null, null, [], [], [], valid: false);

        desync?.Disarm(); // user-space phases must not be rewritten on the wire
        bool ipv6 = options.EnableIPv6 && DirectState.HasGlobalIpv6();
        IPAddress[] cloudflareCandidates = Candidates(options, "x.com", GenericCloudflare, ipv6, extraCandidates?.Invoke("x.com"));
        IPAddress[] fastlyCandidates = Candidates(options, "pbs.twimg.com", GenericFastly, ipv6, extraCandidates?.Invoke("pbs.twimg.com"));

        // Phase 1: which IPs still answer at all. Anything TLS back (ServerHello or alert) proves
        // the data path; silence, EOF or a failed connect marks the IP dead for the relay.
        var cloudflareControl = await ReachabilityAsync("Cloudflare 对照", cloudflareCandidates, CloudflareControlSni, Log, token);
        var fastlyControl = await ReachabilityAsync("Fastly 对照", fastlyCandidates, FastlyControlSni, Log, token);
        var dead = new List<IPAddress>();
        foreach (Outcome o in cloudflareControl.Concat(fastlyControl))
            if (o.Result is ProbeResult.Timeout or ProbeResult.Eof or ProbeResult.TcpFail)
            {
                dead.Add(o.Step.Ip);
                onDead?.Invoke(o.Step.Ip, DateTime.UtcNow + SniRelay.UnreachableLifetime);
            }
        IPAddress[] cloudflareV4 = cloudflareControl.Where(o => o.Reachable && o.Step.Ip.AddressFamily == AddressFamily.InterNetwork).OrderBy(o => o.ConnectMs).Select(o => o.Step.Ip).ToArray();
        IPAddress[] cloudflareV6 = cloudflareControl.Where(o => o.Reachable && o.Step.Ip.AddressFamily == AddressFamily.InterNetworkV6).OrderBy(o => o.ConnectMs).Select(o => o.Step.Ip).ToArray();
        IPAddress[] fastlyV4 = fastlyControl.Where(o => o.Reachable && o.Step.Ip.AddressFamily == AddressFamily.InterNetwork).OrderBy(o => o.ConnectMs).Select(o => o.Step.Ip).ToArray();
        var reachable = cloudflareV4.Concat(cloudflareV6).Concat(fastlyV4).ToList();
        var goodIps = new List<IPAddress>();

        byte[] HelloFor(string sni, bool alternate) =>
            !alternate && helloSource(sni) is { } real && FragmentingStream.TryFindServerName(real, out _, out _) ? real : ClientHelloBuilder.Build(sni, chromeLike: true, sniLate: alternate);

        // Phase 2: the blocked SNI with every strategy, alternating IPs so a residual block on one
        // tuple cannot fail the whole table; a hit is confirmed on the other IP with the other hello.
        HelloStrategy? winner = null;
        IPAddress? winnerIp = null;
        if (cloudflareV4.Length > 0)
        {
            int turn = 0;
            foreach (HelloStrategy strategy in HelloStrategy.All)
            {
                token.ThrowIfCancellationRequested();
                IPAddress ip = cloudflareV4[turn++ % cloudflareV4.Length];
                Outcome first = await ProbeAsync(new Step("Cloudflare x.com", ip, "x.com", strategy), HelloFor("x.com", false), StrategyTimeout, token);
                Log(first);
                if (!first.Ok) continue;
                IPAddress other = cloudflareV4[turn++ % cloudflareV4.Length];
                Outcome second = await ProbeAsync(new Step("Cloudflare x.com 确认", other, "x.com", strategy), HelloFor("x.com", true), StrategyTimeout, token);
                Log(second);
                if (!second.Ok) continue;
                winner = strategy;
                winnerIp = ip;
                goodIps.Add(ip);
                if (!other.Equals(ip)) goodIps.Add(other);
                break;
            }
        }

        // Fastly (twimg images/video): the Cloudflare winner first, then the rest.
        HelloStrategy? fastlyWinner = null;
        if (fastlyV4.Length > 0)
        {
            IEnumerable<HelloStrategy> order = (winner is null ? [] : new[] { winner }).Concat(HelloStrategy.All).Where(s => !s.CloudflareOnly).Distinct();
            int turn = 0;
            foreach (HelloStrategy strategy in order)
            {
                token.ThrowIfCancellationRequested();
                IPAddress ip = fastlyV4[turn++ % fastlyV4.Length];
                Outcome first = await ProbeAsync(new Step("Fastly pbs.twimg.com", ip, "pbs.twimg.com", strategy), HelloFor("pbs.twimg.com", false), StrategyTimeout, token);
                Log(first);
                if (!first.Ok) continue;
                Outcome second = await ProbeAsync(new Step("Fastly pbs.twimg.com 确认", fastlyV4[turn++ % fastlyV4.Length], "pbs.twimg.com", strategy), HelloFor("pbs.twimg.com", true), StrategyTimeout, token);
                Log(second);
                if (!second.Ok) continue;
                fastlyWinner = strategy;
                goodIps.Add(ip);
                break;
            }
        }

        // IPv6: no IPv4 residual risk here, so plain goes first; then the IPv4 winner.
        HelloStrategy? v6Winner = null;
        IPAddress? v6Ip = null;
        if (cloudflareV6.Length > 0)
        {
            IEnumerable<HelloStrategy> order = new[] { HelloStrategy.Plain }.Concat(winner is null ? HelloStrategy.All.Where(s => !s.CloudflareOnly) : [winner]).Distinct();
            int turn = 0;
            foreach (HelloStrategy strategy in order)
            {
                token.ThrowIfCancellationRequested();
                IPAddress ip = cloudflareV6[turn++ % cloudflareV6.Length];
                Outcome first = await ProbeAsync(new Step("Cloudflare IPv6 x.com", ip, "x.com", strategy), HelloFor("x.com", false), StrategyTimeout, token);
                Log(first);
                if (!first.Ok) continue;
                Outcome second = await ProbeAsync(new Step("Cloudflare IPv6 x.com 确认", cloudflareV6[turn++ % cloudflareV6.Length], "x.com", strategy), HelloFor("x.com", true), StrategyTimeout, token);
                Log(second);
                if (!second.Ok) continue;
                v6Winner = strategy;
                v6Ip = ip;
                break;
            }
            // The relay applies one strategy per host, so IPv6 edges are preferred only when they
            // work with the strategy that will actually be used.
            if (v6Winner is not null && (winner is null || v6Winner == winner || v6Winner == HelloStrategy.Plain && winner is null))
            {
                goodIps.AddRange(cloudflareV6);
                winner ??= v6Winner;
                winnerIp ??= v6Ip;
            }
        }

        // Kernel-level desync: only when user space found no Cloudflare winner and the engine is up.
        // The engine is armed per strategy; the probe sends a plain ClientHello and the wire layer
        // rewrites it. A hit is confirmed on another IP before the strategy is accepted.
        if (winner is null && desync is { HasEdges: true } && cloudflareV4.Length > 0)
        {
            byte cap = desync.MinTtlCap();
            IReadOnlyList<DesyncStrategy> table = DesyncStrategy.BuildAll(cap);
            report($"诊断 转入内核级 desync：{table.Count} 种策略，TTL 上限 {(cap == 0 ? "未测得" : cap.ToString())}");
            int turn = 0;
            foreach (DesyncStrategy ds in table)
            {
                token.ThrowIfCancellationRequested();
                IPAddress ip = cloudflareV4[turn++ % cloudflareV4.Length];
                desync.Arm(ds);
                Outcome first = await ProbeAsync(new Step($"desync {ds.Name}", ip, "x.com", HelloStrategy.Plain), HelloFor("x.com", false), StrategyTimeout, token);
                Log(first);
                if (!first.Ok) continue;
                IPAddress other = cloudflareV4[turn++ % cloudflareV4.Length];
                Outcome second = await ProbeAsync(new Step($"desync {ds.Name} 确认", other, "x.com", HelloStrategy.Plain), HelloFor("x.com", true), StrategyTimeout, token);
                Log(second);
                if (!second.Ok) continue;
                desyncWinner = ds;
                desyncWinnerIp = ip;
                goodIps.Add(ip);
                if (!other.Equals(ip)) goodIps.Add(other);
                break;
            }
            desync.Disarm();
        }

        string verdict = Verdict(cloudflareControl, cloudflareV4, cloudflareV6, winner, winnerIp, v6Winner, fastlyV4, fastlyWinner, outcomes);
        if (desyncWinner is not null)
            verdict = $"已找到内核级 desync 方案：{desyncWinner.Name}（{desyncWinner.Summary}），IP {desyncWinnerIp}；中继改发明文 ClientHello，由 WinDivert 在网线层改写，请重新打开 x.com。" +
                      (fastlyV4.Length == 0 ? "" : fastlyWinner is null ? " twimg 的图片/视频可能仍加载不出。" : $" twimg 用 {fastlyWinner.Name}。");
        else if (desync is { HasEdges: true } && winner is null && cloudflareV4.Length > 0)
            verdict += " 已尝试内核级 desync（WinDivert 假包）但各策略仍未拿到 ServerHello：请点“复制诊断信息”反馈，或在 direct-config.json 的 RelayHosts 补充其他 Cloudflare 边缘 IP。";
        else if (desync is null && winner is null && cloudflareV4.Length > 0)
            verdict += $" 内核级 desync（WinDivert 假包）未能启用，所以这轮没试：{desyncError ?? "引擎未启动"}";
        return Finish(verdict, winner, winnerIp, fastlyWinner, reachable, dead, goodIps.Distinct());
    }

    private static string Verdict(List<Outcome> cloudflareControl, IPAddress[] cloudflareV4, IPAddress[] cloudflareV6, HelloStrategy? winner, IPAddress? winnerIp,
        HelloStrategy? v6Winner, IPAddress[] fastlyV4, HelloStrategy? fastlyWinner, List<Outcome> outcomes)
    {
        string fastly = fastlyV4.Length == 0 ? "twimg（图片/视频）的 Fastly IP 全部无响应" :
            fastlyWinner is null ? "twimg（图片/视频）的 SNI 在 Fastly 上仍被拦，图片可能加载不出" : $"twimg 用 {fastlyWinner.Name}";
        if (winner is not null)
        {
            string v6 = v6Winner is not null ? $"；IPv6 路径 {v6Winner.Name} 也可用" : string.Empty;
            return $"已找到可用方案：{winner.Name}（{winner.Summary}），IP {winnerIp}{v6}；{fastly}。中继已切换，请重新打开 x.com。";
        }
        if (cloudflareV4.Length == 0 && cloudflareV6.Length == 0)
        {
            bool anyTcp = cloudflareControl.Any(o => o.Result != ProbeResult.TcpFail);
            return anyTcp ?
                "所有 Cloudflare 候选 IP 发出 ClientHello 后都无响应（IP 级丢包，与 SNI 无关）；中继已尝试通过 DoH 刷新候选，换 IP 或分片都无效。请确认没有代理/TUN 接管。" :
                "所有 Cloudflare 候选 IP 连 TCP 都连不上：请确认当前没有代理/TUN 接管，或网络本身是否可用。";
        }
        var x = outcomes.Where(o => o.Step.Sni == "x.com").ToList();
        if (x.Any(o => o.Result == ProbeResult.Alert) && !x.Any(o => o.Result == ProbeResult.Rst))
            return "可达的 Cloudflare IP 对 x.com 的 SNI 返回 TLS alert：这些边缘 IP 不服务 x.com 域名；已尝试 DoH 刷新候选，可稍后重试或补充 RelayHosts。";
        int layouts = x.Count(o => o.Step.Strategy is not null && o.Step.Strategy.Fragments && o.Result == ProbeResult.Rst);
        int injected = x.Count(o => o.Injected);
        bool plainRst = x.Any(o => o.Step.Strategy == HelloStrategy.Plain && o.Result == ProbeResult.Rst);
        if (layouts > 0)
            return $"通用 Cloudflare IP 可达（无关 SNI 握手正常），但 x.com 的 SNI {(plainRst ? "明文和" : string.Empty)}{layouts} 种分片/错位/延迟布局都被 RST（其中 {injected} 次快于 RTT，为注入）：" +
                   "这与 IP 无关，换 IP 无效；该线路的用户态分片已被重组识别，需要内核级假包（WinDivert）或 ECH。";
        return "结果混合，请点“复制诊断信息”把完整结果发回分析。";
    }

    private static IPAddress[] Candidates(DirectOptions options, string host, string[] generic, bool ipv6,
        IReadOnlyCollection<IPAddress>? extra = null)
    {
        IEnumerable<string> configured = options.RelayHosts.TryGetValue(host, out var list) ? list : [];
        return configured.Concat(generic)
            .Select(text => IPAddress.TryParse(text, out var ip) ? ip : null)
            .Where(ip => ip is not null && (ipv6 || ip.AddressFamily != AddressFamily.InterNetworkV6))
            .Select(ip => ip!)
            .Concat(extra ?? [])
            .Where(ip => ipv6 || ip.AddressFamily != AddressFamily.InterNetworkV6)
            .Distinct()
            .ToArray();
    }

    private static async Task<List<Outcome>> ReachabilityAsync(string name, IPAddress[] candidates, string controlSni, Action<Outcome> log, CancellationToken token)
    {
        var tasks = candidates.Select(ip => ProbeAsync(new Step(name, ip, controlSni, null), ClientHelloBuilder.Build(controlSni), ControlTimeout, token)).ToList();
        var results = new List<Outcome>();
        foreach (Outcome outcome in await Task.WhenAll(tasks))
        {
            log(outcome);
            results.Add(outcome);
        }
        return results;
    }

    internal static async Task<Outcome> ProbeAsync(Step step, byte[] hello, TimeSpan timeoutSpan, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        long connectMs = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(timeoutSpan);
        try
        {
            using var tcp = new TcpClient(step.Ip.AddressFamily) { NoDelay = true };
            try { await tcp.ConnectAsync(step.Ip, 443, timeout.Token); }
            catch (Exception ex) when (ex is SocketException || ex is OperationCanceledException && !token.IsCancellationRequested)
            {
                return new Outcome(step, ProbeResult.TcpFail, "TCP 连接失败", watch.ElapsedMilliseconds, 0);
            }
            connectMs = watch.ElapsedMilliseconds;
            if (!FragmentingStream.TryFindServerName(hello, out _, out _))
                return new Outcome(step, ProbeResult.Other, "探测用 ClientHello 没有 SNI", watch.ElapsedMilliseconds, connectMs);
            var stream = tcp.GetStream();
            await FragmentingStream.WriteShapedAsync(stream, hello, step.Strategy ?? HelloStrategy.Plain, timeout.Token);
            byte[] buffer = new byte[16];
            int count = await stream.ReadAsync(buffer, timeout.Token);
            if (count == 0)
                return new Outcome(step, ProbeResult.Eof, "对端关闭 (EOF)", watch.ElapsedMilliseconds, connectMs);
            return buffer[0] switch
            {
                0x16 => new Outcome(step, ProbeResult.ServerHello, "握手成功（收到 ServerHello）", watch.ElapsedMilliseconds, connectMs),
                0x15 => new Outcome(step, ProbeResult.Alert, "服务器返回 TLS alert", watch.ElapsedMilliseconds, connectMs),
                _ => new Outcome(step, ProbeResult.Other, $"收到非 TLS 响应 0x{buffer[0]:X2}", watch.ElapsedMilliseconds, connectMs)
            };
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new Outcome(step, ProbeResult.Timeout, "超时（发出 ClientHello 后无任何响应）", watch.ElapsedMilliseconds, connectMs);
        }
        catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset })
        {
            return new Outcome(step, ProbeResult.Rst, "被重置 (RST)", watch.ElapsedMilliseconds, connectMs);
        }
        catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut })
        {
            return new Outcome(step, ProbeResult.Timeout, "TCP 重传超时（发出的数据未被确认，疑似被丢包）", watch.ElapsedMilliseconds, connectMs);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            return new Outcome(step, ProbeResult.Eof, "对端关闭 (EOF)", watch.ElapsedMilliseconds, connectMs);
        }
    }
}
