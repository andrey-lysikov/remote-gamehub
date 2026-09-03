//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using RemoteGameHub.App;
using RemoteGameHub.Native;

namespace RemoteGameHub.Session;

// A client for the ViGEm virtual controller bus: the protocol its own client library speaks,
// written out here so the server stays one executable. The bus is optional and may be missing.
internal sealed class ViGEmBus : IDisposable
{
    // The interface class ViGEmBus publishes. Its path differs on every machine.
    private static readonly Guid InterfaceClass = new("96E42B22-F5E9-42F8-B043-ED0F932F014F");

    // CTL_CODE(FILE_DEVICE_BUS_EXTENDER = 0x2a, function, METHOD_BUFFERED = 0, access) is
    // (0x2a << 16) | (access << 14) | (function << 2); functions from 0x801 (ViGEm/Common.h).
    private const uint IOCTL_VIGEM_PLUGIN_TARGET = 0x2AA004;        // function 0x801, write
    private const uint IOCTL_VIGEM_UNPLUG_TARGET = 0x2AA008;        // function 0x802, write
    private const uint IOCTL_VIGEM_CHECK_VERSION = 0x2AA00C;        // function 0x803, write
    private const uint IOCTL_VIGEM_WAIT_DEVICE_READY = 0x2AA010;    // function 0x804, write
    private const uint IOCTL_XUSB_REQUEST_NOTIFICATION = 0x2AE804;  // function 0xA01, read+write
    private const uint IOCTL_XUSB_SUBMIT_REPORT = 0x2AA808;         // function 0xA02, write

    // The protocol version this client speaks. The bus refuses anything else.
    private const uint CommonVersion = 0x0001;

    // Xbox 360 wired. The one every Windows game understands without a driver of its own.
    private const uint TargetTypeXbox360Wired = 0;

