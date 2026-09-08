//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using RemoteGameHub.App;
using RemoteGameHub.Native;

namespace RemoteGameHub.Media;

// One thing a screen can be set to.
internal sealed record DisplayMode(int Width, int Height, int RefreshHz, int BitsPerPixel)
{
    public override string ToString() => $"{Width}x{Height} at {RefreshHz} Hz";
}

// Puts the screen into the size, rate and dynamic range the client asked for — this server cannot
// scale a picture — and restores it at the end. Nothing is written to the registry.
internal sealed class DisplayAdaptation : IDisposable
{
    private readonly string _deviceName;
    private readonly DisplayMode? _previousMode;
    private bool? _previousHdr;
    private readonly int? _previousScale;
    private bool _restored;

    // The screen's rectangle as it is now, which is what the mouse must be mapped onto.
    internal Rect Bounds { get; private set; }

    // Whether this adaptation belongs to the given output — a caller handed one from earlier
    // checks before trusting it for a screen that could have been re-resolved since.
    internal bool IsFor(DisplayOutput output) =>
        string.Equals(_deviceName, output.DeviceName, StringComparison.Ordinal);

    private DisplayAdaptation(string deviceName, Rect bounds, DisplayMode? previousMode,
                              bool? previousHdr, int? previousScale)
    {
        _deviceName = deviceName;
        Bounds = bounds;
        _previousMode = previousMode;
        _previousHdr = previousHdr;
        _previousScale = previousScale;
    }

    // Adapts the screen as far as it can and returns what puts it back. Never throws: a screen
    // that would not change is a stream at the wrong size, which is worth a warning and no more.
    internal static DisplayAdaptation Apply(DisplayOutput output, int width, int height, int fps,
                                            bool wantHdr, bool canEncodeHdr, bool enabled,
                                            bool scaleForClient, bool isGame = false)
    {
        var name = output.DeviceName;
        DisplayMode? previousMode = null;
        bool? previousHdr = null;
        int? previousScale = null;

        try
        {
            if (enabled)
            {
                // HDR first: turning advanced color on or off was seen resetting this screen back
                // to its own mode, which would silently undo a resolution change made before it.
                previousHdr = ApplyHdr(name, wantHdr, canEncodeHdr);
                previousMode = ApplyMode(name, width, height, fps);

                if (scaleForClient)
                {
                    // By the size the client asked for, not the mode the screen landed on: up to 2K
                    // the desktop is at 100%, above it scaled, whatever the screen managed.
                    previousScale = ApplyScale(name, width, height);
                }
                else if (isGame)
                {
                    // Always 100%, whatever an earlier desktop stream left: a game reading a stale
                    // scale draws its UI and cursor wrong for as long as it runs.
                    previousScale = ForceScale100(name);
                }
            }
            else
            {
                Log.Info("[Display] Adapt is off; the screen is left exactly as it is");
            }
        }
        catch (Exception error)
        {
            Log.Warn($"the screen could not be adapted to the client: {error.Message}");
        }

        return new DisplayAdaptation(name, CurrentBounds(output), previousMode, previousHdr,
                                     previousScale);
    }

    // 1920x1080: the size desktop UI is drawn for, and what a client above 2K is measured
    // against to find how much extra scale keeps the desktop legible.
    private const int ReferenceWidth = 1920;
    private const int ReferenceHeight = 1080;

    // 2560x1440: the largest client that is streamed at 100%. Only a client strictly above this
    // has the desktop scaled up.
    private const int UnscaledWidth = 2560;
    private const int UnscaledHeight = 1440;

