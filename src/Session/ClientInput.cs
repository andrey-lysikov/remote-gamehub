//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Text;
using RemoteGameHub.App;
using RemoteGameHub.Native;

namespace RemoteGameHub.Session;

// What the client types, clicks and moves, put into this machine's input queue. Layouts and byte
// orders from moonlight-common-c's Input.h: sizes and mouse coordinates big-endian, the rest little.
internal sealed class ClientInput
{
    // Input.h, the magic values a client at protocol generation 5 or newer sends. Two collide with
    // older generations' meanings, which the version this server announces settles.
    private const uint MagicKeyDown = 0x03;
    private const uint MagicKeyUp = 0x04;
    private const uint MagicMouseMoveAbsolute = 0x05;
    private const uint MagicMouseMoveRelative = 0x06;
    private const uint MagicMouseMoveRelativeGen5 = 0x07;
    private const uint MagicMouseButtonDown = 0x08;
    private const uint MagicMouseButtonUp = 0x09;
    private const uint MagicScroll = 0x0A;
    private const uint MagicMultiController = 0x0C;
    private const uint MagicUtf8Text = 0x17;
    private const uint MagicHorizontalScroll = 0x55000001;
    private const uint MagicControllerArrival = 0x55000004;

    // A scroll packet is this many bytes in all, which tells it from a controller.
    private const int ScrollPacketBytes = 14;

    // The modifier bits the protocol uses, and the keys this side presses for them.
    private const byte ModifierShift = 0x01;
    private const byte ModifierCtrl = 0x02;
    private const byte ModifierAlt = 0x04;
    private const byte ModifierMeta = 0x08;

    private const ushort VkShift = 0x10;
    private const ushort VkControl = 0x11;
    private const ushort VkMenu = 0x12;
    private const ushort VkLeftWindows = 0x5B;

    // Keys on the extended half of the keyboard, which need saying so: without it Windows delivers
    // the numeric-keypad key of the same code, and an arrow key types a digit.
    private static readonly ushort[] ExtendedKeys =
    {
        0x21, 0x22, 0x23, 0x24,   // page up, page down, end, home
        0x25, 0x26, 0x27, 0x28,   // left, up, right, down
        0x2C, 0x2D, 0x2E,         // print screen, insert, delete
        0x6F,                     // divide
        0x90,                     // num lock
        0xA3, 0xA5,               // right control, right alt
        0x5B, 0x5C, 0x5D,         // left and right Windows, menu
    };

    private readonly bool _keyboard;
    private readonly bool _mouse;
    private readonly bool _gamepad;
    private readonly GamepadHub _gamepads;

    // Not readonly, because the screen can move under us: adapting it to the client's
    // resolution changes its rectangle, and the pointer is mapped onto these numbers.
    private Rect _screen;

    // Modifier keys this side is holding down because a client event said to.
    private byte _heldModifiers;

    // The key being held, typed again while it is. See RepeatHeldKeys.
    private readonly Thread _repeatThread;
    private readonly AutoResetEvent _repeatChanged = new(false);
    private readonly int _repeatDelayMs;
    private readonly int _repeatIntervalMs;
    private volatile bool _repeating = true;
    private volatile ushort _repeatKey;
    private volatile byte _repeatModifiers;

    // The virtual desktop, cached. See VirtualDesktop.
    private const int DesktopReadAgainMs = 500;
    private int _desktopRead;
    private int _desktopLeft;
    private int _desktopTop;
    private int _desktopWidth;
    private int _desktopHeight;

    // sizeof(InputRecord), which the struct's explicit layout fixes at forty bytes on x64.
    private const int RecordBytes = 40;

    internal ClientInput(Rect capturedScreen, GamepadHub gamepads)
    {
        _keyboard = AppParameters.Input.Keyboard;
        _mouse = AppParameters.Input.Mouse;
        // Not a setting: a controller bus that is installed is one somebody installed on purpose.
        _gamepad = gamepads.IsAvailable;
        _screen = capturedScreen;
        _gamepads = gamepads;

        (_repeatDelayMs, _repeatIntervalMs) = RepeatRate();

        _repeatThread = new Thread(RepeatHeldKeys)
        {
            IsBackground = true,
            Name = "key repeat",
        };

        _repeatThread.Start();
    }

