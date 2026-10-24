using System;
using System.Collections.Generic;
using System.Linq;

namespace MistSail.Direct;

// Kernel-level (WinDivert) TCP desync against a GFW that reassembles TCP segments and TLS records,
// where every user-space HelloStrategy is reset. The relay sends the ClientHello as one plain
// record; this layer rewrites it on the wire so the GFW's reassembled view and the real server's
// reassembled view disagree, and only the GFW sees a blocked SNI.
//
// The handshake bytes the server finally reassembles are always the exact original ClientHello, so
// the transcript hash still matches. The only injected bytes are "fakes" that are built so the
// server drops them (a short TTL that expires before the server, or a corrupt TCP checksum) while
// the GFW, closer to us, accepts them into its reassembly buffer.

internal enum DesyncMethod
{
    Disorder,       // real tail first, then real head (reorder only; control, no fake)
    Fake,           // fake full ClientHello at the head seq, then the real ClientHello
    FakeDisorder,   // fake head, real tail, real head (fake poisons the split point, out of order)
    FakeSplit       // fake full ClientHello, then real head, then real tail (in order)
}

// How a fake segment is built so the server discards it but the GFW does not.
internal enum DesyncFooling
{
    Ttl,            // fake carries a short IP TTL: reaches the GFW, expires before the server
    BadSum,         // fake carries a corrupt TCP checksum: the GFW may ignore it, the server drops it
    TtlBadSum       // both, for GFWs that need one and servers that drop the other
}

internal sealed record DesyncStrategy(string Name, DesyncMethod Method, DesyncFooling Fooling, byte Ttl, int SplitPos, string Summary)
{
    internal const string Off = "off";
    internal const string Auto = "auto";

    // Split position inside the TLS record payload, counted from the record's first byte. 3 lands
    // just inside the handshake header; a small offset keeps the SNI in the second segment for the
    // disorder/split layouts while the GFW has already committed the fake at the head.
    internal const int DefaultSplit = 3;

    internal bool UsesFake => Method != DesyncMethod.Disorder;
    internal bool UsesTtl => UsesFake && Fooling is DesyncFooling.Ttl or DesyncFooling.TtlBadSum;
    internal bool UsesBadSum => UsesFake && Fooling is DesyncFooling.BadSum or DesyncFooling.TtlBadSum;

    // The TTL ladder the finder walks. The GFW on international paths from China sits a small number
    // of hops away; the finder keeps the lowest TTL that still reaches it (confirmed by a hit), and
    // autottl caps the ladder below the measured hop count to the server so a fake can never arrive.
    internal static readonly byte[] TtlLadder = [3, 4, 5, 6, 7, 8, 10];

    private static DesyncStrategy MakeTtl(DesyncMethod method, byte ttl, string summary) =>
        new($"{Tag(method)}-ttl{ttl}", method, DesyncFooling.Ttl, ttl, DefaultSplit, summary);

    private static string Tag(DesyncMethod method) => method switch
    {
        DesyncMethod.Disorder => "disorder",
        DesyncMethod.Fake => "fake",
        DesyncMethod.FakeDisorder => "fakeddisorder",
        DesyncMethod.FakeSplit => "fakedsplit",
        _ => "desync"
    };

    internal static readonly DesyncStrategy Disorder =
        new("disorder", DesyncMethod.Disorder, DesyncFooling.Ttl, 0, DefaultSplit, "乱序：真实尾段先发、头段后发（对照，无假包）");
    internal static readonly DesyncStrategy FakeBadSum =
        new("fake-badsum", DesyncMethod.Fake, DesyncFooling.BadSum, 0, DefaultSplit, "坏校验和假 ClientHello + 真实 ClientHello");
    internal static readonly DesyncStrategy FakeDisorderBadSum =
        new("fakeddisorder-badsum", DesyncMethod.FakeDisorder, DesyncFooling.BadSum, 0, DefaultSplit, "坏校验和假头 + 真实尾 + 真实头");

    // Finder order: the TTL-fooled families first (most robust against a checksum-verifying GFW),
    // cheapest method first, then the checksum-only variants, then the reorder-only control.
    internal static IReadOnlyList<DesyncStrategy> BuildAll(byte maxTtl)
    {
        var list = new List<DesyncStrategy>();
        foreach (byte ttl in TtlLadder.Where(t => maxTtl == 0 || t < maxTtl))
            list.Add(MakeTtl(DesyncMethod.Fake, ttl, $"假 ClientHello(TTL {ttl}) + 真实 ClientHello"));
        foreach (byte ttl in TtlLadder.Where(t => maxTtl == 0 || t < maxTtl))
            list.Add(MakeTtl(DesyncMethod.FakeDisorder, ttl, $"假头(TTL {ttl}) + 真实尾 + 真实头"));
        foreach (byte ttl in TtlLadder.Where(t => maxTtl == 0 || t < maxTtl))
            list.Add(MakeTtl(DesyncMethod.FakeSplit, ttl, $"假 ClientHello(TTL {ttl}) + 真实头 + 真实尾"));
        list.Add(FakeBadSum);
        list.Add(FakeDisorderBadSum);
        list.Add(Disorder);
        return list;
    }

    internal static readonly IReadOnlyList<DesyncStrategy> All = BuildAll(0);

    internal static bool IsKnownName(string name) =>
        name.Equals(Off, StringComparison.OrdinalIgnoreCase) || name.Equals(Auto, StringComparison.OrdinalIgnoreCase) || TryParse(name, out _);

    internal static bool TryParse(string? name, out DesyncStrategy strategy)
    {
        strategy = Disorder;
        if (string.IsNullOrWhiteSpace(name)) return false;
        var found = All.FirstOrDefault(s => s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (found is null) return false;
        strategy = found;
        return true;
    }

    public override string ToString() => Name;
}
