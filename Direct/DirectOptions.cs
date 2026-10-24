using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace MistSail.Direct;

internal sealed class DohServer
{
    public string Url { get; set; } = string.Empty;
    public string[] BootstrapAddresses { get; set; } = [];
}

internal sealed class DirectOptions
{
    public int ListenPort { get; set; } = 17881;
    // "auto": use what the line finder confirmed (direct-state.json), else HelloStrategy.Default.
    // Any strategy name from HelloStrategy.All (or the legacy tls-record / tcp / off) pins it.
    public string FragmentMode { get; set; } = HelloStrategy.Auto;
    public int FragmentBytes { get; set; } = 8;
    // Pause between the TCP writes of a shaped ClientHello. Windows coalesces back-to-back sends
    // (LSO), so anything below ~15 ms does not reliably produce separate segments.
    public int FragmentDelayMs { get; set; } = HelloStrategy.DefaultGapMs;
    public int ConnectTimeoutSeconds { get; set; } = 6;
    // How long one target may stay silent after the ClientHello before the relay moves on to the
    // next IP or strategy within the same browser connection.
    public int HandshakeTimeoutSeconds { get; set; } = 4;
    public int DnsTimeoutSeconds { get; set; } = 6;
    public int MaxConnections { get; set; } = 256;
    // IPv6 candidates and AAAA answers are used only when the machine has a global IPv6 address.
    public bool EnableIPv6 { get; set; } = true;
    public string[] FragmentDomains { get; set; } = ["*"];
    public Dictionary<string, string[]> HostOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DohServer[] DohServers { get; set; } =
    [
        new() { Url = "https://cloudflare-dns.com/dns-query", BootstrapAddresses = ["1.1.1.1", "1.0.0.1"] },
        new() { Url = "https://dns.quad9.net/dns-query", BootstrapAddresses = ["9.9.9.9", "149.112.112.112"] },
        new() { Url = "https://dns.google/dns-query", BootstrapAddresses = ["8.8.8.8", "8.8.4.4"] }
    ];

    // Dynamic video hosts (rr*.googlevideo.com and friends) cannot be written into the hosts file:
    // Windows hosts has no wildcards. While a global connection is up, a loopback PAC script sends
    // only these suffixes to the loopback CONNECT relay, which resolves them through DoH and shapes
    // the first ClientHello. Everything else keeps the hosts + nginx path. Empty host list = off.
    public bool DynamicProxyEnabled { get; set; } = true;
    public string[] DynamicProxyHosts { get; set; } = ["googlevideo.com", "youtubei.googleapis.com"];
    // Existing corporate PAC / fixed proxy settings are left alone unless the user opts in.
    public bool DynamicProxyOverrideExisting { get; set; }
    // After all candidates for one host failed, refresh that host's candidates through DoH. The
    // systems resolver is never used here (only the loopback video proxy may fall back for videos).
    public bool RefreshCandidatesFromDoh { get; set; } = true;

    // Kernel-level (WinDivert) desync for the X edges, for lines where every user-space HelloStrategy
    // is reset. "auto" (default): the engine loads if WinDivert is present, the finder tries user-space
    // first and falls back to picking a desync strategy; the relay then sends plain ClientHellos while
    // the wire layer rewrites them. "off": user-space relay only (no driver loaded). A strategy name
    // from DesyncStrategy pins one. Requires WinDivert.dll/.sys next to the exe and administrator
    // rights; if the driver cannot load, the relay falls back to user-space shaping and logs it.
    public string DesyncMode { get; set; } = DesyncStrategy.Auto;

    // Global mode: hosts entries for these names point at RelayListenAddress instead of 127.0.0.1,
    // and SniRelay forwards them with real SNI plus ClientHello shaping.
    // An empty address list means "resolve through DoH"; never the system resolver.
    public string RelayListenAddress { get; set; } = "127.0.0.2";
    public Dictionary<string, string[]> RelayHosts { get; set; } = DefaultRelayHosts();

