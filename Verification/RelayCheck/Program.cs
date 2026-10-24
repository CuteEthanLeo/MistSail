// Offline checks for the X relay. Nothing here touches the network: every socket is loopback
// and every "server" is a fake listener in this process.
//
//   dotnet run --project Verification\RelayCheck -- <repo-root> [--write-config]
//
// --write-config regenerates <repo-root>\direct-config.json from the built-in defaults so the
// shipped file can never drift from DirectOptions.

using Microsoft.Win32;
using MistSail.Direct;
using MistSail.Utils;
using System.IO;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

string root = args.Length > 0 ? Path.GetFullPath(args[0]) : Directory.GetCurrentDirectory();
bool writeConfig = args.Contains("--write-config");
int failures = 0;

void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? " — " + detail : string.Empty)}");
    if (!ok) failures++;
}

// 1. Built-in defaults cover every host the X web app needs, and generic edges come before the dead dedicated ones.
DirectOptions defaults = new();
defaults.Validate();
string[] required = ["x.com", "www.x.com", "api.x.com", "upload.x.com", "t.co", "twitter.com", "api.twitter.com",
                     "abs.twimg.com", "abs-0.twimg.com", "pbs.twimg.com", "video.twimg.com", "ton.twimg.com"];
Check("默认 RelayHosts 覆盖 X 主要域名", required.All(defaults.RelayHosts.ContainsKey),
    string.Join(",", required.Where(h => !defaults.RelayHosts.ContainsKey(h))));
Check("默认监听地址为 127.0.0.2", defaults.RelayListenAddress == "127.0.0.2");
Check("默认分片模式 auto（由线路诊断决定）", defaults.FragmentMode == HelloStrategy.Auto && defaults.ConfiguredStrategy is null);
Check("x.com 候选以通用 anycast 开头，专用 IP 在后", defaults.RelayHosts["x.com"][0] == "104.16.123.96" &&
    Array.IndexOf(defaults.RelayHosts["x.com"], "162.159.140.229") > 3);
Check("x.com 候选含 IPv6 边缘", defaults.RelayHosts["x.com"].Any(ip => ip.Contains(':')));
Check("旧配置值仍可解析", HelloStrategy.TryParse("tls-record", out var legacy) && legacy == HelloStrategy.RecordMid &&
    HelloStrategy.TryParse("off", out var off) && off == HelloStrategy.Plain && HelloStrategy.TryParse("tcp", out var tcp) && tcp == HelloStrategy.TcpMid);
Check("未知策略名被拒绝", !HelloStrategy.IsKnownName("bogus"));
Check("动态视频代理默认只含视频/YouTube API 域名",
    defaults.DynamicProxyEnabled && defaults.DynamicProxyHosts.Contains("googlevideo.com") &&
    defaults.DynamicProxyHosts.Contains("youtubei.googleapis.com") && !defaults.DynamicProxyOverrideExisting);
Check("动态视频域名规范化（去 * / 大小写 / 尾点）",
    DirectOptions.NormalizeDynamicHost("*.GOOGLEVIDEO.com") == "googlevideo.com" &&
    DirectOptions.NormalizeDynamicHost(" youtubei.googleapis.com ") == "youtubei.googleapis.com");
Check("动态视频域名拒绝协议、端口与路径", RejectsDynamicHost("https://googlevideo.com") && RejectsDynamicHost("googlevideo.com:443") && RejectsDynamicHost("googlevideo.com/x"));

static bool RejectsDynamicHost(string host)
{
    DirectOptions options = new() { DynamicProxyHosts = [host] };
    try { options.Validate(); }
    catch (InvalidDataException) { return true; }
    return false;
}

// 2. The shipped direct-config.json parses, validates and matches the defaults.
string configPath = Path.Combine(root, "direct-config.json");
if (writeConfig)
    File.WriteAllText(configPath, JsonSerializer.Serialize(defaults, DirectOptions.JsonOptions));
DirectOptions? shipped = null;
try
{
    shipped = JsonSerializer.Deserialize<DirectOptions>(File.ReadAllText(configPath), DirectOptions.JsonOptions);
    shipped?.Validate();
}
catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { Check("direct-config.json 可解析", false, ex.Message); }
if (shipped is not null)
{
    Check("direct-config.json 可解析并通过校验", true);
    Check("direct-config.json 的 RelayHosts 与默认值一致",
        shipped.RelayHosts.Count == defaults.RelayHosts.Count &&
        defaults.RelayHosts.All(pair => shipped.RelayHosts.TryGetValue(pair.Key, out var ips) && ips.SequenceEqual(pair.Value)));
    Check("direct-config.json 的 FragmentMode 为 auto", shipped.FragmentMode == HelloStrategy.Auto);
}

