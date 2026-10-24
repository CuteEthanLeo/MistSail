using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MistSail.Direct;

// Synthetic ClientHello records for the line finder and the offline checks. The layout follows a
// current Chrome ClientHello (TLS 1.3 + 1.2 offer, x25519 key share, ALPN h2, ~1.75 KB with
// padding) so that fragmentation strategies are exercised on a realistic size. The key share is
// random: the server still answers with a real ServerHello, which is all the probes read.
internal static class ClientHelloBuilder
{
    internal const int ChromeLikeSize = 1750;

    // sniLate: put a large padding extension before server_name so the SNI sits beyond the first
    // MSS-sized segment, as happens when Chrome's shuffled extensions place it after the key share.
    // targetSize: pad the record to this many bytes (used to size a fake so it is a complete,
    // self-consistent ClientHello exactly as long as the real segment it overlaps).
    internal static byte[] Build(string serverName, bool chromeLike = true, bool sniLate = false, int targetSize = ChromeLikeSize)
    {
        byte[] name = Encoding.ASCII.GetBytes(serverName);
        using MemoryStream body = new();
        body.Write([0x03, 0x03]);
        body.Write(RandomNumberGenerator.GetBytes(32));
        if (chromeLike)
        {
            body.WriteByte(32);
            body.Write(RandomNumberGenerator.GetBytes(32));
            body.Write(
            [
                0x00, 0x20, // 16 suites
                0x13, 0x01, 0x13, 0x02, 0x13, 0x03, 0xc0, 0x2b, 0xc0, 0x2f, 0xc0, 0x2c, 0xc0, 0x30,
                0xcc, 0xa9, 0xcc, 0xa8, 0xc0, 0x13, 0xc0, 0x14, 0x00, 0x9c, 0x00, 0x9d, 0x00, 0x2f, 0x00, 0x35, 0x00, 0x0a
            ]);
        }
        else
        {
            body.WriteByte(0);
            body.Write([0x00, 0x04, 0x13, 0x01, 0x13, 0x02]);
        }
        body.Write([0x01, 0x00]);

        using MemoryStream ext = new();
        void Extension(ushort type, ReadOnlySpan<byte> data)
        {
            WriteU16(ext, type);
            WriteU16(ext, data.Length);
            ext.Write(data);
        }
        byte[] sni = new byte[name.Length + 5];
        BinaryPrimitives.WriteUInt16BigEndian(sni.AsSpan(0), (ushort)(name.Length + 3));
        sni[2] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(sni.AsSpan(3), (ushort)name.Length);
        name.CopyTo(sni, 5);

        if (chromeLike && sniLate)
            Extension(0x0015, new byte[1200]);
        Extension(0x0000, sni);
        if (chromeLike)
        {
            Extension(0x0017, []);                                   // extended_master_secret
            Extension(0xff01, [0x00]);                               // renegotiation_info
            Extension(0x000a, [0x00, 0x06, 0x00, 0x1d, 0x00, 0x17, 0x00, 0x18]); // supported_groups
            Extension(0x000b, [0x01, 0x00]);                         // ec_point_formats
            Extension(0x0023, []);                                   // session_ticket
            Extension(0x0010, [0x00, 0x0c, 0x02, 0x68, 0x32, 0x08, 0x68, 0x74, 0x74, 0x70, 0x2f, 0x31, 0x2e, 0x31]); // ALPN h2, http/1.1
            Extension(0x0005, [0x01, 0x00, 0x00, 0x00, 0x00]);       // status_request
            Extension(0x000d, [0x00, 0x12, 0x04, 0x03, 0x08, 0x04, 0x04, 0x01, 0x05, 0x03, 0x08, 0x05, 0x05, 0x01, 0x08, 0x06, 0x06, 0x01, 0x02, 0x01]); // signature_algorithms
            Extension(0x0012, []);                                   // signed_certificate_timestamp
            byte[] keyShare = new byte[2 + 4 + 32];
            BinaryPrimitives.WriteUInt16BigEndian(keyShare.AsSpan(0), 36);
            BinaryPrimitives.WriteUInt16BigEndian(keyShare.AsSpan(2), 0x001d);
            BinaryPrimitives.WriteUInt16BigEndian(keyShare.AsSpan(4), 32);
            RandomNumberGenerator.Fill(keyShare.AsSpan(6));
            Extension(0x0033, keyShare);                             // key_share x25519
            Extension(0x002d, [0x01, 0x01]);                         // psk_key_exchange_modes
            Extension(0x002b, [0x04, 0x03, 0x04, 0x03, 0x03]);       // supported_versions 1.3, 1.2
            Extension(0x001b, [0x02, 0x00, 0x02]);                   // compress_certificate brotli
        }
        else
            Extension(0x002b, [0x02, 0x03, 0x04]);

        // Pad to the target size so that the record spans more than one TCP segment like Chrome's.
        int target = chromeLike ? targetSize : 0;
        int current = 5 + 4 + (int)body.Length + 2 + (int)ext.Length;
        int padding = chromeLike ? Math.Max(0, target - current - 4) : 64;
        Extension(0x0015, new byte[padding]);

        WriteU16(body, (int)ext.Length);
        ext.WriteTo(body);
        byte[] bodyBytes = body.ToArray();

        using MemoryStream record = new();
        record.Write([0x16, 0x03, 0x01]);
        WriteU16(record, bodyBytes.Length + 4);
        record.WriteByte(0x01);
        record.WriteByte((byte)((bodyBytes.Length >> 16) & 0xff));
        record.WriteByte((byte)((bodyBytes.Length >> 8) & 0xff));
        record.WriteByte((byte)(bodyBytes.Length & 0xff));
        record.Write(bodyBytes);
        return record.ToArray();
    }

    private static void WriteU16(Stream stream, int value)
    {
        Span<byte> two = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(two, checked((ushort)value));
        stream.Write(two);
    }
}
