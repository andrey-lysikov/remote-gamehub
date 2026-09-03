//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RemoteGameHub.Native;

// Finding a driver by the device interface class it publishes rather than by a fixed path: the
// path contains an instance identifier that differs from machine to machine.
internal static unsafe class SetupApi
{
    private const int DIGCF_PRESENT = 0x02;
    private const int DIGCF_DEVICEINTERFACE = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        internal int Size;
        internal Guid InterfaceClassGuid;
        internal int Flags;
        internal nint Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint SetupDiGetClassDevsW(Guid* classGuid, nint enumerator, nint parent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        nint deviceInfoSet, nint deviceInfoData, Guid* interfaceClassGuid,
        int memberIndex, SpDeviceInterfaceData* interfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(
        nint deviceInfoSet, SpDeviceInterfaceData* interfaceData,
        byte* detailData, int detailSize, int* required, nint deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    // Every path currently published for one device interface class. Empty means the driver is not
    // installed or not started; the two look the same from here, and the caller must say so.
    internal static IReadOnlyList<string> InterfacePaths(Guid interfaceClass)
    {
        var paths = new List<string>();

        var set = SetupDiGetClassDevsW(&interfaceClass, 0, 0, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == -1) throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetClassDevs failed.");

        try
        {
            for (var index = 0; ; index++)
            {
                var data = new SpDeviceInterfaceData { Size = sizeof(SpDeviceInterfaceData) };

                if (!SetupDiEnumDeviceInterfaces(set, 0, &interfaceClass, index, &data))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == Kernel32.ERROR_NO_MORE_ITEMS) break;
                    throw new Win32Exception(error, "SetupDiEnumDeviceInterfaces failed.");
                }

                int required;
                SetupDiGetDeviceInterfaceDetailW(set, &data, null, 0, &required, 0);
                if (required <= 0) continue;

                var buffer = new byte[required];
                fixed (byte* detail = buffer)
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA_W: one DWORD then the path. cbSize is the
                    // header size, 8 on 64-bit — the buffer length gives ERROR_INVALID_USER_BUFFER.
                    *(int*)detail = 8;

                    if (!SetupDiGetDeviceInterfaceDetailW(set, &data, detail, required, null, 0))
                        throw new Win32Exception(Marshal.GetLastWin32Error(),
                            "SetupDiGetDeviceInterfaceDetail failed.");

                    paths.Add(new string((char*)(detail + 4)));
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return paths;
    }
}
