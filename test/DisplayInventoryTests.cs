//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using RemoteGameHub.Media;
using RemoteGameHub.Native;
using Xunit;

namespace RemoteGameHub.Tests;

// Choosing a screen from a list DXGI handed back — never the card itself, so every case is built
// by hand here instead.
public class DisplayInventoryTests
{
    private static DisplayOutput Output(int adapter, int index, string name, bool attached,
                                        int left = 0, int top = 0, int width = 1920, int height = 1080) =>
        new(adapter, index, name,
            new Rect { Left = left, Top = top, Right = left + width, Bottom = top + height }, attached);

    private static GraphicsAdapter Adapter(int index, string name, params DisplayOutput[] outputs) =>
        new(index, name, VendorId: 0x10DE, DeviceId: 0, DedicatedVideoMemory: 0, IsSoftware: false,
            AdapterLuid: default, Outputs: outputs);

    [Fact]
    public void Auto_takes_the_primary_screen_over_a_secondary_one_listed_first()
    {
        var secondary = Output(0, 0, @"\\.\DISPLAY1", attached: true, left: 1920);
        var primary = Output(0, 1, @"\\.\DISPLAY2", attached: true);
        var adapters = new[] { Adapter(0, "NVIDIA GeForce RTX 4080", secondary, primary) };

        var chosen = DisplayInventory.Select(adapters, "auto", preferVirtualDisplay: false, out var reason);

        Assert.Same(primary, chosen);
        Assert.Equal("primary screen", reason);
    }

    [Fact]
    public void Auto_falls_back_to_the_first_attached_screen_when_none_is_primary()
    {
        var offset = Output(0, 0, @"\\.\DISPLAY1", attached: true, left: 1920);
        var adapters = new[] { Adapter(0, "NVIDIA GeForce RTX 4080", offset) };

        var chosen = DisplayInventory.Select(adapters, "auto", preferVirtualDisplay: false, out var reason);

        Assert.Same(offset, chosen);
        Assert.Equal("first screen attached to the desktop", reason);
    }

    [Fact]
    public void A_screen_not_attached_to_the_desktop_is_never_chosen()
    {
        var unplugged = Output(0, 0, @"\\.\DISPLAY1", attached: false);
        var adapters = new[] { Adapter(0, "NVIDIA GeForce RTX 4080", unplugged) };

        var chosen = DisplayInventory.Select(adapters, "auto", preferVirtualDisplay: false, out var reason);

        Assert.Null(chosen);
        Assert.Equal("no output is attached to the desktop", reason);
    }

    [Fact]
    public void Auto_prefers_the_virtual_display_over_the_real_primary_screen_when_asked_to()
    {
        var real = Output(0, 0, @"\\.\DISPLAY1", attached: true);
        var virtualOutput = Output(1, 0, @"\\.\DISPLAY2", attached: true, left: 1920);
        var adapters = new[]
        {
            Adapter(0, "NVIDIA GeForce RTX 4080", real),
            Adapter(1, "Virtual Display Driver", virtualOutput),
        };

        var chosen = DisplayInventory.Select(adapters, "auto", preferVirtualDisplay: true, out var reason);

        Assert.Same(virtualOutput, chosen);
        Assert.Equal("the virtual display driver", reason);
    }

    [Fact]
    public void Asking_for_the_virtual_display_when_none_is_attached_falls_back_to_the_primary_screen()
    {
        var real = Output(0, 0, @"\\.\DISPLAY1", attached: true);
        var virtualNotAttached = Output(1, 0, @"\\.\DISPLAY2", attached: false);
        var adapters = new[]
        {
            Adapter(0, "NVIDIA GeForce RTX 4080", real),
            Adapter(1, "Virtual Display Driver", virtualNotAttached),
        };

        var chosen = DisplayInventory.Select(adapters, "auto", preferVirtualDisplay: true, out var reason);

        Assert.Same(real, chosen);
        Assert.Equal("primary screen", reason);
    }

    [Fact]
    public void An_explicit_output_is_never_overridden_by_the_virtual_display_preference()
    {
        var real = Output(0, 0, @"\\.\DISPLAY1", attached: true);
        var virtualOutput = Output(1, 0, @"\\.\DISPLAY2", attached: true, left: 1920);
        var adapters = new[]
        {
            Adapter(0, "NVIDIA GeForce RTX 4080", real),
            Adapter(1, "Virtual Display Driver", virtualOutput),
        };

        var chosen = DisplayInventory.Select(adapters, "0.0", preferVirtualDisplay: true, out var reason);

        Assert.Same(real, chosen);
        Assert.Equal("selected by number 0.0", reason);
    }

    [Fact]
    public void A_screen_is_found_by_a_piece_of_its_device_name()
    {
        var one = Output(0, 0, @"\\.\DISPLAY1", attached: true);
        var two = Output(0, 1, @"\\.\DISPLAY2", attached: true, left: 1920);
        var adapters = new[] { Adapter(0, "NVIDIA GeForce RTX 4080", one, two) };

        var chosen = DisplayInventory.Select(adapters, "splay2", preferVirtualDisplay: false, out var reason);

        Assert.Same(two, chosen);
        Assert.Equal("selected by name matching \"splay2\"", reason);
    }

    [Fact]
    public void A_name_matching_nothing_attached_is_refused_with_the_name_in_the_reason()
    {
        var one = Output(0, 0, @"\\.\DISPLAY1", attached: true);
        var adapters = new[] { Adapter(0, "NVIDIA GeForce RTX 4080", one) };

        var chosen = DisplayInventory.Select(adapters, "DISPLAY9", preferVirtualDisplay: false, out var reason);

        Assert.Null(chosen);
        Assert.Equal("no screen matches \"DISPLAY9\"", reason);
    }

    [Theory]
    [InlineData("Virtual Display Driver", true)]
    [InlineData("Virtual Display Driver (HDR)", true)]
    [InlineData("IddSampleDriver Device", true)]
    [InlineData("IddSampleDriver Device HDR", true)]
    [InlineData("Parsec Virtual Display Adapter", true)]
    [InlineData("usbmmidd", true)]
    [InlineData("USB Mobile Monitor Virtual Display", true)]
    [InlineData("virtual display driver", true)]
    // The three that share this machine with a virtual display and must never be taken for one:
    // the second is what an RDP session draws on, and choosing it is the fault this all guards.
    [InlineData("NVIDIA GeForce RTX 4080", false)]
    [InlineData("Microsoft Remote Display Adapter", false)]
    [InlineData("Microsoft Basic Render Driver", false)]
    public void A_virtual_display_is_known_by_its_name_whatever_a_build_appends(string name, bool isVirtual)
    {
        var adapter = Adapter(0, name);

        Assert.Equal(isVirtual, adapter.IsVirtualDisplay);
    }

    [Fact]
    public void HasVirtualDisplay_looks_at_every_adapter_whether_attached_or_not()
    {
        var notAttached = Output(1, 0, @"\\.\DISPLAY2", attached: false);
        var adapters = new[]
        {
            Adapter(0, "NVIDIA GeForce RTX 4080"),
            Adapter(1, "Virtual Display Driver", notAttached),
        };

        Assert.True(DisplayInventory.HasVirtualDisplay(adapters));
        Assert.False(DisplayInventory.HasVirtualDisplay(new[] { Adapter(0, "NVIDIA GeForce RTX 4080") }));
    }
}