    // The stream is over. Only the repeat has to be stopped: a key still repeating into a desktop
    // nobody is watching is a key stuck down.
    internal void Release()
    {
        _repeatKey = 0;
        _repeating = false;
        _repeatChanged.Set();

        // Disposed only once the thread is known to be gone: a wait on a disposed event throws on
        // that thread, and an unhandled exception there takes the whole process down.
        if (_repeatThread.Join(1000)) _repeatChanged.Dispose();
    }

    // Told when the screen has been put into a different mode.
    internal void SetScreen(Rect screen) => _screen = screen;

    // Which kinds of input packet have been seen this stream. See Handle.
    private readonly HashSet<uint> _firstSeen = new();

    // Raised when the client presses something — a key or a mouse button, never a movement: a
    // phone reports the finger sliding constantly, and only a press means "I want to see this".
    internal event Action? Pressed;

    // Handles one input payload. Never throws: a malformed packet is dropped with a line in the
    // log, because the alternative is a stream that ends over one bad datagram.
    internal void Handle(ReadOnlySpan<byte> payload)
    {
        try
        {
            if (payload.Length < 8) return;

            // Before anything is injected. The desktop that has the input can change under a
            // running stream, and a thread left on the old one sends keys nowhere at all.
            InputDesktop.Attach();

            var magic = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
            var body = payload[8..];

            // The first packet of each kind, once per stream: a client that sends none — one whose
            // window may not read the keyboard — looks exactly like a server that dropped them.
            var firstOfItsKind = _firstSeen.Add(magic);
            if (firstOfItsKind)
                Log.Input($"the first input packet of type 0x{magic:X} arrived ({payload.Length} bytes)");

            switch (magic)
            {
                case MagicKeyDown: Key(body, down: true); break;
                case MagicKeyUp: Key(body, down: false); break;

                case MagicMouseMoveAbsolute: MoveAbsolute(body); break;
                case MagicMouseMoveRelative:
                case MagicMouseMoveRelativeGen5: MoveRelative(body); break;

                case MagicMouseButtonDown: Button(body, down: true); break;
                case MagicMouseButtonUp: Button(body, down: false); break;

                case MagicHorizontalScroll: HorizontalScroll(body); break;
                case MagicUtf8Text: Text(body); break;
                case MagicMultiController: Controller(body); break;
                case MagicControllerArrival: ControllerArrived(body); break;

                // The one value that is two things: a generation-5 client scrolls with 0x0A where
                // an older one meant a controller, and the two are told apart by length.
                case MagicScroll when payload.Length == ScrollPacketBytes: Scroll(body); break;
                case MagicScroll: Controller(body); break;

                default:
                    // Once per kind, not per packet: a pad with a gyroscope sends a type this
                    // switch does not know at its sensor rate, and each line is a file opened.
                    if (firstOfItsKind)
                        Log.Input($"input type 0x{magic:X} ({payload.Length} bytes) is not handled");
                    break;
            }
        }
        catch (Exception error)
        {
            // Occasionally: a client that sends one malformed packet usually sends many.
            Log.WarnOccasionally("input packet",
                $"an input packet could not be read: {error.GetType().Name}: {error.Message}");
        }
    }

    // ------------------------------------------------------------------ keyboard

    private void Key(ReadOnlySpan<byte> body, bool down)
    {
        if (!_keyboard || body.Length < 6) return;

        if (down) Pressed?.Invoke();

        // The key code is little-endian, and only its low byte is the virtual key; the high byte
        // carries nothing this side uses.
        var keyCode = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(body[1..]) & 0x00FF);
        var modifiers = body[3];

        if (IsModifier(keyCode))
        {
            // A modifier key of its own: track it, so that the synthesis below does not press a
            // key the client is already holding.
            var bit = ModifierBitOf(keyCode);
            if (down) _heldModifiers |= bit;
            else _heldModifiers &= (byte)~bit;

            Send(KeyEvent(keyCode, down));
            return;
        }

