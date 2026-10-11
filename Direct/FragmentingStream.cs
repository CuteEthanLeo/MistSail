using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Sheas_Cealer.Direct;

// Splits only the first ClientHello, at the SNI name. Application data is untouched.
// tls-record mode preserves the handshake bytes but changes their record boundaries.
// tcp mode preserves every byte, changing only write boundaries and pacing.
internal sealed class FragmentingStream(Stream inner, DirectOptions options, bool fragment, bool leaveOpen = false) : Stream
{
    private MemoryStream? pending = fragment ? new MemoryStream() : null;
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // If a producer supplied a partial record and then reads, pass it through.
        await FlushPendingAsync(cancellationToken);
        return await inner.ReadAsync(buffer, cancellationToken);
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (pending is null)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            return;
        }
        pending.Write(buffer.Span);
        byte[] data = pending.ToArray();
        if (data.Length < 5)
            return;
        int recordLength = 5 + BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(3, 2));
        if (data[0] != 22 || recordLength > 18437)
        {
            await FlushPendingAsync(cancellationToken);
            return;
        }
        if (data.Length < recordLength)
            return;
        pending.Dispose();
        pending = null;

        if (!TryFindServerName(data.AsSpan(0, recordLength), out int nameOffset, out int nameLength))
            await inner.WriteAsync(data.AsMemory(0, recordLength), cancellationToken);
        else
        {
            int start = options.FragmentMode == "tcp" ? 0 : 5;
            // The host name itself must never sit whole inside one record: short names such as
            // "x.com" are split in the middle, so no single record carries a matchable SNI.
            int nameChunk = Math.Min(options.FragmentBytes, Math.Max(1, (nameLength + 1) / 2));
            int nameEnd = nameOffset + nameLength;
            while (start < recordLength)
            {
                int end = start < nameOffset ? nameOffset :
                    start < nameEnd ? Math.Min(start + nameChunk, nameEnd) : recordLength;
                if (options.FragmentMode == "tls-record")
                {
                    byte[] header = [data[0], data[1], data[2], 0, 0];
                    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(3), checked((ushort)(end - start)));
                    await inner.WriteAsync(header, cancellationToken);
                }
                await inner.WriteAsync(data.AsMemory(start, end - start), cancellationToken);
                start = end;
                if (start < recordLength && options.FragmentDelayMs > 0)
                    await Task.Delay(options.FragmentDelayMs, cancellationToken);
            }
        }
        if (data.Length > recordLength)
            await inner.WriteAsync(data.AsMemory(recordLength), cancellationToken);
    }

    internal async ValueTask FlushPendingAsync(CancellationToken cancellationToken)
    {
        if (pending is null)
            return;
        byte[] data = pending.ToArray();
        pending.Dispose();
        pending = null;
        if (data.Length > 0)
            await inner.WriteAsync(data, cancellationToken);
    }

    internal static bool TryFindServerName(ReadOnlySpan<byte> record, out int nameOffset, out int nameLength)
    {
        nameOffset = nameLength = 0;
        if (record.Length < 44 || record[0] != 22 || record[5] != 1)
            return false;
        int handshakeEnd = 9 + ((record[6] << 16) | (record[7] << 8) | record[8]);
        if (handshakeEnd > record.Length || handshakeEnd < 44)
            return false;
        int position = 43;
        int sessionLength = record[position++];
        position += sessionLength;
        if (position + 2 > handshakeEnd) return false;
        int cipherLength = BinaryPrimitives.ReadUInt16BigEndian(record.Slice(position, 2));
        position += 2 + cipherLength;
        if (position >= handshakeEnd) return false;
        int compressionLength = record[position++];
        position += compressionLength;
        if (position + 2 > handshakeEnd) return false;
        int extensionLength = BinaryPrimitives.ReadUInt16BigEndian(record.Slice(position, 2));
        position += 2;
        int extensionEnd = position + extensionLength;
        if (extensionEnd > handshakeEnd) return false;
        while (position + 4 <= extensionEnd)
        {
            int kind = BinaryPrimitives.ReadUInt16BigEndian(record.Slice(position, 2));
            int length = BinaryPrimitives.ReadUInt16BigEndian(record.Slice(position + 2, 2));
            position += 4;
            if (position + length > extensionEnd) return false;
            if (kind == 0 && length >= 5)
            {
                int listLength = BinaryPrimitives.ReadUInt16BigEndian(record.Slice(position, 2));
                int item = position + 2;
                int listEnd = item + listLength;
                if (listEnd != position + length) return false;
                while (item + 3 <= listEnd)
                {
                    int type = record[item++];
                    int size = BinaryPrimitives.ReadUInt16BigEndian(record.Slice(item, 2));
                    item += 2;
                    if (item + size > listEnd) return false;
                    if (type == 0 && size > 0)
                    {
                        nameOffset = item;
                        nameLength = size;
                        return true;
                    }
                    item += size;
                }
            }
            position += length;
        }
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            pending?.Dispose();
            if (!leaveOpen) inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
