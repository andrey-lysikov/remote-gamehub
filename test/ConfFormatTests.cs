//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using RemoteGameHub.App;
using Xunit;

namespace RemoteGameHub.Tests;

// The settings as a whole: they survive a write and a read, sample.conf is what the defaults
// produce, and the ports the client is told follow the base the way the protocol says.
public class ConfFormatTests
{
    [Fact]
    public void Settings_survive_a_write_and_a_read()
    {
        var written = new AppConfig
        {
            Debug = false,
            HostName = "Kitchen",
            PortBase = 48989,
            BindAddress = "192.168.1.20",
            WebPort = 8080,
            Upnp = true,
            Steam = false,
            Xbox = true,
            Epic = false,
            Gog = true,
            Ea = false,
            BattleNet = true,
            GamesFolders = new[] { @"D:\Games", @"E:\Old, discs" },
            GamesDepth = 3,
            GamesArtwork = false,
        };

        var read = ConfFormat.Read(ConfFile.Parse(ConfFormat.Write(written)), _ => { });

        Assert.False(read.Debug);
        Assert.Equal("Kitchen", read.HostName);
        Assert.Equal(48989, read.PortBase);
        Assert.Equal("192.168.1.20", read.BindAddress);
        Assert.Equal(8080, read.WebPort);
        Assert.True(read.Upnp);
        Assert.False(read.Steam);
        Assert.True(read.Xbox);
        Assert.False(read.Epic);
        Assert.True(read.Gog);
        Assert.False(read.Ea);
        Assert.True(read.BattleNet);
        Assert.Equal(new[] { @"D:\Games", @"E:\Old, discs" }, read.GamesFolders);
        Assert.Equal(3, read.GamesDepth);
        Assert.False(read.GamesArtwork);
    }

    [Fact]
    public void Out_of_range_numbers_are_clamped_and_the_warning_names_them()
    {
        var warnings = new List<string>();
        var read = ConfFormat.Read(
            ConfFile.Parse("[Network]\nPortBase = 1\n[Games]\nDepth = 99\n"), warnings.Add);

        Assert.Equal(AppParameters.Limits.MinPortBase, read.PortBase);
        Assert.Equal(AppParameters.Limits.MaxGamesFolderDepth, read.GamesDepth);
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("PortBase"));
        Assert.Contains(warnings, w => w.Contains("Depth"));
    }

    [Fact]
    public void Sample_conf_is_what_the_defaults_write()
    {
        var sample = FindInRepository("sample.conf");
        var expected = ConfFormat.Write(new AppConfig());

        Assert.Equal(Normalise(expected), Normalise(File.ReadAllText(sample)));
    }

    [Fact]
    public void Ports_follow_the_base_the_way_moonlight_derives_them()
    {
        var config = new AppConfig { PortBase = 47989 };

        Assert.Equal(47989, config.HttpPort);
        Assert.Equal(47984, config.HttpsPort);
        Assert.Equal(48010, config.RtspPort);
        Assert.Equal(47998, config.VideoPort);
        Assert.Equal(47999, config.ControlPort);
        Assert.Equal(48000, config.AudioPort);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Remote-Gamehub\", true)]
    [InlineData(@"C:\Program Files (x86)\Remote-Gamehub\", true)]
    [InlineData(@"C:\ProgramData\Something\", true)]
    [InlineData(@"C:\Windows\System32\", true)]
    [InlineData(@"D:\Program Files Backup\App\", false)]
    [InlineData(@"C:\Users\Game\Downloads\Remote-Gamehub\build\", false)]
    [InlineData(@"C:\Users\Game\AppData\Local\Remote-Gamehub\", false)]
    public void A_system_folder_is_recognised_by_its_segments(string path, bool system)
    {
        Assert.Equal(system, AppConfig.IsSystemFolder(path));
    }

    private static string Normalise(string text) =>
        text.Replace("\r\n", "\n").TrimStart('\uFEFF').TrimEnd();

    // The repository root, found by walking up from the test binary until the file is met. The
    // binary sits several folders below it, and how many depends on the configuration.
    private static string FindInRepository(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, name);
            if (File.Exists(candidate)) return candidate;

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"{name} was not found above {AppContext.BaseDirectory}");
    }
}
