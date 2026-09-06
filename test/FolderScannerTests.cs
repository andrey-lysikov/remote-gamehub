//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using RemoteGameHub.Library;
using Xunit;

namespace RemoteGameHub.Tests;

// A folder of games, as a person keeps one: which files are games and what to call them.
public class FolderScannerTests
{
    [Fact]
    public void One_entry_per_game_folder_named_after_the_folder()
    {
        using var folder = new TestFolder();
        folder.File(@"Avatar Frontiers of Pandora\afop.exe", new string('x', 5000));
        folder.File(@"Avatar Frontiers of Pandora\UbisoftConnectInstaller.exe", new string('x', 100));
        folder.File(@"Avatar Frontiers of Pandora\bin\engine.exe", new string('x', 9000));
        folder.File(@"FTL_1.6.14\FTLGame.exe", "game");
        folder.File(@"Empty\readme.txt", "nothing startable");

        var games = FolderScanner.Scan(new[] { folder.Path }, depth: 3);

        Assert.Equal(2, games.Count);

        var avatar = Assert.Single(games, g => g.Title == "Avatar Frontiers of Pandora");
        // The shallowest and, among those, the largest: the game, not the installer beside it
        // and not the engine below it.
        Assert.EndsWith("afop.exe", avatar.LaunchCommand);
        Assert.Equal(Path.Combine(folder.Path, "Avatar Frontiers of Pandora"), avatar.InstallPath);

        // Underscores become spaces; the version stays, because it is information.
        Assert.Contains(games, g => g.Title == "FTL 1.6.14");
    }

    [Fact]
    public void Shortcuts_directly_in_the_folder_are_entries_of_their_own()
    {
        using var folder = new TestFolder();
        folder.File("Doom.lnk");
        folder.File("Portal 2.url");
        folder.File("notes.txt");

        var games = FolderScanner.Scan(new[] { folder.Path }, depth: 1);

        Assert.Equal(new[] { "Doom", "Portal 2" }, games.Select(g => g.Title).OrderBy(t => t));
        Assert.All(games, g => Assert.Equal("folder", g.Source));
    }

    [Fact]
    public void Installers_and_support_files_are_not_games()
    {
        using var folder = new TestFolder();
        folder.File("unins000.exe");
        folder.File("vcredist_x64.exe");
        folder.File("DXSETUP.exe");
        folder.File("GameSetup.exe");
        folder.File("CrashHandler64.exe");
        folder.File(@"redist\something.exe");
        folder.File(@"Real Game\game.exe");

        var games = FolderScanner.Scan(new[] { folder.Path }, depth: 2);

        Assert.Equal("Real Game", Assert.Single(games).Title);
    }

    [Fact]
    public void Depth_one_reads_the_folder_itself_only()
    {
        using var folder = new TestFolder();
        folder.File(@"Deeper\game.exe");

        Assert.Empty(FolderScanner.Scan(new[] { folder.Path }, depth: 1));
        Assert.Single(FolderScanner.Scan(new[] { folder.Path }, depth: 2));
    }

    [Fact]
    public void A_missing_folder_is_skipped_not_fatal()
    {
        var games = FolderScanner.Scan(new[] { @"Q:\No\Such\Folder", string.Empty }, depth: 2);
        Assert.Empty(games);
    }
}
