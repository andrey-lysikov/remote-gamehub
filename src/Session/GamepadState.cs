//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

namespace RemoteGameHub.Session;

// The buttons, as both ends of this already name them: Moonlight's control stream and the Xbox 360
// report use the same sixteen bits in the same order, so no translation table is needed.
[Flags]
internal enum GamepadButtons : ushort
{
    None = 0,

    DpadUp = 0x0001,
    DpadDown = 0x0002,
    DpadLeft = 0x0004,
    DpadRight = 0x0008,

    Start = 0x0010,
    Back = 0x0020,
    LeftStick = 0x0040,
    RightStick = 0x0080,

    LeftShoulder = 0x0100,
    RightShoulder = 0x0200,

    // The Xbox button. Moonlight calls it Special, XInput calls it Guide.
    Guide = 0x0400,

    A = 0x1000,
    B = 0x2000,
    X = 0x4000,
    Y = 0x8000,

    // Everything an Xbox 360 pad can report. Bits outside this — the share and touchpad buttons of
    // pads this one does not imitate — are dropped: the bus would present a button no game maps.
    All = DpadUp | DpadDown | DpadLeft | DpadRight | Start | Back | LeftStick | RightStick |
          LeftShoulder | RightShoulder | Guide | A | B | X | Y,
}

// One controller, at one moment. Sticks are the full signed range and triggers are a byte, which
// is what arrives from the client and what the pad reports — no scaling happens anywhere between.
internal readonly record struct GamepadState(
    GamepadButtons Buttons,
    byte LeftTrigger,
    byte RightTrigger,
    short LeftStickX,
    short LeftStickY,
    short RightStickX,
    short RightStickY)
{
    internal static readonly GamepadState Released = new(GamepadButtons.None, 0, 0, 0, 0, 0, 0);

    internal ViGEmBus.XusbReport ToReport() => new()
    {
        Buttons = (ushort)(Buttons & GamepadButtons.All),
        LeftTrigger = LeftTrigger,
        RightTrigger = RightTrigger,
        ThumbLX = LeftStickX,
        ThumbLY = LeftStickY,
        ThumbRX = RightStickX,
        ThumbRY = RightStickY,
    };
}

// What the game asked a controller to do: rumble, and which player light to show.
internal readonly record struct GamepadFeedback(int Index, byte LowFrequencyMotor, byte HighFrequencyMotor, byte LedNumber);
