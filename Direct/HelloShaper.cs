using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace MistSail.Direct;

// How a ClientHello leaves the machine: TLS record boundaries, TCP write boundaries and pacing.
// Handshake bytes are never changed (the transcript hash must still match), so every strategy
// reassembles to the original ClientHello on the server side.
internal enum HelloLayout
{
    Plain,          // one record, one write
    RecordMid,      // records: [pre][name first half][name second half][post], one write per record
    RecordMisalign, // same records, but each write ends 2 bytes into the next record header
    RecordMisalignB,// as above, but the first write ends inside the first record body
    Record2,        // two records split in the middle of the name, one write
    TcpMid,         // one record, TCP write split in the middle of the name
    RecordBytes,    // one record per name byte, misaligned writes
    RecordMidDelay, // two records, long pause between them
    TcpMidDelay,    // one record, TCP split in the middle of the name, long pause
    RecordZero4     // four zero-length handshake records, then Record2 in one write
}

internal sealed record HelloStrategy(string Name, HelloLayout Layout, int GapMs, string Summary)
{
    internal const string Auto = "auto";
    internal const int DefaultGapMs = 15;
    internal const int LongPauseMs = 1200;

    internal static readonly HelloStrategy Plain = new("plain", HelloLayout.Plain, 0, "原样发送（对照）");
    internal static readonly HelloStrategy RecordMisalign = new("record-misalign", HelloLayout.RecordMisalign, DefaultGapMs, "record 分片，TCP 写边界错开 record 边界");
    internal static readonly HelloStrategy RecordMisalignB = new("record-misalign-b", HelloLayout.RecordMisalignB, DefaultGapMs, "record 分片，首包截在第一条 record 体内");
    internal static readonly HelloStrategy Record2 = new("record-2", HelloLayout.Record2, 0, "两条 record，域名中间切开，一次写出");
    internal static readonly HelloStrategy TcpMid = new("tcp-mid", HelloLayout.TcpMid, DefaultGapMs, "单条 record，TCP 在域名中间切开");
    internal static readonly HelloStrategy RecordBytes = new("record-bytes", HelloLayout.RecordBytes, DefaultGapMs, "域名每字节一条 record，写边界错开");
    internal static readonly HelloStrategy RecordMidDelay = new("record-mid-delay", HelloLayout.RecordMidDelay, LongPauseMs, "两条 record，中间停顿 1.2 s");
    internal static readonly HelloStrategy TcpMidDelay = new("tcp-mid-delay", HelloLayout.TcpMidDelay, LongPauseMs, "单条 record，TCP 中间切开并停顿 1.2 s");
    internal static readonly HelloStrategy RecordZero4 = new("record-zero4", HelloLayout.RecordZero4, 0, "前置 4 条空 record（仅 Cloudflare）");
    internal static readonly HelloStrategy RecordMid = new("record-mid", HelloLayout.RecordMid, DefaultGapMs, "旧版：4 条 record 逐条写出");

    // Finder order: cheapest, RFC-clean layouts first; controls and the known-failing layout last.
    internal static readonly IReadOnlyList<HelloStrategy> All =
    [
        RecordMisalign, RecordMisalignB, Record2, TcpMid, RecordBytes, RecordMidDelay, TcpMidDelay, RecordZero4, RecordMid, Plain
    ];

    internal static HelloStrategy Default => RecordMisalign;
    internal bool CloudflareOnly => Layout == HelloLayout.RecordZero4;
    internal bool Fragments => Layout != HelloLayout.Plain;

    internal static bool IsKnownName(string name) => name == Auto || TryParse(name, out _);

