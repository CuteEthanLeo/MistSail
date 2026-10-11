// Offline checks for the X relay. Nothing here touches the network: every socket is loopback
// and every "server" is a fake listener in this process.
//
//   dotnet run --project Verification\RelayCheck -- <repo-root> [--write-config]
//
// --write-config regenerates <repo-root>\direct-config.json from the built-in defaults so the
// shipped file can never drift from DirectOptions.

using MistSail.Direct;
using System.IO;
using System.Buffers.Binary;
using System.Net;
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

// 1. Built-in defaults cover every host the X web app needs.
DirectOptions defaults = new();
defaults.Validate();
string[] required = ["x.com", "www.x.com", "api.x.com", "upload.x.com", "t.co", "twitter.com", "api.twitter.com",
                     "abs.twimg.com", "abs-0.twimg.com", "pbs.twimg.com", "video.twimg.com", "ton.twimg.com"];
Check("默认 RelayHosts 覆盖 X 主要域名", required.All(defaults.RelayHosts.ContainsKey),
    string.Join(",", required.Where(h => !defaults.RelayHosts.ContainsKey(h))));
Check("默认监听地址为 127.0.0.2", defaults.RelayListenAddress == "127.0.0.2");
Check("默认分片模式 tls-record", defaults.FragmentMode == "tls-record");

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
}

// 3. SNI parsing and TLS record fragmentation on a synthetic ClientHello.
byte[] hello = BuildClientHello("x.com");
Check("TryFindServerName 找到 SNI", FragmentingStream.TryFindServerName(hello, out int nameOffset, out int nameLength) &&
    Encoding.ASCII.GetString(hello, nameOffset, nameLength) == "x.com");

foreach (string mode in new[] { "tls-record", "tcp", "off" })
{
    DirectOptions modeOptions = new() { FragmentMode = mode, FragmentBytes = 2, FragmentDelayMs = 0 };
    using MemoryStream sink = new();
    // Same decision the relay makes: "off" means no fragmenting wrapper at all.
    await using (FragmentingStream fragmenting = new(sink, modeOptions, fragment: modeOptions.ShouldFragment("x.com"), leaveOpen: true))
    {
        await fragmenting.WriteAsync(hello);
        await fragmenting.FlushPendingAsync(CancellationToken.None);
    }
    byte[] output = sink.ToArray();
    (int records, byte[] body) = ParseRecords(output);
    bool bodyIntact = body.AsSpan().SequenceEqual(hello.AsSpan(5));
    Check($"分片模式 {mode}：握手字节原样", bodyIntact, $"records={records}, bytes={output.Length}");
    if (mode == "tls-record")
    {
        Check("tls-record 模式确实拆成多个 record（SNI 跨 record）", records >= 3, $"records={records}");
        bool nameWhole = false;
        int pos = 0;
        while (pos + 5 <= output.Length)
        {
            int len = BinaryPrimitives.ReadUInt16BigEndian(output.AsSpan(pos + 3, 2));
            if (output.AsSpan(pos + 5, len).IndexOf("x.com"u8) >= 0) nameWhole = true;
            pos += 5 + len;
        }
        Check("没有任何单个 record 完整包含 \"x.com\"", !nameWhole);
    }
    if (mode is "tcp" or "off") Check($"{mode} 模式保持单个 record", records == 1 && output.AsSpan().SequenceEqual(hello), $"records={records}");
}

