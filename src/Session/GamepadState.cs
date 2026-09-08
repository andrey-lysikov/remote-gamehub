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

// What kind of pad the client says is behind a controller slot (Input.h's LI_CTYPE_*, read in
// ClientInput.ControllerArrived) — which decides which shape GamepadHub presents it as.
internal enum GamepadKind
{
    Xbox,
    PlayStation,
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

    // DS4_BUTTONS from ViGEm/Common.h, verified against the real header. The D-pad is not among
    // them — DS4_REPORT packs it as an eight-way hat in wButtons' own low nibble, not as bits.
    private const ushort Ds4Square = 1 << 4;
    private const ushort Ds4Cross = 1 << 5;
    private const ushort Ds4Circle = 1 << 6;
    private const ushort Ds4Triangle = 1 << 7;
    private const ushort Ds4ShoulderLeft = 1 << 8;
    private const ushort Ds4ShoulderRight = 1 << 9;
    private const ushort Ds4Share = 1 << 12;
    private const ushort Ds4Options = 1 << 13;
    private const ushort Ds4ThumbLeft = 1 << 14;
    private const ushort Ds4ThumbRight = 1 << 15;

    // DS4_BUTTON_DPAD_NONE = 0x8; the seven other nibble values are the compass points clockwise
    // from north.
    private const byte Ds4DpadNone = 0x8;

    internal ViGEmBus.Ds4Report ToDs4Report()
    {
        var buttons = Dpad();

        if (Buttons.HasFlag(GamepadButtons.X)) buttons |= Ds4Square;
        if (Buttons.HasFlag(GamepadButtons.A)) buttons |= Ds4Cross;
        if (Buttons.HasFlag(GamepadButtons.B)) buttons |= Ds4Circle;
        if (Buttons.HasFlag(GamepadButtons.Y)) buttons |= Ds4Triangle;
        if (Buttons.HasFlag(GamepadButtons.LeftShoulder)) buttons |= Ds4ShoulderLeft;
        if (Buttons.HasFlag(GamepadButtons.RightShoulder)) buttons |= Ds4ShoulderRight;
        if (Buttons.HasFlag(GamepadButtons.Back)) buttons |= Ds4Share;
        if (Buttons.HasFlag(GamepadButtons.Start)) buttons |= Ds4Options;
        if (Buttons.HasFlag(GamepadButtons.LeftStick)) buttons |= Ds4ThumbLeft;
        if (Buttons.HasFlag(GamepadButtons.RightStick)) buttons |= Ds4ThumbRight;

        return new ViGEmBus.Ds4Report
        {
            ThumbLX = ToAxisByte(LeftStickX),
            ThumbLY = (byte)(0xFF - ToAxisByte(LeftStickY)),
            ThumbRX = ToAxisByte(RightStickX),
            ThumbRY = (byte)(0xFF - ToAxisByte(RightStickY)),
            Buttons = buttons,
            TriggerL = LeftTrigger,
            TriggerR = RightTrigger,
        };
    }

    private ushort Dpad()
    {
        var up = Buttons.HasFlag(GamepadButtons.DpadUp);
        var down = Buttons.HasFlag(GamepadButtons.DpadDown);
        var left = Buttons.HasFlag(GamepadButtons.DpadLeft);
        var right = Buttons.HasFlag(GamepadButtons.DpadRight);

        return (up, down, left, right) switch
        {
            (true, false, false, false) => 0,
            (true, false, false, true) => 1,
            (false, false, false, true) => 2,
            (false, true, false, true) => 3,
            (false, true, false, false) => 4,
            (false, true, true, false) => 5,
            (false, false, true, false) => 6,
            (true, false, true, false) => 7,
            _ => Ds4DpadNone,
        };
    }

    // XInput's signed -32768..32767, centred on 0, becomes DS4's unsigned 0..255, centred on
    // 0x80: exactly the top byte of the range shifted up by half.
    private static byte ToAxisByte(short value) => (byte)((value + 32768) >> 8);
}

// What the game asked a controller to do: rumble, and which player light to show.
internal readonly record struct GamepadFeedback(int Index, byte LowFrequencyMotor, byte HighFrequencyMotor, byte LedNumber);
