//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using RemoteGameHub.Media;
using RemoteGameHub.App;
using RemoteGameHub.Library;
using RemoteGameHub.Native;
using RemoteGameHub.Protocol;

namespace RemoteGameHub.Session;

// What a client asked for at /launch or /resume, before the RTSP negotiation fills in the rest.
// AudioChannels comes from surroundAudioInfo and picks the sound device, before the game starts.
// Width/Height/Fps/HdrRequested come from the same mode/hdrMode a moment before the RTSP ANNOUNCE
// repeats them, and are zero/false when an older client sends neither.
internal sealed record LaunchRequest(int AppId, byte[] RiKey, uint RiKeyId, int AudioChannels = 2,
                                     int Width = 0, int Height = 0, int Fps = 0,
                                     bool HdrRequested = false);

// One client, streaming. /launch carries the encryption key but not the resolution or the codec,
// which arrive in the RTSP ANNOUNCE, so the session is only built when the negotiation completes.
internal sealed class SessionManager : IDisposable
{
    private readonly AppConfig _config;
    private readonly DisplayOutput _output;
    private readonly EncoderCapabilities _encoder;
    private readonly GameLibrary _games;
    private readonly GamepadHub _gamepads;
    private readonly TrayIcon _tray;
    private readonly SessionWatch _sessionWatch;
    private readonly object _gate = new();

    private LaunchRequest? _pending;
    private StreamSession? _session;
    private GameWatcher? _watcher;

    // The machine's sound moved onto a device that can carry what the client asked for, held here
    // rather than in the session: it is applied before the game starts, which is before there is one.
    private AudioAdaptation? _audio;

    // The screen moved to the size and range the client asked for, same reasoning: a game reads
    // HDR support once at its own startup, so this has to land before StartApplication, not after.
    // Handed to the StreamSession once it exists; disposed here only if one never does.
    private DisplayAdaptation? _display;

    internal SessionManager(AppConfig config, DisplayOutput output, EncoderCapabilities encoder,
                            GameLibrary games, GamepadHub gamepads, TrayIcon tray,
                            SessionWatch sessionWatch)
    {
        _config = config;
        _output = output;
        _encoder = encoder;
        _games = games;
        _gamepads = gamepads;
        _tray = tray;
        _sessionWatch = sessionWatch;
    }

    // The application identifier /serverinfo reports, which is how a client chooses between Start
    // and Resume: the running game first, then the session, then zero.
    internal int CurrentAppId
    {
        get
        {
            lock (_gate)
            {
                if (_watcher is { IsRunning: true }) return _watcher.AppId;
                return _session?.AppId ?? 0;
            }
        }
    }

    // What is happening right now, in the words the status page uses. Under the same lock as the
    // rest, so it cannot describe a session that ended while it was being written out.
    internal (bool Streaming, string Client, string Detail) Status
    {
        get
        {
            lock (_gate)
            {
                if (_session is null) return (false, string.Empty, string.Empty);

                // The same few words the tray and the log carry. What is being sent, with nothing
                // explained: the log is where the reasons are.
                return (true, _session.Negotiation.ClientAddress.ToString(), _session.Detail());
            }
        }
    }

    // Records a launch and, when it names a game rather than the desktop, starts it. Returns
    // false with a reason a client can be told.
    internal bool Launch(LaunchRequest request, out string refusal)
    {
        refusal = string.Empty;

        // A remote desktop session is not refused: duplication does work there. If the capture or
        // the encoder cannot, they say so where they fail.
        if (_sessionWatch.IsRemote)
            Log.Event("this launch is from a remote desktop session; the picture is that session's");

        if (!_encoder.CanStream)
        {
            refusal = _encoder.Refusal ?? "no encoder is available.";
            return false;
        }

        lock (_gate)
        {
            if (_session is not null)
            {
                refusal = "another client is already streaming from this machine.";
                return false;
            }

            _pending = request;
        }

        // Before the game, not after: a game reads the default playback device when it starts, and
        // one started on the old device would keep it for as long as it runs.
        MoveTheSound(request.AudioChannels);
        AdaptTheScreen(request);

        if (request.AppId != AppParameters.Protocol.DesktopAppId && !StartApplication(request.AppId))
        {
            lock (_gate) _pending = null;
            refusal = "that application could not be started.";
            return false;
        }

        Log.Info($"launch accepted for application {request.AppId}; " +
                 "waiting for the client to negotiate the stream");
        return true;
    }

    // Builds the session from the negotiated configuration. Called on the RTSP connection's
    // thread when an ANNOUNCE is accepted.
    internal void Negotiated(StreamNegotiation negotiation)
    {
        LaunchRequest? request;
        lock (_gate)
        {
            if (_session is not null)
            {
                // A second ANNOUNCE for a session already running. Nothing to do: the client is
                // re-sending its configuration, and the stream it describes is the one running.
                return;
            }

            request = _pending;
            if (request is null)
            {
                Log.Warn("a stream was negotiated without a launch; it is ignored");
                return;
            }
        }

        // What the client is shown while the game loads: its own picture and its name. Gathered
        // here because this is where the library and the watcher live.
        var gameId = request.AppId - AppParameters.Protocol.GameAppIdOffset;
        var target = gameId > 0 ? _games.Target(gameId) : null;
        var poster = gameId > 0 ? _games.BoxArtPath(gameId) : null;

        var output = ResolveOutput();
        if (output is null)
        {
            lock (_gate) _pending = null;
            _tray.SetState("waiting for a client");
            return;
        }

        // Taken over rather than reused as-is: ResolveOutput() re-picks the screen fresh, and a
        // virtual display appearing between the launch and now would make the two disagree on
        // which one this adaptation belongs to.
        DisplayAdaptation? preAdapted;
        lock (_gate)
        {
            preAdapted = _display;
            _display = null;
        }

        if (preAdapted is not null && !preAdapted.IsFor(output))
        {
            preAdapted.Dispose();
            preAdapted = null;
        }

        try
        {
            var session = new StreamSession(_config, output, _encoder, _gamepads, _tray,
                request, negotiation, Ended, target?.Title, poster, GameIsUp,
                gamePointer: target?.Pointer ?? false,
                gameQuality: target?.Quality ?? StreamQuality.High,
                showCard: target?.ShowCard ?? true, preAdapted: preAdapted);

            lock (_gate)
            {
                _session = session;
                _pending = null;
            }

            session.Start();
        }
        catch (Exception error)
        {
            preAdapted?.Dispose();
            Log.Error("the stream could not be started", error);
            lock (_gate)
            {
                _session?.Dispose();
                _session = null;
                _pending = null;
            }
            _tray.SetState("waiting for a client");
        }
    }