    internal static bool TryParse(string? name, out HelloStrategy strategy)
    {
        strategy = Default;
        if (string.IsNullOrWhiteSpace(name)) return false;
        switch (name.Trim().ToLowerInvariant())
        {
            case "tls-record": strategy = RecordMid; return true;
            case "tcp": strategy = TcpMid; return true;
            case "off": strategy = Plain; return true;
        }
        var found = All.FirstOrDefault(s => s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (found is null) return false;
        strategy = found;
        return true;
    }

    public override string ToString() => Name;
}

internal readonly record struct HelloWrite(byte[] Bytes, int DelayAfterMs);

internal static class HelloShaper
{
    // Produces the exact sequence of socket writes for one ClientHello record.
    // nameOffset/nameLength index the SNI host name inside `record` (absolute, header included).
    internal static List<HelloWrite> Shape(ReadOnlySpan<byte> record, int nameOffset, int nameLength, HelloStrategy strategy)
    {
        int length = record.Length;
        if (length < 6 || nameOffset < 5 || nameLength < 1 || nameOffset + nameLength > length)
            return [new HelloWrite(record.ToArray(), 0)];

        int nameMid = nameOffset + Math.Max(1, nameLength / 2);
        int nameEnd = nameOffset + nameLength;
        int gap = strategy.GapMs;

        switch (strategy.Layout)
        {
            case HelloLayout.Plain:
                return [new HelloWrite(record.ToArray(), 0)];

            case HelloLayout.TcpMid:
            case HelloLayout.TcpMidDelay:
                return [new HelloWrite(record[..nameMid].ToArray(), gap), new HelloWrite(record[nameMid..].ToArray(), 0)];

            case HelloLayout.Record2:
                return [new HelloWrite(Concat(Records(record, [nameMid], 0)), 0)];

            case HelloLayout.RecordZero4:
                return [new HelloWrite(Concat(Records(record, [nameMid], 4)), 0)];

            case HelloLayout.RecordMidDelay:
            {
                var records = Records(record, [nameMid], 0);
                return [new HelloWrite(records[0], gap), new HelloWrite(records[1], 0)];
            }

            case HelloLayout.RecordMid:
            {
                var records = Records(record, [nameOffset, nameMid, nameEnd], 0);
                return records.Select((r, i) => new HelloWrite(r, i < records.Count - 1 ? gap : 0)).ToList();
            }

            case HelloLayout.RecordMisalign:
                return Misalign(Records(record, [nameOffset, nameMid, nameEnd], 0), firstInsideBody: false, gap);

            case HelloLayout.RecordMisalignB:
                return Misalign(Records(record, [nameOffset, nameMid, nameEnd], 0), firstInsideBody: true, gap);

            case HelloLayout.RecordBytes:
            {
                var cuts = new List<int>();
                for (int i = nameOffset; i <= nameEnd; i++) cuts.Add(i);
                return Misalign(Records(record, cuts, 0), firstInsideBody: false, gap);
            }

            default:
                return [new HelloWrite(record.ToArray(), 0)];
        }
    }

    // Re-frames the record body into several records cut at the given absolute positions
    // (positions are indexes into the original record, 5 <= cut <= record length).
    internal static List<byte[]> Records(ReadOnlySpan<byte> record, IEnumerable<int> cuts, int zeroPrefix)
    {
        var result = new List<byte[]>();
        for (int i = 0; i < zeroPrefix; i++)
            result.Add([record[0], record[1], record[2], 0, 0]);
        int start = 5;
        int length = record.Length;
        foreach (int cut in cuts.Distinct().Where(c => c > 5 && c < length).OrderBy(c => c))
        {
            result.Add(Frame(record, start, cut));
            start = cut;
        }
        result.Add(Frame(record, start, record.Length));
        return result;
    }

    private static byte[] Frame(ReadOnlySpan<byte> record, int start, int end)
    {
        byte[] framed = new byte[5 + end - start];
        framed[0] = record[0]; framed[1] = record[1]; framed[2] = record[2];
        BinaryPrimitives.WriteUInt16BigEndian(framed.AsSpan(3), checked((ushort)(end - start)));
        record[start..end].CopyTo(framed.AsSpan(5));
        return framed;
    }

    // Concatenates the records, then cuts TCP writes 2 bytes past every record boundary so no write
    // ends exactly where a record ends (the first write may instead end inside the first body).
    private static List<HelloWrite> Misalign(List<byte[]> records, bool firstInsideBody, int gap)
    {
        byte[] all = Concat(records);
        var cuts = new List<int>();
        int boundary = 0;
        for (int i = 0; i < records.Count - 1; i++)
        {
            boundary += records[i].Length;
            int cut = boundary + 2;
            if (i == 0 && firstInsideBody)
                cut = Math.Max(6, records[0].Length - Math.Max(1, (records[0].Length - 5) / 2));
            if (cut < all.Length && (cuts.Count == 0 || cut > cuts[^1]))
                cuts.Add(cut);
        }
        var writes = new List<HelloWrite>();
        int start = 0;
        foreach (int cut in cuts)
        {
            writes.Add(new HelloWrite(all[start..cut], gap));
            start = cut;
        }
        writes.Add(new HelloWrite(all[start..], 0));
        return writes;
    }

    internal static byte[] Concat(IEnumerable<byte[]> parts)
    {
        var list = parts.ToList();
        byte[] all = new byte[list.Sum(p => p.Length)];
        int offset = 0;
        foreach (byte[] part in list)
        {
            part.CopyTo(all, offset);
            offset += part.Length;
        }
        return all;
    }

    // Splits a byte stream back into TLS records; used by the offline checks and the finder.
    internal static (int Records, byte[] Body, bool Clean) Reassemble(ReadOnlySpan<byte> data)
    {
        var body = new List<byte>();
        int records = 0;
        int position = 0;
        while (position + 5 <= data.Length)
        {
            if (data[position] != 0x16) return (records, body.ToArray(), false);
            int length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(position + 3, 2));
            if (position + 5 + length > data.Length) return (records, body.ToArray(), false);
            body.AddRange(data.Slice(position + 5, length).ToArray());
            position += 5 + length;
            records++;
        }
        return (records, body.ToArray(), position == data.Length);
    }
}
