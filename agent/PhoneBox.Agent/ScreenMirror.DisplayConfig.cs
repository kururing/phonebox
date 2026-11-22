using System.Runtime.InteropServices;

namespace PhoneBox.Agent;

public sealed partial class ScreenMirror
{
#if WINDOWS
    private const uint QdcAllPaths = 1;
    private const uint QdcOnlyActivePaths = 2;
    private const uint DisplayConfigPathActive = 1;
    private const uint DisplayConfigPathModeIdxInvalid = 0xFFFFFFFF;
    private const uint DisplayConfigRotationIdentity = 1;
    private const uint DisplayConfigScalingPreferred = 128;
    private const uint SdcTopologyExtend = 0x00000004;
    private const uint SdcUseSuppliedDisplayConfig = 0x00000020;
    private const uint SdcApply = 0x00000080;
    private const uint SdcSaveToDatabase = 0x00000200;
    private const uint SdcAllowChanges = 0x00000400;
    private const uint DisplayDeviceAttachedToDesktop = 0x00000001;
    private const uint DisplayDevicePrimaryDevice = 0x00000004;

    private static string? PrimaryDisplayDeviceName()
    {
        var adapter = new DisplayDevice { Cb = (uint)Marshal.SizeOf<DisplayDevice>(), DeviceName = "", DeviceString = "", DeviceID = "", DeviceKey = "" };
        for (uint i = 0; EnumDisplayDevices(null, i, ref adapter, 0); i++)
        {
            var attached = (adapter.StateFlags & DisplayDeviceAttachedToDesktop) != 0;
            var primary = (adapter.StateFlags & DisplayDevicePrimaryDevice) != 0;
            if (attached && primary) return adapter.DeviceName;
            adapter.Cb = (uint)Marshal.SizeOf<DisplayDevice>();
        }
        return null;
    }

    private static string? SecondaryDisplayDeviceName()
    {
        string? fallback = null;
        var adapter = new DisplayDevice { Cb = (uint)Marshal.SizeOf<DisplayDevice>(), DeviceName = "", DeviceString = "", DeviceID = "", DeviceKey = "" };
        for (uint i = 0; EnumDisplayDevices(null, i, ref adapter, 0); i++)
        {
            var attached = (adapter.StateFlags & DisplayDeviceAttachedToDesktop) != 0;
            var primary = (adapter.StateFlags & DisplayDevicePrimaryDevice) != 0;
            if (attached && !primary)
            {
                if (IsVddGdiDevice(adapter.DeviceName)) return adapter.DeviceName;
                fallback ??= adapter.DeviceName;
            }
            adapter.Cb = (uint)Marshal.SizeOf<DisplayDevice>();
        }
        return fallback;
    }