    // Checked again rather than trusted: an index valid at startup can throw
    // DXGI_ERROR_NOT_FOUND later, so this retries a few times before refusing the stream.
    private DisplayOutput? ResolveOutput()
    {
        // Mirrors the handful of quick tries over ~2s OpenCaptureAfterModeChange gives an
        // ordinary mode change: a remote session's own screen is not always there instantly.
        const int attempts = 10;
        string reason = string.Empty;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var inventory = DisplayInventory.Enumerate();
            var output = DisplayInventory.Select(inventory, _config.Output, _config.VirtualDisplay,
                out reason);

            if (output is not null)
            {
                if (attempt > 1)
                    Log.Info($"the screen was found on try {attempt}: {reason}");

                if (output.AdapterIndex != _output.AdapterIndex ||
                    output.OutputIndex != _output.OutputIndex)
                {
                    Log.Info($"the screen to capture is now {output.Label} \"{output.DeviceName}\", " +
                             $"not {_output.Label} as it was at startup ({reason})");
                }

                return output;
            }

            if (attempt < attempts) Thread.Sleep(AppParameters.Capture.RecreateDelayMs);
        }

        Log.Warn($"the stream is refused: the screen this server captures is gone ({reason}).");
        return null;
    }

    // Reattaches a client to the game already running. Nothing is started; it only accepts the key
    // the returning client brings, so its RTSP negotiation has something to belong to.
    internal bool Resume(LaunchRequest request, out string refusal)
    {
        refusal = string.Empty;

        lock (_gate)
        {
            if (_watcher is not { IsRunning: true })
            {
                refusal = "there is nothing running to resume.";
                return false;
            }

            if (_session is not null)
            {
                refusal = "another client is already streaming from this machine.";
                return false;
            }

            // The identifier is the running game's, not whatever the client asked for: it is
            // resuming what is there.
            request = request with { AppId = _watcher.AppId };
            _pending = request;
        }

        MoveTheSound(request.AudioChannels);
        AdaptTheScreen(request);

        Log.Info("resume accepted; waiting for the client to negotiate the stream");
        return true;
    }

    // Ends the session and closes the game, which is what a cancel means. A stream that merely
    // dropped goes through EndStream instead and leaves the game running for Resume.
    internal void Cancel() => Stop(closeTheGame: true);

    // Shutting the server down. The stream is taken down and the game is left alone: this
    // machine is going away, which is no reason for someone's game to.
    internal void Shutdown() => Stop(closeTheGame: false);

    private void Stop(bool closeTheGame)
    {
        EndStream();

        GameWatcher? watcher;
        lock (_gate)
        {
            watcher = _watcher;
            _watcher = null;
        }

        if (watcher is null) return;

        if (closeTheGame) watcher.StopGame();
        watcher.Dispose();
    }

    // Takes down the stream and nothing else. A game that is running stays claimed, so a client
    // whose connection dropped finds Resume waiting for it rather than having to start again.
    private void EndStream()
    {
        StreamSession? session;
        AudioAdaptation? audio;
        DisplayAdaptation? display;
        lock (_gate)
        {
            session = _session;
            audio = _audio;
            display = _display;
            _session = null;
            _pending = null;
            _audio = null;
            _display = null;
        }

        audio?.Dispose();

        // Null here whenever Negotiated() ran: it took this over into the session, which puts the
        // screen back on its own. Left set only when a launch was abandoned before that happened.
        display?.Dispose();

        if (session is null) return;

        Log.Event("the stream ended");
        session.Dispose();
        _tray.SetState("waiting for a client");
    }

    // Called by the session when it stops from the inside. The teardown goes to another thread:
    // this arrives on a thread the teardown waits for, which in place would wait for itself.
    private void Ended(string reason)
    {
        lock (_gate)
        {
            if (_session is null) return;
        }

        Log.Event($"the stream is ending: {reason}");

        // The stream only. A client whose network dropped has not finished with the game.
        Task.Run(EndStream);
    }

    // Puts the machine's sound where the client's channel count can come from, and remembers what
    // to undo. Any move still standing from an abandoned launch is undone first.
    private void MoveTheSound(int channels)
    {
        AudioAdaptation? previous;
        lock (_gate)
        {
            previous = _audio;
            _audio = null;
        }

        previous?.Dispose();

        var adaptation = AudioAdaptation.Apply(channels);
        lock (_gate) _audio = adaptation;
    }

    // Puts the screen where the client's mode and HDR ask, from the same numbers the RTSP ANNOUNCE
    // repeats a moment later — before StartApplication, for the same reason MoveTheSound goes first.
    // A client too old to send mode/hdrMode leaves this a no-op; Negotiated() adapts it as before.
    private void AdaptTheScreen(LaunchRequest request)
    {
        DisplayAdaptation? previous;
        lock (_gate)
        {
            previous = _display;
            _display = null;
        }

        previous?.Dispose();

        if (request.Width <= 0 || request.Height <= 0 || request.Fps <= 0) return;

        var desktop = request.AppId == AppParameters.Protocol.DesktopAppId;
        var wantHdr = request.HdrRequested && _encoder.AnyHdr;

        var adaptation = DisplayAdaptation.Apply(_output, request.Width, request.Height, request.Fps,
            wantHdr, _encoder.AnyHdr, _config.Adapt, scaleForClient: desktop && _config.ScaleDesktop,
            isGame: !desktop);

        lock (_gate) _display = adaptation;
    }

    // Starts the application and begins watching for it. The command may be a steam:// URL, a
    // shell path or an executable, so it goes to the shell; the watcher is what says it started.
    private bool StartApplication(int appId)
    {
        // Already up: a fresh /launch after nothing worse than a dropped connection must not run
        // the command again or restart the watcher, or the card comes back for a game on screen.
        lock (_gate)
        {
            if (_watcher is { IsRunning: true } running && running.AppId == appId) return true;
        }

        var gameId = appId - AppParameters.Protocol.GameAppIdOffset;
        var target = gameId > 0 ? _games.Target(gameId) : null;

        if (target is null)
        {
            Log.Warn($"application {appId} is not in the library; nothing was started");
            return false;
        }

        try
        {
            // A packaged game is addressed by a shell: path, which nothing but Explorer resolves.
            var start = target.Command.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
                ? new ProcessStartInfo("explorer.exe", target.Command) { UseShellExecute = true }
                : new ProcessStartInfo(target.Command) { UseShellExecute = true };

            // From its own folder when one is known: games that look for their data beside the
            // working directory otherwise start into an error naming this server's folder.
            if (!string.IsNullOrWhiteSpace(target.InstallPath) && Directory.Exists(target.InstallPath))
                start.WorkingDirectory = target.InstallPath;

            Process.Start(start);
            Log.Event($"asked the shell to start \"{target.Title}\": {target.Command}");
        }
        catch (Exception error)
        {
            Log.Warn($"\"{target.Command}\" could not be started: {error.Message}");
            return false;
        }

        lock (_gate)
        {
            _watcher?.Dispose();
            _watcher = GameWatcher.Start(appId, target.Title, target.InstallPath, GameFinished);
        }

        return true;
    }

    // Whether the game is on screen yet. Deliberately the stricter of the watcher's two questions,
    // IsOnScreen: the card covers the wait for something to look at, not for a process.
    private bool GameIsUp()
    {
        lock (_gate) return _watcher?.IsOnScreen ?? true;
    }

    // The game has closed, so the stream ends rather than handing the player the host's desktop.
    // Only the stream this game was launched for: the identifier is checked against both.
    private void GameFinished(int appId)
    {
        bool ours;

        lock (_gate)
        {
            if (_watcher?.AppId != appId) return;
            ours = _session?.AppId == appId;
        }

        Log.Info("this machine is no longer running a game");

        // Not on the watcher's own thread: EndStream disposes the session, and the session's
        // teardown waits on threads this one has no business blocking.
        if (ours) Task.Run(() =>
        {
            Log.Event("the stream is ending: the game was closed");
            EndStream();
        });
    }

    public void Dispose() => Shutdown();
}

