//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace RemoteGameHub.Native;

// A display mode as Windows has always described one: 220 bytes on x64, mostly about printers.
// Only the fields that concern a screen are declared, at offsets a compiler printed.
[StructLayout(LayoutKind.Explicit, Size = 220, CharSet = CharSet.Unicode)]
internal unsafe struct DevMode
{
    // The \\.\DISPLAYn name, filled in by the enumeration.
    [FieldOffset(0)] internal fixed char DeviceName[32];

    // Must be set to the size of this structure before any call reads it.
    [FieldOffset(68)] internal ushort Size;

    // Which of the fields below are meant: the DM_ bits.
    [FieldOffset(72)] internal uint Fields;

    [FieldOffset(76)] internal int PositionX;
    [FieldOffset(80)] internal int PositionY;

    [FieldOffset(168)] internal uint BitsPerPixel;
    [FieldOffset(172)] internal uint PelsWidth;
    [FieldOffset(176)] internal uint PelsHeight;
    [FieldOffset(184)] internal uint DisplayFrequency;
}

// Changing what a screen is doing and putting it back. Two ages of API: ChangeDisplaySettingsEx
// names a screen \\.\DISPLAYn as DXGI does, while HDR goes through display configuration's ids.
internal static unsafe class DisplayControl
{
    // wingdi.h: which DEVMODE fields a call should pay attention to.
    internal const uint DM_BITSPERPEL = 0x00040000;
    internal const uint DM_PELSWIDTH = 0x00080000;
    internal const uint DM_PELSHEIGHT = 0x00100000;
    internal const uint DM_DISPLAYFREQUENCY = 0x00400000;

    // winuser.h.
    internal const int ENUM_CURRENT_SETTINGS = -1;

    internal const int DISP_CHANGE_SUCCESSFUL = 0;
    internal const int DISP_CHANGE_RESTART = 1;

    internal const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    internal const uint DISPLAYCONFIG_PATH_ACTIVE = 0x00000001;

    internal const uint DEVICE_INFO_GET_SOURCE_NAME = 1;
    internal const uint DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9;
    internal const uint DEVICE_INFO_SET_ADVANCED_COLOR_STATE = 10;

    private const int ERROR_SUCCESS = 0;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplaySettingsEx(string? deviceName, int modeIndex,
                                                      ref DevMode mode, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")]
    internal static extern int ChangeDisplaySettingsEx(string? deviceName, ref DevMode mode,
                                                       nint window, uint flags, nint parameters);

    // ------------------------------------------------------------------ the display configuration

    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceInfoHeader
    {
        internal uint Type;
        internal uint Size;
        internal uint AdapterIdLow;
        internal int AdapterIdHigh;
        internal uint Id;
    }

    // One source-to-screen path. Only the identifiers and the active flag are named; the declared
    // size is what makes the array Windows fills the size Windows expects.
    [StructLayout(LayoutKind.Explicit, Size = 72)]
    internal struct PathInfo
    {
        [FieldOffset(0)] internal uint SourceAdapterIdLow;
        [FieldOffset(4)] internal int SourceAdapterIdHigh;
        [FieldOffset(8)] internal uint SourceId;

        [FieldOffset(20)] internal uint TargetAdapterIdLow;
        [FieldOffset(24)] internal int TargetAdapterIdHigh;
        [FieldOffset(28)] internal uint TargetId;

        [FieldOffset(68)] internal uint Flags;
    }

    // Sixty-four bytes of mode description this server never looks inside.
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    internal struct ModeInfo
    {
        private long _reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SourceDeviceName
    {
        internal DeviceInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AdvancedColorInfo
    {
        internal DeviceInfoHeader Header;
        // Bit 0: supported. Bit 1: enabled. Bit 3: forced off by policy.
        internal uint Value;
        internal uint ColorEncoding;
        internal uint BitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SetAdvancedColorState
    {
        internal DeviceInfoHeader Header;
        // Bit 0 is the only one that means anything: turn it on.
        internal uint Value;
    }

    internal const uint AdvancedColorSupported = 1 << 0;
    internal const uint AdvancedColorEnabled = 1 << 1;

    // Set when Windows itself will not offer the HDR switch in Settings for this screen, whatever
    // the EDID claims — the same bit this API's raw write would otherwise happily override.
    internal const uint AdvancedColorForceDisabled = 1 << 3;

    // The scaling a screen is set to. Windows exposes no documented way to read or write it: these
    // request types are what Settings sends, negative because Microsoft's private ones count down.
    internal const uint DEVICE_INFO_GET_DPI_SCALE = 0xFFFFFFFD;   // -3
    internal const uint DEVICE_INFO_SET_DPI_SCALE = 0xFFFFFFFC;   // -4

    // The percentages Windows offers, in order. An index into this list is what the calls below
    // actually work in; the numbers themselves never travel.
    internal static readonly int[] DpiPercentages =
        { 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500 };

    // All three fields are relative to the scaling Windows recommends, which is index -Minimum in
    // the list above: Minimum is how far below the recommendation it reaches, starting at 100%.
    [StructLayout(LayoutKind.Sequential)]
    internal struct DpiScaleGet
    {
        internal DeviceInfoHeader Header;
        internal int Minimum;
        internal int Current;
        internal int Maximum;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DpiScaleSet
    {
        internal DeviceInfoHeader Header;
        internal int Relative;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount,
                                                          out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, PathInfo* paths,
                                                 ref uint modeCount, ModeInfo* modes,
                                                 nint currentTopology);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName request);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref AdvancedColorInfo request);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigSetDeviceInfo(ref SetAdvancedColorState request);

    // The active paths as Windows has them. Returns an empty array rather than throwing when the
    // configuration cannot be read: every use degrades to "leave the screen alone", which is safe.
    internal static PathInfo[] ActivePaths()
    {
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount,
                out var modeCount) != ERROR_SUCCESS)
            return Array.Empty<PathInfo>();

        if (pathCount == 0) return Array.Empty<PathInfo>();

        var paths = new PathInfo[pathCount];
        var modes = new ModeInfo[Math.Max(1, modeCount)];

        fixed (PathInfo* pathData = paths)
        fixed (ModeInfo* modeData = modes)
        {
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, pathData,
                    ref modeCount, modeData, 0) != ERROR_SUCCESS)
                return Array.Empty<PathInfo>();
        }

