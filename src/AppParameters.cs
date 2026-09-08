//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

namespace RemoteGameHub;

// Constants that are not settings. Anything a user may reasonably want to change belongs in the
// configuration file; anything that would break the protocol or the clients belongs here.
internal static class AppParameters
{
    internal static class Identity
    {
        // Shown to the user, and to Moonlight as the host name when none is configured.
        internal const string DisplayName = "Remote Game Hub";

        // Base name of the executable, the log and the configuration file.
        internal const string FileBase = "Remote-Gamehub";

        // One file for the launcher, the worker and the service alike; the service's lines are
        // marked. Appends are atomic (see Log.Append), so the three do not lose each other's lines.
        internal const string LogFile = FileBase + ".log";
        internal const string ConfFile = FileBase + ".conf";

        // Folder created under %LOCALAPPDATA% for an installed copy, and the one the installer
        // makes. The same spelling as the executable and the files beside it, not a third one.
        internal const string DataFolder = FileBase;

        // Single-instance mutex. Global, because the ports are machine-wide.
        internal const string Mutex = @"Global\RemoteGameHub.SingleInstance";

        // The scheduled task that starts the server at sign-in. Named after the product so that it
        // is recognisable in Task Scheduler.
        internal const string StartupTask = DisplayName;

        // The Windows service that keeps the server running as SYSTEM. No spaces: this is the name
        // every sc.exe command and the registry key use; DisplayName is what the services list shows.
        internal const string ServiceName = "RemoteGameHub";
    }

    // Where the project lives and where a newer copy of it is looked for. The one thing to correct
    // here the day it is published somewhere else.
    internal static class Links
    {
        internal const string Project = "https://github.com/andrey-lysikov/remote-gamehub";

        // The newest release, as JSON: only the tag is read from it.
        internal const string LatestReleaseApi =
            "https://api.github.com/repos/andrey-lysikov/remote-gamehub/releases/latest";

        // The same release as a page — what the notification and the page open.
        internal const string LatestRelease = Project + "/releases/latest";

        // A third-party project, not ours: this server never fetches or installs it, only points
        // the installer's own checkbox at it and looks for it once it is on the machine.
        internal const string VirtualDisplayDriver =
            "https://github.com/VirtualDrivers/Virtual-Display-Driver/releases";
    }

    // Where the covers come from. Two catalogues, because neither knows everything: Steam's own
    // search by title, and GameDB — IGDB's catalogue as static files, needing no key of any kind.
    internal static class Artwork
    {
        internal const string SteamSearch = "https://store.steampowered.com/api/storesearch/";
        internal const string SteamPictures = "https://cdn.cloudflare.steamstatic.com/steam/apps/";

        // Names beginning with the same two letters, in one file each; then the game by number.
        internal const string GameDbBuckets = "https://app.lizardbyte.dev/GameDB/buckets/";
        internal const string GameDbGames = "https://app.lizardbyte.dev/GameDB/games/";

        // A record names its cover at thumbnail size; the size is a segment of the address, and
        // this one is 528x748 — the nearest above to the 600x900 a client draws.
        internal const string GameDbThumbSize = "t_thumb";
        internal const string GameDbCoverSize = "t_cover_big_2x";
    }

    // Looking for a newer version.
    internal static class Updates
    {
        // The first check waits: at startup the network may not be up, and the server has a
        // client to answer before it has anything to ask GitHub.
        internal static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(5);

        // Then once a day.
        internal static readonly TimeSpan CheckPeriod = TimeSpan.FromDays(1);

        // How long one request may take. The answer is a nicety, not a reading.
        internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    }


    internal static class Logging
    {
        // Past this the log is rotated to <name>.log.1, pushing older numbers up.
        internal const long MaxBytes = 1024 * 1024;

        // Kept as .log.1 through .log.<this>; whatever would become the next number is deleted
        // instead.
        internal const int MaxRotations = 7;

        // The size is re-checked every this many lines, not on every write.
        internal const int CheckEveryLines = 100;

        // Continuation lines are indented to the width of "timestamp + tag ".
        internal const int ContinuationIndent = 30;
    }

    // The GameStream port layout Moonlight expects. The client is given one base port and derives
    // every other from it with these offsets, so the offsets are protocol, not preference.
    internal static class Ports
    {
        internal const int DefaultBase = 47989;

        internal const int HttpsOffset = -5;   // 47984, pairing and the paired queries
        internal const int HttpOffset = 0;     // 47989, unpaired discovery
        internal const int WebOffset = 1;      // 47990, reserved: this server has no web UI
        internal const int VideoOffset = 9;    // 47998/udp, RTP video
        internal const int ControlOffset = 10; // 47999/udp, ENet control channel
        internal const int AudioOffset = 11;   // 48000/udp, RTP audio
        internal const int RtspOffset = 21;    // 48010/tcp, session negotiation
    }