// The running stream: capture, encode, and the three channels going out. One thread drives the
// picture; the control channel and the two receive paths have their own.
internal sealed class StreamSession : IDisposable
{
    // RTP's clock for video, and the unit the timestamps are counted in. Ninety kilohertz is the
    // protocol's, not a choice.
    private const int RtpClockHz = 90000;

    // How long the starting card may stand in for the machine; past this the game is stuck behind
    // something the player can only deal with by seeing it.
    private static readonly TimeSpan CardPatience = TimeSpan.FromSeconds(45);

    // How many times the duplication may be lost and reopened with no frame between. One is a mode
    // change; fifty is ten seconds of a screen that is not coming back.
    private const int MaxLostInARow = 50;

    // The shortest gap between two key frames. A client asking for one per lost frame is asking the
    // same thing many times, and answering each broke the stream rather than mending it.
    private static readonly TimeSpan BetweenKeyFrames = TimeSpan.FromMilliseconds(250);

    // How long an unchanged picture may go unsent. Well inside the seven seconds a client waits
    // before deciding the host has stopped, and long enough that an idle desktop costs nothing.
    private static readonly TimeSpan StillFrameEvery = TimeSpan.FromMilliseconds(500);

    // How long the picture must have been still before frames are held back at all. A desktop in
    // use changes in bursts with gaps between them, and those gaps are not idleness.
    private static readonly TimeSpan StillAfter = TimeSpan.FromSeconds(2);

    // How long one step of the capture loop may take before the log says which step it was. Well
    // past a slow frame: at sixty a second nothing here should take a tenth.
    private static readonly TimeSpan SlowStep = TimeSpan.FromMilliseconds(500);

    private readonly AppConfig _config;
    private readonly DisplayOutput _output;
    private readonly EncoderCapabilities _capabilities;
    private readonly GamepadHub _gamepads;
    private readonly TrayIcon _tray;
    private readonly StreamNegotiation _negotiation;

    // The numbers this session was built from, for the status page to describe.
    internal StreamNegotiation Negotiation => _negotiation;

    // What is actually being sent rather than what was asked for. Set once the capture and the
    // encoder are open.
    internal int SentWidth { get; private set; }
    internal int SentHeight { get; private set; }
    internal bool SentHdr { get; private set; }

    // The screen's own refresh rate while this stream lasts, or zero when the driver does not
    // say. It is not the frame rate: the two differ whenever the screen has no mode to match.
    internal double SentRefreshHz { get; private set; }

    // What became of the client's high dynamic range request, in the words the page uses. Both
    // sides are in it: whether the client asked, and what this machine could do about it.
    internal string HdrState { get; private set; } = "standard range";

