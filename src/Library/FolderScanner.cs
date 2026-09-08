//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.IO.Enumeration;
using RemoteGameHub.App;

namespace RemoteGameHub.Library;

// The optional third source: a folder named in [Games] Folder, walked a few levels deep for
// anything startable. Off until a folder is given, since guessing at one would walk a drive.
internal static class FolderScanner
{
    // What a game folder holds beside the game: installers, redistributables and crash handlers
    // are all executables. Matched against the lower-cased file name.
    private static readonly string[] SkipNames =
    {
        "unins*", "*setup*", "*redist*", "vcredist*", "dxsetup*", "*crashhandler*",
        "*launcher*helper*", "*installer*", "*uninstall*", "*eula*", "*report*",
    };

    // Directories whose whole contents are support files, never games.
    private static readonly string[] SkipDirectories = { "redist", "_CommonRedist", "support" };

    private static readonly string[] Extensions = { ".exe", ".lnk", ".url" };

    internal static IReadOnlyList<ScannedGame> Scan(IReadOnlyList<string> folders, int depth)
    {
        var games = new List<ScannedGame>();

        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;

            try
            {
                if (!Directory.Exists(folder))
                {
                    Log.Warn($"[Games] Folders names \"{folder}\", which does not exist; " +
                             "nothing was scanned from it");
                    continue;
                }

                // Anything startable directly in the scanned folder is its own entry — the
                // folder-of-shortcuts arrangement. Each folder below it is one game; see PickOne.
                foreach (var file in Startable(folder))
                    games.Add(Entry(Tidy(Path.GetFileNameWithoutExtension(file)), file, folder));

                if (depth > 1)
                {
                    foreach (var child in Directory.EnumerateDirectories(folder))
                    {
                        if (SkipDirectories.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase))
                            continue;

                        var chosen = PickOne(child, depth - 1);
                        if (chosen is not null)
                            games.Add(Entry(Tidy(Path.GetFileName(child)), chosen, child));
                    }
                }
            }
            catch (Exception error)
            {
                // One unreadable folder must not cost the others, nor any other source.
                Log.Info($"\"{folder}\" could not be scanned: {error.Message}");
            }
        }

        return games;
    }

    private static ScannedGame Entry(string title, string file, string folder) => new(
        Source: "folder",
        ExternalId: null,
        Title: title,
        // The file itself, through ShellExecute — which is what makes .lnk and .url work at all:
        // they are not executables, they are things the shell knows how to open.
        LaunchCommand: file,
        InstallPath: folder,
        BoxArtPath: null);

    private static IEnumerable<string> Startable(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var name = Path.GetFileName(file);

            if (!Extensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
                continue;
            if (SkipNames.Any(pattern =>
                    FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true)))
                continue;

            yield return file;
        }
    }

    // The one executable that stands for a game folder, or null when it holds none; named after
    // the folder, where the human name lives. The shallowest, and the largest among those.
    private static string? PickOne(string folder, int levelsLeft)
    {
        var best = (string?)null;
        var bestDepth = int.MaxValue;
        var bestSize = -1L;

        void Look(string current, int depth)
        {
            if (depth > levelsLeft) return;

            foreach (var file in Startable(current))
            {
                long size;
                try
                {
                    size = new FileInfo(file).Length;
                }
                catch (Exception)
                {
                    size = 0;   // a shortcut, or a file that went away between the two calls
                }

                if (depth > bestDepth || (depth == bestDepth && size <= bestSize)) continue;

                best = file;
                bestDepth = depth;
                bestSize = size;
            }

            foreach (var child in Directory.EnumerateDirectories(current))
            {
                if (SkipDirectories.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase))
                    continue;

                Look(child, depth + 1);
            }
        }

        Look(folder, 1);
        return best;
    }

    // The file name is all there is to call the game, so it is tidied, not rewritten: underscores
    // become spaces, runs of spaces collapse. Version numbers and capitalisation are left alone.
    private static string Tidy(string name)
    {
        var parts = name.Replace('_', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return string.Join(' ', parts);
    }
}
