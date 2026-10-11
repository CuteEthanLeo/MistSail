using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;

namespace Sheas_Cealer.Direct;

// Real TLS handshakes against the X edge IPs, paired with control handshakes to the same IPs
// using an unrelated, unblocked SNI. The pattern tells IP-level blocking apart from SNI
// detection, and whether fragmentation changes anything. Runs only when the user connects.
internal static class LineProbe
{
    internal sealed record Step(string Name, string Ip, string Sni, bool Fragment);
    internal sealed record Outcome(Step Step, string Result, long Milliseconds, bool Ok);

    internal static IReadOnlyList<Step> DefaultSteps(DirectOptions options)
    {
        string cloudflare = options.RelayHosts.TryGetValue("x.com", out var xIps) && xIps.Length > 0 ? xIps[0] : "162.159.140.229";
        string fastly = options.RelayHosts.TryGetValue("pbs.twimg.com", out var tIps) && tIps.Length > 0 ? tIps[0] : "151.101.76.159";
        return
        [
            new("Cloudflare 对照：x.com 专用 IP + 无关 SNI", cloudflare, "www.cloudflare.com", false),
            new("Cloudflare 测试：x.com 专用 IP + x.com 明文 SNI", cloudflare, "x.com", false),
            new("Cloudflare 测试：x.com 专用 IP + x.com 分片 SNI", cloudflare, "x.com", true),
            new("Cloudflare 测试：通用 anycast IP + x.com 分片 SNI", "104.16.123.96", "x.com", true),
            new("Cloudflare 对照：通用 anycast IP + 无关 SNI", "104.16.123.96", "www.cloudflare.com", false),
            new("Fastly 对照：twimg IP + 无关 SNI", fastly, "www.python.org", false),
            new("Fastly 测试：twimg IP + pbs.twimg.com 分片 SNI", fastly, "pbs.twimg.com", true),
            new("Fastly 测试：其他 Fastly 节点 + pbs.twimg.com 分片 SNI", "151.101.0.159", "pbs.twimg.com", true),
        ];
    }

    internal static async Task<List<Outcome>> RunAsync(DirectOptions options, IEnumerable<Step> steps, Action<string> report, CancellationToken token)
    {
        var outcomes = new List<Outcome>();
        foreach (Step step in steps)
        {
            token.ThrowIfCancellationRequested();
            Outcome outcome = await ProbeAsync(options, step, token);
            outcomes.Add(outcome);
            report($"诊断 {step.Name} [{step.Ip} sni={step.Sni} frag={(step.Fragment ? "on" : "off")}] -> {outcome.Result} {outcome.Milliseconds}ms");
        }
        report("诊断结论：" + Verdict(outcomes));
        return outcomes;
    }

    private static async Task<Outcome> ProbeAsync(DirectOptions options, Step step, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            using var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            try { await tcp.ConnectAsync(IPAddress.Parse(step.Ip), 443, timeout.Token); }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                return new Outcome(step, "TCP 连接失败", watch.ElapsedMilliseconds, false);
            }
            var fragmentOptions = step.Fragment ? options : new DirectOptions { FragmentMode = "off", FragmentDomains = options.FragmentDomains };
            await using var fragmenting = new FragmentingStream(tcp.GetStream(), fragmentOptions, step.Fragment, leaveOpen: true);
            await using var ssl = new SslStream(fragmenting, leaveInnerStreamOpen: true, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = step.Sni,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
            }, timeout.Token);
            string subject = ssl.RemoteCertificate?.Subject ?? "?";
            return new Outcome(step, $"握手成功 ({ssl.SslProtocol}, 证书 {subject})", watch.ElapsedMilliseconds, true);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new Outcome(step, "超时（发出 ClientHello 后无任何响应）", watch.ElapsedMilliseconds, false);
        }
        catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset })
        {
            return new Outcome(step, "被重置 (RST)", watch.ElapsedMilliseconds, false);
        }
        catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut })
        {
            return new Outcome(step, "TCP 重传超时（发出的数据未被确认，疑似被丢包）", watch.ElapsedMilliseconds, false);
        }
        catch (IOException)
        {
            return new Outcome(step, "对端关闭 (EOF)", watch.ElapsedMilliseconds, false);
        }
        catch (AuthenticationException ex)
        {
            return new Outcome(step, $"TLS 协商失败：{ex.Message}", watch.ElapsedMilliseconds, false);
        }
    }

    internal static string Verdict(IReadOnlyList<Outcome> outcomes)
    {
        bool Ok(string prefix) => outcomes.Any(o => o.Step.Name.StartsWith(prefix, StringComparison.Ordinal) && o.Ok);
        bool cfControl = Ok("Cloudflare 对照：x.com 专用");
        bool cfPlain = Ok("Cloudflare 测试：x.com 专用 IP + x.com 明文");
        bool cfFrag = Ok("Cloudflare 测试：x.com 专用 IP + x.com 分片");
        bool cfGeneric = Ok("Cloudflare 测试：通用");
        bool fastlyControl = Ok("Fastly 对照");
        bool fastlyFrag = Ok("Fastly 测试");

        if (cfFrag || cfGeneric) return "分片后 x.com 握手成功；若页面仍打不开，看浏览器报错和 relay.log 的后续记录。";
        if (!cfControl) return "x.com 专用 Cloudflare IP 连无关 SNI 都握手不成功：该 IP 本身被封锁或丢包，换 SNI/分片无用；需要换其他 Cloudflare IP（通用 anycast 对照结果见上）。";
        if (cfPlain) return "明文 x.com 居然握手成功：这条线路并未封锁 x.com 的 SNI，问题在后续 HTTP 层。";
        if (!cfFrag && !fastlyFrag && fastlyControl) return "IP 可达、无关 SNI 正常，但 x.com/twimg 的 SNI 无论分片与否都失败：GFW 已能重组 TLS record 分片，当前分片方式对该线路无效。";
        return "结果混合，请把完整 relay.log 发回分析。";
    }
}