    // Both sides of the high dynamic range question in one phrase: whether the client asked for
    // it, and what this machine could do about it.
    private string DescribeHdr(bool sending)
    {
        if (sending) return "high dynamic range, as the client asked";
        if (!_negotiation.HdrRequested) return "standard range, which is what the client asked for";

        var why =
            _negotiation.Codec == VideoCodec.Hevc
                ? (!_capabilities.Hdr ? "this card cannot encode ten-bit HEVC"
                                      : "the desktop came back in eight bits")
            : _negotiation.Codec == VideoCodec.Av1
                ? (!_capabilities.Av1Hdr ? "this card cannot encode ten-bit AV1"
                                         : "the desktop came back in eight bits")
            : "the client negotiated H.264";

        return $"standard range although the client asked for HDR — {why}";
    }

    // The stream in one line for the log and the tray, the client's address last because it is
    // the part that can be dropped.
    internal string Describe() => $"{Detail()} → {_negotiation.ClientAddress}";

    // What is being sent, in the fewest words that still say it: size, rate, codec, range, bitrate.
    internal string Detail()
    {
        var megabits = _negotiation.BitrateKbps / 1000.0;
        var rate = megabits >= 10
            ? $"{megabits:0} Mb/s"
            : $"{megabits.ToString("0.#", CultureInfo.InvariantCulture)} Mb/s";

        var width = SentWidth > 0 ? SentWidth : _negotiation.Width;
        var height = SentHeight > 0 ? SentHeight : _negotiation.Height;

        var screen = SentRefreshHz > 0 ? $" ({ScreenHz} Hz)" : string.Empty;

        var audio = _negotiation.AudioChannels switch
        {
            8 => "7.1 Surround",
            6 => "5.1 Surround",
            _ => "Stereo",
        };

        var codec = $"{VideoEncoders.Name(_negotiation.Codec)}" +
                   $"{(_negotiation.Yuv444 ? " 4:4:4" : string.Empty)}";

        // Its own segment, as the host line does, rather than tacked onto the codec name: HDR is a
        // property of the picture, not of the codec sending it.
        return $"{width}×{height} {_negotiation.Fps}fps{screen} · {codec}" +
               $"{(SentHdr ? " · HDR" : string.Empty)} · {rate} · {audio}";
    }

    // The screen's refresh rate as it is written. A rate that is not the frame rate is the whole
    // explanation of a picture that stutters at a rate the client and the encoder both agreed to.
    internal string ScreenHz => SentRefreshHz.ToString("0.##", CultureInfo.InvariantCulture);
    private readonly Action<string> _ended;
    // Whether this game asked for a pointer of this server's drawing. See where it is used.
    private readonly bool _gamePointer;

    // What this stream encodes at: the game's own level, one lower when the client is remote.
    private readonly StreamQuality _quality;

    private readonly string? _gameTitle;
    private readonly string? _posterPath;
    private readonly Func<bool> _gameIsUp;
    private readonly bool _showCard;

    private readonly VideoStream _video;
    private readonly AudioStream? _audio;
    private readonly ControlStream _control;
    private readonly ClientInput _input;

    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _keyFrameWanted = true;

    // How many key frames the client has asked for since the report last looked. Counted apart
    // from whether any were sent, which is the whole point of counting it.
    private int _keyFramesAsked;

    // Packets the client says it did not receive, since the report last looked.
    private int _packetsLost;

    // Whether this stream carries a pointer of this server's drawing. See where it is set.
    private bool _drawPointer;

    private volatile bool _cardDismissed;

    // Already applied at /launch, from the same mode/hdrMode the negotiation below repeats — null
    // when the client sent neither, in which case CaptureAndEncode adapts the screen itself.
    private readonly DisplayAdaptation? _preAdapted;

    internal int AppId { get; }

    internal StreamSession(AppConfig config, DisplayOutput output, EncoderCapabilities capabilities,
                           GamepadHub gamepads, TrayIcon tray, LaunchRequest request,
                           StreamNegotiation negotiation, Action<string> ended,
                           string? gameTitle = null, string? posterPath = null,
                           Func<bool>? gameIsUp = null, bool gamePointer = false,
                           StreamQuality gameQuality = StreamQuality.High, bool showCard = true,
                           DisplayAdaptation? preAdapted = null)
    {
        _config = config;
        _output = output;
        _preAdapted = preAdapted;
        _capabilities = capabilities;
        _gamepads = gamepads;
        _tray = tray;
        _negotiation = negotiation;
        _ended = ended;
        _gameTitle = gameTitle;
        _showCard = showCard;
        // Off altogether turns off every pointer this server would draw of its own, the desktop's
        // included: a game's own switch is an override of this, not something beside it.
        _gamePointer = config.VirtualMouse && gamePointer;

        // A client from outside this network is on a link nobody measured, so it is given one level
        // less than the game asks for. Low is the floor: there is nothing below it to drop to.
        var remote = !WebConsole.IsPrivate(negotiation.ClientAddress);
        _quality = remote && gameQuality > StreamQuality.Low ? gameQuality - 1 : gameQuality;

        if (remote)
        {
            Log.Info($"the client is not on this network, so the quality goes from " +
                     $"{gameQuality.ToString().ToLowerInvariant()} to {_quality.ToString().ToLowerInvariant()}");
        }
        _posterPath = posterPath;
        _gameIsUp = gameIsUp ?? (() => true);
        AppId = request.AppId;

        _video = new VideoStream(config.VideoPort, config.BindAddress, negotiation);
        _control = new ControlStream(config.ControlPort, config.BindAddress, request.RiKey);
        _input = new ClientInput(output.Bounds, gamepads);

        _audio = AppParameters.Audio.Enabled
            ? new AudioStream(config.AudioPort, config.BindAddress, AppParameters.Audio.Device,
                              negotiation, request.RiKey, request.RiKeyId)
            : null;

        _control.InputReceived += payload => _input.Handle(payload);

        // A deliberate press means the player wants to see the machine, whatever is loading.
        _input.Pressed += () => _cardDismissed = true;
        _control.IdrRequested += () =>
        {
            _keyFrameWanted = true;
            Interlocked.Increment(ref _keyFramesAsked);
        };

        _control.Terminated += reason => _ended(reason);

        _control.LossReported += (lost, overMs, lastGoodFrame) =>
        {
            if (lost <= 0) return;

            // Occasionally, because the client reports every fifty milliseconds and a lossy
            // minute would be a thousand lines. The numbers are the client's, not this end's.
            Log.WarnOccasionally("client loss",
                $"the client reports {lost} lost packet(s) over {overMs} ms; " +
                $"the last frame it had whole was {lastGoodFrame}");

            Interlocked.Add(ref _packetsLost, lost);
        };


        // Rumble travels back over the same channel. The bus reports a motor in one byte and the
        // protocol carries two: 257 maps 0xFF onto 0xFFFF rather than 0xFF00.
        _rumble = feedback => _control.SendRumble((ushort)feedback.Index,
            (ushort)(feedback.LowFrequencyMotor * 257),
            (ushort)(feedback.HighFrequencyMotor * 257));

        _gamepads.Feedback += _rumble;
    }

