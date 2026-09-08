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

    private static bool Asked(string[] args, string name) =>
        args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    // Whether this is the copy the service started, decided by its argument alone: a worker that
    // guessed wrong would exit for the running service and be restarted for ever. Kept for exit.
    private static bool _isWorker;

    // The log where the server keeps its own, which is the signed-in person's profile when this
    // process is SYSTEM (the service, or the worker it starts) and the caller's own otherwise.
    private static void StartLog()
    {
        if (PlatformGuard.IsSystem)
        {
            AppConfig.ProfileDirectoryOverride = UserContext.ConsoleUserLocalAppData();
        }

        Log.Start(AppConfig.ResolveDirectory(), AppConfig.FallbackDirectory, Version);
    }

    [STAThread]
    private static int Main(string[] args)
    {
        // The service half, which streams nothing and holds no ports. It writes into the server's
        // log under its own name, and moves with it when the worker is started for another person.
        if (Asked(args, "--service"))
        {
            StartLog();
            Log.SetSource("service");
            Log.SetVerbose(true);
            return ServiceHost.Run();
        }

        // The same two things the application does for itself at every start and exit, offered as
        // verbs for the person who would rather set it up once by hand or take it away for good.
        if (Asked(args, "install-service") || Asked(args, "uninstall-service"))
        {
            StartLog();
            return Asked(args, "install-service") ? ServiceControl.Install() : ServiceControl.Uninstall();
        }

        _isWorker = Asked(args, "--worker");

        // The very first statement: everything from here on, including every refusal, has
        // somewhere to be written. The log sits beside the configuration.
        StartLog();

        // Said at once, and by the copy the service started above all: it is a different account
        // from the person at the machine, so "the log" is not necessarily the file they have open.
        if (PlatformGuard.IsSystem)
        {
            Log.Event(AppConfig.ProfileDirectoryOverride is null
                ? "the signed-in account could not be identified, so this server keeps its " +
                  $"configuration and log in SYSTEM's own profile: {AppConfig.FallbackDirectory}"
                : $"this server runs as SYSTEM and keeps its configuration and log in the " +
                  $"signed-in account's profile: {AppConfig.FallbackDirectory}");
        }

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
                      $"HEVC {(probed.Hevc ? "yes" : "no")} (HDR {(probed.Hdr ? "yes" : "no")}), " +
                      $"AV1 {(probed.Av1 ? "yes" : "no")} (HDR {(probed.Av1Hdr ? "yes" : "no")})"
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

            // This copy already has administrator rights, so it makes the service, starts it and
            // steps aside; the worker (LocalSystem) streams. Not for the worker or the capture test.
            if (!_isWorker && !Asked(args, "--capture-test"))
            {
                if (ServiceControl.EnsureRunning(out var refusal))
                {
                    // Waited for rather than assumed: everything from here on is another process
                    // under another account, and if it never starts this line would be a lie.
                    if (ServiceControl.WaitForServer(TimeSpan.FromSeconds(20)))
                    {
                        Log.Event(
                            "the service is running and holds the server, which it started as " +
                            "SYSTEM on this session. This copy has nothing left to do and is " +
                            "exiting; the icon in the notification area belongs to the copy the " +
                            "service started.");
                        return 0;
                    }

                    Log.Warn(
                        "The service was started but the server has not appeared, and this copy " +
                        "is about to exit.\n" +
                        "What the service did is in this same file, on the lines marked " +
                        "\"service:\".\n" +
                        "This copy is running the server itself instead, so the machine is not " +
                        "left with nothing.");

                    // Better a server without the secure desktop than none. The service is stopped
                    // first, or it would start a second copy and the two would fight over the ports.
                    ServiceControl.StopIfRunning(wait: true);
                }

                // Not fatal, deliberately: everything but the secure desktop works in this process,
                // and a machine where a service cannot be made should still stream.
                Log.Warn(
                    $"The service could not be used: {refusal}.\n" +
                    "This copy runs the server itself instead. Everything works except one thing:\n" +
                    "while a program asks for administrator rights, Windows draws the prompt on a\n" +
                    "desktop only LocalSystem may see, so the picture holds still until somebody\n" +
                    "answers it at the machine itself.");
            }

            // Held before anything is opened, so a second copy fails here with a plain reason. The
            // refusal is caught too: SYSTEM's worker owns a mutex an elevated copy may not open.
            Mutex? single = null;
            try
            {
                single = new Mutex(initiallyOwned: true, AppParameters.Identity.Mutex, out var isOnly);
                if (!isOnly)
                {
                    Log.Warn($"another copy of {AppParameters.Identity.DisplayName} is already " +
                             "running; this one is exiting.");
                    return 0;
                }
            }
            catch (UnauthorizedAccessException)
            {
                Log.Warn(
                    $"Another copy of {AppParameters.Identity.DisplayName} is already running, as " +
                    "a more privileged account — the service's worker, which runs as SYSTEM. This " +
                    "one is exiting.\n" +
                    "There is nothing to do: the service already keeps a copy running, and it is " +
                    "the one that can stream a prompt for administrator rights.");
                return 0;
            }

            using (single)
            {

                var preflight = Preflight.Run();
                if (preflight.Outcome == PreflightOutcome.Stop)
                {
                    StartupNotice.Show(preflight.Reason ?? "no reason was recorded.");
                    return 1;
                }

                // Settles the first question a black screen raises, without a client. After the
                // checks above, because it needs the same screen and settings the stream would use.
                if (args.Any(a => a.Equals("--capture-test", StringComparison.OrdinalIgnoreCase)))
                {
                    return CaptureSelfTest.Run(preflight.Output!, preflight.Config!.CaptureCursor,
                                               Path.GetDirectoryName(preflight.Config.Path)!);
                }

                return Serve(preflight);
            }
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

        // As the person signed in, when this server is SYSTEM: a browser or editor started as
        // SYSTEM is SYSTEM's own (Edge refuses outright). An ordinary run already is the person.
        if (PlatformGuard.IsSystem)
        {
            if (SessionLauncher.StartAsConsoleUser(target, null)) return;

            // An address is not tried as SYSTEM: a browser started as it opens nothing, and the
            // silence hid the refusal above. A file still gets notepad below, which does run.
            if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn($"{target} was not opened: it could not be started as the signed-in " +
                         "person (the reason is just above), and a browser does not run as SYSTEM");
                return;
            }
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

        // A step above the Normal every game defaults to, so the capture/encode/send thread's own
        // Highest (see StreamSession.Start) is not just highest among equals with one.
        try
        {
            System.Diagnostics.Process.GetCurrentProcess().PriorityClass =
                System.Diagnostics.ProcessPriorityClass.AboveNormal;
        }
        catch (Exception error)
        {
            Log.Info($"the process priority could not be raised: {error.Message}");
        }

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
            // As the person signed in, when this server is SYSTEM: the launchers live under their
            // HKEY_CURRENT_USER, and SYSTEM's own is empty. Unchanged on an ordinary elevated start.
            UserContext.AsConsoleUser(() => games.Rescan(config));
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
        if (AppParameters.Audio.Enabled) AudioEndpoints.ListToLog();

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
            $"{AppParameters.Identity.DisplayName} v{newer} is available",
            $"This is v{Program.Version}. Click here to open the download page.",
            isError: false,
            onClick: () => Open(updates.Link));
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

        // Whether Windows will sign the person in again after a restart, and a notification when
        // it will not. Off this thread: it asks Windows about the account, which takes a moment.
        var statusPage = $"http://localhost{(config.WebPort == 80 ? string.Empty : $":{config.WebPort}")}/";
        _ = Task.Run(() => AutoLogon.NoticeAtStart(tray, () => Open(statusPage)));

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

                    UserContext.AsConsoleUser(() => games.Rescan(config));
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
            // Off the message loop's thread, since it runs a process and waits for it. Still the
            // sign-in task, service or no service: the task starts the exe, which starts the service.
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

        // Last of all, and only for the worker: everything is shut down, so the service may end this
        // process at once. Otherwise it would see its worker gone and start another, unlike Quit.
        if (_isWorker) ServiceControl.StopIfRunning();

        return 0;
    }
}
