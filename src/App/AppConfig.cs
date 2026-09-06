//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.RegularExpressions;

namespace RemoteGameHub.App;

internal enum VideoCodec
{
    // Nothing this server encodes: what a client's request comes to when it names a codec that
    // was never offered. Not a setting — the client chooses between the two below.
    Auto,
    H264,
    Hevc,
    Av1,
}

// How much work the encoder puts into a picture, chosen per game because a shooter and a strategy
// want opposite things. Mapped to each card's own settings where the encoder is opened.
internal enum StreamQuality
{
    Low,
    Medium,
    High,
    // The best either card does while still obeying the bitrate the client asked for. Not the
    // encoders' own lossless mode: that ignores the bitrate, and no network carries it.
    Lossless,
}

internal enum VideoEncoder
{
    // Picked from the adapter that owns the captured output.
    Auto,
    // NVIDIA NVENC, through nvEncodeAPI64.dll from the display driver.
    NvEnc,
    // AMD AMF, through amfrt64.dll from the display driver.
    Amf,
}

// Everything the file can say, with every default in an initialiser here. Read once at startup:
// the capture, encoder session and listeners are built from these values, and there is no watcher.
internal sealed class AppConfig
{
    // [General]
    // Off; DebugWasAbsent makes the first run verbose regardless, so it is logged in full too.
    internal bool Debug { get; set; } = false;
    internal string HostName { get; set; } = "auto";

    // The cursor this server draws into a stream's own picture, for the desktop always and for a
    // game only when that game's switch asks for one. Off turns both off, whatever a game asks.
    internal bool VirtualMouse { get; set; } = true;

    // Prefer a third-party virtual display driver, when Output is "auto" and one is already on
    // the machine — this server never installs or fetches one; see the installer's checkbox.
    internal bool VirtualDisplay { get; set; } = false;

    // [Network]
    internal int PortBase { get; set; } = AppParameters.Ports.DefaultBase;
    internal string BindAddress { get; set; } = "any";

    // The page that shows what this server is doing and takes a pairing code. Port 80, so that
    // the machine's name or address on its own reaches it.
    internal int WebPort { get; set; } = 80;

    // Ask the router to forward the streaming ports from the internet.
    internal bool Upnp { get; set; }

    // [Display]. The codec, the frame rate and the bitrate have no setting of their own: they are
    // the client's to choose, and every ceiling here was caught halving one silently.
    internal string Output { get; set; } = "auto";
    internal VideoEncoder Encoder { get; set; } = VideoEncoder.Auto;

    // Only the capture self-test reads this — not [Display]: a stream decides its own pointer for
    // itself, always for the desktop and for a game only when that game's own switch asks for one.
    internal bool CaptureCursor { get; set; } = true;

    internal bool Adapt { get; set; } = true;

    // Scale the desktop up for a client whose screen has more pixels than this one. Streams of
    // the desktop only; a game draws itself and is not affected.
    internal bool ScaleDesktop { get; set; } = true;

    // [Games]
    internal bool Steam { get; set; } = true;
    internal bool Xbox { get; set; } = true;
    internal bool Epic { get; set; } = false;
    internal bool Gog { get; set; } = false;
    internal bool Ea { get; set; } = false;
    internal bool BattleNet { get; set; } = true;

    internal IReadOnlyList<string> GamesFolders { get; set; } = Array.Empty<string>();
    internal int GamesDepth { get; set; } = 2;
    internal bool GamesArtwork { get; set; } = true;

    // Where this instance was read from, or where it will be written.
    internal string Path { get; private set; } = string.Empty;

    // true when no file existed and the defaults are in use.
    internal bool IsFirstRun { get; private set; }

    // Set when Debug was absent: the first run is logged in full.
    internal bool DebugWasAbsent { get; private set; }

    // Settings this server knows that the file just read did not — added since, usually by an
    // upgrade; empty on a first run, since nothing is "added" against a file that never existed.
    internal IReadOnlyList<(string Section, string Key)> AddedSettings { get; private set; } =
        Array.Empty<(string, string)>();

    // Derived ports. The client is handed the base and derives the rest, so these must stay in step
    // with what /serverinfo announces.
    internal int HttpPort => PortBase + AppParameters.Ports.HttpOffset;
    internal int HttpsPort => PortBase + AppParameters.Ports.HttpsOffset;
    internal int RtspPort => PortBase + AppParameters.Ports.RtspOffset;
    internal int VideoPort => PortBase + AppParameters.Ports.VideoOffset;
    internal int ControlPort => PortBase + AppParameters.Ports.ControlOffset;
    internal int AudioPort => PortBase + AppParameters.Ports.AudioOffset;

