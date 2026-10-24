using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace MistSail.Direct;

// The kernel-level desync layer. It diverts only outbound TLS ClientHellos to the specific
// Cloudflare/Fastly IPv4 edges the relay uses for X, and (when a strategy is armed) rewrites each
// on the wire so the GFW and the server reassemble different streams. Every other packet it sees is
// re-injected unchanged. Inbound SYN-ACKs from those same edges are read only to estimate the hop
// count to the server (autottl), so a fake's TTL can be kept below it and never reach the server.
//
// Scope is deliberately narrow: the WinDivert filter matches just these edge IPs on port 443, so
// the rest of the machine's traffic is never touched. Requires administrator rights (the app
// already runs elevated) and WinDivert.dll + WinDivert64.sys next to the executable.
internal sealed class WinDivertDesync : IDisposable
{
    internal const string LogFile = SniRelay.LogFile;

    private readonly IPAddress[] edges;
    private readonly string benignSni;
    private readonly int priority;
    private IntPtr handle = WinDivertNative.InvalidHandle;
    private Thread? worker;
    private volatile DesyncStrategy? armed;
    private volatile bool running;
    private int disposed;

    // Per-edge cap on a fake's TTL, derived from the inbound SYN-ACK: fake TTL must stay below the
    // hop count to the server. 0 means "not measured yet".
    private readonly ConcurrentDictionary<uint, byte> ttlCap = new();

    internal event Action<string>? Status;
    // Fired only when the receive loop dies unexpectedly (not on Dispose). The relay uses it to stop
    // sending plain ClientHellos, which would otherwise be unshaped and fail every handshake.
    internal event Action? EngineFailed;

    internal WinDivertDesync(IEnumerable<IPAddress> edgeIps, string benignSni = "www.bing.com", int priority = 0)
    {
        edges = edgeIps.Where(ip => ip.AddressFamily == AddressFamily.InterNetwork).Distinct().ToArray();
        this.benignSni = benignSni;
        this.priority = priority;
    }

    internal bool HasEdges => edges.Length > 0;
    internal DesyncStrategy? Armed => armed;

    internal void Arm(DesyncStrategy? strategy)
    {
        armed = strategy;
        if (strategy is not null)
            Report($"desync 已装填策略 {strategy.Name}（{strategy.Summary}）");
    }

    internal void Disarm() => armed = null;

    // The autottl cap for an edge: fakes above this would reach the server, so the finder stops the
    // ladder there. 0 when the SYN-ACK has not been seen yet.
    internal byte TtlCapFor(IPAddress ip) =>
        ip.AddressFamily == AddressFamily.InterNetwork && ttlCap.TryGetValue(ToKey(ip), out byte cap) ? cap : (byte)0;

    // Smallest cap seen across all edges, for bounding the shared strategy table in the finder.
    internal byte MinTtlCap()
    {
        byte min = 0;
        foreach (byte cap in ttlCap.Values)
            if (cap != 0 && (min == 0 || cap < min)) min = cap;
        return min;
    }