// 4. Loopback relay round-trip: browser -> relay(127.0.0.2) -> fake server(127.0.0.1), all in-process.
{
    using TcpListener fakeServer = new(IPAddress.Loopback, 0);
    fakeServer.Start();
    int fakePort = ((IPEndPoint)fakeServer.LocalEndpoint).Port;
    DirectOptions relayOptions = new() { FragmentBytes = 3, FragmentDelayMs = 1, ConnectTimeoutSeconds = 3 };
    relayOptions.RelayHosts = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { ["self-test.invalid"] = ["127.0.0.1"] };
    relayOptions.Validate();
    using SniRelay relay = new(relayOptions, listenPort: 0, targetPort: fakePort);
    bool started = false;
    try { relay.Start(); started = true; }
    catch (SocketException ex) { Check("中继可绑定 127.0.0.2（Windows 回环）", false, ex.Message); }
    if (started)
    {
        Check("中继可绑定 127.0.0.2（Windows 回环）", relay.EndPoint.Address.ToString() == "127.0.0.2", relay.EndPoint.ToString());
        byte[] clientHello = BuildClientHello("self-test.invalid");
        var serverSide = Task.Run(async () =>
        {
            using TcpClient accepted = await fakeServer.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            NetworkStream stream = accepted.GetStream();
            using MemoryStream received = new();
            byte[] buffer = new byte[4096];
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
            while (received.Length < clientHello.Length + 5 * 16)
            {
                int read;
                try { read = await stream.ReadAsync(buffer, timeout.Token); }
                catch (OperationCanceledException) { break; }
                if (read == 0) break;
                received.Write(buffer, 0, read);
                (int records, byte[] body) = ParseRecords(received.ToArray());
                if (body.Length >= clientHello.Length - 5 && records > 0) { await Task.Delay(100); break; }
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
        (int seenRecords, byte[] seenBody) = ParseRecords(serverSaw);
        Check("假服务器收到分片后的 ClientHello 且字节可重组", seenBody.AsSpan().SequenceEqual(clientHello.AsSpan(5)), $"records={seenRecords}");
        Check("ClientHello 被拆成多个 TLS record", seenRecords >= 3, $"records={seenRecords}");
        Check("浏览器收到假服务器的 ServerHello 字节", replyLength == 6 && reply[0] == 0x16);
        await Task.Delay(300);
        Check("中继记录了“收到 ServerHello”证据", relay.LastResultByHost.TryGetValue("self-test.invalid", out string? evidence) && evidence.Contains("ServerHello"), evidence ?? "(none)");
    }
}

// 5. Loopback relay: unreachable target is reported as a TCP failure (no silent hang).
{
    using TcpListener closed = new(IPAddress.Loopback, 0);
    closed.Start();
    int closedPort = ((IPEndPoint)closed.LocalEndpoint).Port;
    closed.Stop(); // port is now free and refuses connections
    DirectOptions relayOptions = new() { ConnectTimeoutSeconds = 2 };
    relayOptions.RelayHosts = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { ["dead.invalid"] = ["127.0.0.1"] };
    relayOptions.Validate();
    using SniRelay relay = new(relayOptions, listenPort: 0, targetPort: closedPort);
    relay.Start();
    using TcpClient browser = new();
    await browser.ConnectAsync(relay.EndPoint.Address, relay.EndPoint.Port);
    await browser.GetStream().WriteAsync(BuildClientHello("dead.invalid"));
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
    Check("不可达目标：记录为 TCP 连接失败", relay.LastResultByHost.TryGetValue("dead.invalid", out string? evidence) && evidence.Contains("TCP 连接失败"), evidence ?? "(none)");
}

// 6. The upstream rule snapshot that already plays YouTube must stay byte-identical.
string upstreamPath = Path.Combine(root, "vendor", "Cealing-Host-U.json");
string upstreamHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(upstreamPath)));
string expectedHashPath = Path.Combine(root, "vendor", "Cealing-Host-U.sha256");
string expectedHash = File.Exists(expectedHashPath) ? File.ReadAllText(expectedHashPath).Trim().Split(' ')[0] : string.Empty;
Check("vendor/Cealing-Host-U.json 未被改动（与 vendor/Cealing-Host-U.sha256 一致）", expectedHash.Equals(upstreamHash, StringComparison.OrdinalIgnoreCase), upstreamHash);

// 7. The old nginx-based X files must be gone so hosts never points X at Nginx.
Check("已移除 Cealing-Host-X.json", !File.Exists(Path.Combine(root, "Cealing-Host-X.json")));
Check("已移除 Utils/XGlobalConfig.cs", !File.Exists(Path.Combine(root, "Utils", "XGlobalConfig.cs")));

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED (offline only; no network was used)" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

static byte[] BuildClientHello(string serverName)
{
    byte[] name = Encoding.ASCII.GetBytes(serverName);
    using MemoryStream body = new();
    body.Write(new byte[] { 0x03, 0x03 });                       // client_version
    body.Write(RandomNumberGenerator.GetBytes(32));  // random
    body.WriteByte(0);                               // session id
    body.Write(new byte[] { 0x00, 0x04, 0x13, 0x01, 0x13, 0x02 }); // cipher suites
    body.Write(new byte[] { 0x01, 0x00 });                        // compression
    using MemoryStream extensions = new();
    // server_name extension
    extensions.Write(new byte[] { 0x00, 0x00 });
    WriteU16(extensions, name.Length + 5);
    WriteU16(extensions, name.Length + 3);
    extensions.WriteByte(0);
    WriteU16(extensions, name.Length);
    extensions.Write(name);
    // supported_versions (TLS 1.3)
    extensions.Write(new byte[] { 0x00, 0x2b, 0x00, 0x03, 0x02, 0x03, 0x04 });
    // padding so the hello looks like a browser's
    extensions.Write(new byte[] { 0x00, 0x15 });
    WriteU16(extensions, 64);
    extensions.Write(new byte[64]);
    WriteU16(body, (int)extensions.Length);
    extensions.WriteTo(body);
    byte[] bodyBytes = body.ToArray();
    using MemoryStream record = new();
    record.Write(new byte[] { 0x16, 0x03, 0x01 });
    WriteU16(record, bodyBytes.Length + 4);
    record.WriteByte(0x01);
    record.WriteByte((byte)(bodyBytes.Length >> 16));
    record.WriteByte((byte)(bodyBytes.Length >> 8));
    record.WriteByte((byte)bodyBytes.Length);
    record.Write(bodyBytes);
    return record.ToArray();
}

static void WriteU16(Stream stream, int value)
{
    Span<byte> two = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16BigEndian(two, checked((ushort)value));
    stream.Write(two);
}

static (int Records, byte[] Body) ParseRecords(byte[] data)
{
    using MemoryStream body = new();
    int records = 0;
    int position = 0;
    while (position + 5 <= data.Length)
    {
        if (data[position] != 0x16) break;
        int length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(position + 3, 2));
        if (position + 5 + length > data.Length) break;
        body.Write(data, position + 5, length);
        position += 5 + length;
        records++;
    }
    return (records, body.ToArray());
}
