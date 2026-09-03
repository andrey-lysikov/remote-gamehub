//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Windows.Forms;
using RemoteGameHub.Media;
using RemoteGameHub.App;
using RemoteGameHub.Library;
using RemoteGameHub.Session;
using RemoteGameHub.Protocol;

namespace RemoteGameHub;

internal static class Program
{
    // Two numbers, always: 0.1, never 0.1.0. The assembly carries four, the only shape Windows
    // accepts in a manifest, so the trailing ones are trimmed here.
    internal static string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(2) ?? "0.0";

    [STAThread]
    private static int Main(string[] args)
    {
        // The very first statement: everything after it, including every refusal, has somewhere to
        // be written. The log sits beside the configuration, wherever that turns out to be.
        Log.Start(AppConfig.ResolveDirectory(), AppConfig.FallbackDirectory, Version);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception error)
                Log.Crash("unhandled exception", error);
        };
        Application.ThreadException += (_, e) => Log.Crash("unhandled exception on the UI thread", e.Exception);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        try
        {
            // Answers what cannot be answered from the log of a server that refuses to start: what
            // this machine has to stream from. Written to the log, not to a file of its own.
            if (args.Any(a => a.Equals("--list-displays", StringComparison.OrdinalIgnoreCase)))
            {
                Log.SetVerbose(true);
                Log.Info(DisplayInventory.Describe(DisplayInventory.Enumerate()));
                return 0;
            }

            // Opens the encoder and reports what it can do, before the startup checks: a card can
            // encode perfectly well in a session that has no screen to capture.
            if (args.Any(a => a.Equals("--encoder-test", StringComparison.OrdinalIgnoreCase)))
            {
                Log.SetVerbose(true);

                var probeConfig = AppConfig.Load(Log.Warn);
                var adapters = DisplayInventory.Enumerate();
                Log.Info(DisplayInventory.Describe(adapters));

                // The card that drives a screen, if one does; otherwise the first real card. In a
                // remote session no card drives anything, and the encoder is still on the card.
                var card = adapters.FirstOrDefault(a => a.Outputs.Any(o => o.AttachedToDesktop) &&
                                                        (a.IsNvidia || a.IsAmd))
                           ?? adapters.FirstOrDefault(a => a.IsNvidia || a.IsAmd);

                if (card is null)
                {
                    Log.Error("There is no NVIDIA or AMD card on this machine to encode with.");
                    return 1;
                }

                var probed = VideoEncoders.Probe(card, probeConfig);
                Log.Info(probed.Refusal is null
                    ? $"encoder test: {probed.Encoder} opened on adapter {card.Index} " +
                      $"\"{card.Name}\" — H.264 {(probed.H264 ? "yes" : "no")}, " +
                      $"HEVC {(probed.Hevc ? "yes" : "no")}, HDR {(probed.Hdr ? "yes" : "no")}"
                    : $"encoder test: nothing opened.\n{probed.Refusal}");

                return probed.CanStream ? 0 : 1;
            }

            // The game scan, on its own and to the log. Before the startup checks on purpose: what
            // is installed has nothing to do with screens or encoders.
            if (args.Any(a => a.Equals("--scan-games", StringComparison.OrdinalIgnoreCase)))
            {
                Log.SetVerbose(true);

                var scanConfig = AppConfig.Load(Log.Warn);
                using var scanDatabase = Database.Open(Path.GetDirectoryName(scanConfig.Path)!);
                new GameLibrary(scanDatabase).Rescan(scanConfig);
                return 0;
            }

            // Held before anything is opened. Two copies cannot hold the same ports, and the
            // second one would fail at a listener rather than here, where the reason is plain.
            using var single = new Mutex(initiallyOwned: true, AppParameters.Identity.Mutex, out var isOnly);
            if (!isOnly)
            {
                Log.Warn($"another copy of {AppParameters.Identity.DisplayName} is already running; " +
                         "this one is exiting.");
                return 0;
            }

            var preflight = Preflight.Run();
            if (preflight.Outcome == PreflightOutcome.Stop)
            {
                StartupNotice.Show(preflight.Reason ?? "no reason was recorded.");
                return 1;
            }

            // Settles the first question a black screen raises, without a client involved. After
            // the checks above, because it needs the same screen and settings the stream would use.
            if (args.Any(a => a.Equals("--capture-test", StringComparison.OrdinalIgnoreCase)))
            {
                return CaptureSelfTest.Run(preflight.Output!, preflight.Config!.CaptureCursor,
                                           Path.GetDirectoryName(preflight.Config.Path)!);
            }

            return Serve(preflight);
        }
        catch (Exception error)
        {
            Log.Crash("startup failed", error);
            return 1;
        }
    }

    // Hands a file or an address to whatever Windows already opens it with. Everything the menu
    // shows goes through here, and nothing that fails here is worth interrupting anybody for.
    private static void Open(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            Log.Info("there is nothing to open: the path was never set");
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            // A .conf or a .log with nothing registered against it: notepad opens both and is on
            // every Windows, so it is worth a second attempt — for a file, not for an address.
            if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                Log.Info($"{target} could not be opened: {error.Message}");
                return;
            }

            try
            {
                System.Diagnostics.Process.Start("notepad.exe", target);
            }
            catch (Exception)
            {
                Log.Info($"{target} could not be opened: {error.Message}");
            }
        }
    }

    private static int Serve(PreflightResult preflight)
    {
        var config = preflight.Config!;

        using var tray = new TrayIcon();
        tray.SetState("starting");

        // Opened here rather than when the first controller packet arrives: a missing virtual
        // controller bus is something to learn at startup, from the log.
        using var gamepads = GamepadHub.Open();
        if (!gamepads.IsAvailable)
        {
            tray.Notify(AppParameters.Identity.DisplayName,
                "Controllers are unavailable: no controller bus driver is installed. " +
                "Everything else works.", isError: false);
        }

        // Says "will use", not "listening on": nothing is bound until the listeners below open,
        // and announcing ports before they exist sends the next person hunting a firewall rule.
        Log.Info($"ports: {config.HttpPort} (http), {config.HttpsPort} (https), " +
                 $"{config.RtspPort} (rtsp), {config.VideoPort}/{config.ControlPort}/{config.AudioPort} (udp)");

        // Opened and closed once, so that /serverinfo reports what the card can really encode and
        // a driver problem is a line here rather than a client-side timeout at the first launch.
        var encoder = VideoEncoders.Probe(preflight.Adapter!, config);
        if (encoder.CanStream)
        {
            var codecs = new[]
            {
                encoder.H264 ? "H.264" : null,
                encoder.Hevc ? "HEVC" : null,
                encoder.Av1 ? "AV1" : null,
            }.Where(name => name is not null);

            Log.Event($"encoder: {(encoder.Encoder == VideoEncoder.NvEnc ? "NVENC" : "AMF")}, " +
                      string.Join(", ", codecs));
        }
        else
        {
            // Not fatal: the server can still be found and paired with while the user fixes the
            // driver, and every launch answers with this reason instead of a timeout.
            Log.Warn("No encoder could be opened, so nothing can be streamed until this is fixed:\n" +
                     $"    {encoder.Refusal}");
            tray.Notify(AppParameters.Identity.DisplayName,
                "The graphics card's encoder could not be opened; streaming is unavailable. " +
                "The log has the details.", isError: true);
        }

        var directory = Path.GetDirectoryName(config.Path)!;
        var identity = HostIdentity.Load(directory, config.HostName);

        using var database = Database.Open(directory);
        var clients = new ClientStore(database);

        // Only here, at startup. A client removed while it is using the stream would lose it for a
        // reason nothing on either screen could explain.
        clients.ForgetStale();
        Log.Info($"{clients.Count()} client(s) remembered");

        // Scanned before the listeners open, so the first /applist is answered from a finished
        // list. A start is also when the machine is quiet: nobody is waiting on the answer yet.
        var games = new GameLibrary(database);
        try
        {
            games.Rescan(config);
        }
        catch (Exception error)
        {
            // The scanners each swallow their own failures; this catches the database refusing the
            // write. The list is then last start's, which is stale but still true enough to serve.
            Log.Error("the game scan could not be recorded; the list from the last start is served", error);
        }

        // Whether there is a desktop to capture at all. It changes while the server runs — every
        // remote desktop connection takes it away — so it is watched rather than decided once.
        using var session = new SessionWatch();
        Log.Info(session.Describe());

        // Owns whatever is streaming. Created before the listeners, because the first thing a
        // client does after finding this machine may be to ask it to start.
        using var sessions = new SessionManager(config, preflight.Output!, encoder, games,
                                                gamepads, tray, session);

        var pairing = new PairingManager(identity, clients);

        // What each playback device mixes into, once: it is the answer to "why is there no
        // surround", and it changes only when a person changes it in Windows.
        if (config.AudioEnabled) AudioEndpoints.ListToLog();

        GameStreamServer? server = null;
        RtspServer? rtsp = null;
        ServiceDiscovery? discovery = null;

        // The whole network side, opened and closed with the session: while this machine is used
        // over remote desktop, closed ports make it plainly absent rather than falsely available.
        bool OpenNetwork()
        {
            try
            {
                server = new GameStreamServer(config, identity, preflight.Output!, clients, pairing,
                                              games, encoder, sessions);
                server.Start();

                rtsp = new RtspServer(config, encoder);

                // The negotiation is where a stream's numbers are settled, so it is also where the
                // session is actually built: /launch knows the key but not the picture.
                rtsp.Negotiated += sessions.Negotiated;
                rtsp.Start();

                // Started after the ports are open, never before: a client that hears the
                // announcement and connects at once must find something listening.
                discovery = new ServiceDiscovery(config, identity);
                discovery.Start();

                return true;
            }
            catch (Exception error)
            {
                Log.Error(
                    $"The server could not open its ports: {error.Message}\n" +
                    $"    Something else is probably using {config.HttpPort}, {config.HttpsPort} or\n" +
                    $"    {config.RtspPort} — another copy of this server, or Sunshine, or NVIDIA\n" +
                    "    GameStream. Change [Network] PortBase, and enter the same base in the client.");

                CloseNetwork();
                return false;
            }
        }

        void CloseNetwork()
        {
            discovery?.Dispose();
            rtsp?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            server?.DisposeAsync().AsTask().GetAwaiter().GetResult();

            discovery = null;
            rtsp = null;
            server = null;
        }

        // A newer release, looked for a few minutes after the start and once a day after that.
        // Said once per version, in a balloon that opens the download page when clicked.
        using var updates = new UpdateChecker();
        updates.Found += newer => tray.Notify(
            $"{AppParameters.Identity.DisplayName} {newer} is available",
            $"This is {Program.Version}. Click here to open the download page.",
            isError: false,
            onClick: () => Open(AppParameters.Links.LatestRelease));
        updates.Start();

        // The page: what this server is doing, and the box for the four digits a pairing client
        // shows. On a port of its own, answering private addresses only.
        var console = new WebConsole(config, identity, pairing, clients, games, sessions, encoder,
                                     updates, gamepads, directory);
        console.Start();

        // The four digits a pairing client shows are never sent over the protocol — both ends
        // derive their encryption key from them — so they are carried across by hand, per device.
        pairing.PairingStarted += name => tray.Notify(
            AppParameters.Identity.DisplayName,
            $"{name} is pairing. Open http://{Environment.MachineName}" +
            (config.WebPort == 80 ? string.Empty : $":{config.WebPort}") +
            "/ and type the code it is showing.",
            isError: false);


        // Opened whatever the session is. Only the picture needs a desktop: finding this machine,
        // pairing and listing what it offers do not. The refusal is made in the launch itself.
        if (!OpenNetwork())
        {
            StartupNotice.Show("The server could not open its ports.");
            return 1;
        }

        // The router, if it was asked. After the ports are open, so that nothing is ever forwarded
        // to a socket that does not exist yet.
        var forwarding = new PortForwarding(config);
        forwarding.Start();

        // The pictures the client shows beside each game, started here rather than inside the scan:
        // a large library can take minutes, and none of it should hold up a client connecting now.
        using var artwork = new CancellationTokenSource();
        _ = CoverArt.FetchAsync(games, directory, config, artwork.Token);

        // The scan, again, from wherever it is asked for after the start. One at a time — two scans
        // interleaving their writes would each mark the other's games as gone — and never inline.
        var scanning = new object();
        void RescanGames(string why)
        {
            lock (scanning)
            {
                try
                {
                    Log.Info($"scanning the games again: {why}");

                    // The [Games] and [Artwork] settings are read again here, and only they: a
                    // person adding a folder to the file and asking for a refresh means it scanned.
                    try
                    {
                        config.AdoptScanSettings(AppConfig.Load(Log.Warn));
                    }
                    catch (Exception error)
                    {
                        // A file that no longer parses is not a reason to skip the scan: the
                        // settings from the last good read are still in hand.
                        Log.Warn($"the configuration could not be re-read ({error.Message}); " +
                                 "the scan uses the settings from startup");
                    }

                    games.Rescan(config);
                    _ = CoverArt.FetchAsync(games, directory, config, artwork.Token);
                }
                catch (Exception error)
                {
                    Log.Error($"the game scan ({why}) failed", error);
                }
            }
        }

        // The page's Rescan button, handed to another thread here rather than in the page: that
        // call must return before the browser gives up on it, and a scan does not.
        console.Rescan = why => Task.Run(() => RescanGames(why));

        // Scanned once at the start and again only when asked, from the menu or the page. Two
        // timed passes used to follow; they rescanned every library to find nothing new.

        // Whether the server starts at sign-in. Read once here, on a thread of its own because it
        // asks Task Scheduler, and then kept in memory for the menu to draw its check mark from.
        var autostart = new Autostart(directory);
        _ = Task.Run(() =>
        {
            autostart.Refresh();
            Log.Info(!autostart.IsEnabled
                ? "the server is not set to start at sign-in; the tray menu can switch that on"
                : autostart.StartsThisCopy
                    ? "the server is set to start at sign-in"
                    : "the sign-in task starts another copy of this server; switching Autostart " +
                      "off and on again points it at this one");
        });

        // The menu behind the icon's right button, built at the end because every entry belongs to
        // something opened above it. Quit is kept apart: it is the only entry that cannot be undone.
        tray.SetMenu(new[]
        {
            // On another thread: a large library takes seconds to walk, and this is the thread
            // the notification area itself is drawn on.
            new TrayEntry("Refresh games", () => Task.Run(() => RescanGames("asked for from the menu"))),
            new TrayEntry("Show status page", () => Open($"http://localhost" +
                (config.WebPort == 80 ? string.Empty : $":{config.WebPort}") + "/")),
            // Off the message loop's thread for the same reason as the scan: it runs a process
            // and waits for it, and the notification area must not wait with it.
            new TrayEntry("Autostart", () => Task.Run(autostart.Toggle),
                          () => autostart.IsEnabled && autostart.StartsThisCopy),
            TrayEntry.Separator,
            new TrayEntry("Show config", () => Open(config.Path)),
            new TrayEntry("Show log", () => Open(Log.Path)),
            TrayEntry.Separator,
            new TrayEntry("Quit", Application.Exit),
        });

        // What the server is doing, for the log. It follows the session for the life of the
        // process: a remote desktop connection pauses streaming, a disconnection resumes it.
        tray.SetState(session.TrayState);
        session.Changed += watch =>
        {
            tray.SetState(watch.TrayState);
        };

        // A warning, at every start, whatever Debug says. Not a fault — it is what this server was
        // asked to be — but it is the one property that decides what reaching this machine costs.
        Log.Warn("Nobody is asked to approve a client: the first one that asks to pair is admitted,\n" +
                 "and from then on it may connect, see this screen and use this keyboard, with the\n" +
                 "rights this server runs under. That is the intended behaviour on a home network.\n" +
                 "Set [Network] BindAddress to one address of this machine if it is also on a\n" +
                 "network you do not trust.");

        Application.Run(new ApplicationContext());

        // The listeners are shut down before the tray icon goes, so a client reconnecting in those
        // milliseconds is refused; the session first, while the control channel is still up.
        sessions.Shutdown();
        artwork.Cancel();

        CloseNetwork();

        forwarding.DisposeAsync().AsTask().GetAwaiter().GetResult();
        console.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return 0;
    }
}