    // The bus allocates by serial number, and this is as many as it holds.
    internal const int MaxTargets = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct PluginTarget
    {
        internal uint Size;
        internal uint SerialNo;
        internal uint TargetType;
        internal ushort VendorId;
        internal ushort ProductId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnplugTarget
    {
        internal uint Size;
        internal uint SerialNo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CheckVersion
    {
        internal uint Size;
        internal uint Version;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaitDeviceReady
    {
        internal uint Size;
        internal uint SerialNo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SubmitReport
    {
        internal uint Size;
        internal uint SerialNo;
        internal XusbReport Report;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RequestNotification
    {
        internal uint Size;
        internal uint SerialNo;
        internal byte LedNumber;
        internal byte LargeMotor;
        internal byte SmallMotor;
    }

    // The report an Xbox 360 pad sends, byte for byte. The layout is XInput's XINPUT_GAMEPAD
    // and cannot be rearranged.
    [StructLayout(LayoutKind.Sequential)]
    internal struct XusbReport
    {
        internal ushort Buttons;
        internal byte LeftTrigger;
        internal byte RightTrigger;
        internal short ThumbLX;
        internal short ThumbLY;
        internal short ThumbRX;
        internal short ThumbRY;
    }

    private readonly SafeFileHandle _bus;
    private bool _disposed;

    private ViGEmBus(SafeFileHandle bus)
    {
        _bus = bus;
    }

    // Opens the bus, or returns null with a reason written to the log. Never throws: the
    // absence of a driver the user was never asked to install is an ordinary state of affairs.
    internal static ViGEmBus? Open()
    {
        IReadOnlyList<string> paths;
        try
        {
            paths = SetupApi.InterfacePaths(InterfaceClass);
        }
        catch (Exception error)
        {
            Log.Warn($"could not look for the virtual controller bus: {error.Message}");
            return null;
        }

        if (paths.Count == 0)
        {
            Log.Warn(
                "The virtual controller bus (ViGEmBus) is not installed, so controllers on the\n" +
                "client cannot be presented to this machine. The picture, the sound, the keyboard\n" +
                "and the mouse are unaffected.\n" +
                "A gamepad has to exist as a device before a game will see it, and only a driver\n" +
                "can create one; this server talks to that driver rather than being one.\n" +
                "What to do: install ViGEmBus from https://github.com/nefarius/ViGEmBus/releases\n" +
                "and start this server again.");
            return null;
        }

        var path = paths[0];
        if (paths.Count > 1)
            Log.Info($"the virtual controller bus is published at {paths.Count} paths; using {path}");

        var handle = Kernel32.CreateFile(
            path,
            Kernel32.GENERIC_READ | Kernel32.GENERIC_WRITE,
            Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE,
            0, Kernel32.OPEN_EXISTING,
            // Overlapped, because the rumble notification never completes until the guest rumbles.
            Kernel32.FILE_FLAG_OVERLAPPED, 0);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            Log.Warn(
                $"The virtual controller bus is installed but would not open: " +
                $"{new Win32Exception(error).Message} (Win32 {error}).\n" +
                $"    Path: {path}\n" +
                (error == Kernel32.ERROR_ACCESS_DENIED
                    ? "    This usually means the server is not running as administrator.\n"
                    : string.Empty) +
                "    Controllers will not be available; nothing else is affected.");
            return null;
        }

        var bus = new ViGEmBus(handle);

        if (!bus.CheckVersionMatches())
        {
            bus.Dispose();
            return null;
        }

        Log.Info($"virtual controller bus opened at {path}");
        return bus;
    }

    // Whether the driver's service is switched off, which leaves the device interface registered
    // and nothing behind it. Start = 4 is disabled, in the service's own key.
    private static bool DriverIsDisabled()
    {
        var value = Microsoft.Win32.Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\ViGEmBus", "Start", null);

        return value is int start && start == 4;
    }

    private unsafe bool CheckVersionMatches()
    {
        var request = new CheckVersion { Size = (uint)sizeof(CheckVersion), Version = CommonVersion };

        try
        {
            if (Kernel32.Control(_bus, IOCTL_VIGEM_CHECK_VERSION, &request, sizeof(CheckVersion),
                                 null, 0, out _))
            {
                return true;
            }
        }
        catch (Win32Exception error)
        {
            // ERROR_NOT_SUPPORTED here means the control code was not recognised, a mistake in
            // this file; anything else is the bus refusing the version.
            Log.Warn($"the virtual controller bus rejected this client: {error.Message} " +
                     $"(Win32 {error.NativeErrorCode}). " +
                     (error.NativeErrorCode != Kernel32.ERROR_NOT_SUPPORTED
                         ? "A newer ViGEmBus is probably needed."
                         : DriverIsDisabled()
                             ? "The ViGEmBus service is disabled, so nothing is behind the device " +
                               "it left registered. Enable it, or install the driver again."
                             : "The bus does not recognise the request — check the control codes " +
                               "in ViGEmBus.cs against ViGEm/Common.h."));
            return false;
        }

        Log.Warn("the virtual controller bus did not answer the version check in time; " +
                 "controllers will not be available.");
        return false;
    }

    // Plugs in one Xbox 360 controller and returns the serial the bus gave it. The serial is
    // claimed here, not by the driver: another program using ViGEm holds its own, so walk the range.
    internal unsafe uint? PlugIn(ushort vendorId, ushort productId)
    {
        for (uint serial = 1; serial <= MaxTargets; serial++)
        {
            var request = new PluginTarget
            {
                Size = (uint)sizeof(PluginTarget),
                SerialNo = serial,
                TargetType = TargetTypeXbox360Wired,
                VendorId = vendorId,
                ProductId = productId,
            };

            try
            {
                if (!Kernel32.Control(_bus, IOCTL_VIGEM_PLUGIN_TARGET, &request, sizeof(PluginTarget),
                                      null, 0, out _))
                {
                    continue;
                }
            }
            catch (Win32Exception)
            {
                // This serial is taken, by us or by another program on the same bus. Try the next.
                continue;
            }

            WaitUntilReady(serial);
            return serial;
        }

        return null;
    }

    // Waits for Windows to finish building the device stack behind the new pad: without it the
    // first reports go to a device not yet enumerated and are simply lost.
    private unsafe void WaitUntilReady(uint serial)
    {
        var request = new WaitDeviceReady { Size = (uint)sizeof(WaitDeviceReady), SerialNo = serial };

        try
        {
            if (Kernel32.Control(_bus, IOCTL_VIGEM_WAIT_DEVICE_READY, &request, sizeof(WaitDeviceReady),
                                 null, 0, out _))
            {
                return;
            }
        }
        catch (Win32Exception error)
        {
            // Older versions of the bus do not know this control code. There is nothing to wait on
            // there, so the enumeration is given a moment instead.
            Log.Info($"the bus does not support waiting for a device to be ready ({error.NativeErrorCode}); " +
                     "pausing instead");
        }

        Thread.Sleep(150);
    }

    internal unsafe void Unplug(uint serial)
    {
        var request = new UnplugTarget { Size = (uint)sizeof(UnplugTarget), SerialNo = serial };

        try
        {
            Kernel32.Control(_bus, IOCTL_VIGEM_UNPLUG_TARGET, &request, sizeof(UnplugTarget), null, 0, out _);
        }
        catch (Win32Exception error)
        {
            // A pad that is already gone is not a problem worth reporting as one.
            Log.Info($"unplugging controller {serial} returned {error.NativeErrorCode}");
        }
    }

    internal unsafe void Submit(uint serial, in XusbReport report)
    {
        var request = new SubmitReport
        {
            Size = (uint)sizeof(SubmitReport),
            SerialNo = serial,
            Report = report,
        };

        Kernel32.Control(_bus, IOCTL_XUSB_SUBMIT_REPORT, &request, sizeof(SubmitReport), null, 0, out _);
    }

    // Waits for the guest to set the rumble motors or the player light of one pad, and returns
    // what it asked for. Blocks until then or the timeout, which is why it runs on its own thread.
    internal unsafe bool WaitForFeedback(uint serial, int timeoutMs,
                                         out byte largeMotor, out byte smallMotor, out byte ledNumber)
    {
        largeMotor = smallMotor = ledNumber = 0;

        var request = new RequestNotification
        {
            Size = (uint)sizeof(RequestNotification),
            SerialNo = serial,
        };
        var answer = default(RequestNotification);

        try
        {
            if (!Kernel32.Control(_bus, IOCTL_XUSB_REQUEST_NOTIFICATION,
                                  &request, sizeof(RequestNotification),
                                  &answer, sizeof(RequestNotification), out _, timeoutMs))
            {
                return false;   // nothing happened within the timeout; ask again
            }
        }
        catch (Win32Exception error) when (error.NativeErrorCode == Kernel32.ERROR_OPERATION_ABORTED)
        {
            return false;       // the pad was unplugged while waiting
        }

        largeMotor = answer.LargeMotor;
        smallMotor = answer.SmallMotor;
        ledNumber = answer.LedNumber;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _bus.Dispose();
    }
}
