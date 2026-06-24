using System.Runtime.InteropServices;

namespace WinDeploy.App.Services.Sys;

/// <summary>Reads the NVMe SMART / Health Information log (NVMe log page 0x02) of a physical drive. Tries the
/// modern <c>IOCTL_STORAGE_QUERY_PROPERTY</c> (<c>StorageDeviceProtocolSpecificProperty</c> + NVMe) first, then
/// falls back to sending the NVMe "Get Log Page" admin command via <c>IOCTL_STORAGE_PROTOCOL_COMMAND</c>. Both
/// work WITHOUT elevation, and between them they cover internal NVMe drives <b>and</b> NVMe SSDs behind a
/// USB / FireWire / SCSI bridge (external enclosures), which the legacy ATA SMART WMI path can't read.</summary>
public static class NvmeSmart
{
    /// <summary>One decoded NVMe SMART/Health log. Byte / data-unit fields are already converted to bytes;
    /// temperature is in °C (null when the drive reports 0 K = unavailable).</summary>
    public sealed record NvmeHealthLog(
        int CriticalWarning,
        int? TemperatureC,
        int AvailableSpare,
        int AvailableSpareThreshold,
        int PercentageUsed,
        long DataUnitsReadBytes,
        long DataUnitsWrittenBytes,
        long HostReadCommands,
        long HostWriteCommands,
        long ControllerBusyMinutes,
        long PowerCycles,
        long PowerOnHours,
        long UnsafeShutdowns,
        long MediaErrors,
        long ErrorLogEntries);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr templ);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inBuf, uint inSize, byte[] outBuf, uint outSize, ref uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr h);

    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const uint IOCTL_STORAGE_PROTOCOL_COMMAND = 0x002DD3C0;
    private const uint StorageDeviceProtocolSpecificProperty = 50;
    private const uint PropertyStandardQuery = 0;
    private const uint ProtocolTypeNvme = 3;
    private const uint NVMeDataTypeLogPage = 2;
    private const uint NVME_LOG_PAGE_HEALTH_INFO = 0x02;
    private const uint FileShareReadWrite = 0x1 | 0x2;
    private const uint OpenExisting = 3;
    private const int ProtocolSpecificDataSize = 40;          // sizeof STORAGE_PROTOCOL_SPECIFIC_DATA (10 × ULONG)

    /// <summary>Read the SMART/Health log for <c>\\.\PhysicalDrive{index}</c>, or null when the drive isn't
    /// NVMe / both methods are unsupported / access is denied. Index = Get-PhysicalDisk DeviceId (= disk number).</summary>
    public static NvmeHealthLog? Read(int physicalDriveIndex)
    {
        if (physicalDriveIndex < 0) return null;
        // Desired access 0 (no read/write data access) is enough for these query IOCTLs and avoids needing
        // administrator rights — for internal NVMe and for many USB/UASP NVMe enclosures alike.
        var h = CreateFileW($"\\\\.\\PhysicalDrive{physicalDriveIndex}", 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (h == IntPtr.Zero || h == new IntPtr(-1)) return null;
        try { return ReadViaQuery(h) ?? ReadViaProtocolCommand(h); }
        catch { return null; }
        finally { CloseHandle(h); }
    }

    /// <summary>Path 1 — IOCTL_STORAGE_QUERY_PROPERTY / StorageDeviceProtocolSpecificProperty (internal NVMe,
    /// and USB bridges whose driver translates the protocol-specific property query).</summary>
    private static NvmeHealthLog? ReadViaQuery(IntPtr h)
    {
        const int bufSize = 4096;
        var buf = new byte[bufSize];
        void U32(int off, uint v) { buf[off] = (byte)v; buf[off + 1] = (byte)(v >> 8); buf[off + 2] = (byte)(v >> 16); buf[off + 3] = (byte)(v >> 24); }

        // STORAGE_PROPERTY_QUERY { PropertyId; QueryType; } with STORAGE_PROTOCOL_SPECIFIC_DATA inline.
        U32(0, StorageDeviceProtocolSpecificProperty);
        U32(4, PropertyStandardQuery);
        U32(8, ProtocolTypeNvme);
        U32(12, NVMeDataTypeLogPage);
        U32(16, NVME_LOG_PAGE_HEALTH_INFO);    // ProtocolDataRequestValue = log page id
        U32(20, 0);                            // ProtocolDataRequestSubValue
        U32(24, ProtocolSpecificDataSize);     // ProtocolDataOffset (relative to ProtocolSpecificData start)
        U32(28, 512);                          // ProtocolDataLength

        uint returned = 0;
        if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, buf, bufSize, buf, bufSize, ref returned, IntPtr.Zero))
            return null;
        // Output: STORAGE_PROTOCOL_DATA_DESCRIPTOR { Version; Size; ProtocolSpecificData(40) } then the
        // 512-byte log at (ProtocolSpecificData start = 8) + ProtocolDataOffset (40) = 48.
        return Parse(buf, 8 + ProtocolSpecificDataSize);
    }

    /// <summary>Path 2 — send NVMe "Get Log Page" (admin opcode 0x02) via IOCTL_STORAGE_PROTOCOL_COMMAND. Many
    /// USB↔NVMe bridges answer this even when the property query (path 1) doesn't.</summary>
    private static NvmeHealthLog? ReadViaProtocolCommand(IntPtr h)
    {
        const int cmdOff = 80;       // FIELD_OFFSET(STORAGE_PROTOCOL_COMMAND, Command)
        const int dataOff = cmdOff + 64;   // command (NVMe SQE) is 64 bytes; data follows
        const int dataLen = 512;
        const int bufSize = dataOff + dataLen;
        var buf = new byte[bufSize];
        void U32(int off, uint v) { buf[off] = (byte)v; buf[off + 1] = (byte)(v >> 8); buf[off + 2] = (byte)(v >> 16); buf[off + 3] = (byte)(v >> 24); }

        // STORAGE_PROTOCOL_COMMAND header.
        U32(0, 1);                     // Version = STORAGE_PROTOCOL_STRUCTURE_VERSION
        U32(4, cmdOff);                // Length = offset of Command
        U32(8, ProtocolTypeNvme);      // ProtocolType
        U32(24, 64);                   // CommandLength (NVMe SQE)
        U32(36, dataLen);              // DataFromDeviceTransferLength
        U32(40, 15);                   // TimeOutValue (seconds)
        U32(52, dataOff);              // DataFromDeviceBufferOffset

        // NVMe submission-queue entry: Get Log Page, SMART/Health (LID 0x02), 512 bytes (128 dwords) of all NS.
        buf[cmdOff + 0] = 0x02;        // CDW0: opcode = Get Log Page
        U32(cmdOff + 4, 0xFFFFFFFF);   // NSID = all namespaces
        U32(cmdOff + 40, 0x007F0002);  // CDW10: LID=0x02, NUMDL=0x7F (128 dwords − 1)

        uint returned = 0;
        if (!DeviceIoControl(h, IOCTL_STORAGE_PROTOCOL_COMMAND, buf, bufSize, buf, bufSize, ref returned, IntPtr.Zero))
            return null;
        return Parse(buf, dataOff);
    }

    /// <summary>Decode a 512-byte NVMe SMART/Health log located at <paramref name="b"/> in <paramref name="buf"/>.</summary>
    private static NvmeHealthLog? Parse(byte[] buf, int b)
    {
        if (b < 0 || b + 184 > buf.Length) return null;

        long U64(int off)
        {
            long v = 0;
            for (var k = 0; k < 8; k++) v |= (long)buf[b + off + k] << (8 * k);
            return v;
        }

        var kelvin = buf[b + 1] | (buf[b + 2] << 8);
        var poh = U64(128);
        var duw = U64(48);
        var dur = U64(32);

        // Guard against a driver that returns success with an all-zero buffer (no real data).
        if (kelvin == 0 && poh == 0 && duw == 0 && dur == 0 && buf[b + 5] == 0) return null;

        return new NvmeHealthLog(
            CriticalWarning: buf[b + 0],
            TemperatureC: kelvin > 0 ? kelvin - 273 : null,
            AvailableSpare: buf[b + 3],
            AvailableSpareThreshold: buf[b + 4],
            PercentageUsed: buf[b + 5],
            DataUnitsReadBytes: dur * 1000 * 512,
            DataUnitsWrittenBytes: duw * 1000 * 512,
            HostReadCommands: U64(64),
            HostWriteCommands: U64(80),
            ControllerBusyMinutes: U64(96),
            PowerCycles: U64(112),
            PowerOnHours: poh,
            UnsafeShutdowns: U64(144),
            MediaErrors: U64(160),
            ErrorLogEntries: U64(176));
    }
}
