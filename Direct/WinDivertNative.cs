using System;
using System.Runtime.InteropServices;

namespace MistSail.Direct;

// Thin P/Invoke layer over WinDivert 2.2.2 (vendor/windivert, LGPLv3). Only the NETWORK layer
// entry points the desync engine needs are bound. WinDivert.dll loads WinDivert64.sys from its own
// directory, so both must sit next to MistSail.exe (Build-Package.ps1 copies them there).
internal static class WinDivertNative
{
    internal const short LayerNetwork = 0;

    internal const ulong FlagSniff = 0x0001;
    internal const ulong FlagDrop = 0x0002;
    internal const ulong FlagRecvOnly = 0x0004;
    internal const ulong FlagSendOnly = 0x0008;
    internal const ulong FlagNoInstall = 0x0010;
    internal const ulong FlagFragments = 0x0020;

    internal const uint ShutdownRecv = 0x1;
    internal const uint ShutdownSend = 0x2;
    internal const uint ShutdownBoth = 0x3;

    internal const ulong NoIpChecksum = 1;
    internal const ulong NoTcpChecksum = 8;

    internal static readonly IntPtr InvalidHandle = new(-1);

    // WINDIVERT_ADDRESS is 80 bytes. The bit-packed status word sits at offset 8; for the NETWORK
    // layer the union at offset 16 holds IfIdx/SubIfIdx. The remaining union bytes are left as pad.
    [StructLayout(LayoutKind.Sequential, Size = 80)]
    internal struct Address
    {
        internal long Timestamp;   // offset 0
        internal uint Bitfields;   // offset 8: Layer:8 Event:8 Sniffed:1 Outbound:1 Loopback:1 Impostor:1 IPv6:1 IPChecksum:1 TCPChecksum:1 UDPChecksum:1 Reserved1:8
        internal uint Reserved2;   // offset 12
        internal uint IfIdx;       // offset 16 (union: Network.IfIdx)
        internal uint SubIfIdx;    // offset 20 (union: Network.SubIfIdx)

        private const uint OutboundMask = 1u << 17;
        private const uint IPv6Mask = 1u << 20;
        private const uint IPChecksumMask = 1u << 21;
        private const uint TCPChecksumMask = 1u << 22;

        internal readonly bool Outbound => (Bitfields & OutboundMask) != 0;
        internal readonly bool IPv6 => (Bitfields & IPv6Mask) != 0;

        // After we rewrite header bytes and compute checksums ourselves, tell WinDivert the packet's
        // checksums are present so it does not try to "help".
        internal void MarkChecksumsPresent()
        {
            Bitfields |= IPChecksumMask | TCPChecksumMask;
        }
    }

    [DllImport("WinDivert.dll", SetLastError = true, CallingConvention = CallingConvention.StdCall)]
    internal static extern IntPtr WinDivertOpen(
        [MarshalAs(UnmanagedType.LPStr)] string filter, short layer, short priority, ulong flags);

    [DllImport("WinDivert.dll", SetLastError = true, CallingConvention = CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinDivertRecv(
        IntPtr handle, byte[] packet, uint packetLen, out uint recvLen, ref Address addr);

    [DllImport("WinDivert.dll", SetLastError = true, CallingConvention = CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinDivertSend(
        IntPtr handle, byte[] packet, uint packetLen, out uint sendLen, ref Address addr);

    [DllImport("WinDivert.dll", SetLastError = true, CallingConvention = CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinDivertShutdown(IntPtr handle, uint how);

    [DllImport("WinDivert.dll", SetLastError = true, CallingConvention = CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinDivertClose(IntPtr handle);

    [DllImport("WinDivert.dll", SetLastError = true, CallingConvention = CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinDivertSetParam(IntPtr handle, int param, ulong value);

    // True when WinDivert.dll can be loaded at all (present next to the exe on a 64-bit OS).
    internal static bool Available()
    {
        try
        {
            return NativeLibrary.TryLoad("WinDivert.dll", typeof(WinDivertNative).Assembly, null, out _);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or ArgumentException)
        {
            return false;
        }
    }
}
