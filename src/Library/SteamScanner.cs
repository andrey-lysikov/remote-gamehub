//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using Microsoft.Win32;
using RemoteGameHub.App;

namespace RemoteGameHub.Library;

// Finds installed Steam games from Steam's own records: the install path from the registry, the
// library list from libraryfolders.vdf, one appmanifest_*.acf per game. Steam need not be running.
internal static class SteamScanner
{
    // Steamworks Common Redistributables, present on virtually every install. Filtered by app id
    // rather than by name, because the name is localised and the id is not.
    private const string RedistributablesAppId = "228980";

    internal static IReadOnlyList<ScannedGame> Scan()
    {
        var games = new List<ScannedGame>();

        try
        {
            var steam = FindSteam();
            if (steam is null)
            {
                Log.Info("Steam is not installed; nothing to scan");
                return games;
            }

            foreach (var library in Libraries(steam))
            {
                var steamApps = Path.Combine(library, "steamapps");
                if (!Directory.Exists(steamApps))
                {
                    // Named, because a library on a drive that is not there is the usual reason
                    // a game somebody knows they installed is missing from the list.
                    Log.Info($"    Steam library {library} has no steamapps folder and is skipped");
                    continue;
                }

                var manifests = Directory.EnumerateFiles(steamApps, "appmanifest_*.acf").ToList();
                Log.Info($"    Steam library {library}: {manifests.Count} manifest(s)");

                foreach (var manifest in manifests)
                {
                    var game = ReadManifest(manifest, steamApps, steam);
                    if (game is not null) games.Add(game);
                }
            }
        }
        catch (Exception error)
        {
            // One broken source must not cost the other two, so nothing escapes this method.
            Log.Warn($"the Steam scan failed and its games will be missing: {error.Message}");
        }

        return games;
    }

    // The per-user value first: it is the one Steam itself maintains, and on a machine with more
    // than one account it points at the copy this user actually runs.
    private static string? FindSteam()
    {
        var path = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam")
                       ?.GetValue("SteamPath") as string
                   ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")
                       ?.GetValue("InstallPath") as string;

        if (string.IsNullOrWhiteSpace(path)) return null;

        // SteamPath is written with forward slashes; everything downstream joins with Path.Combine
        // and compares against directory listings, so it is normalised once, here.
        path = path.Replace('/', '\\');

        return Directory.Exists(path) ? path : null;
    }

    // Every folder Steam installs into. libraryfolders.vdf lists them all on a current client; the
    // main folder is added regardless, because a fresh install has no vdf yet.
    private static IEnumerable<string> Libraries(string steam)
    {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steam };

        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            try
            {
                var root = ValveKeyValues.Parse(File.ReadAllText(vdf));

                // Current clients write each library as a numbered block with a "path" value;
                // clients from before 2021 wrote the path as the numbered value itself.
                foreach (var block in root.Blocks)
                {
                    var path = block.Value("path");
                    if (!string.IsNullOrWhiteSpace(path)) libraries.Add(path);
                }

                foreach (var (key, value) in root.Values)
                {
                    if (key.All(char.IsAsciiDigit) && !string.IsNullOrWhiteSpace(value))
                        libraries.Add(value);
                }
            }
            catch (FormatException error)
            {
                Log.Info($"libraryfolders.vdf could not be parsed ({error.Message}); " +
                         "only the main Steam folder will be scanned");
            }
        }

        return libraries;
    }

    private static ScannedGame? ReadManifest(string manifest, string steamApps, string steam)
    {
        ValveKeyValues state;
        try
        {
            state = ValveKeyValues.Parse(File.ReadAllText(manifest));
        }
        catch (FormatException error)
        {
            Log.Info($"{Path.GetFileName(manifest)} could not be parsed and is skipped: {error.Message}");
            return null;
        }

        var appId = state.Value("appid");
        var name = state.Value("name");
        var installDir = state.Value("installdir");

        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(name))
        {
            Log.Info($"    {Path.GetFileName(manifest)} names no appid or no game and is skipped");
            return null;
        }

        if (appId == RedistributablesAppId) return null;

        var installPath = string.IsNullOrWhiteSpace(installDir)
            ? null
            : Path.Combine(steamApps, "common", installDir);
        if (installPath is not null && !Directory.Exists(installPath)) installPath = null;

        return new ScannedGame(
            Source: "steam",
            ExternalId: appId,
            Title: name,
            // A URL rather than the executable: through ShellExecute it makes Steam do the
            // launching, the only way the game gets its overlay, cloud saves and DRM ticket.
            LaunchCommand: $"steam://rungameid/{appId}",
            InstallPath: installPath,
            BoxArtPath: BoxArt(steam, appId));
    }

    // The portrait Moonlight draws. Current clients keep it in a folder per game, older ones in a
    // flat file named with the app id; the first that exists wins, and none is recorded as none.
    private static string? BoxArt(string steam, string appId)
    {
        var candidates = new[]
        {
            Path.Combine(steam, "appcache", "librarycache", appId, "library_600x900.jpg"),
            Path.Combine(steam, "appcache", "librarycache", $"{appId}_library_600x900.jpg"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