// 3. SNI parsing and every HelloStrategy on synthetic ClientHellos (small, Chrome-sized, SNI early/late).
foreach ((string label, byte[] hello) in new[]
{
    ("小型", ClientHelloBuilder.Build("x.com", chromeLike: false)),
    ("Chrome 形态", ClientHelloBuilder.Build("x.com", chromeLike: true)),
    ("Chrome 形态 SNI 靠后", ClientHelloBuilder.Build("x.com", chromeLike: true, sniLate: true)),
    ("长域名", ClientHelloBuilder.Build("pbs.twimg.com", chromeLike: true))
})
{
    string expectedName = label == "长域名" ? "pbs.twimg.com" : "x.com";
    bool found = FragmentingStream.TryFindServerName(hello, out int nameOffset, out int nameLength);
    Check($"[{label}] TryFindServerName 找到 SNI", found && Encoding.ASCII.GetString(hello, nameOffset, nameLength) == expectedName, $"len={hello.Length}");
    if (label.StartsWith("Chrome")) Check($"[{label}] 大小接近 Chrome（>1500 字节）", hello.Length > 1500, $"len={hello.Length}");
    if (label == "Chrome 形态 SNI 靠后") Check($"[{label}] SNI 位于第一个 MSS 之外", nameOffset > 1200, $"offset={nameOffset}");

    foreach (HelloStrategy strategy in HelloStrategy.All)
    {
        List<HelloWrite> writes = HelloShaper.Shape(hello, nameOffset, nameLength, strategy);
        byte[] wire = HelloShaper.Concat(writes.Select(w => w.Bytes));
        (int records, byte[] body, bool clean) = HelloShaper.Reassemble(wire);
        Check($"[{label}] {strategy.Name}：record 重组后握手字节原样", clean && body.AsSpan().SequenceEqual(hello.AsSpan(5)), $"records={records}, writes={writes.Count}");

        // No single record may carry the whole host name unless the strategy does not fragment at all.
        bool nameWhole = false;
        int pos = 0;
        while (pos + 5 <= wire.Length)
        {
            int len = BinaryPrimitives.ReadUInt16BigEndian(wire.AsSpan(pos + 3, 2));
            if (wire.AsSpan(pos + 5, len).IndexOf(Encoding.ASCII.GetBytes(expectedName)) >= 0) nameWhole = true;
            pos += 5 + len;
        }
        bool recordLayout = strategy.Layout is not (HelloLayout.Plain or HelloLayout.TcpMid or HelloLayout.TcpMidDelay);
        if (recordLayout) Check($"[{label}] {strategy.Name}：没有任何单个 record 完整包含域名", !nameWhole, $"records={records}");
        else Check($"[{label}] {strategy.Name}：保持单个 record", records == 1 && wire.AsSpan().SequenceEqual(hello));

        switch (strategy.Layout)
        {
            case HelloLayout.RecordMisalign:
            case HelloLayout.RecordMisalignB:
            case HelloLayout.RecordBytes:
            {
                // Record boundaries in the wire stream; every non-final write must end elsewhere.
                var boundaries = new HashSet<int>();
                int p = 0;
                while (p + 5 <= wire.Length) { p += 5 + BinaryPrimitives.ReadUInt16BigEndian(wire.AsSpan(p + 3, 2)); boundaries.Add(p); }
                int end = 0;
                bool aligned = false;
                for (int i = 0; i < writes.Count - 1; i++) { end += writes[i].Bytes.Length; if (boundaries.Contains(end)) aligned = true; }
                Check($"[{label}] {strategy.Name}：TCP 写边界都不落在 record 边界上", !aligned && writes.Count >= 2, $"writes={writes.Count}");
                Check($"[{label}] {strategy.Name}：写之间有停顿", writes.Take(writes.Count - 1).All(w => w.DelayAfterMs >= HelloStrategy.DefaultGapMs));
                if (strategy.Layout == HelloLayout.RecordMisalignB)
                    Check($"[{label}] {strategy.Name}：首次写截在第一条 record 体内", writes[0].Bytes.Length > 5 && writes[0].Bytes.Length < 5 + BinaryPrimitives.ReadUInt16BigEndian(wire.AsSpan(3, 2)));
                else
                    Check($"[{label}] {strategy.Name}：首次写 = 第一条 record + 2 字节", writes[0].Bytes.Length == 5 + BinaryPrimitives.ReadUInt16BigEndian(wire.AsSpan(3, 2)) + 2);
                break;
            }
            case HelloLayout.RecordZero4:
            {
                int zeros = 0;
                int p = 0;
                while (p + 5 <= wire.Length && BinaryPrimitives.ReadUInt16BigEndian(wire.AsSpan(p + 3, 2)) == 0) { zeros++; p += 5; }
                Check($"[{label}] {strategy.Name}：恰有 4 条空 record 且在最前", zeros == 4 && records == 6 && writes.Count == 1, $"zeros={zeros}, records={records}");
                break;
            }
            case HelloLayout.Record2:
                Check($"[{label}] {strategy.Name}：两条 record 一次写出", records == 2 && writes.Count == 1);
                break;
            case HelloLayout.RecordMidDelay:
            case HelloLayout.TcpMidDelay:
                Check($"[{label}] {strategy.Name}：两次写，中间停顿 ≥1 s", writes.Count == 2 && writes[0].DelayAfterMs >= 1000);
                break;
            case HelloLayout.RecordMid:
                Check($"[{label}] {strategy.Name}：4 条 record 各自一次写", records == 4 && writes.Count == 4);
                break;
            case HelloLayout.Plain:
                Check($"[{label}] {strategy.Name}：一次写出", writes.Count == 1 && writes[0].DelayAfterMs == 0);
                break;
        }
    }
}

