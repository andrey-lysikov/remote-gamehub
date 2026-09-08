//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace RemoteGameHub.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct CursorInfo
{
    internal int Size;
    internal int Flags;
    internal nint Cursor;
    internal Point ScreenPosition;
}

// One synthesised event for SendInput. Explicit layout because the mouse and keyboard forms share
// the space: on x64 the discriminator is four bytes, the union begins at eight, the whole is forty.
[StructLayout(LayoutKind.Explicit, Size = 40)]
internal struct InputRecord
{
    [FieldOffset(0)] internal uint Type;

    [FieldOffset(8)] internal int MouseX;
    [FieldOffset(12)] internal int MouseY;
    // Wheel notches, or which X button, depending on the flags.
    [FieldOffset(16)] internal uint MouseData;
    [FieldOffset(20)] internal uint MouseFlags;
    [FieldOffset(24)] internal uint MouseTime;
    [FieldOffset(32)] internal nuint MouseExtraInfo;

    [FieldOffset(8)] internal ushort KeyCode;
    [FieldOffset(10)] internal ushort KeyScanCode;
    [FieldOffset(12)] internal uint KeyFlags;
    [FieldOffset(16)] internal uint KeyTime;
    [FieldOffset(24)] internal nuint KeyExtraInfo;
}

// The pointer, and synthesised input going the other way: Desktop Duplication reports the pointer
// separately from the picture, and a remote client's typing has to reach this machine's queue.
internal static class User32
{
    // winuser.h. Absolute mouse coordinates are 0..65535 across the whole virtual desktop when
    // VIRTUALDESK is set, which is the only way to reach a screen that is not the primary one.
    internal const uint INPUT_MOUSE = 0;
    internal const uint INPUT_KEYBOARD = 1;

    internal const uint MOUSEEVENTF_MOVE = 0x0001;
    internal const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    internal const uint MOUSEEVENTF_LEFTUP = 0x0004;
    internal const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    internal const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    internal const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    internal const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    internal const uint MOUSEEVENTF_XDOWN = 0x0080;
    internal const uint MOUSEEVENTF_XUP = 0x0100;
    internal const uint MOUSEEVENTF_WHEEL = 0x0800;
    internal const uint MOUSEEVENTF_HWHEEL = 0x1000;
    internal const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    internal const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    internal const uint XBUTTON1 = 0x0001;
    internal const uint XBUTTON2 = 0x0002;

    internal const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    internal const uint KEYEVENTF_KEYUP = 0x0002;

    // The scan code carries a UTF-16 code unit and the virtual key is ignored. This is how a
    // character arrives when the client has no key for it: a phone's keyboard sends text, not keys.
    internal const uint KEYEVENTF_UNICODE = 0x0004;

    // The scan code is the authority for this event, which a game reading through DirectInput or
    // Raw Input requires: those see the hardware's code and ignore an event that carries none.
    internal const uint KEYEVENTF_SCANCODE = 0x0008;

    // MapVirtualKey's "virtual key to scan code, extended": the same as the plain form except
    // that the extended keys keep their 0xE0 or 0xE1 prefix in the high byte.
    internal const uint MAPVK_VK_TO_VSC_EX = 4;

    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    internal static extern uint MapVirtualKey(uint code, uint mapType);

    // The shape a window asks for, for the case where Windows is keeping none: a class's cursor is
    // what it would set the moment a pointer entered it.
    internal const int GCLP_HCURSOR = -12;

    // The resting arrow, by the numeric identifier winuser.h gives it.
    internal static readonly nint IDC_ARROW = 32512;

    [DllImport("user32.dll")]
    internal static extern nint WindowFromPoint(Point point);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    internal static extern nuint GetClassLongPtr(nint window, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadCursorW")]
    internal static extern nint LoadCursor(nint instance, nint name);

    // Asking a window what part of itself is under a point. The border shapes are its answer to
    // this and are nowhere in its class, which is why a class cursor is an arrow on a resize edge.
    internal const uint WM_NCHITTEST = 0x0084;

    internal const int HTLEFT = 10;
    internal const int HTRIGHT = 11;
    internal const int HTTOP = 12;
    internal const int HTTOPLEFT = 13;
    internal const int HTTOPRIGHT = 14;
    internal const int HTBOTTOM = 15;
    internal const int HTBOTTOMLEFT = 16;
    internal const int HTBOTTOMRIGHT = 17;

    internal static readonly nint IDC_SIZENWSE = 32642;
    internal static readonly nint IDC_SIZENESW = 32643;
    internal static readonly nint IDC_SIZEWE = 32644;
    internal static readonly nint IDC_SIZENS = 32645;

    // Abandon the question if the window is not answering: a hung application must not take the
    // stream's pointer with it.
    internal const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
    internal static extern nint SendMessageTimeout(nint window, uint message, nint wParam,
                                                   nint lParam, uint flags, uint timeoutMs,
                                                   out nint result);

    // The wheel is counted in notches (WHEEL_DELTA), which is the unit the protocol also uses.

    // Non-zero when this process runs inside a remote desktop session rather than on the console.
    internal const int SM_REMOTESESSION = 0x1000;

    // Whether this machine has a mouse attached at all. Zero on a machine driven only from a
    // client, and the reason Windows draws no pointer on its desktop.
    internal const int SM_MOUSEPRESENT = 19;

    internal const int SM_XVIRTUALSCREEN = 76;
    internal const int SM_YVIRTUALSCREEN = 77;
    internal const int SM_CXVIRTUALSCREEN = 78;
    internal const int SM_CYVIRTUALSCREEN = 79;

    // By reference to the first record rather than by array: the caller holds a span, and copying
    // it into an array made an allocation of every keystroke and every mouse move.
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint count, in InputRecord inputs, int size);

