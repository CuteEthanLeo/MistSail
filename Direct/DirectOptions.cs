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
    public string FragmentMode { get; set; } = "tls-record";
    public int FragmentBytes { get; set; } = 8;
    public int FragmentDelayMs { get; set; } = 8;
    public int ConnectTimeoutSeconds { get; set; } = 6;
    public int DnsTimeoutSeconds { get; set; } = 6;
    public int MaxConnections { get; set; } = 256;
    public bool EnableIPv6 { get; set; } = false;
    public string[] FragmentDomains { get; set; } = ["*"];
    public Dictionary<string, string[]> HostOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DohServer[] DohServers { get; set; } =
    [
        new() { Url = "https://cloudflare-dns.com/dns-query", BootstrapAddresses = ["1.1.1.1", "1.0.0.1"] },
        new() { Url = "https://dns.quad9.net/dns-query", BootstrapAddresses = ["9.9.9.9", "149.112.112.112"] },
        new() { Url = "https://dns.google/dns-query", BootstrapAddresses = ["8.8.8.8", "8.8.4.4"] }
    ];

    // Global mode: hosts entries for these names point at RelayListenAddress instead of 127.0.0.1,
    // and SniRelay forwards them with real SNI plus ClientHello fragmentation.
    // An empty address list means "resolve through DoH"; never the system resolver.
    public string RelayListenAddress { get; set; } = "127.0.0.2";
    public Dictionary<string, string[]> RelayHosts { get; set; } = DefaultRelayHosts();

    internal static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "direct-config.json");
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    internal static Dictionary<string, string[]> DefaultRelayHosts()
    {
        // Looked up on 2026-10-11 via Google DoH: x.com/api/upload/t.co/twitter.com -> Cloudflare
        // (dynamic.x.com.cdn.cloudflare.net), *.twimg.com -> Fastly, abs-0.twimg.com -> X's own range.
        // Dedicated addresses first; generic Cloudflare anycast and the sibling Fastly subnets
        // (same host octet) follow as fallbacks, because the relay demotes addresses that die.
        string[] cloudflare = ["162.159.140.229", "172.66.0.227", "104.16.123.96", "104.16.124.96", "104.18.32.47", "104.17.209.9"];
        string[] fastly = ["151.101.76.159", "151.101.0.159", "151.101.64.159", "151.101.128.159", "151.101.192.159", "146.75.72.157", "151.101.76.158"];
        var hosts = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (string host in new[] { "x.com", "www.x.com", "api.x.com", "upload.x.com", "mobile.x.com", "t.co",
                                        "twitter.com", "www.twitter.com", "api.twitter.com", "upload.twitter.com", "mobile.twitter.com" })
            hosts[host] = cloudflare;
        foreach (string host in new[] { "abs.twimg.com", "pbs.twimg.com", "video.twimg.com", "ton.twimg.com",
                                        "a0.twimg.com", "a1.twimg.com", "a2.twimg.com", "a3.twimg.com", "a4.twimg.com" })
            hosts[host] = fastly;
        hosts["abs-0.twimg.com"] = ["104.244.43.131", "151.101.76.159", "151.101.0.159"];
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
        if (FragmentMode is not ("tls-record" or "tcp" or "off"))
            throw new InvalidDataException("FragmentMode 必须是 tls-record、tcp 或 off。");
        if (ListenPort is < 1024 or > 65535 || FragmentBytes is < 1 or > 1024 || FragmentDelayMs is < 0 or > 100 ||
            ConnectTimeoutSeconds is < 1 or > 30 || DnsTimeoutSeconds is < 1 or > 30 || MaxConnections is < 1 or > 1024)
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
}
