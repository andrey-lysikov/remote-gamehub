//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text;
using RemoteGameHub.App;
using RemoteGameHub.Native;

namespace RemoteGameHub.Session;

// Whether the game asked for is actually running, answered by watching the machine: the command
// handed to the shell returns at once. A full-screen window and a process, and both to finish.
internal sealed class GameWatcher : IDisposable
{
    // How long to wait for the game to appear before saying it probably will not.
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    // Set once the start timeout has passed with nothing found: a pass of the process table is a
    // thousand kernel calls, and a windowed game would pay it twice a second for the whole stream.
    private bool _sweptEnough;

    // How long everything must stay quiet before the game counts as finished. Long while it is
    // young, because a launcher hands over seconds after its own process exits; short after that.
    private static readonly TimeSpan QuietWhileYoung = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan QuietWhenSettled = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan SettledAfter = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    // The shell's own windows, which formally cover the screen and are not games. Copied from
    // System-Spinner, where the list was arrived at by finding out.
    private static readonly string[] ShellClasses =
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow",
    };

    private readonly int _appId;
    private readonly string _title;
    private readonly string? _installPath;
    private readonly Action<int>? _finished;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Thread _thread;

    private Process? _game;

    // The identifier /serverinfo reports while this game is up.
    internal int AppId => _appId;

    // true from the moment the game is first seen until it is gone.
    internal bool IsRunning { get; private set; }

    // true once the game has been seen at all, however briefly.
    internal bool HasStarted { get; private set; }

    // true once the game has actually taken the screen — stricter than HasStarted, and asked by
    // the starting card alone: a process can be up a minute before anything is drawn.
    internal bool IsOnScreen { get; private set; }

    private GameWatcher(int appId, string title, string? installPath, Action<int>? finished)
    {
        _appId = appId;
        _title = title;
        _installPath = installPath;
        _finished = finished;

        _thread = new Thread(Watch) { IsBackground = true, Name = "game-watch" };
    }

    // Begins watching. Returns at once: the answer arrives in the log and through finished,
    // because /launch has to be replied to long before a game of any size has loaded.
    internal static GameWatcher Start(int appId, string title, string? installPath,
                                      Action<int>? finished = null)
    {
        var watcher = new GameWatcher(appId, title, installPath, finished);
        watcher._thread.Start();
        return watcher;
    }

    private void Watch()
    {
        var waiting = Stopwatch.StartNew();
        var quiet = new Stopwatch();
        var up = new Stopwatch();
        var warned = false;

        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                var alive = Look(out var onScreen);

                if (onScreen) IsOnScreen = true;

                if (alive)
                {
                    quiet.Reset();

                    if (!HasStarted)
                    {
                        HasStarted = true;
                        IsRunning = true;
                        up.Restart();
                        Log.Event($"\"{_title}\" is up after {waiting.Elapsed.TotalSeconds:0.0} s" +
                                 (_game is not null ? $" ({_game.ProcessName})" : string.Empty));
                    }
                }
                else if (HasStarted)
                {
                    // Nothing found, but the game was here. It may be between levels or changing
                    // resolution, so the clock has to run out before this counts as an exit.
                    if (!quiet.IsRunning) quiet.Restart();

                    var patience = up.Elapsed >= SettledAfter ? QuietWhenSettled : QuietWhileYoung;

                    if (quiet.Elapsed >= patience)
                    {
                        IsRunning = false;
                        IsOnScreen = false;
                        Log.Event($"\"{_title}\" has finished");
                        _finished?.Invoke(_appId);
                        return;
                    }
                }
                else if (!warned && waiting.Elapsed >= StartTimeout)
                {
                    warned = true;

                    // And stop walking the process table: a minute of it has found nothing, and
                    // another hour of it would find nothing either.
                    _sweptEnough = true;
                    Log.Warn(
                        $"\"{_title}\" was asked to start {StartTimeout.TotalSeconds:0} seconds ago\n" +
                        "and nothing full-screen has appeared. The command was accepted, so the\n" +
                        "usual reasons are a game that is still installing or updating, a store\n" +
                        "that wants someone to sign in, or a launcher waiting on this machine's\n" +
                        "own screen. The stream keeps running and shows whatever is there.");
                }

                _stopping.Token.WaitHandle.WaitOne(PollInterval);
            }
        }
        catch (Exception error)
        {
            // Watching must never be the thing that ends a stream.
            Log.Info($"the watch on \"{_title}\" stopped: {error.GetType().Name}: {error.Message}");
        }
    }

    // One look at the machine. true when the game appears to be there by either signal;
    // onScreen only when it was the full-screen window that said so.
    private bool Look(out bool onScreen)
    {
        onScreen = false;

        // The process first: it is cheap, and it keeps answering while the player has alt-tabbed
        // out of a game that is still running.
        if (_game is not null)
        {
            try
            {
                // The window is still asked about although the process has answered: it is the
                // only signal that says the game is drawing rather than merely running.
                if (!_game.HasExited)
                {
                    onScreen = TryFullscreenWindow(out _);
                    return true;
                }
            }
            catch (Exception)
            {
                // The handle went bad; fall through to looking for the game again.
            }

            _game.Dispose();
            _game = null;
        }

        if (!TryFullscreenWindow(out var processId)) return FoundByInstallPath();

        onScreen = true;

        // A full-screen window is the game. Remember what is behind it, whatever it is called:
        // the name is exactly the thing that cannot be relied on.
        if (processId != 0 && _game is null)
        {
            try
            {
                _game = Process.GetProcessById((int)processId);
            }
            catch (Exception)
            {
                // It may already be gone, or be a process this one may not open. The window
                // itself is signal enough for this poll.
            }
        }

        return true;
    }

    // Whether the foreground window covers its whole monitor and is not one of the shell's.
    // This is System-Spinner's TryFullscreenArea, reduced to the question asked here.
    private static bool TryFullscreenWindow(out uint processId)
    {
        processId = 0;

        var window = User32.GetForegroundWindow();
        if (window == 0) return false;

        var className = new StringBuilder(64);
        if (User32.GetClassName(window, className, className.Capacity) > 0)
        {
            var name = className.ToString();
            foreach (var shell in ShellClasses)
            {
                if (string.Equals(name, shell, StringComparison.Ordinal)) return false;
            }
        }

        if (!User32.GetWindowRect(window, out var bounds)) return false;

        var monitor = User32.MonitorFromWindow(window, User32.MONITOR_DEFAULTTONEAREST);
        var info = new User32.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<User32.MonitorInfo>() };
        if (!User32.GetMonitorInfo(monitor, ref info)) return false;

        // Not "equal" but "no smaller": some games make the window a pixel larger than the screen.
        var screen = info.Monitor;
        var covered = bounds.Left <= screen.Left && bounds.Top <= screen.Top &&
                      bounds.Right >= screen.Right && bounds.Bottom >= screen.Bottom;

        if (!covered) return false;

        User32.GetWindowThreadProcessId(window, out processId);
        return true;
    }

    // The second way in: a process running from the folder the game was installed into, which
    // catches a game up but not yet on screen. Only asked before the game has been seen.
    private bool FoundByInstallPath()
    {
        if (HasStarted || _installPath is null || _sweptEnough) return false;

        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is not null &&
                        path.StartsWith(_installPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _game = process;
                        return true;
                    }
                }
                catch (Exception)
                {
                    // Plenty of processes will not say where they came from, even to an
                    // administrator. They are simply not the one being looked for.
                }

                process.Dispose();
            }
        }
        catch (Exception)
        {
            // Enumerating processes can fail while the machine is busy starting one.
        }

        return false;
    }

    // Closes the game, because a client asked to: politely first, then by force — but only when
    // the process runs from the game's own folder, since it can be the store's client instead.
    internal void StopGame()
    {
        var game = _game;
        if (game is null)
        {
            Log.Event($"\"{_title}\" was not identified as a process, so nothing was closed");
            return;
        }

        try
        {
            if (game.HasExited) return;

            var itIsCertainlyTheGame = IsUnderInstallPath(game);

            if (game.CloseMainWindow())
            {
                game.WaitForExit(5000);
                if (game.HasExited)
                {
                    Log.Event($"\"{_title}\" was asked to close and did");
                    return;
                }
            }

            if (!itIsCertainlyTheGame)
            {
                Log.Warn($"\"{_title}\" did not close when asked. It is not being forced, " +
                         "because this server cannot be certain the process it found is the " +
                         "game rather than the store's own client.");
                return;
            }

            game.Kill(entireProcessTree: true);
            Log.Event($"\"{_title}\" did not close when asked and was ended");
        }
        catch (Exception error)
        {
            Log.Warn($"\"{_title}\" could not be closed: {error.Message}");
        }
    }

    private bool IsUnderInstallPath(Process process)
    {
        if (_installPath is null) return false;

        try
        {
            var path = process.MainModule?.FileName;
            return path is not null &&
                   path.StartsWith(_installPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();

        if (_thread != Thread.CurrentThread) _thread.Join(1000);

        _game?.Dispose();
        _game = null;
        _stopping.Dispose();
    }
}
