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
            if (familyName is null &&
                FamilyNameFromPublisher(identityName, identity?.Attribute("Publisher")?.Value) is { } computed)
            {
                familyName = computed;
                Log.Info($"    \"{title}\": no package list names it; its family name is computed " +
                         $"from the publisher as {computed}");
            }

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

    private const string PackagesSubPath =
        @"Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    // As SYSTEM the scan impersonates the person, but the HKCU\Software\Classes symlink can still
    // resolve to SYSTEM's own Classes, which has no games: so the person's hive is opened by SID.
    private static RegistryKey? OpenPackages()
    {
        if (App.PlatformGuard.IsSystem && App.UserContext.ConsoleUserSid() is { } sid)
        {
            var byUser = Registry.Users.OpenSubKey($@"{sid}_Classes\{PackagesSubPath}");
            if (byUser is not null) return byUser;
        }

        return Registry.CurrentUser.OpenSubKey($@"Software\Classes\{PackagesSubPath}");
    }

    // Machine-wide lists of installed packages, by full name. Game Pass games are installed by
    // Gaming Services for every account, and are not always in the person's own repository above.
    private static readonly string[] MachinePackageLists =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Applications",
        @"SOFTWARE\Microsoft\GamingServices\PackageRepository\Package",
    };

    // The family name is the identity name plus a publisher hash, absent from the config. It comes
    // from a key named after the package, Name_Version_Architecture_ResourceId_PublisherHash: the
    // person's AppModel repository first, then the machine's own lists.
    private static string? ResolveFamilyName(string identityName)
    {
        using (var packages = OpenPackagesQuietly())
        {
            if (FamilyNameIn(packages, identityName) is { } own) return own;
        }

        foreach (var list in MachinePackageLists)
        {
            try
            {
                using var packages = Registry.LocalMachine.OpenSubKey(list);
                if (FamilyNameIn(packages, identityName) is { } machine) return machine;
            }
            catch (Exception error)
            {
                Log.Info($"HKLM\\{list} could not be read: {error.Message}");
            }
        }

        return null;
    }

    private static RegistryKey? OpenPackagesQuietly()
    {
        try
        {
            return OpenPackages();
        }
        catch (Exception error)
        {
            Log.Warn($"the package repository could not be read: {error.Message}");
            return null;
        }
    }

    private static string? FamilyNameIn(RegistryKey? packages, string identityName)
    {
        if (packages is null) return null;

        foreach (var fullName in packages.GetSubKeyNames())
        {
            if (FamilyNameOf(fullName, identityName) is { } family) return family;
        }

        return null;
    }

    // The family name as Windows derives it, when no list of packages names this one: the identity
    // name, an underscore, and the publisher's hash — the first 8 bytes of the SHA-256 of the
    // publisher string in UTF-16LE, as 13 characters of Crockford's base32. The same 8wekyb3d8bbwe
    // every Microsoft package ends in, computed rather than looked up.
    internal static string? FamilyNameFromPublisher(string identityName, string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher)) return null;

        const string alphabet = "0123456789abcdefghjkmnpqrstvwxyz";

        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.Unicode.GetBytes(publisher));
        var bits = BitConverter.ToUInt64([hash[7], hash[6], hash[5], hash[4], hash[3], hash[2], hash[1], hash[0]]);

        // 64 bits padded with one zero to 65, read five at a time from the top.
        var text = new StringBuilder(13);
        for (var i = 0; i < 13; i++)
        {
            var shift = 64 - 5 * (i + 1);
            var index = shift >= 0 ? (int)((bits >> shift) & 31) : (int)((bits << 1) & 31);
            text.Append(alphabet[index]);
        }

        return $"{identityName}_{text}";
    }

    // The family name out of a package's full name, when that package is the identity asked for.
    internal static string? FamilyNameOf(string fullName, string identityName)
    {
        if (!fullName.StartsWith(identityName + "_", StringComparison.OrdinalIgnoreCase)) return null;

        var hash = fullName[(fullName.LastIndexOf('_') + 1)..];
        return hash.Length == 0 ? null : $"{identityName}_{hash}";
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