    // Kept so that it can be taken off the hub again: the hub outlives every session, and a
    // handler left behind would rumble a client that has gone.
    private readonly Action<GamepadFeedback> _rumble;

    internal void Start()
    {
        // Network input is not "local" to Windows: left alone, the display sleeps and stops
        // composing, and this server would go on sending the same last frame, a freeze.
        Kernel32.SetThreadExecutionState(
            Kernel32.ES_CONTINUOUS | Kernel32.ES_SYSTEM_REQUIRED | Kernel32.ES_DISPLAY_REQUIRED);

        _control.Start();
        _video.Start();
        _audio?.Start();

        _running = true;
        // Above normal so the OS scheduler does not sit on this thread mid frame: VideoStream's
        // own pacer spins rather than sleeps, and a preempted spin reads in the log as a stall.
        _thread = new Thread(CaptureAndEncode) { IsBackground = true, Name = "stream",
                                                 Priority = ThreadPriority.Highest };
        _thread.Start();

        _tray.SetState(Describe());

        // The end of a stream is recorded three ways; without this the log read, with Debug
        // off, as a stream that ended without ever starting.
        Log.Event($"the stream started: {Describe()}");
    }

    // The picture, from the screen to the wire. One frame per tick of the negotiated rate whether
    // or not anything changed: a stream that stops looks to the client like one that broke.
    private void CaptureAndEncode()
    {
        DisplayAdaptation? display = null;
        StartingCard? card = null;
        DesktopDuplicator? duplicator = null;
        ColourConverter? converter = null;
        IVideoEncoder? encoder = null;
        var clock = Stopwatch.StartNew();
        uint frameIndex = 0;

        try
        {
            // HDR only when the client asked and the card/codec (HEVC or AV1) can send ten bits;
            // whether the screen really is in HDR is for the capture below to answer.
            var hdr = _negotiation.HdrRequested &&
                      ((_negotiation.Codec == VideoCodec.Hevc && _capabilities.Hdr) ||
                       (_negotiation.Codec == VideoCodec.Av1 && _capabilities.Av1Hdr));

            // Said here rather than left to the capture to discover, because this is the only
            // place that knows the client asked at all.
            HdrState = DescribeHdr(hdr);
            if (_negotiation.HdrRequested && !hdr) Log.Info($"HDR: {HdrState}");

            // Before any capture: the screen is moved as close to the request as it goes, so its
            // size and rectangle are read afterwards. Scaling is for a desktop stream only.
            var desktop = AppId == AppParameters.Protocol.DesktopAppId;

            // Already done at /launch, before this game read the screen, when the client sent
            // mode/hdrMode there; otherwise done here, same as it always was.
            display = _preAdapted ?? DisplayAdaptation.Apply(_output, _negotiation.Width,
                _negotiation.Height, _negotiation.Fps, hdr, _capabilities.AnyHdr, _config.Adapt,
                scaleForClient: desktop && _config.ScaleDesktop, isGame: !desktop);

            _input.SetScreen(display.Bounds);

            // Into the desktop unless the virtual cursor is off, into a game only when it was
            // marked as needing one: two pointers a step apart is worse than none.
            _drawPointer = (desktop && _config.VirtualMouse) || _gamePointer;

            if (desktop && !_config.VirtualMouse)
            {
                Log.Info("the virtual cursor is turned off, so the desktop stream carries no " +
                         "pointer of this server's drawing");
            }
            else if (!desktop)
            {
                Log.Info(_gamePointer
                    ? "this game is marked as needing a pointer, so one is drawn into its picture"
                    : "this is a game, so the pointer is left to the game to draw");
            }

            duplicator = DesktopDuplicator.Create(_output, _drawPointer, preferHdr: hdr);

            // A machine with no mouse of its own hides the pointer, so it is nudged once here
            // rather than left to the first move the client sends.
            if (_drawPointer) ClientInput.ShowPointer();

            // The screen may have refused HDR, or DXGI handed back the ordinary form: the encoder
            // is told what is in the texture, not what was asked for.
            var tenBit = duplicator.IsHdrDesktop;
            var sentHdr = tenBit;
            if (hdr && !tenBit)
            {
                Log.Warn("high dynamic range was agreed with the client, but the desktop came " +
                         "back in eight bits; the stream is standard range");

                // HDR is turned off again for this stream. That is a mode change, which the
                // duplication does not survive, so it is opened again once the screen settles.
                if (display.DropHdr())
                {
                    duplicator.Dispose();
                    duplicator = OpenCaptureAfterModeChange();
                }
            }

            if (duplicator.Width != _negotiation.Width || duplicator.Height != _negotiation.Height)
            {
                // There is no scaler here: what the card captured is what it encodes. The client
                // reads the real size out of the stream; the line is for the person wondering.
                Log.Warn(
                    $"The client asked for {_negotiation.Width}x{_negotiation.Height} and this\n" +
                    $"screen is {duplicator.Width}x{duplicator.Height}. The screen's own size is\n" +
                    "what is sent: this server does not scale the picture. Set the client's\n" +
                    "resolution to match the screen to avoid the client scaling it instead.");
            }

            try
            {
                // The shader turns the half-float capture into ten-bit P010 only, never 4:4:4, so
                // both asked for together would otherwise mismatch what the driver is fed.
                if (tenBit && _negotiation.Yuv444)
                {
                    Log.Info("4:4:4 colour was asked for together with HDR; HDR sends 4:2:0 " +
                             "chroma only, so this stream keeps 4:2:0");
                }

                var yuv444 = !tenBit && _negotiation.Yuv444;

                if (tenBit)
                {
                    converter = ColourConverter.Open(duplicator.Device, duplicator.Context,
                        duplicator.Width, duplicator.Height, fullRange: false);
                }

                encoder = VideoEncoders.Open(duplicator.Device, _capabilities, _negotiation.Codec,
                    duplicator.Width, duplicator.Height, _negotiation.BitrateKbps,
                    _negotiation.Fps, tenBit, yuv444, _quality);
            }
            catch (Exception error) when (tenBit)
            {
                // Eight bits instead of no stream; the screen leaves HDR too, since half floats
                // are not something the eight-bit encoder can read.
                Log.Warn($"the ten-bit path would not open ({error.Message}); " +
                         "the stream falls back to eight bits");

                converter?.Dispose();
                converter = null;

                if (display.DropHdr())
                {
                    duplicator.Dispose();
                    duplicator = OpenCaptureAfterModeChange();
                }

                encoder = VideoEncoders.Open(duplicator.Device, _capabilities, _negotiation.Codec,
                    duplicator.Width, duplicator.Height, _negotiation.BitrateKbps,
                    _negotiation.Fps, hdr: false, yuv444: _negotiation.Yuv444, quality: _quality);

                sentHdr = false;
            }

            // What is really going out, now that the capture and the encoder have answered. The
            // tray and the page read these rather than the negotiation, which can differ.
            SentWidth = duplicator.Width;
            SentHeight = duplicator.Height;
            SentHdr = sentHdr;
            SentRefreshHz = duplicator.RefreshRate;
            HdrState = DescribeHdr(sentHdr);

            // Made before a single frame has been sent, so the first thing the client ever sees
            // of this machine is the card and not its desktop. It costs one drawing and one copy.
            if (_showCard && _gameTitle is not null && !_gameIsUp())
            {
                card = StartingCard.Create(duplicator.Device, duplicator.Context,
                    duplicator.Width, duplicator.Height, duplicator.FrameFormat,
                    _gameTitle, _posterPath);
            }

            var cardShownAt = clock.Elapsed;
            var frameInterval = TimeSpan.FromSeconds(1.0 / Math.Max(1, _negotiation.Fps));

            // One interval ahead, so that the first turn of the loop waits for a picture rather
            // than sending the empty texture it starts with.
            var nextFrame = clock.Elapsed + frameInterval;

            // What the loop is doing, counted and written out now and then: whether the desktop
            // produced frames and whether anything was sent, which a black screen needs.
            var report = new FrameReport(clock);

            // Whether the client has been told what colour the stream is. Once per stream, after
            // the first frame: see below.
            var hdrAnnounced = false;

            // Consecutive lost duplications with nothing captured between. A mode change costs
            // one; a screen gone for good would otherwise be reopened for ever.
            var lostInARow = 0;

            // When the last key frame went out, for the rationing further down. MinValue rather
            // than zero: the first frame of a stream is a key frame and must not wait for it.
            var lastKeyFrame = TimeSpan.MinValue;

            // When anything at all was last sent, and when the picture last changed, for the
            // still-desktop case below.
            var lastSent = TimeSpan.MinValue;
            var lastChange = TimeSpan.Zero;

            // A turn of this loop is a sixtieth of a second's work; when one takes a second the
            // picture has stopped, and which call it stopped in is the whole diagnosis.
            var stepStarted = clock.Elapsed;

            void Step(string what)
            {
                var taken = clock.Elapsed - stepStarted;
                stepStarted = clock.Elapsed;

                if (taken < SlowStep) return;

                Log.Warn($"the stream stopped for " +
                         $"{taken.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s " +
                         $"in {what}");
            }

            while (_running)
            {
                stepStarted = clock.Elapsed;

                // One capture at least, then as many as fit before the frame is due. The waiting
                // is inside AcquireNextFrame, not a sleep, which cost the stream half its frames.
                CaptureStatus status;
                var deskChanged = false;
                do
                {
                    var untilFrame = nextFrame - clock.Elapsed;
                    var timeout = untilFrame <= TimeSpan.Zero
                        ? 0
                        : (int)Math.Min(AppParameters.Capture.AcquireTimeoutMs,
                                        Math.Ceiling(untilFrame.TotalMilliseconds));

                    status = duplicator.TryCapture(timeout);
                    if (status == CaptureStatus.Frame) deskChanged = true;
                    report.Captured(status, duplicator.LastAccumulatedFrames);
                }
                while (_running && status is CaptureStatus.Frame or CaptureStatus.Idle &&
                       clock.Elapsed < nextFrame);

                Step("the capture");

                if (status == CaptureStatus.Lost)
                {
                    var width = duplicator.Width;
                    var height = duplicator.Height;
                    var format = duplicator.FrameFormat;

                    if (++lostInARow > MaxLostInARow)
                    {
                        Log.Warn(
                            "The desktop has not produced a frame for a long time, however often\n" +
                            "the duplication is opened again. The stream is ending rather than\n" +
                            "sending a still picture that nothing will replace.");
                        _ended("the desktop stopped producing frames");
                        return;
                    }

                    if (!duplicator.Reopen())
                    {
                        Thread.Sleep(AppParameters.Capture.RecreateDelayMs);
                        continue;
                    }

                    // Reopen() always hands back a new frame texture, same size or not; keeping
                    // the old encoder was seen crashing on it once (NV_ENC_ERR_INVALID_PARAM).
                    if (duplicator.Width != width || duplicator.Height != height ||
                        duplicator.FrameFormat != format)
                    {
                        Log.Info($"the screen changed to {duplicator.Width}x{duplicator.Height} " +
                                 $"(format {duplicator.FrameFormat}); the encoder is being rebuilt");
                    }
                    else
                    {
                        Log.Info("the desktop capture was reopened; the encoder is being rebuilt " +
                                 "with it");
                    }

                    encoder.Dispose();
                    encoder = VideoEncoders.Open(duplicator.Device, _capabilities,
                        _negotiation.Codec, duplicator.Width, duplicator.Height,
                        _negotiation.BitrateKbps, _negotiation.Fps,
                        duplicator.FrameFormat == Dxgi.DXGI_FORMAT_R10G10B10A2_UNORM,
                        _negotiation.Yuv444, _quality);
                    _keyFrameWanted = true;

                    continue;
                }

                if (status == CaptureStatus.Unavailable)
                {
                    // The lock screen, a prompt on the secure desktop, or a remote desktop
                    // connection taking the console away. All come back on their own.
                    Log.WarnOccasionally("desktop unavailable",
                        "There is nothing to capture at the moment. That is the lock screen, a\n" +
                        "prompt, or someone having connected to this machine over remote desktop —\n" +
                        "which disconnects the console session and takes the picture with it. The\n" +
                        "stream stays connected and resumes on its own once the console is back.");

                    Thread.Sleep(AppParameters.Capture.RecreateDelayMs);
                    continue;
                }

                lostInARow = 0;

                report.KeyFramesAsked(Interlocked.Exchange(ref _keyFramesAsked, 0));
                report.PacketsLost(Interlocked.Exchange(ref _packetsLost, 0));

                var now = clock.Elapsed;

                // Exactly one interval, so the rate does not drift with the time a frame took; a
                // stream a whole frame behind starts from now rather than catching up in a burst.
                nextFrame += frameInterval;
                if (nextFrame < now) nextFrame = now + frameInterval;

                // Nothing is sent before the client's first ping, because there is nowhere to
                // send it: the ping is what reveals the port its side of the network chose.
                if (!_video.HasPeer) continue;

                // The card stands in for the desktop until the game is on screen, the player
                // presses something, or it has stood long enough that something is wrong.
                if (card is not null)
                {
                    var reason =
                        _gameIsUp() ? "the game is on screen" :
                        _cardDismissed ? "the client pressed something" :
                        clock.Elapsed - cardShownAt > CardPatience ? "the game is taking too long" :
                        null;

                    if (reason is not null)
                    {
                        Log.Info($"the starting card is going away: {reason}");
                        card.Dispose();
                        card = null;

                        // The picture changes completely, so the client needs a frame it can
                        // start again from rather than a difference against a card.
                        _keyFrameWanted = true;
                    }
                    else
                    {
                        card.Update();
                    }
                }

                // The pointer is composited once, for the frame about to go out: drawn while
                // capturing, a move over an unchanged desktop leaves it beside the last one.
                var pointerMoved = card is null && duplicator.DrawPointer();
                Step("the pointer");

                if (deskChanged || pointerMoved) lastChange = now;

                // A desktop nobody has touched is the same picture sixty times a second, and never
                // while it is in use: the rate control expects frames at the rate it was opened with.
                var still = card is null && !deskChanged && !pointerMoved && !_keyFrameWanted &&
                            now - lastChange > StillAfter;

                if (still && now - lastSent < StillFrameEvery) continue;

                lastSent = now;

                // Rationed: a key frame is twenty times the size of an ordinary one, and answering
                // every request filled the line with them and left the stream broken until it closed.
                var wantKeyFrame = _keyFrameWanted &&
                                   (lastKeyFrame == TimeSpan.MinValue ||
                                    now - lastKeyFrame >= BetweenKeyFrames);

                if (wantKeyFrame)
                {
                    _keyFrameWanted = false;
                    lastKeyFrame = now;
                }

                // The captured frame, or the ten-bit conversion of it: an HDR desktop reaches the
                // encoder only through the shader.
                var picture = card?.Texture ?? duplicator.FrameTexture;
                if (converter is not null)
                {
                    // Only ever the desktop's own pointer, and only when this frame is the
                    // desktop's: the starting card draws nothing DrawPointer would recognise.
                    converter.Convert(picture, card is null ? duplicator.CursorOverlay : 0);
                    picture = converter.Output;
                }

                var unit = encoder.Encode(picture, wantKeyFrame);
                Step("the encoder");
                if (unit.IsEmpty) continue;

                frameIndex++;
                _video.SendFrame(unit.Span, wantKeyFrame, frameIndex,
                    (uint)(now.TotalSeconds * RtpClockHz));
                report.Sent(unit.Length, wantKeyFrame, card is not null);
                Step("sending the frame");

                // Once, after the first frame: whether the picture is high dynamic range is not
                // in the stream, and the control channel drops anything sent before it connects.
                if (!hdrAnnounced)
                {
                    hdrAnnounced = true;
                    _control.SendHdrMode(SentHdr, duplicator.Hdr);

                    if (SentHdr)
                    {
                        Log.Info("the client was told the stream is high dynamic range, and what " +
                                 $"this screen shows: up to {duplicator.Hdr.MaxLuminance} nits, " +
                                 $"{duplicator.Hdr.MaxFullFrameLuminance} over a whole white frame");
                    }
                }
            }
        }
        catch (Exception error)
        {
            Log.Error("the stream stopped", error);
            if (_running) _ended("the capture or the encoder failed");
        }
        finally
        {
            card?.Dispose();
            encoder?.Dispose();
            converter?.Dispose();
            duplicator?.Dispose();

            // Last, after the duplication is gone: putting the screen back is itself a mode
            // change, which a live duplication would not survive.
            display?.Dispose();
        }
    }