    // Sets the desktop scale for the client's size: 100% within 2K, above it the extra pixels over
    // the ordinary desktop stepped to 25%, always from 100%. Returns the setting replaced or null.
    private static int? ApplyScale(string deviceName, int clientWidth, int clientHeight)
    {
        if (clientWidth <= 0 || clientHeight <= 0) return null;

        var path = FindPath(deviceName);
        if (path is null) return null;

        var scale = DisplayControl.DpiScale(path.Value);
        if (scale is null)
        {
            Log.Info("Windows would not say what this screen is scaled to; it is left alone");
            return null;
        }

        var fitsUnscaled = clientWidth <= UnscaledWidth && clientHeight <= UnscaledHeight;
        var ratio = fitsUnscaled
            ? 1.0
            : Math.Min((double)clientWidth / ReferenceWidth, (double)clientHeight / ReferenceHeight);
        var wanted = fitsUnscaled ? 100 : Nearest(100 * ratio, scale.Value.Maximum);

        if (wanted == scale.Value.Current) return null;

        if (!DisplayControl.SetDpiScale(path.Value, wanted))
        {
            Log.Info($"the desktop could not be scaled to {wanted}% for this client");
            return null;
        }

        if (fitsUnscaled)
            Log.Event($"the client's {clientWidth}x{clientHeight} is within {UnscaledWidth}x" +
                     $"{UnscaledHeight}, so the desktop is put to 100% for this stream; it was " +
                     $"{scale.Value.Current}% and goes back to that afterwards");
        else
            Log.Event($"the client's {clientWidth}x{clientHeight} is " +
                     $"{ratio.ToString("0.00", CultureInfo.InvariantCulture)} times {ReferenceWidth}x" +
                     $"{ReferenceHeight}, so the desktop is scaled to {wanted}% for this stream; it " +
                     $"was {scale.Value.Current}% and goes back to that afterwards");

        return scale.Value.Current;
    }

    // A game gets none of the above: whatever the desktop stream before it left the scale at,
    // put back to 100% before this one reads it. Returns the setting replaced or null.
    private static int? ForceScale100(string deviceName)
    {
        var path = FindPath(deviceName);
        if (path is null) return null;

        var scale = DisplayControl.DpiScale(path.Value);
        if (scale is null) return null;

        if (scale.Value.Current == 100) return null;

        if (!DisplayControl.SetDpiScale(path.Value, 100))
        {
            Log.Info("the desktop could not be scaled back to 100% for this game");
            return null;
        }

        Log.Event($"the desktop was at {scale.Value.Current}% from an earlier stream; " +
                 "put back to 100% for this game, and will return to that afterwards");

        return scale.Value.Current;
    }

    // The percentage Windows offers nearest the one wanted, stepped in the usual 25%, never below
    // 100 and never above what the screen allows.
    private static int Nearest(double percent, int maximum)
    {
        var best = 100;

        foreach (var offered in DisplayControl.DpiPercentages)
        {
            if (offered > maximum) break;
            if (Math.Abs(offered - percent) < Math.Abs(best - percent)) best = offered;
        }

        return best;
    }

    // Changes the mode when a closer one exists, and answers with the one that was replaced —
    // or null when nothing was changed and there is nothing to put back.
    private static DisplayMode? ApplyMode(string deviceName, int width, int height, int fps)
    {
        var current = Current(deviceName);
        if (current is null)
        {
            Log.Warn($"the current mode of {deviceName} could not be read; it is left alone");
            return null;
        }

        var supported = Supported(deviceName);
        if (supported.Count == 0)
        {
            Log.Warn($"{deviceName} lists no modes; it is left alone");
            return null;
        }

        var wanted = Choose(supported, width, height, fps);
        if (wanted is null) return null;

        if (wanted.Width == current.Width && wanted.Height == current.Height &&
            wanted.RefreshHz == current.RefreshHz)
        {
            Log.Info($"the screen is already {current}, which is what the client asked for");
            return null;
        }

        if (!SetPatiently(deviceName, wanted))
        {
            Log.Warn($"the screen refused {wanted} on {ModeAttempts} tries; it stays at {current}");
            return null;
        }

        Log.Event($"the client asked for {width}x{height} at {fps} fps; " +
                 $"the screen was {current} and is now {wanted}, and will be put back afterwards");

        return current;
    }

    // A screen that has only just come back — the console handed over from remote desktop, a
    // monitor waking — refuses a mode it lists as supported until its stack settles.
    private const int ModeAttempts = 4;
    private const int ModeRetryDelayMs = 250;

    private static bool SetPatiently(string deviceName, DisplayMode wanted)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (Set(deviceName, wanted))
            {
                if (attempt > 1) Log.Info($"the screen took {wanted} on try {attempt}");
                return true;
            }

            if (attempt >= ModeAttempts) return false;