    // ------------------------------------------------------------------ locations

    // config next to the executable, so a folder can be carried on a stick.
    internal static string PortablePath =>
        System.IO.Path.Combine(AppContext.BaseDirectory, AppParameters.Identity.ConfFile);

    internal static string UserPath =>
        System.IO.Path.Combine(FallbackDirectory, AppParameters.Identity.ConfFile);

    // Where a profile-kept configuration goes when the process is not its owner: set by the worker
    // running as SYSTEM, whose LocalApplicationData is under System32 where nobody would look.
    internal static string? ProfileDirectoryOverride { get; set; }

    internal static string FallbackDirectory => System.IO.Path.Combine(
        ProfileDirectoryOverride ??
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppParameters.Identity.DataFolder);

    // The portable file wins, unless the executable sits in a system folder — an installed copy
    // always keeps its settings in the profile of whoever runs it.
    internal static IReadOnlyList<string> Candidates() =>
        IsSystemFolder(AppContext.BaseDirectory)
            ? new[] { UserPath }
            : new[] { PortablePath, UserPath };

    // The folder the log shares with the configuration.
    internal static string ResolveDirectory()
    {
        var candidates = Candidates();
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return System.IO.Path.GetDirectoryName(candidate)!;
        }
        return System.IO.Path.GetDirectoryName(candidates[0])!;
    }

    // Whole path segments, on any drive: "D:\Program Files Backup\App" is not a system folder.
    private static readonly Regex SystemSegment = new(
        @"(^|[\\/])(Program Files|Program Files \(x86\)|ProgramData|Windows)([\\/]|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A path test, not a write test: running as administrator, a write into Program Files
    // succeeds and leaves the settings where an unelevated program could never read them.
    internal static bool IsSystemFolder(string path) => SystemSegment.IsMatch(path);

    // ------------------------------------------------------------------ reading and writing

    // Reads the first candidate that exists. Parse errors are thrown, not swallowed: the caller
    // refuses to start rather than run on defaults while an edited setting is ignored.
    internal static AppConfig Load(Action<string> warn)
    {
        var candidates = Candidates();

        // Left over from an older portable copy next to an installed executable: it is not read,
        // and staying silent would mean edits in it have no effect at all.
        if (IsSystemFolder(AppContext.BaseDirectory) && File.Exists(PortablePath))
        {
            warn($"A configuration file was found next to the executable but is not used, because the\n" +
                 $"executable is installed in a system folder:\n" +
                 $"    {PortablePath}\n" +
                 $"Edits made in it have no effect. The file in use is:\n" +
                 $"    {UserPath}");
        }

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate)) continue;

            var file = ConfFile.Parse(File.ReadAllText(candidate));
            var config = ConfFormat.Read(file, warn);
            config.Path = candidate;
            config.DebugWasAbsent = !file.Has("General", "Debug");
            // Read() above already asked for every setting there is; whatever it did not find in
            // the file is everything to report and add back with its default.
            config.AddedSettings = file.MissingKeys;
            return config;
        }

        return new AppConfig { Path = candidates[0], IsFirstRun = true, DebugWasAbsent = true };
    }

    // Takes [Games] and [Artwork] from a freshly read file, so "Refresh games" notices a folder
    // added since startup. Only these: the rest built the listeners and the encoder session.
    internal void AdoptScanSettings(AppConfig fresh)
    {
        Steam = fresh.Steam;
        Xbox = fresh.Xbox;
        Epic = fresh.Epic;
        Gog = fresh.Gog;
        Ea = fresh.Ea;
        BattleNet = fresh.BattleNet;

        GamesFolders = fresh.GamesFolders;
        GamesDepth = fresh.GamesDepth;

        GamesArtwork = fresh.GamesArtwork;
    }

    // Writes to Path. Throws; the caller decides how loud that is.
    internal void Save()
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // Rewritten whole, comments and all. Comments of the user's own are lost on any rewrite;
        // that is accepted, because the comments the application writes are the documentation.
        File.WriteAllText(Path, ConfFormat.Write(this), new UTF8Encoding(true));
    }

    // Tries the path it was read from, then every candidate in order, and returns the first that
    // worked, or null. A failed write is not fatal: the settings then apply until restart.
    internal string? SaveSomewhere()
    {
        var tried = new List<string> { Path };
        tried.AddRange(Candidates().Where(c => !string.Equals(c, Path, StringComparison.OrdinalIgnoreCase)));

        foreach (var candidate in tried)
        {
            try
            {
                Path = candidate;
                Save();
                return candidate;
            }
            catch (Exception)
            {
                // Try the next one; the caller names them all if none worked.
            }
        }

        return null;
    }
}
