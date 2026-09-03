//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using RemoteGameHub.App;
using RemoteGameHub.Library;
using Xunit;

namespace RemoteGameHub.Tests;

// The list of games between scans: what a scan adds, what it hides when a game has gone, and
// what somebody typed on the page that no scan may overwrite.
public class GameLibraryTests
{
    // Every store off; only the folder given is scanned.
    private static AppConfig FoldersOnly(string folder) => new()
    {
        Steam = false, Xbox = false, Epic = false, Gog = false, Ea = false, BattleNet = false,
        GamesFolders = new[] { folder },
        GamesDepth = 2,
    };

    [Fact]
    public void A_scan_lists_what_it_found_and_hides_what_has_gone()
    {
        using var folder = new TestFolder();
        using var database = Database.Open(folder.Path);
        var library = new GameLibrary(database);

        var games = Path.Combine(folder.Path, "games");
        folder.File(@"games\Alpha\alpha.exe");
        folder.File(@"games\Beta\beta.exe");

        library.Rescan(FoldersOnly(games));
        Assert.Equal(new[] { "Alpha", "Beta" }, library.List().Select(g => g.Title));

        // A game rescanned keeps its row and therefore its number.
        var alphaId = library.List().Single(g => g.Title == "Alpha").Id;
        library.Rescan(FoldersOnly(games));
        Assert.Equal(alphaId, library.List().Single(g => g.Title == "Alpha").Id);

        var betaId = library.List().Single(g => g.Title == "Beta").Id;
        Directory.Delete(Path.Combine(games, "Beta"), recursive: true);
        library.Rescan(FoldersOnly(games));

        Assert.Equal(new[] { "Alpha" }, library.List().Select(g => g.Title));
        Assert.Null(library.Target(betaId));

        // Back within the keeping period: the same row returns.
        folder.File(@"games\Beta\beta.exe");
        library.Rescan(FoldersOnly(games));
        Assert.Equal(new[] { "Alpha", "Beta" }, library.List().Select(g => g.Title));
    }

    [Fact]
    public void What_was_typed_on_the_page_outlives_the_scan()
    {
        using var folder = new TestFolder();
        using var database = Database.Open(folder.Path);
        var library = new GameLibrary(database);

        var games = Path.Combine(folder.Path, "games");
        folder.File(@"games\afop\afop.exe");
        library.Rescan(FoldersOnly(games));

        var row = library.Details().Single();
        library.Save(row.Id, "Avatar: Frontiers of Pandora", row.LaunchCommand, Path.Combine(games, "afop"));

        library.Rescan(FoldersOnly(games));

        var detail = Assert.Single(library.Details());
        Assert.Equal("Avatar: Frontiers of Pandora", detail.Title);
        Assert.True(detail.Manual);
        Assert.Single(library.List());   // the scanner's "afop" did not come back beside it
    }

    [Fact]
    public void A_game_added_by_hand_is_kept_and_can_be_removed()
    {
        using var folder = new TestFolder();
        using var database = Database.Open(folder.Path);
        var library = new GameLibrary(database);

        var id = library.Save(0, "Dolphin", "steam://rungameid/12345", null);
        Assert.True(id > 0);

        var target = library.Target(id);
        Assert.NotNull(target);
        Assert.Equal("Dolphin", target.Title);
        Assert.Equal("steam://rungameid/12345", target.Command);

        // A scan that finds nothing does not throw away what somebody entered when it cannot
        // tell: a steam:// address is unanswerable from the file system, so the row stays.
        library.Rescan(FoldersOnly(Path.Combine(folder.Path, "none")));
        Assert.Contains(library.List(), g => g.Id == id);

        library.Remove(id);
        Assert.DoesNotContain(library.List(), g => g.Id == id);
        Assert.Null(library.Target(id));
    }

    [Fact]
    public void The_same_title_from_two_sources_is_listed_once()
    {
        using var folder = new TestFolder();
        using var database = Database.Open(folder.Path);
        var library = new GameLibrary(database);

        var games = Path.Combine(folder.Path, "games");
        folder.File(@"games\Portal 2\portal2.exe");

        // Typed first, then found by the scanner under a name that differs only in punctuation.
        library.Save(0, "Portal-2", "steam://rungameid/620", null);
        library.Rescan(FoldersOnly(games));

        var detail = Assert.Single(library.Details());
        Assert.Equal("Portal-2", detail.Title);
        Assert.Equal("steam://rungameid/620", detail.LaunchCommand);
    }

    [Fact]
    public void Artwork_is_recorded_and_only_offered_to_games_without_it()
    {
        using var folder = new TestFolder();
        using var database = Database.Open(folder.Path);
        var library = new GameLibrary(database);

        var first = library.Save(0, "One", @"C:\one.exe", null);
        var second = library.Save(0, "Two", @"C:\two.exe", null);

        Assert.Equal(2, library.NeedingArtwork(TimeSpan.FromDays(30)).Count);

        var cover = folder.File(@"covers\1.jpg", "not really a picture");
        library.RecordArtwork(first, cover);
        library.RecordArtwork(second, null);   // searched for and not found

        Assert.Equal(cover, library.BoxArtPath(first));
        Assert.Null(library.BoxArtPath(second));
        Assert.Empty(library.NeedingArtwork(TimeSpan.FromDays(30)));   // not asked again for a month
        // Unless the wait is over. A negative wait puts the cutoff in the future, which is the
        // one way to be certain of that with stamps that count in whole seconds.
        Assert.Single(library.NeedingArtwork(TimeSpan.FromDays(-1)));
        Assert.Equal(new[] { cover }, library.ArtworkPaths());
    }
}
