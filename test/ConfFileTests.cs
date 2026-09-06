//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using RemoteGameHub.App;
using Xunit;

namespace RemoteGameHub.Tests;

// The hand-editable file: what it forgives, what it refuses, and how it says so.
public class ConfFileTests
{
    [Fact]
    public void Sections_keys_comments_and_blank_lines()
    {
        var file = ConfFile.Parse("""
            # a comment
            ; another kind

            [General]
              HostName = living-room

            [network]
            PortBase=48989
            """);

        Assert.Equal("living-room", file.Text("general", "hostname", "x"));
        Assert.Equal(48989, file.Number("Network", "PortBase", 0, 1, 65535));
        Assert.True(file.Has("General", "HostName"));
        Assert.False(file.Has("General", "Missing"));
    }

    [Fact]
    public void Every_key_asked_for_and_not_found_is_reported_once()
    {
        var file = ConfFile.Parse("[General]\nHostName = living-room\n");

        file.Text("General", "HostName", "x");     // present: not a gap
        file.Bool("General", "Debug", false);      // absent: a gap
        file.Number("Network", "PortBase", 47989, 1, 65535);   // absent, new section: a gap

        Assert.Equal(
            new[] { ("General", "Debug"), ("Network", "PortBase") },
            file.MissingKeys);
    }

    [Fact]
    public void An_empty_value_keeps_the_default_except_for_lists()
    {
        var file = ConfFile.Parse("[Games]\nFolders =\nDepth =\n");

        Assert.Empty(file.List("Games", "Folders", new[] { "default" }));
        Assert.Equal(2, file.Number("Games", "Depth", 2, 1, 8));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("FALSE", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public void Booleans_in_the_four_spellings(string text, bool expected)
    {
        var file = ConfFile.Parse($"[General]\nDebug = {text}\n");
        Assert.Equal(expected, file.Bool("General", "Debug", !expected));
    }

    [Fact]
    public void A_boolean_spelt_otherwise_is_an_error_not_false()
    {
        var file = ConfFile.Parse("[General]\nDebug = yes\n");
        Assert.Throws<FormatException>(() => file.Bool("General", "Debug", false));
    }

    [Fact]
    public void Numbers_out_of_range_are_clamped_and_reported()
    {
        var file = ConfFile.Parse("[General]\nBitrate = 5000000\n");
        string? said = null;

        var value = file.Number("General", "Bitrate", 20000, 500, 500_000, message => said = message);

        Assert.Equal(500_000, value);
        Assert.Contains("5000000", said);
        Assert.Contains("500000", said);
    }

    [Fact]
    public void Enums_are_read_case_insensitively()
    {
        var file = ConfFile.Parse("[General]\nCodec = HEVC\n");
        Assert.Equal(VideoCodec.Hevc, file.Enum("General", "Codec", VideoCodec.Auto));

        var unknown = ConfFile.Parse("[General]\nCodec = vp9\n");
        Assert.Throws<FormatException>(() => unknown.Enum("General", "Codec", VideoCodec.Auto));
    }

    [Fact]
    public void Lists_split_on_commas_unless_quoted_or_bracketed()
    {
        var file = ConfFile.Parse(
            "[Games]\nFolders = D:\\Games, \"E:\\Discs, old\", [F:\\Emulators (2004)] , G:\\More\n");

        Assert.Equal(
            new[] { @"D:\Games", @"E:\Discs, old", @"F:\Emulators (2004)", @"G:\More" },
            file.List("Games", "Folders", Array.Empty<string>()));
    }

    [Theory]
    [InlineData("[General\nDebug = true", "1")]
    [InlineData("Debug = true", "1")]
    [InlineData("[General]\njust words", "2")]
    [InlineData("[General]\n = 1", "2")]
    public void Mistakes_are_refused_with_the_line_number(string text, string line)
    {
        var error = Assert.Throws<FormatException>(() => ConfFile.Parse(text));
        Assert.StartsWith($"Line {line}:", error.Message);
    }

    [Fact]
    public void Writer_produces_what_the_parser_reads()
    {
        var writer = new ConfFile.Writer();
        writer.Section("General");
        writer.Note("A note,\nover two lines.");
        writer.Key("Debug", true);
        writer.Key("Fps", 60);
        writer.Key("Folders", new[] { @"D:\Games", @"E:\Discs, old" });

        var file = ConfFile.Parse(writer.ToString());

        Assert.True(file.Bool("General", "Debug", false));
        Assert.Equal(60, file.Number("General", "Fps", 0, 1, 240));
        // The comma inside a folder survives: the writer quotes it, the reader honours the quotes.
        Assert.Equal(new[] { @"D:\Games", @"E:\Discs, old" }, file.List("General", "Folders", Array.Empty<string>()));
    }
}
