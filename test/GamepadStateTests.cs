//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using RemoteGameHub.Session;
using Xunit;

namespace RemoteGameHub.Tests;

// The one place a DualShock 4 report is built from what the client sent: wrong bits or a flipped
// axis here would be invisible until somebody actually presses the button on a real pad.
public class GamepadStateTests
{
    // ViGEm/Common.h's DS4_BUTTONS, re-derived independently of GamepadState.ToDs4Report so a
    // mistake there does not also make the test that checks it wrong.
    private const ushort Square = 1 << 4, Cross = 1 << 5, Circle = 1 << 6, Triangle = 1 << 7;
    private const ushort ShoulderLeft = 1 << 8, ShoulderRight = 1 << 9;
    private const ushort Share = 1 << 12, Options = 1 << 13;
    private const ushort ThumbLeft = 1 << 14, ThumbRight = 1 << 15;

    [Fact]
    public void Face_and_menu_buttons_land_on_their_DS4_bits()
    {
        var state = new GamepadState(
            GamepadButtons.A | GamepadButtons.B | GamepadButtons.X | GamepadButtons.Y |
            GamepadButtons.LeftShoulder | GamepadButtons.RightShoulder |
            GamepadButtons.Back | GamepadButtons.Start |
            GamepadButtons.LeftStick | GamepadButtons.RightStick,
            0, 0, 0, 0, 0, 0);

        var report = state.ToDs4Report();

        // The low nibble is the D-pad hat, always present; DS4_BUTTON_DPAD_NONE = 0x8 here since
        // this state presses no direction — the dpad encoding has its own test below.
        Assert.Equal(
            Cross | Circle | Square | Triangle | ShoulderLeft | ShoulderRight |
            Share | Options | ThumbLeft | ThumbRight | 0x8,
            report.Buttons);
    }

    // GamepadButtons' own bits — DpadUp=1, DpadDown=2, DpadLeft=4, DpadRight=8 — packed as one int
    // per InlineData's own restrictions, unpacked into GamepadButtons below.
    [Theory]
    [InlineData(1, 0)]                     // up
    [InlineData(1 | 8, 1)]                 // up + right
    [InlineData(8, 2)]                     // right
    [InlineData(2 | 8, 3)]                 // down + right
    [InlineData(2, 4)]                     // down
    [InlineData(2 | 4, 5)]                 // down + left
    [InlineData(4, 6)]                     // left
    [InlineData(1 | 4, 7)]                 // up + left
    [InlineData(0, 8)]                     // DS4_BUTTON_DPAD_NONE
    public void The_dpad_is_an_eight_way_hat_in_the_low_nibble(int bits, int expected)
    {
        var dpad = (GamepadButtons)bits;
        var report = new GamepadState(dpad, 0, 0, 0, 0, 0, 0).ToDs4Report();

        Assert.Equal(expected, report.Buttons & 0xF);
    }

    [Theory]
    [InlineData(short.MinValue, (byte)0)]
    [InlineData((short)0, (byte)128)]
    [InlineData(short.MaxValue, (byte)255)]
    public void Sticks_are_rescaled_from_signed_XInput_to_unsigned_DS4(short xinput, byte ds4)
    {
        var state = new GamepadState(GamepadButtons.None, 0, 0, xinput, xinput, xinput, xinput);
        var report = state.ToDs4Report();

        Assert.Equal(ds4, report.ThumbLX);
        Assert.Equal(ds4, report.ThumbRX);

        // Y is inverted relative to X: XInput "up" is positive, DS4's is the low byte.
        Assert.Equal((byte)(255 - ds4), report.ThumbLY);
        Assert.Equal((byte)(255 - ds4), report.ThumbRY);
    }

    [Fact]
    public void Triggers_pass_through_unchanged()
    {
        var report = new GamepadState(GamepadButtons.None, 12, 34, 0, 0, 0, 0).ToDs4Report();

        Assert.Equal(12, report.TriggerL);
        Assert.Equal(34, report.TriggerR);
    }
}
