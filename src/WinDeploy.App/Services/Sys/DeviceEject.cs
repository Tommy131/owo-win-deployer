using System.Runtime.InteropServices;
using System.Text;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.Services.Sys;

/// <summary>Safely ejects a removable disk (the "安全删除硬件" operation) by physical-drive index via the
/// Configuration Manager (CfgMgr32) — the same path Explorer uses. Maps the drive number to its device node,
/// walks up to the top-most removable ancestor (the hot-pluggable USB enclosure / card reader, not just the
/// disk function) and requests its ejection. When something still holds the device open, Windows vetoes the
/// request and we surface the reason instead of letting the OS show its own popup.</summary>
public static class DeviceEject
{
    public enum EjectStatus { Success, Busy, NotFound, Failed }
    public readonly record struct EjectResult(EjectStatus Status, string? Reason);

    /// <summary>Eject the device backing physical-drive <paramref name="physicalDriveIndex"/>. Blocking — call
    /// off the UI thread. Does not require elevation for ordinary removable media.</summary>
    public static EjectResult Eject(int physicalDriveIndex)
    {
        if (physicalDriveIndex < 0) return new(EjectStatus.NotFound, null);

        var devInst = FindDevInstByDriveNumber(physicalDriveIndex);
        if (devInst == 0) return new(EjectStatus.NotFound, null);

        // Walk up to the FIRST removable ancestor — the actual hot-pluggable unit (the USB enclosure / card
        // reader). NOT the disk function node (Windows rejects ejecting that with VetoIllegalDeviceRequest), and
        // NOT the top-most removable node either: the chain above can include external hubs/docks that are also
        // "removable", and ejecting one of those would tear down unrelated devices. The first removable node
        // above the disk is the storage device itself — exactly what "安全删除硬件" ejects.
        uint target = devInst, cur = devInst;
        while (CM_Get_Parent(out var parent, cur, 0) == CR_SUCCESS)
        {
            if (IsRemovable(parent)) { target = parent; break; }
            cur = parent;
        }

        var veto = new StringBuilder(MAX_PATH);
        var cr = CM_Request_Device_EjectW(target, out int vetoType, veto, (uint)veto.Capacity, 0);
        if (cr == CR_SUCCESS && vetoType == PNP_VetoTypeUnknown)
            return new(EjectStatus.Success, null);
        if (cr == CR_REMOVE_VETOED || vetoType != PNP_VetoTypeUnknown)
            return new(EjectStatus.Busy, DescribeVeto(vetoType, veto.ToString()));
        return new(EjectStatus.Failed, Localizer.Format("sysov.eject.veto.cmError", cr.ToString("X8")));
    }

    /// <summary>Force-eject: dismount every volume on the disk — even with open handles — to drop whatever was
    /// vetoing the normal eject, then request the PnP eject again. May cause data loss for unflushed writes, so
    /// callers must confirm with the user first. Blocking — call off the UI thread.</summary>
    public static EjectResult ForceEject(int physicalDriveIndex)
    {
        if (physicalDriveIndex < 0) return new(EjectStatus.NotFound, null);
        // 1) Close any handle OUR OWN process holds on the disk (e.g. a hardware-monitor SMART handle) — those
        //    veto the eject from inside WinDeploy and can't be freed by dismounting volumes.
        CloseOwnHandlesToDisk(physicalDriveIndex);
        // 2) Force-dismount every volume to drop handles held by OTHER processes (Explorer, editors, …).
        foreach (var letter in VolumesOnDrive(physicalDriveIndex))
            ForceDismount(letter);
        // 3) Request the PnP eject again.
        return Eject(physicalDriveIndex);
    }

    /// <summary>Find and forcibly close every handle THIS process holds on the given physical drive (or a volume
    /// on it). Each of our handles is duplicated and probed with IOCTL_STORAGE_GET_DEVICE_NUMBER — a robust match
    /// that needs no object-name parsing and works no matter which library opened the handle. Returns the count
    /// closed.</summary>
    private static int CloseOwnHandlesToDisk(int driveNumber)
    {
        var closed = 0;
        var cur = GetCurrentProcess();
        foreach (var hv in EnumOwnHandles())
        {
            // Duplicate first to probe safely; if it's our disk, re-duplicate with CLOSE_SOURCE to kill the original.
            if (!DuplicateHandle(cur, hv, cur, out var probe, 0, false, DUPLICATE_SAME_ACCESS)) continue;
            var isTarget = HandleDeviceNumber(probe) == driveNumber;
            CloseHandle(probe);
            if (isTarget && DuplicateHandle(cur, hv, cur, out var sink, 0, false, DUPLICATE_CLOSE_SOURCE))
            {
                CloseHandle(sink);
                closed++;
            }
        }
        return closed;
    }