    internal void Start()
    {
        if (!WinDivertNative.Available())
            throw new DllNotFoundException("找不到 WinDivert.dll（应与 MistSail.exe 同目录）。");
        string filter = BuildFilter();
        handle = WinDivertNative.WinDivertOpen(filter, WinDivertNative.LayerNetwork, (short)priority, 0);
        if (handle == WinDivertNative.InvalidHandle)
        {
            int err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"WinDivertOpen 失败（错误码 {err}）：{DescribeOpenError(err)}");
        }
        running = true;
        worker = new Thread(RecvLoop) { IsBackground = true, Name = "windivert-desync" };
        worker.Start();
        Report($"desync 引擎已启动：{edges.Length} 个 IPv4 边缘，优先级 {priority}");
    }

    // WinDivertOpen error codes (WinDivert 2.2 docs plus what driver-load failures surface as) mapped
    // to something the user can act on. 1450 is the driver's DriverEntry failing; with no
    // CodeIntegrity event that is almost always third-party security software vetoing the load.
    internal static string DescribeOpenError(int err)
    {
        string? security = SecuritySoftware();
        string vetoHint = security is null ?
            "常见原因是安全软件拦截了驱动加载：请在其“驱动加载/系统加固”防护里放行 WinDivert64.sys，或临时关闭该防护后重启 MistSail。" :
            $"检测到 {security}，它会拦截 WinDivert 这类网络驱动的加载：请在其防护设置/信任区里放行 MistSail.exe 与 WinDivert64.sys（或临时关闭“驱动加载”类防护），然后重启 MistSail。";
        return err switch
        {
            2 or 3 => "找不到 WinDivert64.sys（应与 MistSail.exe 同目录，可能被杀软隔离）。",
            5 => "权限不足：需要以管理员身份运行 MistSail。",
            577 => "驱动签名校验失败（Windows 拒绝了 WinDivert64.sys 的签名，或文件被改动）。",
            654 => "系统里已加载另一个版本的 WinDivert 驱动（其他加速/抓包软件在用）：关掉那个软件或重启电脑后再试。",
            1058 => "Base Filtering Engine (BFE) 服务被禁用，WinDivert 依赖它。",
            1275 => "驱动被系统或安全软件屏蔽（如“易受攻击驱动阻止列表”或杀软）。" + vetoHint,
            1450 => "驱动加载被拒绝（系统资源不足/驱动入口失败）。" + vetoHint,
            _ => "需要管理员权限，且 WinDivert64.sys 可被加载。" + vetoHint
        };
    }

    // Security products known to veto WinDivert loads, detected by their kernel driver's service key
    // (readable without elevation).
    private static string? SecuritySoftware()
    {
        (string service, string name)[] known = [("hrwfpdrv", "火绒安全"), ("sysdiag", "火绒安全"), ("360FsFlt", "360 安全卫士"), ("TAOKernelEx", "腾讯电脑管家"), ("QMUdisk", "腾讯电脑管家")];
        try
        {
            using var services = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            foreach ((string service, string name) in known)
            {
                using var key = services?.OpenSubKey(service);
                if (key is not null) return name;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { }
        return null;
    }

    private string BuildFilter()
    {
        string outSet = string.Join(" or ", edges.Select(ip => $"ip.DstAddr == {ip}"));
        string inSet = string.Join(" or ", edges.Select(ip => $"ip.SrcAddr == {ip}"));
        // Outbound TLS records to the edges, plus inbound SYN-ACKs from them for autottl.
        return $"ip and tcp and ((outbound and tcp.DstPort == 443 and tcp.PayloadLength > 0 and ({outSet})) " +
               $"or (inbound and tcp.SrcPort == 443 and tcp.Syn and tcp.Ack and ({inSet})))";
    }

    private void RecvLoop()
    {
        try
        {
            byte[] buffer = new byte[65535];
            var addr = new WinDivertNative.Address();
            while (running)
            {
                if (!WinDivertNative.WinDivertRecv(handle, buffer, (uint)buffer.Length, out uint len, ref addr))
                {
                    if (!running) break;
                    int err = Marshal.GetLastWin32Error();
                    if (err == 995) break; // ERROR_OPERATION_ABORTED (shutdown)
                    continue;
                }
                try { Handle(buffer, (int)len, ref addr); }
                catch (Exception ex)
                {
                    // A malformed/edge-case packet must never terminate the process: this is a raw
                    // background thread, so an unhandled exception here kills the whole app.
                    DirectLog.Write(LogFile, $"desync 处理包异常（已重新注入原包）：{ex.GetType().Name} {ex.Message}");
                    try { Reinject(buffer, (int)len, ref addr); }
                    catch (Exception reinject) { DirectLog.Write(LogFile, $"desync 重新注入失败：{reinject.GetType().Name} {reinject.Message}"); }
                }
            }
        }
        catch (Exception ex)
        {
            // Last line of defence for the native receive loop. Stop rewriting instead of taking
            // the whole UI process down; the next global reconnect can re-arm the engine.
            running = false;
            armed = null;
            DirectLog.Write(LogFile, $"desync 接收线程异常退出：{ex.GetType().Name} {ex.Message}");
            try { EngineFailed?.Invoke(); }
            catch (Exception handler) { DirectLog.Write(LogFile, $"desync 失败回调异常：{handler.GetType().Name}"); }
        }
    }

    private void Handle(byte[] buffer, int len, ref WinDivertNative.Address addr)
    {
        var span = buffer.AsSpan(0, len);
        if (!addr.Outbound)
        {
            LearnTtl(span);
            Reinject(buffer, len, ref addr);
            return;
        }

        DesyncStrategy? strategy = armed;
        if (strategy is null || !PacketDesync.TryParse(span, out var layout))
        {
            Reinject(buffer, len, ref addr);
            return;
        }

        var payload = span.Slice(layout.PayloadOffset, layout.PayloadLength);
        if (payload.Length < 6 || payload[0] != 0x16 || payload[1] != 0x03 ||
            !FragmentingStream.TryFindServerName(payload, out int nameOffset, out int nameLength))
        {
            Reinject(buffer, len, ref addr); // not a ClientHello with a locatable SNI: leave it
            return;
        }

        // Build a fake that is a complete benign ClientHello exactly as long as this segment.
        byte[] benign = ClientHelloBuilder.Build(benignSni, chromeLike: true, sniLate: false, targetSize: Math.Max(64, layout.PayloadLength));
        List<InjectPacket> packets = PacketDesync.Build(span, strategy, benign);
        if (packets.Count == 0)
        {
            Reinject(buffer, len, ref addr);
            return;
        }

        string sni = System.Text.Encoding.ASCII.GetString(payload.Slice(nameOffset, nameLength)).TrimEnd('.');
        string dst = FormatDst(span, layout);
        foreach (InjectPacket packet in packets)
        {
            // Our builder already wrote correct checksums (or a deliberate bad one); tell WinDivert
            // not to touch them by marking them present on the injection address.
            var inject = addr;
            inject.MarkChecksumsPresent();
            if (!WinDivertNative.WinDivertSend(handle, packet.Bytes, (uint)packet.Bytes.Length, out _, ref inject))
                Report($"desync 注入失败 role={packet.Role} host={sni} ip={dst}（错误码 {Marshal.GetLastWin32Error()}）");
        }
        Report($"desync host={sni} ip={dst} 策略={strategy.Name} 注入 {packets.Count} 包（原包已丢弃）");
        // Original is intentionally not re-injected: the real bytes travel in the segments above.
    }

    internal void LearnTtl(ReadOnlySpan<byte> span)
    {
        // Inbound SYN-ACK: observed IP TTL -> hops from server -> cap a fake below that hop count.
        if (span.Length < 20 || (span[0] >> 4) != 4) return;
        byte observed = span[8];
        // ReadUInt32BigEndian avoids the checked-cast overflow that killed the app on the first
        // SYN-ACK from a 162.x/172.x edge (the project builds with CheckForOverflowUnderflow).
        uint srcKey = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(12, 4));
        int initial = observed <= 64 ? 64 : observed <= 128 ? 128 : 255;
        int hops = initial - observed;
        if (hops is < 2 or > 64) return;
        byte cap = (byte)Math.Max(2, hops - 1);
        ttlCap[srcKey] = cap;
    }

    private void Reinject(byte[] buffer, int len, ref WinDivertNative.Address addr)
    {
        WinDivertNative.WinDivertSend(handle, buffer, (uint)len, out _, ref addr);
    }

    private static string FormatDst(ReadOnlySpan<byte> span, PacketDesync.Layout layout) =>
        $"{span[16]}.{span[17]}.{span[18]}.{span[19]}";

    private static uint ToKey(IPAddress ip) =>
        BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());

    private void Report(string text)
    {
        DirectLog.Write(LogFile, text);
        Status?.Invoke(text);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        running = false;
        if (handle != WinDivertNative.InvalidHandle)
        {
            WinDivertNative.WinDivertShutdown(handle, WinDivertNative.ShutdownBoth);
            WinDivertNative.WinDivertClose(handle);
            handle = WinDivertNative.InvalidHandle;
        }
        try { worker?.Join(TimeSpan.FromSeconds(2)); } catch (ThreadStateException) { }
        DirectLog.Write(LogFile, "desync 引擎已停止。");
    }
}