    // Opens the capture again after a colour-state change, which the desktop is away for: asked a
    // few times over a couple of seconds, and the last refusal is thrown.
    private DesktopDuplicator OpenCaptureAfterModeChange()
    {
        const int attempts = 10;

        for (var attempt = 1; ; attempt++)
        {
            Thread.Sleep(AppParameters.Capture.RecreateDelayMs);

            try
            {
                return DesktopDuplicator.Create(_output, _drawPointer, preferHdr: false);
            }
            catch (Exception error) when (attempt < attempts)
            {
                Log.Info($"the desktop is not back yet after the HDR change ({error.Message}); " +
                         "asking again");
            }
        }
    }

    // What the capture loop did, written under Debug at the first frame and then every ten
    // seconds: frames, bytes, and the split between fresh captures and re-sent still pictures.
    private sealed class FrameReport(Stopwatch clock)
    {
        private static readonly TimeSpan Every = TimeSpan.FromSeconds(10);

        private TimeSpan _since = clock.Elapsed;
        private int _fresh, _idle, _unavailable, _sent, _keyFrames, _asked, _lost;
        private long _composed;
        private long _bytes;
        private bool _first = true;

        internal void Captured(CaptureStatus status, uint accumulated)
        {
            switch (status)
            {
                case CaptureStatus.Frame: _fresh++; break;
                case CaptureStatus.Idle: _idle++; break;
                case CaptureStatus.Unavailable: _unavailable++; break;
            }

            // What the desktop actually composed, which is not what this end captured: the two
            // apart say the picture is slow because this end is late, not because nothing moved.
            _composed += accumulated;
        }

