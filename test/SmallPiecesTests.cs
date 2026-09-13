//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Net;
using RemoteGameHub.App;
using RemoteGameHub.Library;
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

    [Theory]
    [InlineData("steam://rungameid/10", "steam://rungameid/10", "")]
    [InlineData(@"C:\Games\My Game\game.exe", @"C:\Games\My Game\game.exe", "")]
    [InlineData(@"""C:\Games\My Game\game.exe""", @"C:\Games\My Game\game.exe", "")]
    [InlineData(@"""C:\Battle.net\Battle.net.exe"" --exec=""launch Pro""",
                @"C:\Battle.net\Battle.net.exe", @"--exec=""launch Pro""")]
    [InlineData(@"C:\GOG Games\Doom\DOSBOX\dosbox.exe -conf ""..\dosbox.conf"" -noconsole",
                @"C:\GOG Games\Doom\DOSBOX\dosbox.exe", @"-conf ""..\dosbox.conf"" -noconsole")]
    [InlineData(@"shell:AppsFolder\Game.exe App!App", @"shell:AppsFolder\Game.exe App!App", "")]
    public void A_launch_command_is_split_only_after_a_quoted_path(string command, string file,
                                                                   string arguments)
    {
        Assert.Equal((file, arguments), SessionLauncher.SplitCommand(command));
    }

    [Theory]
    [InlineData(@"""Blizzard Uninstaller.exe"" --lang=enUS --uid=wow_enus", "wow")]
    [InlineData(@"""Blizzard Uninstaller.exe"" --lang=enUS --uid=hs_beta", "hs_beta")]
    [InlineData(@"""Blizzard Uninstaller.exe"" --uid=fenris --displayname=""Diablo IV""", "fenris")]
    [InlineData(@"""Blizzard Uninstaller.exe"" --lang=enUS", null)]
    public void A_battle_net_uid_loses_only_a_language_suffix(string command, string? uid)
    {
        Assert.Equal(uid, LauncherScanners.UidFrom(command));
    }

    [Fact]
    public void A_battle_net_game_is_started_by_its_product_code_not_its_page()
    {
        Assert.Equal(@"""C:\Battle.net\Battle.net.exe"" --exec=""launch Fen""",
                     LauncherScanners.BattleNetLaunch(@"C:\Battle.net\Battle.net.exe", "Fen", "fenris"));

        // Without the launcher or the code, the page is what there is.
        Assert.Equal("battlenet://fenris",
                     LauncherScanners.BattleNetLaunch(@"C:\Battle.net\Battle.net.exe", null, "fenris"));
        Assert.Equal("battlenet://fenris", LauncherScanners.BattleNetLaunch(null, "Fen", "fenris"));
    }

    [Fact]
    public void Battle_net_product_codes_are_read_from_its_own_product_db()
    {
        // Two installs as the agent writes them, with a number field in between that is not ours.
        var data = Field(1, Concat(Text(1, "fenris"), Text(2, "Fen"),
                                   Field(3, Text(1, "D:/Games/Diablo IV")), new byte[] { 0x20, 0x01 }))
            .Concat(Field(1, Concat(Text(1, "wow_enus"), Text(2, "wow"))))
            .Concat(Field(1, Text(1, "agent")))
            .ToArray();

        var products = LauncherScanners.ReadProductDb(data);

        Assert.Equal(2, products.Count);
        Assert.Equal(new LauncherScanners.BattleNetProduct("fenris", "Fen", "D:/Games/Diablo IV"),
                     products[0]);

        // By folder, whatever the slashes; by uid without its language otherwise.
        Assert.Equal("Fen", LauncherScanners.ProductFor(products, "other", @"D:\Games\Diablo IV\")?.Code);
        Assert.Equal("wow", LauncherScanners.ProductFor(products, "wow", null)?.Code);
        Assert.Null(LauncherScanners.ProductFor(products, "prometheus", @"C:\Overwatch"));

        // Not a protocol buffer at all: nothing, rather than an exception.
        Assert.Empty(LauncherScanners.ReadProductDb(new byte[] { 0x0A, 0xFF, 0xFF }));

        static byte[] Text(int field, string text) => Field(field, System.Text.Encoding.UTF8.GetBytes(text));
        static byte[] Field(int field, byte[] value) =>
            new[] { (byte)(field << 3 | 2), (byte)value.Length }.Concat(value).ToArray();
        static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();
    }

    [Fact]
    public void Ea_offer_identifiers_are_read_from_the_installer_manifest()
    {
        const string manifest = """
            <?xml version="1.0" encoding="utf-8"?>
            <DiPManifest version="4.0">
              <gameTitles><gameTitle locale="en_US">Battlefield 1</gameTitle></gameTitles>
              <contentIDs><contentID>1026023</contentID><contentID> 1026480 </contentID><contentID>1026023</contentID></contentIDs>
            </DiPManifest>
            """;

        Assert.Equal(new[] { "1026023", "1026480" }, LauncherScanners.EaOffersFrom(manifest));
        Assert.Empty(LauncherScanners.EaOffersFrom("not xml"));
    }

    [Theory]
    [InlineData("Microsoft.ForzaHorizon6_1.2.3.0_x64__8wekyb3d8bbwe", "Microsoft.ForzaHorizon6",
                "Microsoft.ForzaHorizon6_8wekyb3d8bbwe")]
    [InlineData("Microsoft.ForzaHorizon6Demo_1.0.0.0_x64__8wekyb3d8bbwe", "Microsoft.ForzaHorizon6", null)]
    [InlineData("Microsoft.ForzaHorizon6_1.2.3.0_x64__", "Microsoft.ForzaHorizon6", null)]
    public void An_xbox_family_name_is_the_identity_and_the_publisher_hash(string fullName,
                                                                           string identity,
                                                                           string? family)
    {
        Assert.Equal(family, RemoteGameHub.Library.XboxScanner.FamilyNameOf(fullName, identity));
    }

    [Fact]
    public void An_xbox_family_name_can_be_computed_from_the_publisher()
    {
        // Every Microsoft package's family name ends in this hash; it is the known answer.
        Assert.Equal("Microsoft.ForzaHorizon6_8wekyb3d8bbwe",
            RemoteGameHub.Library.XboxScanner.FamilyNameFromPublisher("Microsoft.ForzaHorizon6",
                "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"));

        Assert.Null(RemoteGameHub.Library.XboxScanner.FamilyNameFromPublisher("Game", null));
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
    [InlineData(8, false, 768)]
    [InlineData(8, true, 1920)]
    public void The_audio_bitrate_follows_the_layout(int channels, bool highQuality, int kbps)
    {
        Assert.Equal(kbps, StreamNegotiation.AudioBitrateFor(channels, highQuality));
    }

    [Theory]
    [InlineData("172.30.212.52", "172.30.212.52")]
    [InlineData("192.168.1.5", "192.168.1.5")]
    [InlineData("::ffff:172.30.212.52", "172.30.212.52")]
    public void The_address_told_to_a_client_is_the_one_it_reached(string local, string expected)
    {
        Assert.Equal(expected, Peer.ThisMachine(new IPEndPoint(IPAddress.Parse(local), 47989)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0.0.0.0")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.10.10")]
    public void A_client_is_never_told_an_address_it_cannot_dial(string? local)
    {
        // Loopback is allowed as the last resort — a machine with no card up has nothing else —
        // but the unspecified and link-local answers are the ones a client silently fails on.
        var endpoint = local is null ? null : new IPEndPoint(IPAddress.Parse(local), 47989);

        var answer = IPAddress.Parse(Peer.ThisMachine(endpoint));

        Assert.NotEqual(IPAddress.Any, answer);
        Assert.False(answer.GetAddressBytes() is [169, 254, ..]);
    }
}