    // The desktop receiving input now — Default while somebody is signed in, Winlogon while locked
    // or a UAC prompt is up. A thread is bound to its own, and SendInput answers for that one.
    internal const uint DESKTOP_ALL = 0x000F01FF;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit,
                                                 uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetThreadDesktop(nint desktop);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseDesktop(nint desktop);

    // UOI_NAME. The name is the only way to tell two desktop handles apart: OpenInputDesktop
    // hands out a fresh handle every time it is called, for the same desktop.
    internal const int UOI_NAME = 2;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true,
               EntryPoint = "GetUserObjectInformationW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetUserObjectInformation(nint handle, int index, char[] buffer,
                                                         uint length, out uint needed);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    // The keyboard's repeat settings, as Control Panel writes them: the delay is 0 to 3, a quarter
    // of a second each, and the speed 0 to 31 over the range 2.5 to 30 repeats a second.
    internal const uint SPI_GETKEYBOARDSPEED = 0x000A;
    internal const uint SPI_GETKEYBOARDDELAY = 0x0016;

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SystemParametersInfo(uint action, uint param, out int value,
                                                     uint update);

    // ------------------------------------------------------------------ windows and screens

    // The monitor nearest a window, the only sensible answer for a game.
    internal const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo
    {
        // Set to the size of this structure before the call, or it is refused.
        internal int Size;
        internal Rect Monitor;
        internal Rect Work;
        internal uint Flags;
    }

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint window, out Rect rect);

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    internal static extern int GetClassName(nint window, System.Text.StringBuilder name, int capacity);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    // The pointer is on screen. Absent while a game has hidden it.
    internal const int CURSOR_SHOWING = 0x00000001;

    // Windows is suppressing the pointer because the user is working by touch. The handle is still
    // valid, and drawing it would put a pointer on the client's screen that is not on this one.
    internal const int CURSOR_SUPPRESSED = 0x00000002;

    internal const int DI_NORMAL = 0x0003;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorInfo(ref CursorInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DrawIconEx(nint dc, int x, int y, nint icon,
                                           int width, int height, uint step,
                                           nint brush, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetIconInfo(nint icon, out IconInfo info);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(nint handle);

    // Building a cursor out of the masks the duplication hands over. CreateIconIndirect copies the
    // bitmaps into its own, so both are deleted straight after it.
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint CreateIconIndirect(ref IconInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(nint icon);

    // A one-bit-per-pixel bitmap for the AND/XOR masks. Rows must be aligned to two bytes, which
    // is why the shape's own pitch is not handed over unchanged.
    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint CreateBitmap(int width, int height, uint planes, uint bitsPerPixel,
                                             byte[] bits);

    internal const uint DIB_RGB_COLORS = 0;
    internal const uint BI_RGB = 0;

    // A colour bitmap with an alpha channel, which a device-dependent bitmap cannot carry. The
    // height is negative in the header, for rows from the top down as the shape has them.
    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint CreateDIBSection(nint dc, ref BitmapInfoHeader header, uint usage,
                                                 out nint bits, nint section, uint offset);

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfoHeader
    {
        internal uint Size;
        internal int Width;
        internal int Height;
        internal ushort Planes;
        internal ushort BitCount;
        internal uint Compression;
        internal uint ImageSize;
        internal int XPixelsPerMeter;
        internal int YPixelsPerMeter;
        internal uint ColoursUsed;
        internal uint ColoursImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IconInfo
    {
        internal int IsIcon;
        // Where inside the drawing the pointer actually points.
        internal int HotspotX;
        internal int HotspotY;
        internal nint MaskBitmap;
        internal nint ColorBitmap;
    }
}