// FragmentingStream (used by DoH bootstrap and the CONNECT transport) must shape the same way.
{
    byte[] hello = ClientHelloBuilder.Build("x.com", chromeLike: false);
    foreach (string mode in new[] { "record-misalign", "tls-record", "tcp", "off", "auto" })
    {
        DirectOptions modeOptions = new() { FragmentMode = mode, FragmentDelayMs = 0 };
        modeOptions.Validate();
        using MemoryStream sink = new();
        await using (FragmentingStream fragmenting = new(sink, modeOptions.StrategyFor("x.com"), leaveOpen: true))
        {
            // Two partial writes, as a TLS stack may do: the stream must buffer until the record is whole.
            await fragmenting.WriteAsync(hello.AsMemory(0, 20));
            await fragmenting.WriteAsync(hello.AsMemory(20));
            await fragmenting.FlushPendingAsync(CancellationToken.None);
        }
        (int records, byte[] body, bool clean) = HelloShaper.Reassemble(sink.ToArray());
        Check($"FragmentingStream 模式 {mode}：握手字节原样", clean && body.AsSpan().SequenceEqual(hello.AsSpan(5)), $"records={records}");
        if (mode is "off" or "tcp") Check($"FragmentingStream 模式 {mode}：单 record", records == 1);
        else Check($"FragmentingStream 模式 {mode}：多 record", records >= 2, $"records={records}");
    }
}

// 4. Loopback relay round-trip: browser -> relay(127.0.0.2) -> fake server(127.0.0.1), all in-process.
{
    using TcpListener fakeServer = new(IPAddress.Loopback, 0);
    fakeServer.Start();
    int fakePort = ((IPEndPoint)fakeServer.LocalEndpoint).Port;
    DirectOptions relayOptions = new() { FragmentDelayMs = 1, ConnectTimeoutSeconds = 3 };
    relayOptions.RelayHosts = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { ["self-test.invalid"] = ["127.0.0.1"] };
    relayOptions.Validate();
    using SniRelay relay = new(relayOptions, listenPort: 0, targetPort: fakePort);
    bool started = false;
    try { relay.Start(); started = true; }
    catch (SocketException ex) { Check("中继可绑定 127.0.0.2（Windows 回环）", false, ex.Message); }
    if (started)
    {
        Check("中继可绑定 127.0.0.2（Windows 回环）", relay.EndPoint.Address.ToString() == "127.0.0.2", relay.EndPoint.ToString());
        Check("中继默认策略为 record-misalign", relay.CurrentStrategy == HelloStrategy.RecordMisalign with { GapMs = 1 }, relay.CurrentStrategy.Name);
        byte[] clientHello = ClientHelloBuilder.Build("self-test.invalid", chromeLike: true);
        var serverSide = Task.Run(async () =>
        {
            using TcpClient accepted = await fakeServer.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            NetworkStream stream = accepted.GetStream();
            using MemoryStream received = new();
            byte[] buffer = new byte[4096];
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
            while (true)
            {
                int read;
                try { read = await stream.ReadAsync(buffer, timeout.Token); }
                catch (OperationCanceledException) { break; }
                if (read == 0) break;
                received.Write(buffer, 0, read);
                (int records, byte[] body, bool clean) = HelloShaper.Reassemble(received.ToArray());
                if (clean && body.Length >= clientHello.Length - 5 && records > 0) { await Task.Delay(100); break; }
            }
            await stream.WriteAsync(new byte[] { 0x16, 0x03, 0x03, 0x00, 0x01, 0x02 });
            await Task.Delay(200);
            return received.ToArray();
        });
        using TcpClient browser = new();
        await browser.ConnectAsync(relay.EndPoint.Address, relay.EndPoint.Port);
        NetworkStream browserStream = browser.GetStream();
        await browserStream.WriteAsync(clientHello);
        byte[] reply = new byte[16];
        int replyLength;
        using (CancellationTokenSource replyTimeout = new(TimeSpan.FromSeconds(5)))
            replyLength = await browserStream.ReadAsync(reply, replyTimeout.Token);
        byte[] serverSaw = await serverSide;
        (int seenRecords, byte[] seenBody, bool seenClean) = HelloShaper.Reassemble(serverSaw);
        Check("假服务器收到分片后的 ClientHello 且字节可重组", seenClean && seenBody.AsSpan().SequenceEqual(clientHello.AsSpan(5)), $"records={seenRecords}");
        Check("ClientHello 被拆成多个 TLS record", seenRecords >= 3, $"records={seenRecords}");
        Check("浏览器收到假服务器的 ServerHello 字节", replyLength == 6 && reply[0] == 0x16);
        await Task.Delay(300);
        Check("中继记录了“收到 ServerHello”证据并带策略名", relay.LastResultByHost.TryGetValue("self-test.invalid", out string? evidence) && evidence.Contains("ServerHello") && evidence.Contains("[record-misalign]"), evidence ?? "(none)");
        Check("中继缓存了浏览器的 ClientHello 供诊断重放", relay.LastClientHelloByHost.TryGetValue("self-test.invalid", out byte[]? cached) && cached.AsSpan().SequenceEqual(clientHello));
    }
}