        // Key frames the client asked for, whether or not they were sent: the two numbers apart are
        // how a stream saying "I cannot decode this" is told from one nobody is asking anything of.
        internal void KeyFramesAsked(int count) => _asked += count;

        internal void PacketsLost(int count) => _lost += count;

        internal void Sent(int bytes, bool keyFrame, bool card)
        {
            _sent++;
            _bytes += bytes;
            if (keyFrame) _keyFrames++;

            if (_first)
            {
                _first = false;
                Log.Event($"first frame sent: {bytes} bytes, {(keyFrame ? "a key frame" : "not a key frame")}" +
                         (card ? ", the starting card" : ", the desktop"));
            }

            var elapsed = clock.Elapsed - _since;
            if (elapsed < Every) return;

            var kbit = _bytes * 8 / Math.Max(1.0, elapsed.TotalSeconds) / 1000;
            Log.Info($"stream: {_sent} frames sent in {elapsed.TotalSeconds:0} s " +
                     $"({kbit.ToString("0", CultureInfo.InvariantCulture)} kbit/s, " +
                     $"{_keyFrames} key frame(s)" +
                     (_asked > _keyFrames ? $" of {_asked} asked for" : string.Empty) +
                     (_lost > 0 ? $", {_lost} packet(s) lost on the way" : string.Empty) +
                     $"); the desktop changed {_fresh} times" +
                     (_composed > _fresh ? $" of {_composed} it composed" : string.Empty) +
                     $" and was " +
                     $"idle {_idle} times" +
                     (_unavailable > 0 ? $", unavailable {_unavailable} times" : string.Empty));

            _since = clock.Elapsed;
            _fresh = _idle = _unavailable = _sent = _keyFrames = _asked = _lost = 0;
            _composed = 0;
            _bytes = 0;
        }
    }