        return paths[..(int)pathCount];
    }

    // The \\.\DISPLAYn name a path's source answers to, or null.
    internal static string? SourceName(in PathInfo path)
    {
        var request = new SourceDeviceName
        {
            Header = new DeviceInfoHeader
            {
                Type = DEVICE_INFO_GET_SOURCE_NAME,
                Size = (uint)Marshal.SizeOf<SourceDeviceName>(),
                AdapterIdLow = path.SourceAdapterIdLow,
                AdapterIdHigh = path.SourceAdapterIdHigh,
                Id = path.SourceId,
            },
            ViewGdiDeviceName = string.Empty,
        };

        return DisplayConfigGetDeviceInfo(ref request) == ERROR_SUCCESS
            ? request.ViewGdiDeviceName
            : null;
    }

    // What a path's screen is doing about high dynamic range. The identifiers are the target's
    // (the screen), not the source's; with the source's the call returns nothing at all.
    internal static AdvancedColorInfo? ColourInfo(in PathInfo path)
    {
        var request = new AdvancedColorInfo
        {
            Header = new DeviceInfoHeader
            {
                Type = DEVICE_INFO_GET_ADVANCED_COLOR_INFO,
                Size = (uint)Marshal.SizeOf<AdvancedColorInfo>(),
                AdapterIdLow = path.TargetAdapterIdLow,
                AdapterIdHigh = path.TargetAdapterIdHigh,
                Id = path.TargetId,
            },
        };

        return DisplayConfigGetDeviceInfo(ref request) == ERROR_SUCCESS ? request : null;
    }

    internal static bool SetColourState(in PathInfo path, bool enabled)
    {
        var request = new SetAdvancedColorState
        {
            Header = new DeviceInfoHeader
            {
                Type = DEVICE_INFO_SET_ADVANCED_COLOR_STATE,
                Size = (uint)Marshal.SizeOf<SetAdvancedColorState>(),
                AdapterIdLow = path.TargetAdapterIdLow,
                AdapterIdHigh = path.TargetAdapterIdHigh,
                Id = path.TargetId,
            },
            Value = enabled ? 1u : 0u,
        };

        return DisplayConfigSetDeviceInfo(ref request) == ERROR_SUCCESS;
    }

    // What the screen is scaled to now and the most it can be, as percentages; null when Windows
    // refuses. Unlike the colour calls this one names the source rather than the target.
    internal static (int Current, int Maximum)? DpiScale(in PathInfo path)
    {
        var request = new DpiScaleGet
        {
            Header = new DeviceInfoHeader
            {
                Type = DEVICE_INFO_GET_DPI_SCALE,
                Size = (uint)Marshal.SizeOf<DpiScaleGet>(),
                AdapterIdLow = path.SourceAdapterIdLow,
                AdapterIdHigh = path.SourceAdapterIdHigh,
                Id = path.SourceId,
            },
        };

        if (DisplayConfigGetDeviceInfo(ref request) != ERROR_SUCCESS) return null;

        var recommended = -request.Minimum;
        var current = recommended + request.Current;
        var maximum = recommended + request.Maximum;

        if (current < 0 || current >= DpiPercentages.Length) return null;
        if (maximum < 0 || maximum >= DpiPercentages.Length) maximum = DpiPercentages.Length - 1;

        return (DpiPercentages[current], DpiPercentages[maximum]);
    }

    // Scales the desktop on this screen to the nearest percentage Windows offers at or below the
    // one asked for. False when the request is refused or the percentage is not one of them.
    internal static bool SetDpiScale(in PathInfo path, int percent)
    {
        var wanted = Array.IndexOf(DpiPercentages, percent);
        if (wanted < 0) return false;

        var read = new DpiScaleGet
        {
            Header = new DeviceInfoHeader
            {
                Type = DEVICE_INFO_GET_DPI_SCALE,
                Size = (uint)Marshal.SizeOf<DpiScaleGet>(),
                AdapterIdLow = path.SourceAdapterIdLow,
                AdapterIdHigh = path.SourceAdapterIdHigh,
                Id = path.SourceId,
            },
        };

        if (DisplayConfigGetDeviceInfo(ref read) != ERROR_SUCCESS) return false;

        // The value written is relative to the recommendation, exactly as the one read is, and
        // Windows refuses anything outside the range it just reported.
        var relative = Math.Clamp(wanted + read.Minimum, read.Minimum, read.Maximum);

        var request = new DpiScaleSet
        {
            Header = new DeviceInfoHeader
            {
                Type = DEVICE_INFO_SET_DPI_SCALE,
                Size = (uint)Marshal.SizeOf<DpiScaleSet>(),
                AdapterIdLow = path.SourceAdapterIdLow,
                AdapterIdHigh = path.SourceAdapterIdHigh,
                Id = path.SourceId,
            },
            Relative = relative,
        };

        return DisplayConfigSetDeviceInfo(ref request) == ERROR_SUCCESS;
    }

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DpiScaleGet request);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigSetDeviceInfo(ref DpiScaleSet request);
}