// 5. In-connection failover: the first target resets after the ClientHello, the second answers.
{
    IPAddress resetAddress = IPAddress.Parse("127.0.0.3");
    IPAddress goodAddress = IPAddress.Parse("127.0.0.4");
    using TcpListener resetServer = new(resetAddress, 0);
    resetServer.Start();
    int port = ((IPEndPoint)resetServer.LocalEndpoint).Port;
    using TcpListener goodServer = new(goodAddress, port);
    goodServer.Start();
    var resetSide = Task.Run(async () =>
    {
        using TcpClient accepted = await resetServer.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        byte[] buffer = new byte[64];
        await accepted.GetStream().ReadAsync(buffer);
        accepted.LingerState = new LingerOption(true, 0); // abortive close => RST
        accepted.Close();
    });
    var goodSide = Task.Run(async () =>
    {
        using TcpClient accepted = await goodServer.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(8));
        NetworkStream stream = accepted.GetStream();
        byte[] buffer = new byte[4096];
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
        using MemoryStream received = new();
        while (true)
        {
            int read;
            try { read = await stream.ReadAsync(buffer, timeout.Token); } catch (OperationCanceledException) { break; }
            if (read == 0) break;
            received.Write(buffer, 0, read);
            if (HelloShaper.Reassemble(received.ToArray()).Clean) break;
        }
        await stream.WriteAsync(new byte[] { 0x16, 0x03, 0x03, 0x00, 0x01, 0x02 });
        await Task.Delay(200);
    });
    DirectOptions relayOptions = new() { FragmentDelayMs = 1, ConnectTimeoutSeconds = 3, HandshakeTimeoutSeconds = 2 };
    relayOptions.RelayHosts = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { ["failover.invalid"] = [resetAddress.ToString(), goodAddress.ToString()] };
    relayOptions.Validate();
    // GoodIps pins the resetting target first so the failover path is exercised deterministically.
    DirectState state = new() { GoodIps = [resetAddress.ToString()], NetworkKey = DirectState.CurrentNetworkKey(), SavedAt = DateTime.UtcNow };
    using SniRelay relay = new(relayOptions, listenPort: 0, targetPort: port, state: state);
    relay.Start();
    using TcpClient browser = new();
    await browser.ConnectAsync(relay.EndPoint.Address, relay.EndPoint.Port);
    await browser.GetStream().WriteAsync(ClientHelloBuilder.Build("failover.invalid"));
    byte[] reply = new byte[8];
    int replyLength = 0;
    try
    {
        using CancellationTokenSource replyTimeout = new(TimeSpan.FromSeconds(10));
        replyLength = await browser.GetStream().ReadAsync(reply, replyTimeout.Token);
    }
    catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
    await Task.WhenAll(resetSide, goodSide).WaitAsync(TimeSpan.FromSeconds(10));
    await Task.Delay(300);
    Check("故障切换：浏览器仍收到 ServerHello", replyLength == 6 && reply[0] == 0x16, $"len={replyLength}");
    Check("故障切换：证据来自第二个目标", relay.LastResultByHost.TryGetValue("failover.invalid", out string? evidence) && evidence.Contains(goodAddress.ToString()) && evidence.Contains("ServerHello"), evidence ?? "(none)");
    DirectState snapshot = relay.Snapshot();
    Check("故障切换：RST 不把 IP 记为不可达", !snapshot.DeadIps.ContainsKey(resetAddress.ToString()), string.Join(",", snapshot.DeadIps.Keys));
}

// 6. A target known to be dead is never tried while another candidate exists.
{
    IPAddress deadAddress = IPAddress.Parse("127.0.0.5");
    IPAddress goodAddress = IPAddress.Parse("127.0.0.6");
    using TcpListener deadServer = new(deadAddress, 0);
    deadServer.Start();
    int port = ((IPEndPoint)deadServer.LocalEndpoint).Port;
    using TcpListener goodServer = new(goodAddress, port);
    goodServer.Start();
    bool deadTouched = false;
    _ = Task.Run(async () =>
    {
        try { using var accepted = await deadServer.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(6)); deadTouched = true; }
        catch (TimeoutException) { }
    });
    var goodSide = Task.Run(async () =>
    {
        using TcpClient accepted = await goodServer.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(6));
        byte[] buffer = new byte[4096];
        await accepted.GetStream().ReadAsync(buffer);
        await Task.Delay(50);
        await accepted.GetStream().WriteAsync(new byte[] { 0x16, 0x03, 0x03, 0x00, 0x01, 0x02 });
        await Task.Delay(200);
    });
    DirectOptions relayOptions = new() { FragmentDelayMs = 1, ConnectTimeoutSeconds = 3, HandshakeTimeoutSeconds = 2 };
    relayOptions.RelayHosts = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { ["dead.invalid"] = [deadAddress.ToString(), goodAddress.ToString()] };
    relayOptions.Validate();
    DirectState state = new() { DeadIps = new() { [deadAddress.ToString()] = DateTime.UtcNow.AddHours(1) }, NetworkKey = DirectState.CurrentNetworkKey(), SavedAt = DateTime.UtcNow };
    using SniRelay relay = new(relayOptions, listenPort: 0, targetPort: port, state: state);
    relay.Start();
    using TcpClient browser = new();
    await browser.ConnectAsync(relay.EndPoint.Address, relay.EndPoint.Port);
    await browser.GetStream().WriteAsync(ClientHelloBuilder.Build("dead.invalid"));
    byte[] reply = new byte[8];
    int replyLength = 0;
    try
    {
        using CancellationTokenSource replyTimeout = new(TimeSpan.FromSeconds(8));
        replyLength = await browser.GetStream().ReadAsync(reply, replyTimeout.Token);
    }
    catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
    await goodSide.WaitAsync(TimeSpan.FromSeconds(8));
    await Task.Delay(300);
    Check("已知死 IP：浏览器直接从备选目标拿到 ServerHello", replyLength == 6 && reply[0] == 0x16, $"len={replyLength}");
    Check("已知死 IP：从未被连接", !deadTouched);
}