    private static bool IsVddGdiDevice(string gdiDeviceName)
    {
        if (QueryDisplayConfigPaths(QdcOnlyActivePaths, out var paths, out _) != 0) return false;
        foreach (var path in paths)
        {
            if (!IsVddTarget(path.TargetInfo.AdapterId, path.TargetInfo.Id)) continue;
            var sourceName = new DisplayConfigSourceDeviceName
            {
                Type = 1,
                Size = (uint)Marshal.SizeOf<DisplayConfigSourceDeviceName>(),
                AdapterId = path.SourceInfo.AdapterId,
                Id = path.SourceInfo.Id,
            };
            if (DisplayConfigGetDeviceInfo(ref sourceName) == 0 &&
                string.Equals(sourceName.ViewGdiDeviceName, gdiDeviceName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string? AttachVddDisplay()
    {
        if (SecondaryDisplayDeviceName() is not null) return null;

        var rc = QueryDisplayConfigPaths(QdcOnlyActivePaths, out var activePaths, out var activeModes);
        if (rc != 0) return $"QueryDisplayConfig thất bại (mã {rc}).";

        foreach (var path in activePaths)
        {
            if (IsVddTarget(path.TargetInfo.AdapterId, path.TargetInfo.Id))
                return WaitForSecondaryDisplay() is not null ? null : "Màn hình ảo VDD chưa sẵn sàng.";
        }

        rc = QueryDisplayConfigPaths(QdcAllPaths, out var allPaths, out _);
        if (rc != 0) return $"QueryDisplayConfig(ALL) thất bại (mã {rc}).";

        var usedSources = new HashSet<(uint Low, int High, uint Id)>(
            activePaths.Select(p => (p.SourceInfo.AdapterId.LowPart, p.SourceInfo.AdapterId.HighPart, p.SourceInfo.Id)));

        DisplayConfigPathInfo? candidate = null;
        foreach (var path in allPaths)
        {
            if (path.TargetInfo.TargetAvailable == 0) continue;
            if (!IsVddTarget(path.TargetInfo.AdapterId, path.TargetInfo.Id)) continue;
            var sourceKey = (path.SourceInfo.AdapterId.LowPart, path.SourceInfo.AdapterId.HighPart, path.SourceInfo.Id);
            if (usedSources.Contains(sourceKey)) continue;

            var p = path;
            p.Flags = DisplayConfigPathActive;
            p.SourceInfo.ModeInfoIdx = DisplayConfigPathModeIdxInvalid;
            p.TargetInfo.ModeInfoIdx = DisplayConfigPathModeIdxInvalid;
            if (p.TargetInfo.Rotation == 0) p.TargetInfo.Rotation = DisplayConfigRotationIdentity;
            if (p.TargetInfo.Scaling == 0) p.TargetInfo.Scaling = DisplayConfigScalingPreferred;
            candidate = p;
            break;
        }

        if (candidate is null) return "Không tìm thấy màn hình ảo Virtual Display Driver.";

        var combined = new DisplayConfigPathInfo[activePaths.Length + 1];
        Array.Copy(activePaths, combined, activePaths.Length);
        combined[^1] = candidate.Value;

        rc = SetDisplayConfig((uint)combined.Length, combined, (uint)activeModes.Length, activeModes,
            SdcApply | SdcUseSuppliedDisplayConfig | SdcAllowChanges | SdcSaveToDatabase);

        if (rc != 0)
        {
            var resetPaths = (DisplayConfigPathInfo[])combined.Clone();
            for (var i = 0; i < resetPaths.Length; i++)
            {
                resetPaths[i].SourceInfo.ModeInfoIdx = DisplayConfigPathModeIdxInvalid;
                resetPaths[i].TargetInfo.ModeInfoIdx = DisplayConfigPathModeIdxInvalid;
            }
            rc = SetDisplayConfig((uint)resetPaths.Length, resetPaths, 0, null,
                SdcApply | SdcUseSuppliedDisplayConfig | SdcAllowChanges | SdcSaveToDatabase);
        }

        if (rc != 0)
            rc = SetDisplayConfig(0, null, 0, null, SdcApply | SdcTopologyExtend);

        if (rc != 0) return $"Không gắn được màn hình ảo VDD (SetDisplayConfig mã {rc}).";

        return WaitForSecondaryDisplay() is not null ? null : "Đã bật VDD nhưng Windows chưa nhận màn hình phụ.";
    }

    private static string? WaitForSecondaryDisplay(int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var name = SecondaryDisplayDeviceName();
            if (name is not null) return name;
            if (Environment.TickCount64 >= deadline) return null;
            Thread.Sleep(50);
        }
    }

    private static bool DetachVddDisplay()
    {
        if (QueryDisplayConfigPaths(QdcOnlyActivePaths, out var activePaths, out var activeModes) != 0)
            return false;

        var kept = activePaths.Where(p => !IsVddTarget(p.TargetInfo.AdapterId, p.TargetInfo.Id)).ToArray();
        if (kept.Length == activePaths.Length) return true;
        if (kept.Length == 0) return false;

        var rc = SetDisplayConfig((uint)kept.Length, kept, (uint)activeModes.Length, activeModes,
            SdcApply | SdcUseSuppliedDisplayConfig | SdcAllowChanges | SdcSaveToDatabase);
        if (rc != 0)
        {
            rc = SetDisplayConfig((uint)kept.Length, kept, (uint)activeModes.Length, activeModes,
                SdcApply | SdcUseSuppliedDisplayConfig | SdcAllowChanges);
        }
        return rc == 0;
    }

    private static int QueryDisplayConfigPaths(uint flags, out DisplayConfigPathInfo[] paths, out DisplayConfigModeInfo[] modes)
    {
        paths = [];
        modes = [];
        var rc = GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount);
        if (rc != 0) return rc;
        paths = new DisplayConfigPathInfo[pathCount];
        modes = new DisplayConfigModeInfo[modeCount];
        rc = QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
        if (rc != 0) return rc;
        if (pathCount < paths.Length) Array.Resize(ref paths, (int)pathCount);
        if (modeCount < modes.Length) Array.Resize(ref modes, (int)modeCount);
        return 0;
    }

    private static bool IsVddTarget(Luid adapterId, uint targetId)
    {
        var target = new DisplayConfigTargetDeviceName
        {
            Type = 2,
            Size = (uint)Marshal.SizeOf<DisplayConfigTargetDeviceName>(),
            AdapterId = adapterId,
            Id = targetId,
        };
        if (DisplayConfigGetDeviceInfo(ref target) != 0) return false;
        return (target.MonitorFriendlyDeviceName?.Contains("VDD", StringComparison.OrdinalIgnoreCase) ?? false)
            || (target.MonitorDevicePath?.Contains("MTT1337", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigRational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public DisplayConfigRational RefreshRate;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo SourceInfo;
        public DisplayConfigPathTargetInfo TargetInfo;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigModeInfo
    {
        public uint InfoType;
        public uint Id;
        public Luid AdapterId;
        public ulong D0, D1, D2, D3, D4, D5;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigSourceDeviceName
    {
        public uint Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigTargetDeviceName
    {
        public uint Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string MonitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string MonitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public uint Cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DisplayDevice lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DisplayConfigPathInfo[] pathArray,
        ref uint numModeInfoArrayElements, [Out] DisplayConfigModeInfo[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint numPathArrayElements, [In] DisplayConfigPathInfo[]? pathArray,
        uint numModeInfoArrayElements, [In] DisplayConfigModeInfo[]? modeInfoArray, uint flags);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigTargetDeviceName requestPacket);
#endif
}
