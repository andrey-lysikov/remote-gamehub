//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Net;
using RemoteGameHub.App;
using RemoteGameHub.Media;
using RemoteGameHub.Protocol;
using Xunit;

namespace RemoteGameHub.Tests;

// The small decisions that are made in one place each and would be silently wrong elsewhere:
// which addresses the page answers, which version counts as newer, which screen mode is nearest.
public class SmallPiecesTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.20.30.40", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.1.5", true)]
    [InlineData("169.254.10.10", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.128.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("2001:db8::1", false)]
    [InlineData("::ffff:192.168.1.5", true)]
    [InlineData("::ffff:8.8.8.8", false)]
    public void The_page_answers_private_addresses_only(string address, bool isPrivate)
    {
        Assert.Equal(isPrivate, WebConsole.IsPrivate(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("0.2", "0.1", true)]
    [InlineData("0.10", "0.9", true)]
    [InlineData("1.0", "0.9", true)]
    [InlineData("0.1", "0.1", false)]
    [InlineData("0.1", "0.2", false)]
    [InlineData("0.1.1", "0.1", true)]
    [InlineData("soon", "0.1", false)]
    [InlineData("0.2", "", false)]
    public void A_newer_version_is_compared_as_numbers(string latest, string current, bool newer)
    {
        Assert.Equal(newer, UpdateChecker.IsNewer(latest, current));
    }

    [Fact]
    public void The_nearest_screen_mode_is_chosen_by_size_then_by_rate()
    {
        var modes = new[]
        {
            new DisplayMode(2560, 1440, 120, 32),
            new DisplayMode(2560, 1440, 60, 32),
            new DisplayMode(1920, 1080, 120, 32),
            new DisplayMode(1920, 1080, 60, 32),
            new DisplayMode(1280, 720, 60, 32),
        };

        // The exact size, at the rate nearest the frame rate.
        Assert.Equal(new DisplayMode(1920, 1080, 60, 32), DisplayAdaptation.Choose(modes, 1920, 1080, 60));

        // A size the screen does not have: the nearest one, and the higher of two rates equally
        // far from what was asked — a rate above costs nothing, one below caps the stream.
        Assert.Equal(new DisplayMode(2560, 1440, 120, 32), DisplayAdaptation.Choose(modes, 3024, 1964, 90));

        Assert.Null(DisplayAdaptation.Choose(Array.Empty<DisplayMode>(), 1920, 1080, 60));
        Assert.Null(DisplayAdaptation.Choose(modes, 0, 0, 60));
    }

    [Fact]
    public void A_rate_the_screen_does_not_have_is_taken_as_a_multiple_of_it()
    {
        var modes = new[]
        {
            new DisplayMode(1920, 1080, 59, 32),
            new DisplayMode(1920, 1080, 100, 32),
            new DisplayMode(1920, 1080, 120, 32),
            new DisplayMode(1920, 1080, 240, 32),
        };

        // 50 fps has no mode of its own: twice it, not the nearest. 50 frames out of 59.94 drop
        // one ten times a second; 50 out of 100 drop none.
        Assert.Equal(100, DisplayAdaptation.Choose(modes, 1920, 1080, 50)!.RefreshHz);

        // 59 is 59.94 truncated, so for 60 fps it is the rate itself and beats any multiple.
        Assert.Equal(59, DisplayAdaptation.Choose(modes, 1920, 1080, 60)!.RefreshHz);

        // The smallest multiple, not the largest: a screen driven faster than it need be costs
        // the game the frames it renders.
        Assert.Equal(59, DisplayAdaptation.Choose(modes, 1920, 1080, 30)!.RefreshHz);

        // Nothing divides into 90: the nearest rate, as before.
        var fast = new[] { new DisplayMode(1920, 1080, 120, 32), new DisplayMode(1920, 1080, 240, 32) };
        Assert.Equal(120, DisplayAdaptation.Choose(fast, 1920, 1080, 90)!.RefreshHz);
    }

    [Fact]
    public void The_port_offsets_are_the_protocols()
    {
        // The client is handed the base and derives the rest with exactly these offsets; they
        // are checked here so that a tidy-up cannot move one.
        Assert.Equal(-5, AppParameters.Ports.HttpsOffset);
        Assert.Equal(0, AppParameters.Ports.HttpOffset);
        Assert.Equal(9, AppParameters.Ports.VideoOffset);
        Assert.Equal(10, AppParameters.Ports.ControlOffset);
        Assert.Equal(11, AppParameters.Ports.AudioOffset);
        Assert.Equal(21, AppParameters.Ports.RtspOffset);
        Assert.Equal(47989, AppParameters.Ports.DefaultBase);
    }

    [Fact]
    public void The_version_is_two_numbers()
    {
        Assert.Matches(@"^\d+\.\d+$", Program.Version);
    }

    [Theory]
    [InlineData(2, false, 192)]
    [InlineData(2, true, 480)]
    [InlineData(6, false, 576)]
    [InlineData(6, true, 1440)]
    public void The_audio_bitrate_follows_the_layout(int channels, bool highQuality, int kbps)
    {
        Assert.Equal(kbps, StreamNegotiation.AudioBitrateFor(channels, highQuality));
    }
}
