//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using RemoteGameHub.Protocol;
using Xunit;

namespace RemoteGameHub.Tests;

// The page is three files built into the executable rather than string literals in C#. What that
// costs is these tests: the compiler no longer notices a file left out of the build, or a slot in
// the markup that nothing fills.
public class WebAssetsTests
{
    // Every slot the whole page asks for, and the only ones WebConsole.Page() knows how to fill.
    private static readonly string[] PageSlots =
    {
        "theme", "name", "version", "project", "style", "script", "host", "status",
        "pairclass", "who", "autologon", "games", "clients", "blockedbox", "pointer", "log",
    };

    [Fact]
    public void The_page_its_styles_and_its_script_are_all_in_the_executable()
    {
        Assert.Contains("<header>", WebAssets.Part("page"));
        Assert.Contains("<footer id=logbox>", WebAssets.Part("page"));

        // A stylesheet and a script that are merely there, rather than merely not missing: an
        // empty resource is what a file dropped from the csproj looks like.
        Assert.Contains("--bg:", WebAssets.Style);
        Assert.Contains("addEventListener", WebAssets.Script);

        // The tags belong to the markup, so that the two files are a stylesheet and a script and
        // can be read by anything that knows what those are.
        Assert.DoesNotContain("<style>", WebAssets.Style);
        Assert.DoesNotContain("<script>", WebAssets.Script);
    }

    [Fact]
    public void The_page_asks_for_exactly_the_slots_the_server_fills()
    {
        Assert.Equal(PageSlots.OrderBy(name => name),
                     WebAssets.SlotsIn(WebAssets.Part("page")).OrderBy(name => name));
    }

    [Fact]
    public void The_pieces_that_are_only_sometimes_drawn_are_there_with_their_own_slots()
    {
        Assert.Contains("id=autologon", WebAssets.Part("autologon"));
        Assert.Equal(new[] { "account" }, WebAssets.SlotsIn(WebAssets.Part("autologon")));

        Assert.Contains("id=blockedbox", WebAssets.Part("blocked"));
        Assert.Equal(new[] { "blocked" }, WebAssets.SlotsIn(WebAssets.Part("blocked")));

        Assert.Contains("id=pointer", WebAssets.Part("pointer"));
        Assert.Empty(WebAssets.SlotsIn(WebAssets.Part("pointer")));
    }

    [Fact]
    public void A_value_is_never_read_for_slots_of_its_own()
    {
        // A game somebody called "{{log}}" is a game called "{{log}}", not the log.
        var filled = WebAssets.Fill("<i>{{games}}</i><b>{{log}}</b>",
                                    ("games", "{{log}}"), ("log", "the log"));

        Assert.Equal("<i>{{log}}</i><b>the log</b>", filled);
    }

    [Fact]
    public void A_slot_nothing_fills_leaves_a_hole_rather_than_the_slot_itself()
    {
        Assert.Equal("<i></i>", WebAssets.Fill("<i>{{nobody}}</i>"));
    }

    [Fact]
    public void A_part_that_does_not_exist_is_empty_rather_than_a_crash()
    {
        Assert.Equal(string.Empty, WebAssets.Part("nothing"));
    }
}