    internal static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "direct-config.json");
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    internal static Dictionary<string, string[]> DefaultRelayHosts()
    {
        // x.com/api/upload/t.co/twitter.com -> Cloudflare (dynamic.x.com.cdn.cloudflare.net),
        // *.twimg.com -> Fastly, abs-0.twimg.com -> X's own range.
        // Generic Cloudflare anycast first: the dedicated x.com addresses (162.159.140.229,
        // 172.66.0.227) accept TCP but drop every ClientHello on lines measured in 2026-10, and the
        // relay would otherwise burn its handshake timeout on them before every fallback.
        // The IPv6 entries are Cloudflare edges that served the x.com zone in 2024-11 reports; they
        // are used only when the machine has global IPv6 and the line finder confirms them.
        string[] cloudflare = ["104.16.123.96", "104.16.124.96", "104.18.32.47", "104.17.209.9", "162.159.140.229", "172.66.0.227",
                               "2606:4700:7::e3", "2a06:98c1:58::e3", "2606:4700::6810:84e5"];
        string[] fastly = ["151.101.0.159", "151.101.64.159", "151.101.128.159", "151.101.192.159", "151.101.76.159", "146.75.72.157", "151.101.76.158"];
        var hosts = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (string host in new[] { "x.com", "www.x.com", "api.x.com", "upload.x.com", "mobile.x.com", "t.co",
                                        "twitter.com", "www.twitter.com", "api.twitter.com", "upload.twitter.com", "mobile.twitter.com" })
            hosts[host] = cloudflare;
        foreach (string host in new[] { "abs.twimg.com", "pbs.twimg.com", "video.twimg.com", "ton.twimg.com",
                                        "a0.twimg.com", "a1.twimg.com", "a2.twimg.com", "a3.twimg.com", "a4.twimg.com" })
            hosts[host] = fastly;
        hosts["abs-0.twimg.com"] = ["104.244.43.131", "151.101.0.159", "151.101.76.159"];
        foreach (string host in new[] { "help.x.com", "about.x.com", "business.x.com", "developer.x.com", "pro.x.com", "ads.x.com",
                                        "analytics.twitter.com", "syndication.twitter.com", "platform.twitter.com", "cdn.syndication.twimg.com" })
            hosts[host] = [];
        return hosts;
    }

    internal static DirectOptions Load()
    {
        if (!File.Exists(ConfigPath))
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new DirectOptions(), JsonOptions));
        var options = JsonSerializer.Deserialize<DirectOptions>(File.ReadAllText(ConfigPath), JsonOptions)
            ?? throw new InvalidDataException("direct-config.json 不能为空。");
        options.Validate();
        return options;
    }

    internal void Validate()
    {
        if (!HelloStrategy.IsKnownName(FragmentMode))
            throw new InvalidDataException($"FragmentMode 必须是 auto、{string.Join("、", HelloStrategy.All.Select(s => s.Name))} 之一（或旧值 tls-record、tcp、off）。");
        if (!DesyncStrategy.IsKnownName(DesyncMode))
            throw new InvalidDataException($"DesyncMode 必须是 off、auto 或 {string.Join("、", DesyncStrategy.All.Select(s => s.Name))} 之一。");
        if (ListenPort is < 1024 or > 65535 || FragmentBytes is < 1 or > 1024 || FragmentDelayMs is < 0 or > 2000 ||
            ConnectTimeoutSeconds is < 1 or > 30 || HandshakeTimeoutSeconds is < 1 or > 30 || DnsTimeoutSeconds is < 1 or > 30 || MaxConnections is < 1 or > 1024)
            throw new InvalidDataException("直连配置的分段大小、延迟、超时或连接数超出范围。");
        if (FragmentDomains is null || FragmentDomains.Any(string.IsNullOrWhiteSpace) || DohServers is null || DohServers.Length is < 1 or > 8 || HostOverrides is null)
            throw new InvalidDataException("直连配置缺少域名或 DNS 项。");
        foreach (var server in DohServers)
            if (server is null || !Uri.TryCreate(server.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                uri.Port != 443 || !string.IsNullOrEmpty(uri.UserInfo) || server.BootstrapAddresses is null ||
                server.BootstrapAddresses.Length is < 1 or > 8 || server.BootstrapAddresses.Any(ip => !IPAddress.TryParse(ip, out _)))
                throw new InvalidDataException("DoH 必须使用 HTTPS 443，并提供有效的 BootstrapAddresses IP。");
        foreach (var pair in HostOverrides)
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null || pair.Value.Length is < 1 or > 16 ||
                pair.Value.Any(ip => !IPAddress.TryParse(ip, out _)))
                throw new InvalidDataException("HostOverrides 必须是域名到 IP 数组的映射。");
        HostOverrides = new Dictionary<string, string[]>(HostOverrides, StringComparer.OrdinalIgnoreCase);

        if (DynamicProxyHosts is null)
            throw new InvalidDataException("DynamicProxyHosts 不能为空；可以是空数组。");
        var dynamicHosts = new List<string>(DynamicProxyHosts.Length);
        foreach (string host in DynamicProxyHosts)
        {
            string normalized = NormalizeDynamicHost(host);
            string[] labels = normalized.Split('.');
            if (normalized.Length is < 1 or > 253 || labels.Length < 2 ||
                labels.Any(label => label.Length is < 1 or > 63 || !label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ||
                                    label[0] == '-' || label[^1] == '-'))
                throw new InvalidDataException("DynamicProxyHosts 必须是域名（可带前导 *），不能包含协议、端口、空格或通配符。");
            if (!dynamicHosts.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                dynamicHosts.Add(normalized);
        }
        if (dynamicHosts.Count > 32)
            throw new InvalidDataException("DynamicProxyHosts 最多 32 项。");
        DynamicProxyHosts = dynamicHosts.ToArray();

        if (!IPAddress.TryParse(RelayListenAddress, out var relayAddress) || relayAddress.AddressFamily != AddressFamily.InterNetwork ||
            relayAddress.GetAddressBytes()[0] != 127 || relayAddress.Equals(IPAddress.Loopback))
            throw new InvalidDataException("RelayListenAddress 必须是 127.0.0.1 以外的 127.x.x.x 回环地址，例如 127.0.0.2。");
        if (RelayHosts is null)
            throw new InvalidDataException("RelayHosts 不能为空；可以是空对象 {}。");
        foreach (var pair in RelayHosts)
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '*')) ||
                pair.Value is null || pair.Value.Length > 16 || pair.Value.Any(ip => !IPAddress.TryParse(ip, out _)))
                throw new InvalidDataException("RelayHosts 必须是域名到 IP 数组的映射（数组可为空）。");
        RelayHosts = new Dictionary<string, string[]>(RelayHosts, StringComparer.OrdinalIgnoreCase);
    }

    internal bool ShouldFragment(string host) => FragmentMode != "off" && FragmentDomains.Any(domain =>
        domain == "*" || host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith("." + domain.TrimStart('*', '.'), StringComparison.OrdinalIgnoreCase));

    // "*.googlevideo.com" and "googlevideo.com" are both stored as "googlevideo.com"; the PAC script
    // matches the suffix for every subdomain.
    internal static string NormalizeDynamicHost(string? host) =>
        (host ?? string.Empty).Trim().ToLowerInvariant().TrimStart('*', '.');

    internal bool ShouldUseDynamicProxy(string host) => DynamicProxyEnabled && DynamicProxyHosts.Any(suffix =>
        host.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase));

    internal bool DesyncEnabled => !DesyncMode.Equals(DesyncStrategy.Off, StringComparison.OrdinalIgnoreCase);

    // The desync strategy pinned in the config, or null for "auto" (let the finder choose).
    internal DesyncStrategy? ConfiguredDesync =>
        !DesyncEnabled || DesyncMode.Equals(DesyncStrategy.Auto, StringComparison.OrdinalIgnoreCase) || !DesyncStrategy.TryParse(DesyncMode, out var d) ? null : d;

    // The IPv4 edges the desync layer may touch: the configured X/twimg addresses plus the finder's
    // generic Cloudflare/Fastly lists. Nothing else on the machine is diverted.
    internal IPAddress[] DesyncEdges()
    {
        var ips = new List<IPAddress>();
        foreach (var pair in RelayHosts)
            foreach (string text in pair.Value)
                if (IPAddress.TryParse(text, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
                    ips.Add(ip);
        foreach (string text in LineProbe.GenericCloudflare.Concat(LineProbe.GenericFastly))
            if (IPAddress.TryParse(text, out var ip))
                ips.Add(ip);
        return ips.Distinct().ToArray();
    }

    // The strategy pinned in the config, or null for "auto".
    internal HelloStrategy? ConfiguredStrategy =>
        FragmentMode.Equals(HelloStrategy.Auto, StringComparison.OrdinalIgnoreCase) || !HelloStrategy.TryParse(FragmentMode, out var strategy) ? null : WithGap(strategy);

    // Strategy for a host that is not served by SniRelay (DoH bootstrap, the CONNECT transport):
    // the configured one, else the default; null when the host is excluded from shaping.
    internal HelloStrategy? StrategyFor(string host) => ShouldFragment(host) ? ConfiguredStrategy ?? WithGap(HelloStrategy.Default) : null;

    internal HelloStrategy WithGap(HelloStrategy strategy) =>
        strategy.GapMs == HelloStrategy.DefaultGapMs && FragmentDelayMs != HelloStrategy.DefaultGapMs ? strategy with { GapMs = FragmentDelayMs } : strategy;
}