// 7. Unreachable target is reported as a TCP failure (no silent hang).
{
    using TcpListener closed = new(IPAddress.Loopback, 0);
    closed.Start();
    int closedPort = ((IPEndPoint)closed.LocalEndpoint).Port;
    closed.Stop(); // port is now free and refuses connections
    DirectOptions relayOptions = new() { ConnectTimeoutSeconds = 2 };
    relayOptions.RelayHosts = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { ["refused.invalid"] = ["127.0.0.1"] };
    relayOptions.Validate();
    using SniRelay relay = new(relayOptions, listenPort: 0, targetPort: closedPort);
    relay.Start();
    using TcpClient browser = new();
    await browser.ConnectAsync(relay.EndPoint.Address, relay.EndPoint.Port);
    await browser.GetStream().WriteAsync(ClientHelloBuilder.Build("refused.invalid"));
    byte[] reply = new byte[8];
    int replyLength;
    try
    {
        using CancellationTokenSource replyTimeout = new(TimeSpan.FromSeconds(8));
        replyLength = await browser.GetStream().ReadAsync(reply, replyTimeout.Token);
    }
    catch (Exception ex) when (ex is IOException or OperationCanceledException) { replyLength = 0; }
    await Task.Delay(300);
    Check("不可达目标：浏览器侧连接被关闭", replyLength == 0);
    Check("不可达目标：记录为 TCP 连接失败", relay.LastResultByHost.TryGetValue("refused.invalid", out string? evidence) && evidence.Contains("TCP 连接失败"), evidence ?? "(none)");
}

// 8. direct-state.json round-trip, expiry and network binding.
{
    string statePath = Path.Combine(Path.GetTempPath(), $"mistsail-state-{Guid.NewGuid():N}.json");
    try
    {
        DirectState state = new()
        {
            Strategy = "record-2",
            HostStrategies = new(StringComparer.OrdinalIgnoreCase) { ["pbs.twimg.com"] = "record-misalign-b" },
            GoodIps = ["104.16.123.96"],
            DeadIps = new() { ["162.159.140.229"] = DateTime.UtcNow.AddHours(6), ["172.66.0.227"] = DateTime.UtcNow.AddHours(-1) },
            NetworkKey = "net-a",
            SavedAt = DateTime.UtcNow
        };
        state.Save(statePath);
        DirectState? loaded = DirectState.Load(statePath, "net-a", DateTime.UtcNow);
        Check("状态文件往返", loaded is not null && loaded.Strategy == "record-2" && loaded.HostStrategies["pbs.twimg.com"] == "record-misalign-b" && loaded.GoodIps.SequenceEqual(["104.16.123.96"]));
        Check("状态文件：过期的死 IP 被丢弃", loaded is not null && loaded.DeadIps.ContainsKey("162.159.140.229") && !loaded.DeadIps.ContainsKey("172.66.0.227"));
        Check("状态文件：换网络即作废", DirectState.Load(statePath, "net-b", DateTime.UtcNow) is null);
        Check("状态文件：12 小时后作废", DirectState.Load(statePath, "net-a", DateTime.UtcNow + DirectState.Lifetime + TimeSpan.FromMinutes(1)) is null);
        Check("状态文件：未知策略名被忽略", File.Exists(statePath) && Ignores(statePath));
    }
    finally { try { File.Delete(statePath); } catch (IOException) { } }

    static bool Ignores(string path)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(new DirectState { Strategy = "bogus", NetworkKey = "k", SavedAt = DateTime.UtcNow }, DirectOptions.JsonOptions));
        DirectState? loaded = DirectState.Load(path, "k", DateTime.UtcNow);
        return loaded is not null && loaded.Strategy is null;
    }
}

// 9. The upstream rule snapshot that already plays YouTube must stay byte-identical.
string upstreamPath = Path.Combine(root, "vendor", "Cealing-Host-U.json");
string upstreamHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(upstreamPath)));
string expectedHashPath = Path.Combine(root, "vendor", "Cealing-Host-U.sha256");
string expectedHash = File.Exists(expectedHashPath) ? File.ReadAllText(expectedHashPath).Trim().Split(' ')[0] : string.Empty;
Check("vendor/Cealing-Host-U.json 未被改动（与 vendor/Cealing-Host-U.sha256 一致）", expectedHash.Equals(upstreamHash, StringComparison.OrdinalIgnoreCase), upstreamHash);