    /// <summary>Device number reported by a handle (disk or volume), or -1 if the handle isn't a storage device.</summary>
    private static int HandleDeviceNumber(IntPtr handle)
    {
        var size = Marshal.SizeOf<STORAGE_DEVICE_NUMBER>();
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (DeviceIoControl(handle, IOCTL_STORAGE_GET_DEVICE_NUMBER, IntPtr.Zero, 0, buf, (uint)size, out _, IntPtr.Zero))
                return Marshal.PtrToStructure<STORAGE_DEVICE_NUMBER>(buf).DeviceNumber;
        }
        catch { /* not a device handle */ }
        finally { Marshal.FreeHGlobal(buf); }
        return -1;
    }

    /// <summary>Handle values currently open in THIS process, via NtQuerySystemInformation.</summary>
    private static List<IntPtr> EnumOwnHandles()
    {
        var list = new List<IntPtr>();
        long pid = Environment.ProcessId;
        var len = 0x40000;
        var buf = Marshal.AllocHGlobal(len);
        try
        {
            int status;
            while ((status = NtQuerySystemInformation(SystemExtendedHandleInformation, buf, len, out var ret)) == STATUS_INFO_LENGTH_MISMATCH)
            {
                Marshal.FreeHGlobal(buf);
                len = Math.Max(ret, len * 2);
                buf = Marshal.AllocHGlobal(len);
            }
            if (status != 0) return list;
            var count = Marshal.ReadIntPtr(buf).ToInt64();
            var entrySize = Marshal.SizeOf<SYSTEM_HANDLE_ENTRY_EX>();
            var first = buf + IntPtr.Size * 2;   // skip NumberOfHandles + Reserved
            for (long i = 0; i < count; i++)
            {
                var e = Marshal.PtrToStructure<SYSTEM_HANDLE_ENTRY_EX>(first + (nint)(i * entrySize));
                if (e.UniqueProcessId.ToInt64() == pid) list.Add(e.HandleValue);
            }
        }
        catch { /* best-effort */ }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    /// <summary>Drive letters of every mounted volume backed by the given physical drive (e.g. 'E'). Lets callers
    /// know which letters will disappear once the disk is ejected.</summary>
    public static List<char> VolumesOnDrive(int driveNumber)
    {
        var result = new List<char>();
        var mask = GetLogicalDrives();
        for (var i = 0; i < 26; i++)
        {
            if ((mask & (1u << i)) == 0) continue;
            var letter = (char)('A' + i);
            if (GetDeviceNumber($@"\\.\{letter}:") == driveNumber) result.Add(letter);
        }
        return result;
    }

    /// <summary>Lock (best-effort) then forcibly dismount a volume — the dismount succeeds even if other
    /// processes hold handles open, invalidating them so the device node can be released.</summary>
    private static void ForceDismount(char letter)
    {
        var hf = CreateFileW($@"\\.\{letter}:", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (hf == INVALID_HANDLE_VALUE) return;
        try
        {
            DeviceIoControl(hf, FSCTL_LOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);       // best-effort
            DeviceIoControl(hf, FSCTL_DISMOUNT_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);   // force, ignores open handles
            var allow = Marshal.AllocHGlobal(1);   // clear any software media-removal lock
            try { Marshal.WriteByte(allow, 0); DeviceIoControl(hf, IOCTL_STORAGE_MEDIA_REMOVAL, allow, 1, IntPtr.Zero, 0, out _, IntPtr.Zero); }
            finally { Marshal.FreeHGlobal(allow); }
        }
        finally { CloseHandle(hf); }
    }

    /// <summary>Whether a physical drive is a hot-pluggable device worth offering an eject button for.</summary>
    public static bool IsRemovableBus(string? bus)
    {
        var b = (bus ?? "").Trim();
        return b.Equals("USB", StringComparison.OrdinalIgnoreCase) || b == "1394"
            || b.Equals("SD", StringComparison.OrdinalIgnoreCase) || b.Equals("MMC", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Turn a PnP veto into a user-facing Chinese explanation; appends the blocking app/handle name
    /// when Windows reports one (e.g. an open Explorer window or a program with a file open on the drive).</summary>
    private static string DescribeVeto(int vetoType, string? name)
    {
        var why = vetoType switch
        {
            PNP_VetoOutstandingOpen => Localizer.T("sysov.eject.veto.outstandingOpen"),
            PNP_VetoPendingClose    => Localizer.T("sysov.eject.veto.pendingClose"),
            PNP_VetoWindowsApp      => Localizer.T("sysov.eject.veto.windowsApp"),
            PNP_VetoWindowsService  => Localizer.T("sysov.eject.veto.windowsService"),
            PNP_VetoDevice          => Localizer.T("sysov.eject.veto.device"),
            PNP_VetoDriver          => Localizer.T("sysov.eject.veto.driver"),
            PNP_VetoInsufficientRights => Localizer.T("sysov.eject.veto.insufficientRights"),
            _ => Localizer.T("sysov.eject.veto.default"),
        };
        return string.IsNullOrWhiteSpace(name) ? why : Localizer.Format("sysov.eject.veto.source", why, name);
    }

    /// <summary>Whether a device node is hot-pluggable, via the runtime devnode status flag DN_REMOVABLE_DEVICE.
    /// (The registry CAPABILITIES property is unreliable for USB/UASP nodes — it can be absent or return a stale
    /// shared value — so we don't use CM_Get_DevNode_Registry_Property here.)</summary>
    private static bool IsRemovable(uint devInst)
    {
        var cr = CM_Get_DevNode_Status(out var status, out _, devInst, 0);
        return cr == CR_SUCCESS && (status & DN_REMOVABLE_DEVICE) != 0;
    }

    /// <summary>Map a physical-drive number to its disk device node by enumerating the disk device-interface
    /// class and matching each interface's STORAGE_DEVICE_NUMBER.</summary>
    private static uint FindDevInstByDriveNumber(int driveNumber)
    {
        var guid = GUID_DEVINTERFACE_DISK;
        var h = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (h == INVALID_HANDLE_VALUE) return 0;
        try
        {
            var iface = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(h, IntPtr.Zero, ref guid, i, ref iface); i++)
            {
                SetupDiGetDeviceInterfaceDetail(h, ref iface, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
                if (required == 0) continue;
                var detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);   // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize
                    var info = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                    if (!SetupDiGetDeviceInterfaceDetail(h, ref iface, detail, required, out _, ref info)) continue;
                    var path = Marshal.PtrToStringUni(detail + 4);   // skip the cbSize DWORD
                    if (string.IsNullOrEmpty(path)) continue;
                    if (GetDeviceNumber(path) == driveNumber) return info.DevInst;
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(h); }
        return 0;
    }

    private static int GetDeviceNumber(string devicePath)
    {
        var hf = CreateFileW(devicePath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (hf == INVALID_HANDLE_VALUE) return -1;
        try
        {
            var size = Marshal.SizeOf<STORAGE_DEVICE_NUMBER>();
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                if (DeviceIoControl(hf, IOCTL_STORAGE_GET_DEVICE_NUMBER, IntPtr.Zero, 0, buf, (uint)size, out _, IntPtr.Zero))
                    return Marshal.PtrToStructure<STORAGE_DEVICE_NUMBER>(buf).DeviceNumber;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { CloseHandle(hf); }
        return -1;
    }

    // ── native interop ──────────────────────────────────────────────────────────
    private static readonly Guid GUID_DEVINTERFACE_DISK = new("53f56307-b6bf-11d0-94f2-00a0c91efb8b");
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    private const uint DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x2D1080;
    private const uint FSCTL_LOCK_VOLUME = 0x00090018, FSCTL_DISMOUNT_VOLUME = 0x00090020, IOCTL_STORAGE_MEDIA_REMOVAL = 0x002D4804;
    private const uint DN_REMOVABLE_DEVICE = 0x00004000;    // devnode status: device can be hot-unplugged
    private const int MAX_PATH = 260;
    private const int CR_SUCCESS = 0, CR_REMOVE_VETOED = 0x17;
    private const int SystemExtendedHandleInformation = 64;
    private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    private const uint DUPLICATE_CLOSE_SOURCE = 0x1, DUPLICATE_SAME_ACCESS = 0x2;
    private const int PNP_VetoTypeUnknown = 0, PNP_VetoPendingClose = 2, PNP_VetoWindowsApp = 3,
        PNP_VetoWindowsService = 4, PNP_VetoOutstandingOpen = 5, PNP_VetoDevice = 6, PNP_VetoDriver = 7,
        PNP_VetoInsufficientRights = 12;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA { public uint cbSize; public Guid InterfaceClassGuid; public uint Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_DEVICE_NUMBER { public int DeviceType; public int DeviceNumber; public int PartitionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_HANDLE_ENTRY_EX
    {
        public IntPtr Object;
        public IntPtr UniqueProcessId;
        public IntPtr HandleValue;
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData, ref Guid InterfaceClassGuid, uint MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData, IntPtr DeviceInterfaceDetailData, uint DeviceInterfaceDetailDataSize, out uint RequiredSize, IntPtr DeviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData, IntPtr DeviceInterfaceDetailData, uint DeviceInterfaceDetailDataSize, out uint RequiredSize, ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetLogicalDrives();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr hSourceProcessHandle, IntPtr hSourceHandle, IntPtr hTargetProcessHandle, out IntPtr lpTargetHandle, uint dwDesiredAccess, bool bInheritHandle, uint dwOptions);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int SystemInformationClass, IntPtr SystemInformation, int SystemInformationLength, out int ReturnLength);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Request_Device_EjectW(uint dnDevInst, out int pVetoType, StringBuilder pszVetoName, uint ulNameLength, uint ulFlags);
}
