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

    [Theory]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\cs2.exe", "Steam")]
    [InlineData(@"d:/steamlibrary/steamapps/common/counter-strike global offensive/game/bin/cs2.exe", "Steam")]
    [InlineData(@"""D:\Games\Diablo IV\Diablo IV.exe"" -launch", "Battle.net")]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive Tools\x.exe", null)]
    [InlineData(@"D:\Games\Other\other.exe", null)]
    [InlineData("steam://rungameid/730", null)]
    public void A_folder_find_inside_a_store_game_folder_gives_way_to_the_store(string command, string? store)
    {
        var storeFolders = new[]
        {
            (@"D:\STEAMLIBRARY\STEAMAPPS\COMMON\COUNTER-STRIKE GLOBAL OFFENSIVE", "Steam"),
            (@"D:\GAMES\DIABLO IV", "Battle.net"),
        };

        var found = new ScannedGame("folder", null, "whatever", command, null, null);
        Assert.Equal(store, GameLibrary.InStoreFolder(found, storeFolders));
    }

    [Fact]
    public void A_store_starting_folder_is_kept_until_somebody_names_another_install_folder()
    {
        using var folder = new TestFolder();
        using var database = Database.Open(folder.Path);
        var library = new GameLibrary(database);

        var games = Path.Combine(folder.Path, "games");
        folder.File(@"games\Alpha\alpha.exe");
        library.Rescan(FoldersOnly(games));

        var row = library.Details().Single();
        using (var set = database.Command("UPDATE games SET working_dir = 'D:\\DOSBOX' WHERE id = $id;"))
        {
            set.Parameters.AddWithValue("$id", row.Id);
            set.ExecuteNonQuery();
        }

        Assert.Equal(@"D:\DOSBOX", library.Target(row.Id)!.WorkingDirectory);

        // A new name keeps the store's folders; a new install folder drops the starting one.
        library.Save(row.Id, "Alpha!", row.LaunchCommand, row.InstallPath);
        Assert.Equal(@"D:\DOSBOX", library.Target(row.Id)!.WorkingDirectory);

        library.Save(row.Id, "Alpha!", row.LaunchCommand, Path.Combine(games, "elsewhere"));
        Assert.Null(library.Target(row.Id)!.WorkingDirectory);
    }

    [Fact]
    public void Saving_only_the_switches_leaves_a_game_the_scanners()
    {
        using var folder = new TestFolder();
        using var database = Database.Open(folder.Path);
        var library = new GameLibrary(database);

        var games = Path.Combine(folder.Path, "games");
        folder.File(@"games\Alpha\alpha.exe");
        library.Rescan(FoldersOnly(games));

        // What the editor sends when only the quality or the pointer was changed.
        var row = library.Details().Single();
        library.Save(row.Id, row.Title, row.LaunchCommand, row.InstallPath);

        Assert.False(library.Details().Single().Manual);
    }

    [Fact]
    public void A_removed_edit_does_not_hold_back_the_game_the_scan_finds()
    {
        using var folder = new TestFolder();
        using var database = Database.Open(folder.Path);
        var library = new GameLibrary(database);

        var games = Path.Combine(folder.Path, "games");
        var alpha = folder.File(@"games\Alpha\alpha.exe");
        library.Rescan(FoldersOnly(games));

        // Edited to start something else under the same name, then taken off the list.
        var row = library.Details().Single();
        library.Save(row.Id, "Alpha", @"C:\elsewhere\alpha.exe", row.InstallPath);
        library.Remove(row.Id);

        library.Rescan(FoldersOnly(games));

        var detail = Assert.Single(library.Details());
        Assert.False(detail.Manual);
        Assert.Equal(alpha, detail.LaunchCommand);
    }

    [Fact]
    public void A_reset_game_is_listed_again_as_the_scan_finds_it()
    {
        using var folder = new TestFolder();
        using var database = Database.Open(folder.Path);
        var library = new GameLibrary(database);

        var games = Path.Combine(folder.Path, "games");
        folder.File(@"games\Alpha\alpha.exe");
        library.Rescan(FoldersOnly(games));

        var row = library.Details().Single();
        library.Save(row.Id, "Alpha, renamed", row.LaunchCommand, row.InstallPath);
        library.RecordQuality(row.Id, StreamQuality.Low);

        Assert.StartsWith("Reset.", library.Reset(row.Id));
        Assert.Empty(library.Details());

        library.Rescan(FoldersOnly(games));

        var detail = Assert.Single(library.Details());
        Assert.Equal("Alpha", detail.Title);
        Assert.False(detail.Manual);
        Assert.Equal(StreamQuality.High, detail.Quality);

        // Nothing to go back to for a game somebody added.
        var added = library.Save(0, "Dolphin", "steam://rungameid/12345", null);
        Assert.DoesNotContain("Reset.", library.Reset(added));
        Assert.Contains(library.Details(), game => game.Id == added);
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

    [Fact]
    public void A_cover_missing_from_disk_is_offered_again()
    {
        using var folder = new TestFolder();
        using var database = Database.Open(folder.Path);
        var library = new GameLibrary(database);

        var id = library.Save(0, "One", @"C:\one.exe", null);
        var cover = folder.File(@"covers\1.jpg", "not really a picture");
        library.RecordArtwork(id, cover);
        Assert.Empty(library.NeedingArtwork(TimeSpan.FromDays(30)));

        // The cache folder was cleared by hand: the row still points at the file, but it is gone.
        File.Delete(cover);
        Assert.Equal(1, library.ForgetMissingArtwork());

        Assert.Null(library.BoxArtPath(id));
        Assert.Single(library.NeedingArtwork(TimeSpan.FromDays(30)));

        // A second pass with nothing newly missing finds nothing to forget.
        Assert.Equal(0, library.ForgetMissingArtwork());
    }
}
