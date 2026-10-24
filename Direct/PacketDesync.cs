using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace MistSail.Direct;

internal readonly record struct InjectPacket(byte[] Bytes, bool Fake, string Role);

// Pure, managed construction of the desync packet sequence for one captured outbound IPv4 TCP
// ClientHello packet. No WinDivert, no sockets: the offline self-check exercises this directly.
// IP/TCP checksums are computed here (so a "fake" can be deliberately corrupted); the live engine
// only parses offsets, injects these bytes, and drops the original.
internal static class PacketDesync
{
    internal readonly record struct Layout(int IhlBytes, int TcpOffset, int TcpHeaderBytes, int PayloadOffset, int PayloadLength, uint Seq, byte Ttl)
    {
        internal bool Valid => IhlBytes >= 20 && TcpHeaderBytes >= 20 && PayloadLength > 0;
    }

    // Parses an IPv4 + TCP packet enough to locate the TCP payload and read the sequence number.
    internal static bool TryParse(ReadOnlySpan<byte> packet, out Layout layout)
    {
        layout = default;
        if (packet.Length < 20 || (packet[0] >> 4) != 4) return false;
        int ihl = (packet[0] & 0x0F) * 4;
        if (ihl < 20 || packet.Length < ihl + 20) return false;
        if (packet[9] != 6) return false; // TCP
        int totalLen = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2));
        if (totalLen < ihl + 20 || totalLen > packet.Length) return false;
        int tcpHeader = ((packet[ihl + 12] >> 4) & 0x0F) * 4;
        if (tcpHeader < 20 || ihl + tcpHeader > totalLen) return false;
        int payloadOffset = ihl + tcpHeader;
        int payloadLength = totalLen - payloadOffset;
        uint seq = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(ihl + 4, 4));
        layout = new Layout(ihl, ihl, tcpHeader, payloadOffset, payloadLength, seq, packet[8]);
        return true;
    }

    // Returns the ordered packets to inject in place of the captured one, or an empty list when the
    // packet cannot be parsed (the caller then re-injects the original unchanged).
    internal static List<InjectPacket> Build(ReadOnlySpan<byte> packet, DesyncStrategy strategy, ReadOnlySpan<byte> benignHello)
    {
        var result = new List<InjectPacket>();
        if (!TryParse(packet, out Layout layout))
            return result;

        int l = layout.PayloadLength;
        int p = Math.Clamp(strategy.SplitPos, 1, l - 1);
        uint seq = layout.Seq;
        uint tailSeq = seq + (uint)p;
        ReadOnlySpan<byte> real = packet.Slice(layout.PayloadOffset, l);
        byte ttl = strategy.UsesTtl ? strategy.Ttl : (byte)0;
        bool bad = strategy.UsesBadSum;

        // Local functions cannot capture the Span parameters, so each segment is built inline.
        switch (strategy.Method)
        {
            case DesyncMethod.Disorder:
                result.Add(new InjectPacket(Segment(packet, layout, real[p..], tailSeq, 0, false), false, "real-tail"));
                result.Add(new InjectPacket(Segment(packet, layout, real[..p], seq, 0, false), false, "real-head"));
                break;

            case DesyncMethod.Fake:
                result.Add(new InjectPacket(Segment(packet, layout, Resize(benignHello, l), seq, ttl, bad, randomizeId: true), true, "fake"));
                result.Add(new InjectPacket(Segment(packet, layout, real, seq, 0, false), false, "real"));
                break;

            case DesyncMethod.FakeSplit:
                result.Add(new InjectPacket(Segment(packet, layout, Resize(benignHello, l), seq, ttl, bad, randomizeId: true), true, "fake"));
                result.Add(new InjectPacket(Segment(packet, layout, real[..p], seq, 0, false), false, "real-head"));
                result.Add(new InjectPacket(Segment(packet, layout, real[p..], tailSeq, 0, false), false, "real-tail"));
                break;

            case DesyncMethod.FakeDisorder:
                result.Add(new InjectPacket(Segment(packet, layout, Resize(benignHello, l), seq, ttl, bad, randomizeId: true), true, "fake"));
                result.Add(new InjectPacket(Segment(packet, layout, real[p..], tailSeq, 0, false), false, "real-tail"));
                result.Add(new InjectPacket(Segment(packet, layout, real[..p], seq, 0, false), false, "real-head"));
                break;
        }
        return result;
    }

    // Builds one IPv4 TCP segment from the captured packet's headers with a replacement payload,
    // sequence number and TTL. Keeps the original TCP options, flags, window and ack. Recomputes the
    // IP checksum always; the TCP checksum is valid unless badSum corrupts it.
    private static byte[] Segment(ReadOnlySpan<byte> template, Layout layout, ReadOnlySpan<byte> payload, uint seq, byte ttl, bool badSum, bool randomizeId = false)
    {
        int total = layout.PayloadOffset + payload.Length;
        byte[] buf = new byte[total];
        template[..layout.PayloadOffset].CopyTo(buf);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2, 2), (ushort)total);
        if (randomizeId)
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(4, 2), (ushort)RandomNumberGenerator.GetInt32(1, 0xFFFF));
        if (ttl != 0)
            buf[8] = ttl;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(layout.TcpOffset + 4, 4), seq);
        payload.CopyTo(buf.AsSpan(layout.PayloadOffset));

        WriteIpChecksum(buf, layout.IhlBytes);
        WriteTcpChecksum(buf, layout);
        if (badSum)
        {
            // Flip the TCP checksum to a value that can never be correct for these bytes.
            int off = layout.TcpOffset + 16;
            ushort good = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(off, 2));
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(off, 2), (ushort)(good ^ 0xFFFF));
        }
        return buf;
    }

    private static byte[] Resize(ReadOnlySpan<byte> source, int length)
    {
        byte[] buf = new byte[length];
        source[..Math.Min(source.Length, length)].CopyTo(buf);
        return buf; // shorter source => trailing zeros; longer => truncated
    }

    private static void WriteIpChecksum(byte[] packet, int ihl)
    {
        packet[10] = 0;
        packet[11] = 0;
        ushort sum = Ones(packet.AsSpan(0, ihl));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10, 2), sum);
    }

    private static void WriteTcpChecksum(byte[] packet, Layout layout)
    {
        int tcpLen = layout.TcpHeaderBytes + (packet.Length - layout.PayloadOffset);
        int cksumOff = layout.TcpOffset + 16;
        packet[cksumOff] = 0;
        packet[cksumOff + 1] = 0;

        uint sum = 0;
        // Pseudo-header: src addr, dst addr, zero, protocol, TCP length.
        sum += (uint)((packet[12] << 8) | packet[13]);
        sum += (uint)((packet[14] << 8) | packet[15]);
        sum += (uint)((packet[16] << 8) | packet[17]);
        sum += (uint)((packet[18] << 8) | packet[19]);
        sum += 6; // protocol TCP
        sum += (uint)tcpLen;
        sum += Sum(packet.AsSpan(layout.TcpOffset, tcpLen));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(cksumOff, 2), Fold(sum));
    }

    private static ushort Ones(ReadOnlySpan<byte> data) => Fold(Sum(data));

    private static uint Sum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        int i = 0;
        for (; i + 1 < data.Length; i += 2)
            sum += (uint)((data[i] << 8) | data[i + 1]);
        if (i < data.Length)
            sum += (uint)(data[i] << 8);
        return sum;
    }

    private static ushort Fold(uint sum)
    {
        while ((sum >> 16) != 0)
            sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)(~sum & 0xFFFF);
    }
}