        Press(keyCode, modifiers, down);

        // Windows repeats a held key in the keyboard's own hardware, which SendInput does not
        // imitate: a key held down on the client would otherwise arrive once.
        if (down) RepeatFrom(keyCode, modifiers);
        else if (keyCode == _repeatKey) StopRepeating();
    }

    // The key, with the modifiers the client holds and this side does not. Pressed before it and
    // released after rather than left held: a lost packet would strand a modifier down.
    private void Press(ushort keyCode, byte modifiers, bool down)
    {
        var missing = down ? (byte)(modifiers & ~_heldModifiers) : (byte)0;

        Span<InputRecord> events = stackalloc InputRecord[8];
        var count = 0;

        if ((missing & ModifierShift) != 0) events[count++] = KeyEvent(VkShift, true);
        if ((missing & ModifierCtrl) != 0) events[count++] = KeyEvent(VkControl, true);
        if ((missing & ModifierAlt) != 0) events[count++] = KeyEvent(VkMenu, true);
        if ((missing & ModifierMeta) != 0) events[count++] = KeyEvent(VkLeftWindows, true);

        events[count++] = KeyEvent(keyCode, down);

        if ((missing & ModifierMeta) != 0) events[count++] = KeyEvent(VkLeftWindows, false);
        if ((missing & ModifierAlt) != 0) events[count++] = KeyEvent(VkMenu, false);
        if ((missing & ModifierCtrl) != 0) events[count++] = KeyEvent(VkControl, false);
        if ((missing & ModifierShift) != 0) events[count++] = KeyEvent(VkShift, false);

        Send(events[..count]);
    }

    // One key repeats at a time, the last one pressed, which is what a keyboard does. A client
    // that sends repeats of its own restarts the delay with each, so this never doubles them.
    private void RepeatFrom(ushort keyCode, byte modifiers)
    {
        _repeatKey = keyCode;
        _repeatModifiers = modifiers;
        _repeatChanged.Set();
    }

    private void StopRepeating()
    {
        _repeatKey = 0;
        _repeatChanged.Set();
    }

    // A thread of this stream's own, not a timer on the pool: a repeat must be typed from a thread
    // bound to the input desktop, and moving a pool thread there moves one the application shares.
    private void RepeatHeldKeys()
    {
        try
        {
            while (_repeating)
            {
                _repeatChanged.WaitOne();

                var keyCode = _repeatKey;
                if (!_repeating) return;
                if (keyCode == 0) continue;

                // Nothing repeats until the key has been held for the delay, and a key pressed or
                // released in the meantime starts the wait over.
                if (_repeatChanged.WaitOne(_repeatDelayMs)) continue;

                while (_repeating && _repeatKey == keyCode)
                {
                    // The desktop can change while a key is held, and a repeat typed into the old
                    // one arrives nowhere: the key then looks stuck rather than repeating.
                    InputDesktop.Attach();
                    Press(keyCode, _repeatModifiers, down: true);

                    if (_repeatChanged.WaitOne(_repeatIntervalMs)) break;
                }
            }
        }
        finally
        {
            InputDesktop.Detach();
        }
    }

    // What Control Panel is set to, read once: a stream is short, and a person changing the
    // repeat rate mid-stream is not a case worth a settings hook for.
    private static (int Delay, int Interval) RepeatRate()
    {
        var delay = User32.SystemParametersInfo(User32.SPI_GETKEYBOARDDELAY, 0, out var steps, 0)
            ? (Math.Clamp(steps, 0, 3) + 1) * 250
            : 500;

        var interval = User32.SystemParametersInfo(User32.SPI_GETKEYBOARDSPEED, 0, out var speed, 0)
            ? (int)Math.Round(1000.0 / (2.5 + Math.Clamp(speed, 0, 31) * (27.5 / 31.0)))
            : 33;

        return (delay, interval);
    }

    private static bool IsModifier(ushort keyCode) =>
        keyCode is VkShift or VkControl or VkMenu or VkLeftWindows or 0x5C   // right Windows
            or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;                 // the left/right pairs

    private static byte ModifierBitOf(ushort keyCode) => keyCode switch
    {
        VkShift or 0xA0 or 0xA1 => ModifierShift,
        VkControl or 0xA2 or 0xA3 => ModifierCtrl,
        VkMenu or 0xA4 or 0xA5 => ModifierAlt,
        _ => ModifierMeta,
    };

    // One key event, carrying the scan code as well as the virtual key: a game reading through
    // DirectInput or Raw Input drops an event whose scan code is zero. KEYEVENTF_SCANCODE says so.
    private static InputRecord KeyEvent(ushort keyCode, bool down)
    {
        var flags = down ? 0u : User32.KEYEVENTF_KEYUP;
        if (Array.IndexOf(ExtendedKeys, keyCode) >= 0) flags |= User32.KEYEVENTF_EXTENDEDKEY;

        // MAPVK_VK_TO_VSC_EX, which returns 0xE0 or 0xE1 in the high byte for the extended keys
        // rather than losing the distinction.
        var scan = User32.MapVirtualKey(keyCode, User32.MAPVK_VK_TO_VSC_EX);
        if ((scan & 0xFF00) is 0xE000 or 0xE100) flags |= User32.KEYEVENTF_EXTENDEDKEY;

        if (scan != 0) flags |= User32.KEYEVENTF_SCANCODE;

        return new InputRecord
        {
            Type = User32.INPUT_KEYBOARD,
            KeyCode = keyCode,
            KeyScanCode = (ushort)(scan & 0xFF),
            KeyFlags = flags,
        };
    }

    // Text the client's keyboard could not express as key codes — a phone's on-screen keyboard
    // sends this. Each UTF-16 code unit goes in as its own event, which surrogate pairs need.
    private void Text(ReadOnlySpan<byte> body)
    {
        if (!_keyboard || body.IsEmpty) return;

        var text = Encoding.UTF8.GetString(body).TrimEnd('\0');
        if (text.Length == 0) return;

        Span<InputRecord> events = stackalloc InputRecord[2];
        foreach (var unit in text)
        {
            events[0] = new InputRecord
            {
                Type = User32.INPUT_KEYBOARD,
                KeyScanCode = unit,
                KeyFlags = User32.KEYEVENTF_UNICODE,
            };
            events[1] = new InputRecord
            {
                Type = User32.INPUT_KEYBOARD,
                KeyScanCode = unit,
                KeyFlags = User32.KEYEVENTF_UNICODE | User32.KEYEVENTF_KEYUP,
            };
            Send(events);
        }
    }

    // ------------------------------------------------------------------ mouse

    // An absolute position inside the client's own viewport, mapped onto the captured screen's
    // rectangle and then to 0-65535 across the whole virtual desktop, not the primary screen.
    private void MoveAbsolute(ReadOnlySpan<byte> body)
    {
        if (!_mouse || body.Length < 10) return;

        var x = BinaryPrimitives.ReadInt16BigEndian(body);
        var y = BinaryPrimitives.ReadInt16BigEndian(body[2..]);
        var referenceWidth = BinaryPrimitives.ReadInt16BigEndian(body[6..]);
        var referenceHeight = BinaryPrimitives.ReadInt16BigEndian(body[8..]);

        if (referenceWidth <= 0 || referenceHeight <= 0) return;

        var onScreenX = _screen.Left + (double)x * _screen.Width / referenceWidth;
        var onScreenY = _screen.Top + (double)y * _screen.Height / referenceHeight;

        var (virtualLeft, virtualTop, virtualWidth, virtualHeight) = VirtualDesktop();
        if (virtualWidth <= 1 || virtualHeight <= 1) return;

        SendAbsolute(onScreenX, onScreenY, virtualLeft, virtualTop, virtualWidth, virtualHeight);

        ReportMapping(x, y, referenceWidth, referenceHeight, onScreenX, onScreenY,
                      virtualLeft, virtualTop, virtualWidth, virtualHeight);
    }

    private void SendAbsolute(double x, double y, int virtualLeft, int virtualTop,
                              int virtualWidth, int virtualHeight)
    {
        // The normalised space has 65536 positions covering the desktop's width, so the last
        // pixel is 65535 and the divisor is one less than the width.
        var normalisedX = (x - virtualLeft) * 65535.0 / (virtualWidth - 1);
        var normalisedY = (y - virtualTop) * 65535.0 / (virtualHeight - 1);

        Send(new InputRecord
        {
            Type = User32.INPUT_MOUSE,
            MouseX = (int)Math.Round(Math.Clamp(normalisedX, 0, 65535)),
            MouseY = (int)Math.Round(Math.Clamp(normalisedY, 0, 65535)),
            MouseFlags = User32.MOUSEEVENTF_MOVE | User32.MOUSEEVENTF_ABSOLUTE |
                         User32.MOUSEEVENTF_VIRTUALDESK,
        });
    }

    // The shape the last mapping report was written for. One line is written whenever any of the
    // numbers behind the mapping changes, and not per event: a client sends hundreds a second.
    private (int, int, int, int, int, int, int, int) _lastReported;

    private void ReportMapping(int x, int y, int referenceWidth, int referenceHeight,
                               double onScreenX, double onScreenY,
                               int virtualLeft, int virtualTop, int virtualWidth, int virtualHeight)
    {
        var shape = (referenceWidth, referenceHeight, _screen.Left, _screen.Top,
                     _screen.Width, _screen.Height, virtualWidth, virtualHeight);

        if (shape == _lastReported) return;
        _lastReported = shape;

        // Where the pointer actually went, read back rather than assumed: a difference from what
        // was asked for points at the normalisation, an agreement at the rectangle above.
        var info = new CursorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<CursorInfo>() };
        var landed = User32.GetCursorInfo(ref info)
            ? $"{info.ScreenPosition.X},{info.ScreenPosition.Y}"
            : "unreadable";

        Log.Input(
            $"pointer mapping: client {x},{y} of {referenceWidth}x{referenceHeight} -> screen " +
            $"{onScreenX:0},{onScreenY:0} on {_screen.Left},{_screen.Top} " +
            $"{_screen.Width}x{_screen.Height}, desktop {virtualLeft},{virtualTop} " +
            $"{virtualWidth}x{virtualHeight}; the pointer is now at {landed}");
    }

    // The virtual desktop's rectangle, re-read at most twice a second. It changes when a screen
    // is added or its mode changes; asking per event was four user32 calls on the busiest packet.
    private (int Left, int Top, int Width, int Height) VirtualDesktop()
    {
        var now = Environment.TickCount;
        if (_desktopWidth > 0 && (uint)(now - _desktopRead) < DesktopReadAgainMs)
            return (_desktopLeft, _desktopTop, _desktopWidth, _desktopHeight);

        _desktopRead = now;
        _desktopLeft = User32.GetSystemMetrics(User32.SM_XVIRTUALSCREEN);
        _desktopTop = User32.GetSystemMetrics(User32.SM_YVIRTUALSCREEN);
        _desktopWidth = User32.GetSystemMetrics(User32.SM_CXVIRTUALSCREEN);
        _desktopHeight = User32.GetSystemMetrics(User32.SM_CYVIRTUALSCREEN);

        return (_desktopLeft, _desktopTop, _desktopWidth, _desktopHeight);
    }

    // Genuinely relative and unbounded: a game's camera look must keep turning past a screen
    // edge, which a clamped absolute move (tried once) capped at the edge instead.
    private void MoveRelative(ReadOnlySpan<byte> body)
    {
        if (!_mouse || body.Length < 4) return;

        Send(new InputRecord
        {
            Type = User32.INPUT_MOUSE,
            MouseX = BinaryPrimitives.ReadInt16BigEndian(body),
            MouseY = BinaryPrimitives.ReadInt16BigEndian(body[2..]),
            MouseFlags = User32.MOUSEEVENTF_MOVE,
        });
    }

    private void Button(ReadOnlySpan<byte> body, bool down)
    {
        if (!_mouse || body.IsEmpty) return;

        if (down) Pressed?.Invoke();

        uint flags;
        uint data = 0;

        switch (body[0])
        {
            case 1: flags = down ? User32.MOUSEEVENTF_LEFTDOWN : User32.MOUSEEVENTF_LEFTUP; break;
            case 2: flags = down ? User32.MOUSEEVENTF_MIDDLEDOWN : User32.MOUSEEVENTF_MIDDLEUP; break;
            case 3: flags = down ? User32.MOUSEEVENTF_RIGHTDOWN : User32.MOUSEEVENTF_RIGHTUP; break;
            case 4:
                flags = down ? User32.MOUSEEVENTF_XDOWN : User32.MOUSEEVENTF_XUP;
                data = User32.XBUTTON1;
                break;
            case 5:
                flags = down ? User32.MOUSEEVENTF_XDOWN : User32.MOUSEEVENTF_XUP;
                data = User32.XBUTTON2;
                break;
            default:
                Log.Input($"mouse button {body[0]} is not one this machine has");
                return;
        }

        Send(new InputRecord
        {
            Type = User32.INPUT_MOUSE,
            MouseFlags = flags,
            MouseData = data,
        });
    }

    // The wheel. The protocol counts in the same units Windows does — 120 to a notch — so the
    // amount travels through untouched, which is what lets a trackpad send fractions of a notch.
    private void Scroll(ReadOnlySpan<byte> body)
    {
        if (!_mouse || body.Length < 2) return;

        Send(new InputRecord
        {
            Type = User32.INPUT_MOUSE,
            MouseFlags = User32.MOUSEEVENTF_WHEEL,
            MouseData = unchecked((uint)(int)BinaryPrimitives.ReadInt16BigEndian(body)),
        });
    }

    private void HorizontalScroll(ReadOnlySpan<byte> body)
    {
        if (!_mouse || body.Length < 2) return;

        Send(new InputRecord
        {
            Type = User32.INPUT_MOUSE,
            MouseFlags = User32.MOUSEEVENTF_HWHEEL,
            MouseData = unchecked((uint)(int)BinaryPrimitives.ReadInt16BigEndian(body)),
        });
    }

    // ------------------------------------------------------------------ controllers

    // One controller's whole state. The buttons need no translation — see the note on
    // GamepadButtons — so this is a matter of reading the fields in the right order and byte order.
    private void Controller(ReadOnlySpan<byte> body)
    {
        if (!_gamepad || body.Length < 26) return;

        var controllerNumber = BinaryPrimitives.ReadInt16LittleEndian(body[2..]);
        var activeMask = BinaryPrimitives.ReadUInt16LittleEndian(body[4..]);
        var buttons = BinaryPrimitives.ReadUInt16LittleEndian(body[8..]);

        _gamepads.SetActive(activeMask);

        _gamepads.Update(controllerNumber, new GamepadState(
            Buttons: (GamepadButtons)buttons & GamepadButtons.All,
            LeftTrigger: body[10],
            RightTrigger: body[11],
            LeftStickX: BinaryPrimitives.ReadInt16LittleEndian(body[12..]),
            LeftStickY: BinaryPrimitives.ReadInt16LittleEndian(body[14..]),
            RightStickX: BinaryPrimitives.ReadInt16LittleEndian(body[16..]),
            RightStickY: BinaryPrimitives.ReadInt16LittleEndian(body[18..])));
    }

    // Sent once when the pad appears, before any state — so the kind recorded here is always
    // set by the time GamepadHub.Update plugs it in.
    private void ControllerArrived(ReadOnlySpan<byte> body)
    {
        if (!_gamepad || body.Length < 8) return;

        var number = body[0];
        var type = body[1];
        var capabilities = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);

        var isPlayStation = type == 2;
        _gamepads.SetKind(number, isPlayStation ? GamepadKind.PlayStation : GamepadKind.Xbox);

        var kind = type switch
        {
            1 => "an Xbox controller",
            2 => "a PlayStation controller",
            3 => "a Nintendo controller",
            4 => "a Steam controller",
            _ => "a controller of an unnamed kind",
        };

        var can = new List<string>();
        if ((capabilities & 0x01) != 0) can.Add("analogue triggers");
        if ((capabilities & 0x02) != 0) can.Add("rumble");
        if ((capabilities & 0x04) != 0) can.Add("trigger rumble");
        if ((capabilities & 0x08) != 0) can.Add("a touchpad");
        if ((capabilities & 0x10) != 0) can.Add("an accelerometer");
        if ((capabilities & 0x20) != 0) can.Add("a gyroscope");
        if ((capabilities & 0x40) != 0) can.Add("battery reporting");
        if ((capabilities & 0x80) != 0) can.Add("a colour light");

        Log.Info($"controller {number} is {kind}" +
                 (can.Count > 0 ? $" with {string.Join(", ", can)}" : " with nothing it reports") +
                 (isPlayStation
                     ? ". It is presented to this machine as a DualShock 4 or DualSense pad."
                     : ". It is presented to this machine as a wired Xbox pad."));
    }

    // ------------------------------------------------------------------ delivery

    // Makes Windows show the pointer, by moving it one pixel and straight back: a machine with no
    // mouse keeps it hidden, and a relative move of zero is discarded. Once, for the desktop.
    internal static void ShowPointer()
    {
        var info = new CursorInfo
        {
            Size = System.Runtime.InteropServices.Marshal.SizeOf<CursorInfo>(),
        };

        if (!User32.GetCursorInfo(ref info)) return;

        var virtualLeft = User32.GetSystemMetrics(User32.SM_XVIRTUALSCREEN);
        var virtualTop = User32.GetSystemMetrics(User32.SM_YVIRTUALSCREEN);
        var virtualWidth = User32.GetSystemMetrics(User32.SM_CXVIRTUALSCREEN);
        var virtualHeight = User32.GetSystemMetrics(User32.SM_CYVIRTUALSCREEN);
        if (virtualWidth <= 1 || virtualHeight <= 1) return;

        var x = info.ScreenPosition.X;
        var y = info.ScreenPosition.Y;
        var nudged = x + 1 < virtualLeft + virtualWidth ? x + 1 : x - 1;

        Span<InputRecord> pair = stackalloc InputRecord[2];
        pair[0] = Absolute(nudged, y, virtualLeft, virtualTop, virtualWidth, virtualHeight);
        pair[1] = Absolute(x, y, virtualLeft, virtualTop, virtualWidth, virtualHeight);
        Send(pair);
    }

    private static InputRecord Absolute(int x, int y, int virtualLeft, int virtualTop,
                                        int virtualWidth, int virtualHeight) =>
        new()
        {
            Type = User32.INPUT_MOUSE,
            MouseX = (int)Math.Round(Math.Clamp(
                (x - virtualLeft) * 65535.0 / (virtualWidth - 1), 0, 65535)),
            MouseY = (int)Math.Round(Math.Clamp(
                (y - virtualTop) * 65535.0 / (virtualHeight - 1), 0, 65535)),
            MouseFlags = User32.MOUSEEVENTF_MOVE | User32.MOUSEEVENTF_ABSOLUTE |
                         User32.MOUSEEVENTF_VIRTUALDESK,
        };

    private static void Send(InputRecord single)
    {
        Span<InputRecord> one = stackalloc InputRecord[1];
        one[0] = single;
        Send(one);
    }

    private static void Send(ReadOnlySpan<InputRecord> events)
    {
        if (events.IsEmpty) return;

        // The count accepted is the only failure report SendInput offers. The size is a constant
        // here rather than Marshal.SizeOf, which is a type lookup on a path that runs at input rate.
        var sent = User32.SendInput((uint)events.Length, events[0], RecordBytes);

        if (sent != events.Length)
        {
            // The usual cause is a more privileged window in the foreground — a UAC prompt, or
            // the secure desktop — which this server cannot type into whatever rights it has.
            Log.Input($"Windows accepted {sent} of {events.Length} input events");
        }
    }
}
