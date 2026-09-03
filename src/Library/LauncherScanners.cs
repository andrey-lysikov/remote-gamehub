//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Microsoft.Win32;
using RemoteGameHub.App;

namespace RemoteGameHub.Library;

// The stores that are not Steam and not Xbox: Epic, GOG, EA and Battle.net. Epic keeps a JSON
// manifest per game, the other three the registry. None of the four was read on a real machine.
internal static class LauncherScanners
{
    // ------------------------------------------------------------------ Epic

    // Epic writes one .item file per installed game under ProgramData, JSON naming the game, where
    // it went, and the three identifiers its own launcher needs to start it.
    internal static IReadOnlyList<ScannedGame> Epic()
    {
        var games = new List<ScannedGame>();

        try
        {
            var manifests = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic", "EpicGamesLauncher", "Data", "Manifests");

            if (!Directory.Exists(manifests))
            {
                Log.Info("Epic is not installed; nothing to scan");
                return games;
            }

            foreach (var file in Directory.EnumerateFiles(manifests, "*.item"))
            {
                try
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(file));
                    var root = json.RootElement;

                    var title = Text(root, "DisplayName");
                    var installLocation = Text(root, "InstallLocation");
                    var space = Text(root, "CatalogNamespace");
                    var item = Text(root, "CatalogItemId");
                    var app = Text(root, "AppName");

                    if (title is null || space is null || item is null || app is null) continue;

                    // Epic's own launcher takes the three identifiers joined by colons, escaped.
                    // Starting the executable skips an entitlement check some games need.
                    var launch = $"com.epicgames.launcher://apps/" +
                                 $"{space}%3A{item}%3A{app}?action=launch&silent=true";

                    games.Add(new ScannedGame("epic", app, title, launch,
                        Directory.Exists(installLocation ?? string.Empty) ? installLocation : null,
                        null));
                }
                catch (Exception error)
                {
                    Log.Info($"{Path.GetFileName(file)} could not be read and is skipped: {error.Message}");
                }
            }
        }
        catch (Exception error)
        {
            Log.Warn($"the Epic scan failed and its games will be missing: {error.Message}");
        }

        return games;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // ------------------------------------------------------------------ GOG

    // GOG registers each installed game under its own key, with the command its launcher would use.
    // That command is preferred: it gets the overlay and cloud saves the executable does not.
    internal static IReadOnlyList<ScannedGame> Gog()
    {
        var games = new List<ScannedGame>();

        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games")
                             ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\GOG.com\Games");

            if (root is null)
            {
                Log.Info("GOG is not installed; nothing to scan");
                return games;
            }

            foreach (var id in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(id);
                if (key is null) continue;

                var title = key.GetValue("gameName") as string;
                var path = key.GetValue("path") as string;
                var exe = key.GetValue("exe") as string;
                var command = key.GetValue("launchCommand") as string;

                if (title is null) continue;

                var launch = !string.IsNullOrWhiteSpace(command) ? command
                    : !string.IsNullOrWhiteSpace(exe) ? exe
                    : null;

                if (launch is null) continue;

                games.Add(new ScannedGame("gog", id, title, launch,
                    Directory.Exists(path ?? string.Empty) ? path : null, null));
            }
        }
        catch (Exception error)
        {
            Log.Warn($"the GOG scan failed and its games will be missing: {error.Message}");
        }

        return games;
    }

    // ------------------------------------------------------------------ EA

    // EA's games — under both the old Origin name and the current client — are registered by
    // their offer identifier, which is also how the client is asked to start one.
    internal static IReadOnlyList<ScannedGame> Ea()
    {
        var games = new List<ScannedGame>();

        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Origin Games")
                             ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Origin Games");

            if (root is null)
            {
                Log.Info("no EA games are registered; nothing to scan");
                return games;
            }

            foreach (var offer in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(offer);
                if (key is null) continue;

                var install = key.GetValue("InstallDir") as string
                              ?? key.GetValue("Install Dir") as string;

                // The key carries no title of its own on most installs, so the folder's name is
                // what there is to call it; the online catalogue turns that into a name later.
                var title = key.GetValue("DisplayName") as string
                            ?? (install is not null
                                ? new DirectoryInfo(install.TrimEnd('\\')).Name
                                : null);

                if (title is null) continue;

                games.Add(new ScannedGame("ea", offer, title,
                    $"origin2://game/launch?offerIds={offer}",
                    Directory.Exists(install ?? string.Empty) ? install : null, null));
            }
        }
        catch (Exception error)
        {
            Log.Warn($"the EA scan failed and its games will be missing: {error.Message}");
        }

        return games;
    }

    // ------------------------------------------------------------------ Battle.net

    // Blizzard's games are found through their uninstall entries; the code its launcher answers to
    // is buried in the command as --uid=, with a language suffix to come off — convention, not law.
    internal static IReadOnlyList<ScannedGame> BattleNet()
    {
        var games = new List<ScannedGame>();

        try
        {
            using var uninstall = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");

            if (uninstall is null) return games;

            foreach (var name in uninstall.GetSubKeyNames())
            {
                using var key = uninstall.OpenSubKey(name);
                if (key is null) continue;

                var publisher = key.GetValue("Publisher") as string ?? string.Empty;
                if (!publisher.Contains("Blizzard", StringComparison.OrdinalIgnoreCase)) continue;

                var command = key.GetValue("UninstallString") as string;
                if (command is null || !command.Contains("--uid=", StringComparison.OrdinalIgnoreCase))
                    continue;

                var title = key.GetValue("DisplayName") as string ?? name;
                var install = key.GetValue("InstallLocation") as string;

                // Battle.net itself appears in this list; it is a launcher, not a game.
                if (title.Contains("Battle.net", StringComparison.OrdinalIgnoreCase)) continue;

                var uid = UidFrom(command);
                if (uid is null) continue;

                games.Add(new ScannedGame("battlenet", uid, title, $"battlenet://{uid}",
                    Directory.Exists(install ?? string.Empty) ? install : null, null));

                Log.Info($"    Battle.net: \"{title}\" will be started as battlenet://{uid}");
            }
        }
        catch (Exception error)
        {
            Log.Warn($"the Battle.net scan failed and its games will be missing: {error.Message}");
        }

        return games;
    }

    // The product code out of an uninstall command, with the language suffix removed:
    // wow_enus becomes wow, which is what the launcher's own addresses use.
    private static string? UidFrom(string uninstallCommand)
    {
        const string marker = "--uid=";

        var start = uninstallCommand.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;

        start += marker.Length;

        var end = start;
        while (end < uninstallCommand.Length && !char.IsWhiteSpace(uninstallCommand[end])) end++;

        var uid = uninstallCommand[start..end].Trim('"');
        if (uid.Length == 0) return null;

        var underscore = uid.IndexOf('_');
        return underscore > 0 ? uid[..underscore] : uid;
    }
}
