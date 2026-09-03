//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;
using RemoteGameHub.App;

namespace RemoteGameHub.Library;

// Xbox and Game Pass games from the markers Windows leaves: .GamingRoot at a drive root naming the
// games folder, MicrosoftGame.config in each game's. Not the package manager, which needs WinRT.
internal static class XboxScanner
{
    internal static IReadOnlyList<ScannedGame> Scan()
    {
        var games = new List<ScannedGame>();

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;

                var root = ReadGamingRoot(drive);
                if (root is null)
                {
                    Log.Info($"    drive {drive.Name} has no Xbox games folder");
                    continue;
                }

                var folders = Directory.EnumerateDirectories(root).ToList();
                Log.Info($"    Xbox games folder {root}: {folders.Count} folder(s)");

                foreach (var folder in folders)
                {
                    var game = ReadGame(folder);
                    if (game is not null) games.Add(game);
                }
            }
        }
        catch (Exception error)
        {
            // One broken source must not cost the other two, so nothing escapes this method.
            Log.Warn($"the Xbox scan failed and its games will be missing: {error.Message}");
        }

        return games;
    }

    // .GamingRoot is binary: the signature "RGBX", a four-byte number that is 1 on every file
    // seen, then the folder's path relative to the drive in UTF-16LE. Everything read defensively.
    private static string? ReadGamingRoot(DriveInfo drive)
    {
        var marker = Path.Combine(drive.RootDirectory.FullName, ".GamingRoot");

        try
        {
            if (!File.Exists(marker)) return null;

            const int PathOffset = 8;   // the signature, then the four-byte number described above

            var bytes = File.ReadAllBytes(marker);
            if (bytes.Length is < PathOffset + 2 or > 4096) return null;
            if (bytes[0] != 'R' || bytes[1] != 'G' || bytes[2] != 'B' || bytes[3] != 'X') return null;

            var text = Encoding.Unicode.GetString(bytes, PathOffset, bytes.Length - PathOffset);
            var nul = text.IndexOf('\0');
            if (nul >= 0) text = text[..nul];

            var relative = text.Trim().TrimStart('\\', '/');
            if (relative.Length == 0) return null;

            var folder = Path.Combine(drive.RootDirectory.FullName, relative);
            return Directory.Exists(folder) ? folder : null;
        }
        catch (Exception error)
        {
            Log.Info($"{marker} could not be read and drive {drive.Name} is skipped: {error.Message}");
            return null;
        }
    }

    private static ScannedGame? ReadGame(string folder)
    {
        // The config sits in the game's folder on some installs and one level down in "Content" on
        // others — Game Pass has used both layouts. A folder with neither is simply not a game.
        var config = new[]
        {
            Path.Combine(folder, "MicrosoftGame.config"),
            Path.Combine(folder, "Content", "MicrosoftGame.config"),
        }.FirstOrDefault(File.Exists);

        if (config is null)
        {
            // Said by name: a game being downloaded, or one laid out in a way this scanner has not
            // met, looks exactly like this, and the folder's name is what tells the two apart.
            Log.Info($"    {folder} has no MicrosoftGame.config and is not listed");
            return null;
        }

        try
        {
            var xml = XDocument.Load(config);
            var gameRoot = Path.GetDirectoryName(config)!;

            var identity = xml.Root?.Element("Identity");
            var identityName = identity?.Attribute("Name")?.Value;
            if (string.IsNullOrWhiteSpace(identityName))
            {
                Log.Info($"{config} has no Identity name and is skipped");
                return null;
            }

            var shellVisuals = xml.Root?.Element("ShellVisuals");
            var displayName = shellVisuals?.Attribute("DefaultDisplayName")?.Value;

            // The display name can be an "ms-resource:" reference into the package's resource
            // index, unresolvable without loading it; the identity name is always a real string.
            var title = string.IsNullOrWhiteSpace(displayName)
                        || displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)
                ? identityName
                : displayName;

            var executable = xml.Root?.Element("ExecutableList")?.Elements("Executable").FirstOrDefault();
            var executableName = executable?.Attribute("Name")?.Value;
            var applicationId = executable?.Attribute("Id")?.Value;

            var familyName = ResolveFamilyName(identityName);

            string launch;
            string? externalId;

            if (familyName is not null && !string.IsNullOrWhiteSpace(applicationId))
            {
                // shell: activates the packaged application, the only start that gets it its
                // container and licence check. Handed to explorer.exe — the scheme has no handler.
                launch = $@"shell:AppsFolder\{familyName}!{applicationId}";
                externalId = familyName;
            }
            else if (!string.IsNullOrWhiteSpace(executableName)
                     && File.Exists(Path.Combine(gameRoot, executableName)))
            {
                launch = Path.Combine(gameRoot, executableName);
                externalId = null;
                Log.Info($"\"{title}\": the package family name could not be resolved, so it is " +
                         "recorded with its executable as the launch command. It may refuse to " +
                         "start that way, but a game that starts the wrong way is still better " +
                         "than one that is not listed.");
            }
            else
            {
                Log.Info($"\"{title}\" in {folder} has neither a resolvable package nor a " +
                         "findable executable and is skipped");
                return null;
            }

            return new ScannedGame(
                Source: "xbox",
                ExternalId: externalId,
                Title: title,
                LaunchCommand: launch,
                InstallPath: folder,
                BoxArtPath: BoxArt(gameRoot, shellVisuals));
        }
        catch (Exception error)
        {
            Log.Info($"{config} could not be read and the game is skipped: {error.Message}");
            return null;
        }
    }

    // The family name is the identity name plus a publisher hash, absent from the config. It comes
    // from the AppModel key, whose names are Name_Version_Architecture_ResourceId_PublisherHash.
    private static string? ResolveFamilyName(string identityName)
    {
        try
        {
            using var packages = Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion" +
                @"\AppModel\Repository\Packages");

            if (packages is null) return null;

            foreach (var fullName in packages.GetSubKeyNames())
            {
                if (!fullName.StartsWith(identityName + "_", StringComparison.OrdinalIgnoreCase))
                    continue;

                var hash = fullName[(fullName.LastIndexOf('_') + 1)..];
                if (hash.Length == 0) continue;

                return $"{identityName}_{hash}";
            }
        }
        catch (Exception error)
        {
            Log.Warn($"the package repository could not be read: {error.Message}");
        }

        return null;
    }

    // The image ShellVisuals names, or the conventional StoreLogo.png. Landscape and square logos
    // are all these packages carry, so the client letterboxes whatever it gets.
    private static string? BoxArt(string gameRoot, XElement? shellVisuals)
    {
        var named = shellVisuals?.Attribute("StoreLogo")?.Value;

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(named)) candidates.Add(Path.Combine(gameRoot, named));
        candidates.Add(Path.Combine(gameRoot, "StoreLogo.png"));

        return candidates.FirstOrDefault(File.Exists);
    }
}
