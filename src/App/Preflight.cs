//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using RemoteGameHub.Media;

namespace RemoteGameHub.App;

internal enum PreflightOutcome
{
    Continue,
    // The reason has already been written to the log. The process exits quietly.
    Stop,
}

internal sealed record PreflightResult(
    PreflightOutcome Outcome,
    string? Reason,
    AppConfig? Config,
    GraphicsAdapter? Adapter,
    DisplayOutput? Output);

// Every condition this server needs, checked in one place before the tray icon appears. Scattered
// checks produce a server that starts, accepts a connection and only then finds nothing to send.
internal static class Preflight
{
    internal static PreflightResult Run()
    {
        // Before the configuration is read, and therefore while everything is still being logged.
        Log.Info(PlatformGuard.DescribeWindows());

        // The one line that answers, from the log alone, why a stream froze at a prompt for
        // administrator rights: whether this copy is the service's worker or an ordinary one.
        Log.Event(PlatformGuard.DescribeIdentity());

        if (!PlatformGuard.IsWindows11OrNewer)
        {
            return Stop(
                "This server needs Windows 11 or newer.\n" +
                $"    Found: {PlatformGuard.DescribeWindows()}, and build " +
                $"{PlatformGuard.MinimumBuild} is the minimum.\n" +
                "    The capture and encoder paths it uses were not complete before that, and\n" +
                "    running it on an older build produces a stream that stops at the first change\n" +
                "    of resolution rather than an error at startup.");
        }

        if (!PlatformGuard.IsElevated)
        {
            return Stop(
                "This server needs administrator rights and does not have them.\n" +
                "    Without them the desktop cannot be captured while a full-screen game is in\n" +
                "    the foreground, the encoder session cannot be opened, and input from the\n" +
                "    client cannot reach an elevated window.\n" +
                "    What to do: start it again through \"Run as administrator\", or let the\n" +
                "    installed copy start itself at sign-in, which does this without asking.");
        }

        // A remote desktop session is not a reason to refuse: it takes only the picture, and the
        // server can still be found and paired with. It is watched from SessionWatch instead.

        AppConfig config;
        try
        {
            config = AppConfig.Load(Log.Warn);
        }
        catch (FormatException error)
        {
            // Not swallowed and not replaced by defaults: a user who edited a setting and is being
            // ignored is worse off than one who is told the file has a mistake on a named line.
            return Stop(
                "The configuration file has a mistake and was not read:\n" +
                $"    {error.Message}\n" +
                $"    File: {FirstCandidate()}\n" +
                "    What to do: correct that line, or delete the file — it is written again from\n" +
                "    the defaults, with every setting explained above it.");
        }
        catch (Exception error)
        {
            return Stop(
                "The configuration file could not be read:\n" +
                $"    {error.GetType().Name}: {error.Message}\n" +
                $"    File: {FirstCandidate()}");
        }

        // From here on, only what the user asked for; everything above is written whatever Debug
        // says. A file with no Debug key is the first run, which is logged in full.
        Log.SetVerbose(config.Debug || config.DebugWasAbsent);
        Log.Info($"configuration: {(config.IsFirstRun ? "none found, using the defaults" : config.Path)}");

        var inventory = DisplayInventory.Enumerate();
        Log.Info(DisplayInventory.Describe(inventory));

        if (config.VirtualDisplay && !DisplayInventory.HasVirtualDisplay(inventory))
        {
            Log.Info(
                "[General] VirtualDisplay is on, but no virtual display driver was found on this " +
                "machine;\n    this stream uses an ordinary screen, as if the setting were off.\n" +
                $"    What to do: install one from {AppParameters.Links.VirtualDisplayDriver}");
        }

        var output = DisplayInventory.Select(inventory, config.Output, config.VirtualDisplay,
            out var reason);
        if (output is null)
        {
            return Stop(
                $"There is no screen to capture: {reason}.\n" +
                "    This server streams what a graphics card is actually drawing, so it needs a\n" +
                "    monitor that is switched on, or an EDID emulator plugged into the card. A\n" +
                "    card with nothing attached produces no output to duplicate.\n" +
                $"    Setting: [Display] Output = {config.Output}\n" +
                "    The screens this machine has are listed above; the value in brackets after\n" +
                "    \"Output =\" is what to write there.");
        }

        var adapter = inventory.First(a => a.Index == output.AdapterIndex);
        Log.Event($"capturing {output.DeviceName} ({output.Bounds.Width}x{output.Bounds.Height}), " +
                 $"{reason}, on adapter {adapter.Index} \"{adapter.Name}\"");

        // What this screen can be set to: the server puts it into the mode nearest what a client
        // asks for, so the list it chose from belongs in the log.
        DescribeModes(output.DeviceName);

        if (!adapter.IsNvidia && !adapter.IsAmd)
        {
            return Stop(
                $"The screen to capture belongs to {adapter.Vendor}, which this server cannot\n" +
                "    encode with.\n" +
                $"    Adapter {adapter.Index}: \"{adapter.Name}\"\n" +
                "    Video is encoded on the graphics card, by NVENC on NVIDIA cards and by AMF on\n" +
                "    AMD cards. There is no encoder on the processor to fall back to: encoding a\n" +
                "    desktop in software cannot hold the latency this server exists for, and a\n" +
                "    stream that quietly became unplayable would be worse than this message.\n" +
                "    What to do: attach the monitor to the NVIDIA or AMD card, or point\n" +
                "    [Display] Output at a screen that is already on one.");
        }

        if (!adapter.LooksDiscrete)
        {
            // A warning, not a refusal: the judgement is made from the memory the adapter reports,
            // and the encoder itself is the test that decides.
            var memory = (adapter.DedicatedVideoMemory / (1024.0 * 1024.0 * 1024.0))
                .ToString("0.0", CultureInfo.InvariantCulture);

            Log.Warn(
                $"Adapter {adapter.Index} \"{adapter.Name}\" reports {memory} GB of dedicated\n" +
                "memory, which is what an integrated graphics part reports. Its encoder is shared\n" +
                "with everything else the processor is doing, and the stream may stutter under load.\n" +
                "If a discrete card is present, attach the monitor to it or set [Display] Output to\n" +
                "one of its screens.");
        }

        WriteConfigurationIfNeeded(config);

        return new PreflightResult(PreflightOutcome.Continue, null, config, adapter, output);
    }

