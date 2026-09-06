//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using RemoteGameHub.Library;
using Xunit;

namespace RemoteGameHub.Tests;

// Steam's own file format, as libraryfolders.vdf and the manifests use it.
public class ValveKeyValuesTests
{
    [Fact]
    public void Reads_nested_blocks_and_values()
    {
        var root = ValveKeyValues.Parse("""
            "libraryfolders"
            {
                "0"
                {
                    "path"        "C:\\Program Files (x86)\\Steam"
                    "label"       ""
                    "apps"
                    {
                        "228980"    "123"
                    }
                }
                "1"
                {
                    "path"        "D:\\SteamLibrary"
                }
            }
            """);

        Assert.Equal("libraryfolders", root.Name);
        Assert.Equal(2, root.Blocks.Count);
        Assert.Equal(@"C:\Program Files (x86)\Steam", root.Blocks[0].Value("path"));
        Assert.Equal(@"D:\SteamLibrary", root.Blocks[1].Value("path"));
        Assert.Equal("123", root.Blocks[0].Blocks[0].Value("228980"));
    }

    [Fact]
    public void Keys_are_case_insensitive_like_steam_reads_them()
    {
        var root = ValveKeyValues.Parse("\"AppState\" { \"appid\" \"275850\" \"Name\" \"No Man's Sky\" }");

        Assert.Equal("275850", root.Value("AppId"));
        Assert.Equal("No Man's Sky", root.Value("name"));
    }

    [Fact]
    public void Comments_and_bare_numbers_are_accepted()
    {
        var root = ValveKeyValues.Parse("""
            "AppState" // the manifest
            {
                "StateFlags"   4
                // "commented"  "out"
                "installdir"   "Kena"
            }
            """);

        Assert.Equal("4", root.Value("StateFlags"));
        Assert.Equal("Kena", root.Value("installdir"));
        Assert.Null(root.Value("commented"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"root\"")]
    [InlineData("\"root\" { \"key\" }")]
    [InlineData("\"root\" { \"key\" \"value\"")]
    [InlineData("\"root\" { \"open")]
    public void Malformed_text_is_refused_rather_than_half_read(string text)
    {
        Assert.Throws<FormatException>(() => ValveKeyValues.Parse(text));
    }
}