            Thread.Sleep(ModeRetryDelayMs);
        }
    }

    // HDR on when the client can show it and the card can encode it in ten bits, off otherwise —
    // an eight-bit stream of an HDR desktop is washed out. Whatever it was is restored at the end.
    private static bool? ApplyHdr(string deviceName, bool wantHdr, bool canEncodeHdr)
    {
        var path = FindPath(deviceName);
        if (path is null) return null;

        var info = DisplayControl.ColourInfo(path.Value);
        if (info is null) return null;

        var supported = (info.Value.Value & DisplayControl.AdvancedColorSupported) != 0;
        var forceDisabled = (info.Value.Value & DisplayControl.AdvancedColorForceDisabled) != 0;
        var enabled = (info.Value.Value & DisplayControl.AdvancedColorEnabled) != 0;

        if (!supported)
        {
            if (wantHdr) Log.Info("the client asked for HDR; this screen does not do it");
            return null;
        }

        // The same switch Settings itself withholds for this screen despite what its EDID claims.
        // The raw API below would still turn it on if asked, into a state games do not trust.
        if (forceDisabled)
        {
            if (wantHdr) Log.Info("the client asked for HDR; Windows will not offer it on this " +
                                  "screen, so it is left in standard range");
            return null;
        }

        if (wantHdr && !canEncodeHdr)
        {
            Log.Warn(
                "The client asked for HDR, but this card has no ten-bit HEVC encoder, so the\n" +
                "stream is standard range. Turn HDR off in the client to stop it asking.");
        }

        var target = wantHdr && canEncodeHdr;
        if (target == enabled) return null;

        if (!DisplayControl.SetColourState(path.Value, target))
        {
            Log.Warn($"HDR could not be turned {(target ? "on" : "off")} on {deviceName}");
            return null;
        }

        Log.Event(target
            ? "HDR was turned on for this stream"
            : "HDR was on and has been turned off for this stream, because the picture would be " +
              "sent washed out otherwise; the screen is put back as it was afterwards");

        return enabled;
    }

    // Turns HDR off after the capture has said it cannot do ten bits. Returns whether anything
    // changed — a change loses the duplication, and the caller has to open it again.
    internal bool DropHdr()
    {
        try
        {
            var wasEnabled = ApplyHdr(_deviceName, wantHdr: false, canEncodeHdr: true);
            if (wasEnabled is null) return false;

            // Set only if untouched until now: if this session turned HDR on, _previousHdr
            // already holds the off it must go back to.
            _previousHdr ??= wasEnabled;
            return true;
        }
        catch (Exception error)
        {
            Log.Warn($"HDR could not be turned off again: {error.Message}");
            return false;
        }
    }

    // ------------------------------------------------------------------ reading and choosing

    // What the screen is doing now.
    internal static DisplayMode? Current(string deviceName)
    {
        var mode = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };

        return DisplayControl.EnumDisplaySettingsEx(deviceName,
            DisplayControl.ENUM_CURRENT_SETTINGS, ref mode, 0)
            ? Describe(mode)
            : null;
    }

    // Every mode the driver will admit to, with the same size and rate in several colour depths
    // folded together.
    internal static IReadOnlyList<DisplayMode> Supported(string deviceName)
    {
        var modes = new List<DisplayMode>();
        var seen = new HashSet<(int, int, int)>();

        for (var index = 0; ; index++)
        {
            var mode = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
            if (!DisplayControl.EnumDisplaySettingsEx(deviceName, index, ref mode, 0)) break;

            var described = Describe(mode);
            if (described.BitsPerPixel < 32) continue;
            if (!seen.Add((described.Width, described.Height, described.RefreshHz))) continue;

            modes.Add(described);
        }

        return modes;
    }

    private static DisplayMode Describe(in DevMode mode) => new(
        (int)mode.PelsWidth, (int)mode.PelsHeight,
        (int)mode.DisplayFrequency, (int)mode.BitsPerPixel);

    // The closest mode to what was asked for: the nearest size, and within that the best refresh
    // rate for the frame rate — the exact rate, else a whole multiple of it, else the nearest.
    internal static DisplayMode? Choose(IReadOnlyList<DisplayMode> modes, int width, int height,
                                        int fps)
    {
        if (modes.Count == 0 || width <= 0 || height <= 0) return null;

        var closestGeometry = modes.Min(m => Distance(m, width, height));

        return modes
            .Where(m => Distance(m, width, height) == closestGeometry)
            .OrderBy(m => RefreshRank(m.RefreshHz, fps))
            .ThenBy(m => Math.Abs(m.RefreshHz - fps))
            .ThenByDescending(m => m.RefreshHz)
            .First();
    }

    // How well a screen rate carries a frame rate, lower being better: the rate itself, then a
    // whole multiple of it, then anything else. Windows truncates, so 59 Hz is 59.94 and is 60.
    private static int RefreshRank(int refreshHz, int fps)
    {
        if (fps <= 0 || refreshHz <= 0) return 2;
        if (refreshHz == fps || refreshHz + 1 == fps) return 0;

        // A rate under the frame rate caps the stream. Above it, only a multiple samples evenly:
        // 50 frames out of 59.94 drop one ten times a second, 50 out of 100 drop none.
        if (refreshHz < fps) return 2;
        return (refreshHz + 1) % fps <= 1 ? 1 : 2;
    }

    private static int Distance(DisplayMode mode, int width, int height) =>
        Math.Abs(mode.Width - width) + Math.Abs(mode.Height - height);

    private static bool Set(string deviceName, DisplayMode mode)
    {
        var devMode = new DevMode
        {
            Size = (ushort)Marshal.SizeOf<DevMode>(),
            Fields = DisplayControl.DM_PELSWIDTH | DisplayControl.DM_PELSHEIGHT |
                     DisplayControl.DM_BITSPERPEL | DisplayControl.DM_DISPLAYFREQUENCY,
            PelsWidth = (uint)mode.Width,
            PelsHeight = (uint)mode.Height,
            BitsPerPixel = (uint)mode.BitsPerPixel,
            DisplayFrequency = (uint)mode.RefreshHz,
        };

        // No CDS_UPDATEREGISTRY: the change lasts for as long as this process wants it and no
        // longer. A server that dies without restoring leaves a screen that fixes itself.
        var result = DisplayControl.ChangeDisplaySettingsEx(deviceName, ref devMode, 0, 0, 0);

        return result is DisplayControl.DISP_CHANGE_SUCCESSFUL or DisplayControl.DISP_CHANGE_RESTART;
    }

    // The display-configuration path whose source carries this \\.\DISPLAYn name. The
    // two APIs used here name a screen in completely different ways, and this is the bridge.
    private static DisplayControl.PathInfo? FindPath(string deviceName)
    {
        foreach (var path in DisplayControl.ActivePaths())
        {
            if ((path.Flags & DisplayControl.DISPLAYCONFIG_PATH_ACTIVE) == 0) continue;

            if (string.Equals(DisplayControl.SourceName(path), deviceName, StringComparison.Ordinal))
                return path;
        }

        return null;
    }

    // The screen's rectangle now, re-read rather than remembered: a mode change moves the
    // desktop's other screens around it, and the pointer is mapped onto these numbers.
    private static Rect CurrentBounds(DisplayOutput output)
    {
        try
        {
            var fresh = DisplayInventory.Enumerate()
                .SelectMany(adapter => adapter.Outputs)
                .FirstOrDefault(o => string.Equals(o.DeviceName, output.DeviceName,
                    StringComparison.Ordinal));

            return fresh?.Bounds ?? output.Bounds;
        }
        catch (Exception)
        {
            return output.Bounds;
        }
    }

    public void Dispose()
    {
        if (_restored) return;
        _restored = true;

        // Timed per step, not only as a whole: a slow restore is a mode change, an HDR toggle or
        // a DPI change taking its own time with the display driver, and the log should say which.
        var whole = Stopwatch.StartNew();

        try
        {
            if (_previousHdr is { } hdr)
            {
                var step = Stopwatch.StartNew();
                var path = FindPath(_deviceName);
                if (path is not null) DisplayControl.SetColourState(path.Value, hdr);
                Log.Info($"HDR restored in {step.ElapsedMilliseconds} ms");
            }

            if (_previousScale is { } scale)
            {
                var step = Stopwatch.StartNew();
                var path = FindPath(_deviceName);
                if (path is not null && DisplayControl.SetDpiScale(path.Value, scale))
                    Log.Info($"the desktop is scaled back to {scale}% in {step.ElapsedMilliseconds} ms");
                else
                    Log.Warn($"the desktop could not be scaled back to {scale}% " +
                             $"({step.ElapsedMilliseconds} ms)");
            }

            if (_previousMode is not null)
            {
                var step = Stopwatch.StartNew();
                if (Set(_deviceName, _previousMode))
                    Log.Event($"the screen is back to {_previousMode} in {step.ElapsedMilliseconds} ms");
                else
                    Log.Warn($"the screen could not be put back to {_previousMode} " +
                             $"({step.ElapsedMilliseconds} ms)");
            }
        }
        catch (Exception error)
        {
            Log.Warn($"the screen could not be restored: {error.Message} " +
                     $"({whole.ElapsedMilliseconds} ms)");
        }

        if (whole.ElapsedMilliseconds > 500)
            Log.Warn($"restoring the screen took {whole.ElapsedMilliseconds} ms in total");
    }
}
