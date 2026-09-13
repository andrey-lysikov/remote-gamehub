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
                var workingDir = key.GetValue("workingDir") as string;

                if (title is null) continue;

                var launch = !string.IsNullOrWhiteSpace(command) ? command
                    : !string.IsNullOrWhiteSpace(exe) ? exe
                    : null;

                if (launch is null) continue;

                // The folder GOG starts it from, which is not always its install folder: a DOSBox
                // game's arguments name its configuration relative to the DOSBOX folder.
                games.Add(new ScannedGame("gog", id, title, launch,
                    Directory.Exists(path ?? string.Empty) ? path : null, null,
                    Directory.Exists(workingDir ?? string.Empty) ? workingDir : null));
            }
        }
        catch (Exception error)
        {
            Log.Warn($"the GOG scan failed and its games will be missing: {error.Message}");
        }

        return games;
    }

    // ------------------------------------------------------------------ EA

    // EA's games are registered by their offer identifier, which is also how the client is asked
    // to start one. Origin wrote them under "Origin Games"; the EA app does not always, and its
    // games are found through their uninstall entries instead (see EaAppGames).
    internal static IReadOnlyList<ScannedGame> Ea()
    {
        var games = new List<ScannedGame>();

        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Origin Games")
                             ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Origin Games");

            foreach (var offer in root?.GetSubKeyNames() ?? [])
            {
                using var key = root!.OpenSubKey(offer);
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

        games.AddRange(EaAppGames(games));

        if (games.Count == 0) Log.Info("no EA games are registered; nothing to scan");
        return games;
    }

    // The EA app's games: an uninstall entry published by Electronic Arts whose folder holds
    // __Installer\installerdata.xml, the manifest naming the game's content identifiers. Those are
    // the offer identifiers origin2:// takes. Games already found under "Origin Games" are skipped.
    private static IEnumerable<ScannedGame> EaAppGames(IReadOnlyList<ScannedGame> known)
    {
        var games = new List<ScannedGame>();
        var seenOffers = new HashSet<string>(known.Select(g => g.ExternalId ?? string.Empty),
                                             StringComparer.OrdinalIgnoreCase);
        var seenFolders = new HashSet<string>(
            known.Select(g => NormaliseFolder(g.InstallPath)).OfType<string>());

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var uninstall = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;

                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(name);
                    if (key is null) continue;

                    var publisher = key.GetValue("Publisher") as string ?? string.Empty;
                    if (!publisher.Contains("Electronic Arts", StringComparison.OrdinalIgnoreCase)) continue;

                    var install = key.GetValue("InstallLocation") as string;
                    if (string.IsNullOrWhiteSpace(install)) continue;

                    var manifest = Path.Combine(install, "__Installer", "installerdata.xml");
                    if (!File.Exists(manifest)) continue;

                    var folder = NormaliseFolder(install);
                    if (folder is null || !seenFolders.Add(folder)) continue;

                    var offers = EaOffersFrom(File.ReadAllText(manifest));
                    if (offers.Count == 0 || offers.Any(seenOffers.Contains)) continue;

                    var title = key.GetValue("DisplayName") as string
                                ?? new DirectoryInfo(install.TrimEnd('\\')).Name;
                    var launch = $"origin2://game/launch?offerIds={string.Join(",", offers)}";

                    foreach (var offer in offers) seenOffers.Add(offer);
                    games.Add(new ScannedGame("ea", offers[0], title, launch, install.TrimEnd('\\'), null));

                    Log.Info($"    EA app: \"{title}\" will be started as {launch}");
                }
            }
            catch (Exception error)
            {
                Log.Info($"the EA app's uninstall entries could not be read: {error.Message}");
            }
        }

        return games;
    }

    // The content identifiers out of an EA installerdata.xml, in the order written. Looked for by
    // element name wherever they are, since the manifest's layout differs between its versions.
    internal static IReadOnlyList<string> EaOffersFrom(string manifestXml)
    {
        try
        {
            return System.Xml.Linq.XDocument.Parse(manifestXml)
                .Descendants()
                .Where(element => element.Name.LocalName.Equals("contentID", StringComparison.OrdinalIgnoreCase))
                .Select(element => element.Value.Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    // ------------------------------------------------------------------ Battle.net

    // Blizzard's games are found through their uninstall entries, which name the game by its
    // install uid (--uid=). The launcher starts a game by that same uid with --exec="launch_uid",
    // so nothing else needs looking up. A battlenet:// address only opens the launcher on the
    // game's page, and --exec="launch" wants a launch code (SC2) that is neither the uid (s2)
    // nor the product code product.db keeps (s2).
    internal static IReadOnlyList<ScannedGame> BattleNet()
    {
        var games = new List<ScannedGame>();

        try
        {
            using var uninstall = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");

            if (uninstall is null) return games;

            var launcher = BattleNetExecutable(uninstall);
            if (launcher is null)
                Log.Warn("Battle.net.exe was not found; its games will only open the launcher on their page");

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

                var launch = BattleNetLaunch(launcher, uid);

                games.Add(new ScannedGame("battlenet", uid, title, launch,
                    Directory.Exists(install ?? string.Empty) ? install : null, null));

                Log.Info($"    Battle.net: \"{title}\" will be started as {launch}");
            }
        }
        catch (Exception error)
        {
            Log.Warn($"the Battle.net scan failed and its games will be missing: {error.Message}");
        }

        return games;
    }

    // The command that starts the game rather than the launcher's page for it; the page's address
    // when the launcher is not where it should be, which is at least the right game in the window.
    internal static string BattleNetLaunch(string? launcher, string uid) =>
        launcher is not null
            ? $"\"{launcher}\" --exec=\"launch_uid {uid}\""
            : $"battlenet://{uid}";

    private static string? NormaliseFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;

        // Forward slashes and back slashes alike; upper case, as Windows compares paths.
        return folder.Replace('/', '\\').TrimEnd('\\').ToUpperInvariant();
    }

    // Battle.net.exe, from its own uninstall entry, or where it installs by default.
    private static string? BattleNetExecutable(RegistryKey uninstall)
    {
        try
        {
            using var own = uninstall.OpenSubKey("Battle.net");
            if (own?.GetValue("InstallLocation") is string folder)
            {
                var exe = Path.Combine(folder, "Battle.net.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        catch (Exception)
        {
            // Not found here is not a failed scan; the default folder is asked next.
        }

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Battle.net", "Battle.net.exe");

        return File.Exists(fallback) ? fallback : null;
    }

    // The languages a uid may end in: wow_enus is wow installed in English. Only these come
    // off — hs_beta is Hearthstone's whole uid, and cutting at the first underscore lost that.
    private static readonly HashSet<string> Locales = new(StringComparer.OrdinalIgnoreCase)
    {
        "enus", "engb", "dede", "eses", "esmx", "frfr", "itit", "kokr", "plpl", "ptbr", "ptpt",
        "ruru", "zhcn", "zhtw", "jajp", "thth",
    };

    // The install uid out of an uninstall command, without its language suffix.
    internal static string? UidFrom(string uninstallCommand)
    {
        const string marker = "--uid=";

        var start = uninstallCommand.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;

        start += marker.Length;

        var end = start;
        while (end < uninstallCommand.Length && !char.IsWhiteSpace(uninstallCommand[end])) end++;

        var uid = uninstallCommand[start..end].Trim('"');
        if (uid.Length == 0) return null;

        return WithoutLocale(uid);
    }

    private static string WithoutLocale(string uid)
    {
        var underscore = uid.LastIndexOf('_');
        return underscore > 0 && Locales.Contains(uid[(underscore + 1)..]) ? uid[..underscore] : uid;
    }
}
