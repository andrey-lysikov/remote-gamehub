//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RemoteGameHub.Native;

// Opening a device and talking to it, which is what the virtual controller bus needs, plus the two
// calls that load the drivers' encoder libraries at run time rather than as static imports.
internal static unsafe class Kernel32
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "LoadLibraryW")]
    internal static extern nint LoadLibrary(string fileName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true,
               BestFitMapping = false)]
    internal static extern nint GetProcAddress(nint module, string name);

    internal const uint GENERIC_READ = 0x80000000;
    internal const uint GENERIC_WRITE = 0x40000000;
    internal const uint FILE_SHARE_READ = 0x00000001;
    internal const uint FILE_SHARE_WRITE = 0x00000002;
    internal const uint OPEN_EXISTING = 3;
    internal const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    internal const int ERROR_IO_PENDING = 997;
    internal const int ERROR_ACCESS_DENIED = 5;
    internal const int ERROR_NOT_SUPPORTED = 50;
    internal const int ERROR_NO_MORE_ITEMS = 259;
    internal const int ERROR_OPERATION_ABORTED = 995;
    internal const uint WAIT_OBJECT_0 = 0;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    internal static extern SafeFileHandle CreateFile(
        string fileName, uint access, uint shareMode, nint security,
        uint creationDisposition, uint flags, nint template);

    // Keeps the screen awake during a stream: network traffic is not "activity" to Windows' own
    // idle timer, and a sleeping display stops composing, freezing the picture until input wakes it.
    [DllImport("kernel32.dll")]
    internal static extern uint SetThreadExecutionState(uint flags);

    internal const uint ES_CONTINUOUS = 0x80000000;
    internal const uint ES_SYSTEM_REQUIRED = 0x00000001;
    internal const uint ES_DISPLAY_REQUIRED = 0x00000002;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint controlCode,
        void* input, int inputSize, void* output, int outputSize,
        int* returned, NativeOverlapped* overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOverlappedResult(
        SafeFileHandle device, NativeOverlapped* overlapped, int* transferred,
        [MarshalAs(UnmanagedType.Bool)] bool wait);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateEventW")]
    private static extern nint CreateEvent(nint security,
        [MarshalAs(UnmanagedType.Bool)] bool manualReset,
        [MarshalAs(UnmanagedType.Bool)] bool initialState, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CancelIoEx(SafeFileHandle device, NativeOverlapped* overlapped);

    // Sends one control code and waits for the answer. The handle is opened overlapped because the
    // bus's notification request never completes until the guest sends rumble.
    internal static bool Control(SafeFileHandle device, uint controlCode,
                                 void* input, int inputSize, void* output, int outputSize,
                                 out int returned, int timeoutMs = 5000)
    {
        returned = 0;

        var wait = CreateEvent(0, manualReset: true, initialState: false, null);
        if (wait == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateEvent failed.");

        try
        {
            var overlapped = new NativeOverlapped { EventHandle = wait };
            int transferred;

            if (DeviceIoControl(device, controlCode, input, inputSize, output, outputSize,
                                &transferred, &overlapped))
            {
                returned = transferred;
                return true;
            }

            var error = Marshal.GetLastWin32Error();
            if (error != ERROR_IO_PENDING) throw new Win32Exception(error);

            if (WaitForSingleObject(wait, (uint)timeoutMs) != WAIT_OBJECT_0)
            {
                // The driver never answered. Withdraw the request before the overlapped structure,
                // which lives on this stack, goes out of scope: a late completion would overwrite it.
                CancelIoEx(device, &overlapped);
                GetOverlappedResult(device, &overlapped, &transferred, wait: true);
                return false;
            }

            if (!GetOverlappedResult(device, &overlapped, &transferred, wait: true))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            returned = transferred;
            return true;
        }
        finally
        {
            CloseHandle(wait);
        }
    }
}
