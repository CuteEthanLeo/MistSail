using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace MistSail.Direct;

// What the line finder learned, kept next to direct-config.json so the relay starts with it.
// Never written back into direct-config.json (that file must stay equal to the defaults).
internal sealed class DirectState
{
    public string? Strategy { get; set; }
    public string? Desync { get; set; }
    public Dictionary<string, string> HostStrategies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string[] GoodIps { get; set; } = [];
    public Dictionary<string, DateTime> DeadIps { get; set; } = [];
    public string NetworkKey { get; set; } = string.Empty;
    public DateTime SavedAt { get; set; }

    internal static string FilePath => Path.Combine(AppContext.BaseDirectory, "direct-state.json");
    internal static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    internal static DirectState? Load() => Load(FilePath, CurrentNetworkKey(), DateTime.UtcNow);

    internal static DirectState? Load(string path, string networkKey, DateTime now)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var state = JsonSerializer.Deserialize<DirectState>(File.ReadAllText(path), DirectOptions.JsonOptions);
            if (state is null || state.SavedAt + Lifetime < now || !state.NetworkKey.Equals(networkKey, StringComparison.Ordinal))
                return null;
            state.DeadIps = state.DeadIps.Where(pair => IPAddress.TryParse(pair.Key, out _) && pair.Value > now).ToDictionary(p => p.Key, p => p.Value);
            state.GoodIps = state.GoodIps.Where(ip => IPAddress.TryParse(ip, out _)).ToArray();
            state.HostStrategies = new Dictionary<string, string>(state.HostStrategies.Where(p => HelloStrategy.TryParse(p.Value, out _)), StringComparer.OrdinalIgnoreCase);
            if (state.Strategy is not null && !HelloStrategy.TryParse(state.Strategy, out _)) state.Strategy = null;
            if (state.Desync is not null && !DesyncStrategy.TryParse(state.Desync, out _)) state.Desync = null;
            return state;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal void Save() => Save(FilePath);

    internal void Save(string path)
    {
        try
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, DirectOptions.JsonOptions));
            File.Move(temp, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DirectLog.Write(SniRelay.LogFile, $"无法写入 {Path.GetFileName(path)}：{ex.Message}");
        }
    }

    internal static void Delete()
    {
        try { File.Delete(FilePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // Local unicast addresses of the adapters that are up: a different network (or a TUN adapter
    // appearing) invalidates what was learned.
    internal static string CurrentNetworkKey()
    {
        try
        {
            var addresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Select(u => u.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork || (a.AddressFamily == AddressFamily.InterNetworkV6 && IsGlobalIpv6(a)))
                .Select(a => a.ToString())
                .Distinct()
                .OrderBy(a => a, StringComparer.Ordinal);
            return string.Join(",", addresses);
        }
        catch (NetworkInformationException) { return string.Empty; }
    }

    internal static bool HasGlobalIpv6()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Any(u => u.Address.AddressFamily == AddressFamily.InterNetworkV6 && IsGlobalIpv6(u.Address));
        }
        catch (NetworkInformationException) { return false; }
    }

    internal static bool IsGlobalIpv6(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6 || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal ||
            address.IsIPv6Multicast || address.IsIPv6Teredo || IPAddress.IPv6Loopback.Equals(address))
            return false;
        byte first = address.GetAddressBytes()[0];
        return (first & 0xfe) != 0xfc && (first & 0xe0) == 0x20; // not ULA fc00::/7; global 2000::/3
    }

    // Names of adapters that would hijack the relay's egress (TUN mode of Clash, sing-box, ...).
    internal static string[] TunAdapters()
    {
        string[] markers = ["wintun", "tap-windows", "tap-win", "clash", "mihomo", "meta tunnel", "sing-tun", "sing-box", "hiddify", "nekobox", "v2ray", "xray", "tun2socks"];
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                .Where(nic => markers.Any(m => nic.Description.Contains(m, StringComparison.OrdinalIgnoreCase) || nic.Name.Contains(m, StringComparison.OrdinalIgnoreCase)) ||
                              nic.Name.Equals("Meta", StringComparison.OrdinalIgnoreCase))
                .Select(nic => $"{nic.Name} ({nic.Description})")
                .ToArray();
        }
        catch (NetworkInformationException) { return []; }
    }
}