    // Values the GameStream protocol expects to see, not values this server would choose: Moonlight
    // decides what a host can do from the versions it reports, and withholds features otherwise.
    internal static class Protocol
    {
        // The GameStream version. Clients gate features on it.
        internal const string AppVersion = "7.1.431.-1";

        internal const string GfeVersion = "3.23.0.74";

        // Clients read this to tell an idle host from one already streaming. What matters is the
        // _SERVER_BUSY ending, which this is the absence of.
        internal const string StateFree = "SUNSHINE_SERVER_FREE";

        internal const string StateBusy = "SUNSHINE_SERVER_BUSY";

        // The identifier of the one thing this server offers: the desktop itself.
        internal const int DesktopAppId = 1;

        internal const string DesktopAppTitle = "Desktop";

        // A game's identifier over the protocol is its database row id plus this. Desktop is 1 and
        // the database numbers its rows from 1, so the two ranges must never be able to meet.
        internal const int GameAppIdOffset = 1000;

        // The codec bits of ServerCodecModeSupport, from Limelight.h's SCM_ values.
        internal const int CodecH264 = 0x0001;
        internal const int CodecHevc = 0x0100;
        internal const int CodecHevcMain10 = 0x0200;
        internal const int CodecAv1Main8 = 0x0001_0000;
        internal const int CodecAv1Main10 = 0x0002_0000;
        internal const int CodecH264High8_444 = 0x0004_0000;
        internal const int CodecHevcRext8_444 = 0x0008_0000;

        // x-ss-video[0].chromaSamplingType: the client's choice between quartered colour and all
        // of it. Only ever 1 when the bits above said this server can send it.
        internal const int ChromaSampling444 = 1;

        // What the client puts in x-nv-vqos[0].bitStreamFormat to name the codec it chose.
        internal const int BitStreamH264 = 0;
        internal const int BitStreamHevc = 1;
        internal const int BitStreamAv1 = 2;

        // What MaxLumaPixelsHEVC reports when HEVC is available: the number GeForce Experience
        // reported and Sunshine therefore reports (nvhttp.cpp). Clients compare against it.
        internal const long MaxLumaPixelsHevc = 1869449984;
    }

    // Bounds for configured values. Out-of-range numbers are clamped and logged, never thrown:
    // a typo in a bitrate must not keep the server from starting.
    internal static class Limits
    {
        internal const int MinPortBase = 1024;
        internal const int MaxPortBase = 65000;

        // The page's port may be one of the reserved ones — 80 is the point of it — which is why
        // this range starts lower than the block above.
        internal const int MinWebPort = 1;
        internal const int MaxWebPort = 65535;

        // What a client may ask for and be given. Neither is a setting: the client knows what its
        // screen and its network do, and these only keep a nonsensical request out of the encoder.
        internal const int MinBitrateKbps = 500;
        internal const int MaxBitrateKbps = 500_000;
        internal const int MinFps = 10;
        internal const int MaxFps = 240;

        // How deep the optional games folder may be walked. Past eight levels a mistyped path
        // pointed at a drive root would turn the scan into a disk crawl.
        internal const int MinGamesFolderDepth = 1;
        internal const int MaxGamesFolderDepth = 8;
    }

    // Never a setting: a client that asks for sound gets it. Left here rather than dropped, so a
    // machine that genuinely has none is one line to build with rather than a feature to write.
    internal static class Audio
    {
        internal const bool Enabled = true;
        internal const string Device = "auto";
    }

    // Never a setting either: a controller works or does not depending on whether ViGEmBus is
    // installed, which is the one input switch this server actually exposes — see GamepadHub.
    internal static class Input
    {
        internal const bool Keyboard = true;
        internal const bool Mouse = true;
    }

    internal static class Capture
    {
        // How long a single AcquireNextFrame may block. The desktop produces nothing while
        // it is idle, and a capture loop that waits forever cannot notice a stop request.
        internal const int AcquireTimeoutMs = 100;

        // Desktop Duplication is lost on a mode change, a resolution change, a full-screen
        // transition and a session switch. It is not an error; the duplication is re-created.
        internal const int RecreateDelayMs = 200;

        // How long the desktop may be unavailable (a UAC prompt, the lock screen) before the stream
        // ends. Generous: a client can answer the prompt, and walking to the machine takes a while.
        internal const int UnavailablePatienceMs = 10 * 60 * 1000;
    }

    // Taking the console back from a remote desktop session, so that there is a screen at all.
    // Windows moves the session in a moment; the graphics behind it take appreciably longer.
    internal static class Handover
    {
        // How long the session is given to stop being a remote one after WTSConnectSession returns.
        internal const int SessionMs = 10 * 1000;

        // How long a screen is then given to appear; a monitor waking to answer the card is slow.
        internal const int ScreenMs = 20 * 1000;

        // Between one look and the next while waiting for either of those.
        internal const int PollMs = 250;
    }
}