    // The screen's current mode and the sizes it offers, one line each, so that a stream at an
    // unexpected resolution can be explained from the log.
    private static void DescribeModes(string deviceName)
    {
        try
        {
            var current = DisplayAdaptation.Current(deviceName);
            var modes = DisplayAdaptation.Supported(deviceName);

            if (current is not null) Log.Info($"the screen is currently {current}");
            if (modes.Count == 0) return;

            var sizes = modes
                .GroupBy(m => (m.Width, m.Height))
                .OrderByDescending(g => g.Key.Width * g.Key.Height)
                .Select(g => $"{g.Key.Width}x{g.Key.Height} @ " +
                             string.Join('/', g.Select(m => m.RefreshHz).Distinct().OrderByDescending(r => r)));

            Log.Info($"the screen offers {modes.Count} mode(s):\n    " + string.Join("\n    ", sizes));
        }
        catch (Exception error)
        {
            Log.Info($"the screen's modes could not be listed: {error.Message}");
        }
    }

    // The first run writes the file so every setting is documented; an upgrade that added some
    // does the same, so an older file gains them at their defaults instead of running without.
    private static void WriteConfigurationIfNeeded(AppConfig config)
    {
        if (!config.IsFirstRun && !config.DebugWasAbsent && config.AddedSettings.Count == 0) return;

        if (config.AddedSettings.Count > 0)
        {
            Log.Event($"{config.AddedSettings.Count} new setting(s), added to this server since " +
                      "the configuration file was last written, are added to it now at their " +
                      "defaults:\n" +
                      string.Join("\n", config.AddedSettings.Select(s => $"    [{s.Section}] {s.Key}")));
        }

        var written = config.SaveSomewhere();
        if (written is not null)
        {
            Log.Event($"configuration written to {written}");
            return;
        }

        // Not fatal: the defaults apply until restart. Both folders are named, because which one
        // was refused is the whole question.
        Log.Warn(
            "The configuration file could not be written. The defaults are in use and will be\n" +
            "again at the next start; any change made now is lost on exit.\n" +
            "Tried:\n" +
            string.Join("\n", AppConfig.Candidates().Select(path => "    " + path)));
    }

    private static string FirstCandidate() => AppConfig.Candidates()[0];

    private static PreflightResult Stop(string reason)
    {
        Log.Error(reason);
        return new PreflightResult(PreflightOutcome.Stop, reason, null, null, null);
    }
}