// 9b. Local supplement rules: google.com.hk is added here, never in the hash-protected upstream file.
{
    string localPath = Path.Combine(root, "Cealing-Host-L.json");
    bool exists = File.Exists(localPath);
    bool parsed = false;
    bool hasGoogleHk = false;
    Check("Cealing-Host-L.json 存在", exists, localPath);
    if (exists)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(localPath));
            parsed = true;
            foreach (JsonElement rule in document.RootElement.EnumerateArray())
            {
                if (rule.ValueKind != JsonValueKind.Array || rule.GetArrayLength() != 3) continue;
                if (rule[1].GetString() != "g.cn" || rule[2].GetString() != "183.56.143.147") continue;
                foreach (JsonElement domain in rule[0].EnumerateArray())
                    if (string.Equals(domain.GetString(), "*google.com.hk", StringComparison.OrdinalIgnoreCase))
                        hasGoogleHk = true;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Check("Cealing-Host-L.json 可解析", false, ex.Message);
        }
    }
    Check("Cealing-Host-L.json 可解析", parsed);
    Check("本地规则包含 google.com.hk（g.cn / 183.56.143.147）", hasGoogleHk);
}

// 10. The old nginx-based X files must be gone so hosts never points X at Nginx.
Check("已移除 Cealing-Host-X.json", !File.Exists(Path.Combine(root, "Cealing-Host-X.json")));
Check("已移除 Utils/XGlobalConfig.cs", !File.Exists(Path.Combine(root, "Utils", "XGlobalConfig.cs")));

// 11. Kernel-level desync (PacketDesync): pure, in-memory packet math — no driver, no network.
{
    Check("默认 DesyncMode 为 auto（内核方案由诊断自动决定）", defaults.DesyncMode == DesyncStrategy.Auto && defaults.DesyncEnabled && defaults.ConfiguredDesync is null);
    Check("DesyncMode 解析 off/auto/策略名，拒绝未知", DesyncStrategy.IsKnownName("off") && DesyncStrategy.IsKnownName("auto") &&
        DesyncStrategy.IsKnownName(DesyncStrategy.All[0].Name) && !DesyncStrategy.IsKnownName("bogus"));
    IPAddress[] edges = defaults.DesyncEdges();
    Check("DesyncEdges 含 Cloudflare/Fastly IPv4，且不含 IPv6", edges.Length > 0 &&
        edges.All(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) &&
        edges.Any(ip => ip.ToString() == "104.16.123.96") && edges.Any(ip => ip.ToString() == "146.75.72.157"));

    uint seq = 1000;
    byte[] realHello = ClientHelloBuilder.Build("x.com", chromeLike: true);
    byte[] packet = BuildPacket(realHello, seq);
    byte[] benign = ClientHelloBuilder.Build("www.bing.com", chromeLike: true, sniLate: false, targetSize: realHello.Length);
    Check("合成外发包可被 PacketDesync 解析", PacketDesync.TryParse(packet, out var synLayout) && synLayout.PayloadLength == realHello.Length);

    DesyncStrategy[] sample =
    [
        DesyncStrategy.All.First(s => s.Method == DesyncMethod.Fake && s.UsesTtl),
        DesyncStrategy.All.First(s => s.Method == DesyncMethod.FakeSplit && s.UsesTtl),
        DesyncStrategy.All.First(s => s.Method == DesyncMethod.FakeDisorder && s.UsesTtl),
        DesyncStrategy.FakeBadSum,
        DesyncStrategy.Disorder
    ];
    foreach (DesyncStrategy ds in sample)
    {
        var packets = PacketDesync.Build(packet, ds, benign);
        bool structureOk = ds.Method switch
        {
            DesyncMethod.Disorder => packets.Count == 2 && packets.All(p => !p.Fake),
            DesyncMethod.Fake => packets.Count == 2 && packets[0].Fake && !packets[1].Fake,
            _ => packets.Count == 3 && packets[0].Fake && !packets[1].Fake && !packets[2].Fake
        };
        Check($"desync[{ds.Name}] 包结构正确", structureOk);

        // The real (non-fake) segments, ordered by sequence, must reassemble to the exact original.
        byte[] reassembled = Reassemble(packets.Where(p => !p.Fake).Select(p => p.Bytes));
        Check($"desync[{ds.Name}] 真实段按序重组 == 原始 ClientHello", reassembled.SequenceEqual(realHello));

        // Every real segment carries valid IP and TCP checksums and the original TTL.
        Check($"desync[{ds.Name}] 真实段校验和有效、TTL 不变",
            packets.Where(p => !p.Fake).All(p => ValidIp(p.Bytes) && ValidTcp(p.Bytes) && p.Bytes[8] == 64));

        if (ds.UsesFake)
        {
            byte[] fake = packets[0].Bytes;
            Check($"desync[{ds.Name}] 假包携带无害 SNI（非 x.com）", FakeSni(fake) == "www.bing.com");
            Check($"desync[{ds.Name}] 假包 IP 校验和有效", ValidIp(fake));
            if (ds.UsesTtl)
                Check($"desync[{ds.Name}] 假包 TTL == {ds.Ttl} 且 TCP 校验和有效", fake[8] == ds.Ttl && ValidTcp(fake));
            if (ds.UsesBadSum)
                Check($"desync[{ds.Name}] 假包 TCP 校验和被故意写坏", !ValidTcp(fake));
        }
    }

    // autottl ladder is bounded below the measured hop count so a fake can never reach the server.
    Check("TTL 上限会裁剪策略表", DesyncStrategy.BuildAll(5).All(s => !s.UsesTtl || s.Ttl < 5) &&
        DesyncStrategy.BuildAll(5).Count < DesyncStrategy.BuildAll(0).Count);

    static byte[] BuildPacket(byte[] payload, uint seq)
    {
        byte[] pkt = new byte[40 + payload.Length];
        pkt[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(2), (ushort)pkt.Length);
        pkt[8] = 64;
        pkt[9] = 6;
        new byte[] { 10, 0, 0, 2 }.CopyTo(pkt, 12);
        new byte[] { 104, 16, 123, 96 }.CopyTo(pkt, 16);
        BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(20), 50000);
        BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(22), 443);
        BinaryPrimitives.WriteUInt32BigEndian(pkt.AsSpan(24), seq);
        pkt[32] = 0x50;
        pkt[33] = 0x18;
        BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(34), 65535);
        payload.CopyTo(pkt, 40);
        return pkt;
    }
    static byte[] Reassemble(IEnumerable<byte[]> segments)
    {
        var ordered = segments
            .Select(s => { PacketDesync.TryParse(s, out var l); return (seq: l.Seq, payload: s[l.PayloadOffset..]); })
            .OrderBy(e => e.seq).ToList();
        return ordered.SelectMany(e => e.payload).ToArray();
    }
    static string FakeSni(byte[] fake)
    {
        PacketDesync.TryParse(fake, out var fl);
        ReadOnlySpan<byte> payload = fake.AsSpan(fl.PayloadOffset, fl.PayloadLength);
        return FragmentingStream.TryFindServerName(payload, out int fo, out int flen)
            ? Encoding.ASCII.GetString(payload.Slice(fo, flen).ToArray()) : string.Empty;
    }
    static bool ValidIp(byte[] pkt) { int ihl = (pkt[0] & 0x0F) * 4; return Fold(Sum(pkt.AsSpan(0, ihl))) == 0; }
    static bool ValidTcp(byte[] pkt)
    {
        int ihl = (pkt[0] & 0x0F) * 4;
        int tcpLen = BinaryPrimitives.ReadUInt16BigEndian(pkt.AsSpan(2)) - ihl;
        uint sum = 0;
        sum += (uint)((pkt[12] << 8) | pkt[13]); sum += (uint)((pkt[14] << 8) | pkt[15]);
        sum += (uint)((pkt[16] << 8) | pkt[17]); sum += (uint)((pkt[18] << 8) | pkt[19]);
        sum += 6; sum += (uint)tcpLen; sum += Sum(pkt.AsSpan(ihl, tcpLen));
        return Fold(sum) == 0;
    }
    static uint Sum(ReadOnlySpan<byte> data)
    {
        uint sum = 0; int i = 0;
        for (; i + 1 < data.Length; i += 2) sum += (uint)((data[i] << 8) | data[i + 1]);
        if (i < data.Length) sum += (uint)(data[i] << 8);
        return sum;
    }
    static ushort Fold(uint sum) { while ((sum >> 16) != 0) sum = (sum & 0xFFFF) + (sum >> 16); return (ushort)(~sum & 0xFFFF); }
}