    public void Dispose()
    {
        var whole = Stopwatch.StartNew();
        _running = false;

        // Releases the hold Start() put on the display and the system, so this machine sleeps on
        // its own schedule again once nothing is streaming from it.
        Kernel32.SetThreadExecutionState(Kernel32.ES_CONTINUOUS);

        _gamepads.Feedback -= _rumble;
        _input.Release();

        // Never from the capture thread itself, which would be a wait that cannot end.
        if (_thread is not null && _thread != Thread.CurrentThread)
        {
            var joined = _thread.Join(2000);
            if (!joined)
            {
                Log.Warn($"the capture thread did not stop within 2000 ms; its own teardown " +
                         "(the screen restore, most likely) is still running in the background");
            }
            else if (whole.ElapsedMilliseconds > 500)
            {
                Log.Info($"the capture thread stopped in {whole.ElapsedMilliseconds} ms");
            }
        }

        // The control stream last: it is the one that says goodbye, and it should still be able
        // to when the picture has already stopped.
        var audioStarted = whole.ElapsedMilliseconds;
        _audio?.Dispose();
        if (_audio is not null && whole.ElapsedMilliseconds - audioStarted > 500)
            Log.Info($"the sound was put back in {whole.ElapsedMilliseconds - audioStarted} ms");

        _video.Dispose();
        _control.Dispose();

        if (whole.ElapsedMilliseconds > 1000)
            Log.Warn($"ending this stream took {whole.ElapsedMilliseconds} ms in total");
    }
}