// 12. A whole connection made only of silent targets must fail fast, not hang for tens of seconds.
{
    using TcpListener blackhole = new(IPAddress.Parse("127.0.0.7"), 0);
    blackhole.Start();
    int port = ((IPEndPoint)blackhole.LocalEndpoint).Port;
    var held = new List<TcpClient>();
    _ = Task.Run(async () =>
    {
        try
        {
            while (true)
            {
                TcpClient client = await blackhole.AcceptTcpClientAsync();
                lock (held) held.Add(client);
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException or InvalidOperationException) { }
    });
    try
    {
        DirectOptions relayOptions = new() { FragmentDelayMs = 1, ConnectTimeoutSeconds = 1, HandshakeTimeoutSeconds = 1 };
        relayOptions.RelayHosts = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { ["blackhole.invalid"] = ["127.0.0.7"] };
        relayOptions.Validate();
        using SniRelay relay = new(relayOptions, listenPort: 0, targetPort: port);
        relay.Start();
        using TcpClient browser = new();
        await browser.ConnectAsync(relay.EndPoint.Address, relay.EndPoint.Port);
        await browser.GetStream().WriteAsync(ClientHelloBuilder.Build("blackhole.invalid"));
        byte[] reply = new byte[8];
        var watch = Stopwatch.StartNew();
        int replyLength;
        try
        {
            using CancellationTokenSource replyTimeout = new(TimeSpan.FromSeconds(10));
            replyLength = await browser.GetStream().ReadAsync(reply, replyTimeout.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { replyLength = -1; }
        watch.Stop();
        await Task.Delay(200);
        Check("静默候选：整条连接在 3 次尝试后关闭（不再挂起几十秒）",
            replyLength == 0 && watch.Elapsed < TimeSpan.FromSeconds(7), $"{watch.ElapsedMilliseconds}ms");
        Check("静默候选：最后证据为 Timeout",
            relay.LastResultByHost.TryGetValue("blackhole.invalid", out string? evidence) && evidence.Contains("无任何响应"), evidence ?? "(none)");
    }
    finally
    {
        lock (held) foreach (TcpClient client in held) client.Dispose();
        blackhole.Stop();
    }
}

// 13. Loopback PAC generation/serving and system-PAC URL recognition. No registry writes here.
{
    string script = PacServer.BuildScript(17881, ["googlevideo.com", "youtubei.googleapis.com"]);
    Check("PAC 脚本：视频域名走本机代理", script.Contains("PROXY 127.0.0.1:17881", StringComparison.Ordinal) &&
        script.Contains("dnsDomainIs(host, \".\" + d)", StringComparison.Ordinal) && script.Contains("\"googlevideo.com\"", StringComparison.Ordinal));
    Check("PAC 脚本：其余域名 DIRECT", script.Contains("return \"DIRECT\";", StringComparison.Ordinal));
    using PacServer server = new(["googlevideo.com"], 17881);
    server.Start();
    Check("PAC 只监听 127.0.0.1 临时端口", server.Port > 0 && server.PacUrl.StartsWith("http://127.0.0.1:", StringComparison.Ordinal), server.PacUrl);
    using HttpClient pacClient = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
    string body = await pacClient.GetStringAsync(server.PacUrl);
    Check("PAC HTTP 返回可执行脚本", body.Contains("FindProxyForURL", StringComparison.Ordinal) && body.Contains("googlevideo.com", StringComparison.Ordinal));
    Check("系统 PAC URL 识别只接受本程序回环地址",
        SystemProxy.IsOwnPacUrl("http://127.0.0.1:17882/mistsail.pac") &&
        !SystemProxy.IsOwnPacUrl("https://127.0.0.1:17882/mistsail.pac") &&
        !SystemProxy.IsOwnPacUrl("http://example.com/mistsail.pac") &&
        !SystemProxy.IsOwnPacUrl(null));

    DirectOptions noRefresh = new() { RefreshCandidatesFromDoh = false };
    using SniRelay relay = new(noRefresh, listenPort: 0, targetPort: 1);
    int added = await relay.RefreshCandidatesAsync(["x.com"], CancellationToken.None);
    Check("关闭 RefreshCandidatesFromDoh 时不做 DoH", added == 0);
}

// 14. Full PAC write/restore path in a throwaway HKCU subtree (never the real Internet Settings).
{
    string testKeyPath = @"Software\MistSail\ProxyTest-" + Guid.NewGuid().ToString("N");
    string testBackup = Path.Combine(Path.GetTempPath(), $"mistsail-proxy-{Guid.NewGuid():N}.json");
    string oldKeyPath = SystemProxy.SettingsKeyPath;
    string oldBackupPath = SystemProxy.BackupFilePath;
    const string ourPac = "http://127.0.0.1:45678/mistsail.pac";
    const string corpPac = "https://corp.example/proxy.pac";
    try
    {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(testKeyPath)!)
        {
            key.SetValue("AutoConfigURL", corpPac, RegistryValueKind.String);
            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            key.SetValue("ProxyServer", "10.0.0.1:8080", RegistryValueKind.String);
        }
        SystemProxy.SettingsKeyPath = testKeyPath;
        SystemProxy.BackupFilePath = testBackup;

        Check("已有 PAC 时默认不覆盖", !SystemProxy.Enable(ourPac, false, out string? reason) && reason?.Contains("PAC") == true, reason ?? "(none)");
        Check("已有 PAC 时原值保持不变", SystemProxy.CurrentAutoConfigUrl == corpPac);
        Check("显式覆盖后启用本机 PAC", SystemProxy.Enable(ourPac, true, out reason), reason ?? "(none)");
        Check("覆盖时固定代理被暂停", SystemProxy.CurrentAutoConfigUrl == ourPac &&
            ReadDword(testKeyPath, "ProxyEnable") == 0);
        SystemProxy.Restore();
        Check("恢复原 PAC 与固定代理", SystemProxy.CurrentAutoConfigUrl == corpPac &&
            ReadDword(testKeyPath, "ProxyEnable") == 1 && !File.Exists(testBackup));

        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(testKeyPath, writable: true)!)
        {
            key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
            key.DeleteValue("ProxyEnable", throwOnMissingValue: false);
            key.DeleteValue("ProxyServer", throwOnMissingValue: false);
        }
        Check("无原设置时启用并恢复为未设置", SystemProxy.Enable(ourPac, false, out reason) &&
            SystemProxy.CurrentAutoConfigUrl == ourPac);
        SystemProxy.Restore();
        Check("无原设置时恢复后删除 AutoConfigURL", SystemProxy.CurrentAutoConfigUrl is null or "");
    }
    finally
    {
        SystemProxy.SettingsKeyPath = oldKeyPath;
        SystemProxy.BackupFilePath = oldBackupPath;
        try { Registry.CurrentUser.DeleteSubKeyTree(testKeyPath, throwOnMissingSubKey: false); } catch (Exception) { }
        try { File.Delete(testBackup); } catch (IOException) { }
    }

    static int? ReadDword(string keyPath, string name)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(name) is int value ? value : null;
    }
}

// 15. WinDivert autottl: high first octets must not overflow the checked uint cast. This exact
// OverflowException in LearnTtl terminated MistSail three times on 2026-10-24 (event 1026).
{
    using WinDivertDesync engine = new(new[] { IPAddress.Parse("162.159.140.229") });
    byte[] synack = new byte[40];
    synack[0] = 0x45;
    synack[8] = 53; // observed TTL -> 11 hops -> cap 10
    synack[12] = 162;
    synack[13] = 159;
    synack[14] = 140;
    synack[15] = 229;
    engine.LearnTtl(synack);
    Check("desync TTL 学习：高位首字节 IP 不溢出且得到 hop cap",
        engine.TtlCapFor(IPAddress.Parse("162.159.140.229")) > 0);
}

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED (offline only; no network was used)" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;
