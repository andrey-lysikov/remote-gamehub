//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Runtime.InteropServices;
using RemoteGameHub.Native;

namespace RemoteGameHub.App;

// The LocalSystem half: draws nothing, streams nothing, holds no ports. It keeps one copy of the
// server running as SYSTEM where the person is, which is what lets it capture the secure desktop.
internal static class ServiceHost
{
    // Held in static fields so the garbage collector does not move or collect the delegates while
    // Windows holds pointers to them.
    private static Advapi32ServiceMain? _serviceMain;
    private static Advapi32Handler? _handler;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Advapi32ServiceMain(uint argc, nint argv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint Advapi32Handler(uint control, uint eventType, nint eventData, nint context);

    private static nint _statusHandle;
    private static ServiceStatus _status;

    private static readonly ManualResetEvent Stopping = new(false);

    // Raised by the control handler on any session change. It only wakes the supervisor early;
    // what the supervisor then finds is what decides, and most session changes decide nothing.
    private static readonly AutoResetEvent SessionChanged = new(false);

    // The two the supervisor sleeps on between its turns, in this order: the stop is checked first
    // because a session change arriving with it is of no interest.
    private static readonly WaitHandle[] Wakes = { Stopping, SessionChanged };

    // The server as SYSTEM on the console. Zero while nobody is signed in to start it for.
    private static nint _worker;

    // The worker's own number, kept beside the handle: the handle says nothing a person can look
    // up afterwards, and a worker that died is named by its number in every other log there is.
    private static uint _workerPid;

    // Why this service is stopping, said by the control handler and read where the worker is
    // ended: a shutdown ends the worker on its own, and that is not the same as a fault.
    private static string _stopReason = "the service was asked to stop";

    // How long the worker may take to close itself before it is ended: enough for the listeners
    // and the stream, short enough not to hold up a shutdown. WAIT_OBJECT_0 (zero) means it left.
    private const uint GracefulExitMs = 4000;

    // Enters the service control dispatcher and does not return until the service is stopped. The
    // one path that reaches here is the exe started by the service control manager with --service.
    internal static int Run()
    {
        _serviceMain = ServiceMain;

        var table = new[]
        {
            new ServiceTableEntry
            {
                Name = Marshal.StringToHGlobalUni(AppParameters.Identity.ServiceName),
                Proc = Marshal.GetFunctionPointerForDelegate(_serviceMain),
            },
            new ServiceTableEntry { Name = 0, Proc = 0 },
        };

        if (!Advapi32.StartServiceCtrlDispatcher(table))
        {
            // The usual reason is being run from a console rather than by the service control
            // manager. Said plainly, because it is what a person testing by hand will hit.
            Log.Error($"this copy was started with --service but not by the service control " +
                      $"manager (Win32 {Marshal.GetLastWin32Error()}). Install and start the " +
                      $"service instead: \"{Environment.ProcessPath}\" install-service");
            return 1;
        }

        return 0;
    }

    private static void ServiceMain(uint argc, nint argv)
    {
        _handler = Handler;
        _statusHandle = Advapi32.RegisterServiceCtrlHandlerEx(
            AppParameters.Identity.ServiceName,
            Marshal.GetFunctionPointerForDelegate(_handler), 0);

        if (_statusHandle == 0)
        {
            Log.Error($"the service control handler could not be registered (Win32 " +
                      $"{Marshal.GetLastWin32Error()})");
            return;
        }

        _status = new ServiceStatus
        {
            ServiceType = Advapi32.SERVICE_WIN32_OWN_PROCESS,
            ControlsAccepted = Advapi32.SERVICE_ACCEPT_STOP | Advapi32.SERVICE_ACCEPT_SHUTDOWN |
                               Advapi32.SERVICE_ACCEPT_POWEREVENT |
                               Advapi32.SERVICE_ACCEPT_SESSIONCHANGE,
        };

        Report(Advapi32.SERVICE_START_PENDING, waitHintMs: 3000);
        SessionLauncher.EnablePrivileges();
        Report(Advapi32.SERVICE_RUNNING);

        Log.Event("the service started; it will keep the server running as SYSTEM on the console. " +
                  $"The console is session {Wtsapi32.WTSGetActiveConsoleSessionId()}, and this " +
                  $"service runs in session " +
                  (Kernel32.ProcessIdToSessionId((uint)Environment.ProcessId, out var own)
                      ? own.ToString()
                      : "unknown"));

        try
        {
            Supervise();
        }
        catch (Exception error)
        {
            Log.Crash("the service supervisor stopped", error);
        }
        finally
        {
            StopWorker();
            Report(Advapi32.SERVICE_STOPPED);
            Log.Event("the service stopped");
        }
    }

    // Keeps one worker alive in the session somebody is looking at, moving it only when the person
    // is elsewhere. Not by console number: remote desktop hands the console to an empty sign-in screen.
    private static void Supervise()
    {
        // The session the worker was started in; NoSession while there is no worker.
        var workerSession = Wtsapi32.NoSession;

        // When the worker was last started and when it ended. The wait before the next start is
        // measured from the end: from the start, one that ran for hours has always waited enough.
        var startedAt = DateTime.MinValue;
        var endedAt = DateTime.MinValue;
        var backoff = TimeSpan.Zero;
        var maximumBackoff = TimeSpan.FromMinutes(1);

        // Settling time after any death: a sign-out or a shutdown ends the worker seconds before
        // Windows admits the session is going, and one started into those seconds dies at once.
        var settle = TimeSpan.FromSeconds(3);

        // Quick exits in a row. A refusal will not change by being asked again, so after this many
        // the service stops itself; the worker's log has the reason and a new start tries again.
        const int GiveUpAfter = 5;
        var failures = 0;

        // What was last said about whether there is a session to stream for, so that the answer
        // is written when it changes and not once a second for as long as it does not.
        var wasReady = false;
        var lastWhy = string.Empty;

        while (!Stopping.WaitOne(0))
        {
            var console = Wtsapi32.WTSGetActiveConsoleSessionId();
            var target = ChooseSession(console, out var why);

            if (_worker != 0 && IsAlive(_worker))
            {
                if (target != Wtsapi32.NoSession && target != workerSession)
                {
                    // The person really is elsewhere now: another account took the console, or the
                    // console came back while the worker sat in a session they left.
                    Log.Event($"somebody is signed in and looking at session {target} (the " +
                              $"console is session {console}); the worker in session " +
                              $"{workerSession} is being moved there");
                    StopWorker();
                    workerSession = Wtsapi32.NoSession;

                    // Whatever went wrong in the old session says nothing about this one.
                    failures = 0;
                    backoff = TimeSpan.Zero;
                    startedAt = DateTime.MinValue;
                }
                else if (target == Wtsapi32.NoSession)
                {
                    // Nobody is looking at anything (remote desktop closed, or the sign-in screen).
                    // The worker stays for the person's return, unless they signed out of it.
                    var state = Wtsapi32.StateOf(workerSession);
                    if (state is null || !SignedInQuietly(workerSession))
                    {
                        Log.Event($"session {workerSession}, where the worker runs, is " +
                                  $"{Wtsapi32.DescribeState(state)} and nobody is signed in to " +
                                  "it any more; the worker is being stopped");
                        StopWorker();
                        workerSession = Wtsapi32.NoSession;
                    }
                }
            }

            if (_worker != 0 && !IsAlive(_worker))
            {
                var lived = DateTime.UtcNow - startedAt;
                var quickly = lived < TimeSpan.FromSeconds(20);

                // Read before the handle goes: the code and the state of the session it ran in
                // are the whole of the difference between a sign-out and a crash.
                var code = ExitCodeOf(_worker);
                var state = Wtsapi32.DescribeState(Wtsapi32.StateOf(workerSession));
                var signedIn = workerSession != Wtsapi32.NoSession && SignedInQuietly(workerSession);
                var pid = _workerPid;

                endedAt = DateTime.UtcNow;
                backoff = quickly
                    ? TimeSpan.FromSeconds(Math.Min(maximumBackoff.TotalSeconds,
                                                    Math.Max(2, backoff.TotalSeconds * 2)))
                    : settle;

                failures = quickly ? failures + 1 : 0;

                Kernel32.CloseHandle(_worker);
                _worker = 0;
                _workerPid = 0;

                // Said before anything is decided, so that it is written even when what follows
                // is the service giving up: this is the one record of how the worker ended.
                if (code == DbgTerminateProcess)
                {
                    // Windows ending the session's processes: a sign-out or shutdown, not a fault.
                    // The session still reads as signed in; the news arrives after the worker is gone.
                    Log.Event(
                        $"the worker (pid {pid}) ended after {lived.TotalSeconds:0} s, " +
                        $"{DescribeExit(code)}: a sign-out or a shutdown, not a fault");
                }
                else
                {
                    Log.Warn(
                        $"the worker (pid {pid}) ended after {lived.TotalSeconds:0} s, " +
                        $"{DescribeExit(code)}.\n" +
                        $"The session it ran in ({workerSession}) is {state} and " +
                        (signedIn ? "somebody is still signed in to it" : "nobody is signed in to it") +
                        ".\n" +
                        (signedIn
                            ? "Its own lines above say why it stopped, if it was able to say."
                            : "A worker that ends as its session does was ended by Windows with the " +
                              "session, which is\nordinary: a sign-out or a shutdown reaches it before " +
                              "it reaches this service."));
                }

                workerSession = Wtsapi32.NoSession;

                if (failures >= GiveUpAfter)
                {
                    Log.Warn(
                        $"The server would not stay running: {failures} attempts, each of them " +
                        "over in seconds.\n" +
                        "The service is stopping rather than starting it again and again. Why it " +
                        "refuses is in this log, on the lines the server wrote.\n" +
                        "Starting the application again is what tries once more.");

                    Stopping.Set();
                    break;
                }

                Log.Info($"the worker will be started again in {backoff.TotalSeconds:0} s, if " +
                         "there is still somebody to start it for");
            }

            // Whether there is anybody to stream for, said only when the answer changes and never
            // while a worker runs: "nothing to start for" would read as though there were no server.
            var ready = target != Wtsapi32.NoSession;

            if (_worker == 0 && (ready != wasReady || (!ready && why != lastWhy)))
            {
                wasReady = ready;
                lastWhy = why;

                Log.Event(ready
                    ? (target == console
                          ? $"session {target} is the console and somebody is signed in to it; " +
                            "the server belongs there"
                          : $"session {target} is a remote desktop session somebody is signed in " +
                            $"to, and the console (session {console}) has nobody; the server " +
                            "belongs with the person")
                    : $"nothing to start the server for: {why}. The service waits, and starts it " +
                      "as soon as that changes.");
            }

            // The target is the one read at the top of this turn, so a settled wait ends on a
            // fresh answer: by then a session that was going has gone, and nothing is started.
            if (_worker == 0 && ready && DateTime.UtcNow - endedAt >= backoff)
            {
                startedAt = DateTime.UtcNow;
                FollowSession(target);
                _worker = SessionLauncher.StartServerAsSystem(target, "--worker");
                _workerPid = _worker == 0 ? 0 : Kernel32.GetProcessId(_worker);
                workerSession = _worker == 0 ? Wtsapi32.NoSession : target;

                if (_worker == 0)
                {
                    // Nothing to do but wait and try again; a tight retry would fill the log. The
                    // wait runs from here, since no worker ran to end and set the mark itself.
                    endedAt = DateTime.UtcNow;
                    backoff = TimeSpan.FromSeconds(Math.Min(maximumBackoff.TotalSeconds,
                                                            Math.Max(2, backoff.TotalSeconds * 2)));
                }
                else
                {
                    backoff = TimeSpan.Zero;
                }

                // A worker that could not be launched at all counts the same as one that would not
                // stay: the reason is on this side rather than in the server, but it is as final.
                if (_worker == 0 && ++failures >= GiveUpAfter)
                {
                    Log.Warn($"The server could not be started {failures} times running. The " +
                             "service is stopping; the reason is in the lines above.");

                    Stopping.Set();
                    break;
                }
            }

            // A turn a second, sooner when a session moves. Most moves (lock, unlock, a prompt)
            // change nothing, so the answer is re-read rather than acted on — no needless restarts.
            WaitHandle.WaitAny(Wakes, 1000);
        }
    }

    // Moves this log into the profile of the person signed in to the session the worker is about
    // to serve: the worker writes there too, so the two halves land in one file.
    private static void FollowSession(uint session)
    {
        var profile = UserContext.LocalAppDataOf(session);
        if (profile is null ||
            string.Equals(profile, AppConfig.ProfileDirectoryOverride, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        AppConfig.ProfileDirectoryOverride = profile;
        Log.MoveTo(AppConfig.ResolveDirectory(), AppConfig.FallbackDirectory,
                   $"the service's log continues in the profile of the person signed in to " +
                   $"session {session}");
    }

    // The session the server belongs in now, or NoSession with the reason: the console when
    // somebody is signed in to it, else an active remote desktop session. Never 0 or a left one.
    private static uint ChooseSession(uint console, out string why)
    {
        if (console == Wtsapi32.NoSession)
        {
            why = "no session is attached to the console";
        }
        else if (SomeoneSignedIn(console, out why))
        {
            return console;
        }

        foreach (var (id, state) in Wtsapi32.Sessions())
        {
            if (id == 0 || id == console || state != Wtsapi32.WTSActive) continue;
            if (!SignedInQuietly(id)) continue;

            why = string.Empty;
            return id;
        }

        why += ", and nobody is signed in over remote desktop";
        return Wtsapi32.NoSession;
    }

    // Whether somebody is signed in to the session, with no comment when it cannot be asked: this
    // is put to every session once a second, and the console's own check does say so.
    private static bool SignedInQuietly(uint session)
    {
        if (!Wtsapi32.WTSQueryUserToken(session, out var token)) return false;

        Kernel32.CloseHandle(token);
        return true;
    }

    // ERROR_NO_TOKEN. What Windows answers about a session nobody has signed in to, which is the
    // sign-in screen and the one case where waiting is the right thing to do.
    private const int ErrorNoToken = 1008;

    private static bool SomeoneSignedIn(uint session, out string why)
    {
        why = string.Empty;

        if (Wtsapi32.WTSQueryUserToken(session, out var token))
        {
            Kernel32.CloseHandle(token);
            return true;
        }

        var error = Marshal.GetLastWin32Error();

        if (error == ErrorNoToken)
        {
            why = $"nobody is signed in to session {session}";
            return false;
        }

        // Anything else is a fault on this side (the privilege not switched on, most likely) and
        // says nothing about the session, so the launch goes ahead and reports for itself.
        Log.Warn($"whether anybody is signed in to session {session} could not be established: " +
                 $"{new Win32Exception(error).Message} (Win32 {error}). " +
                 "The server is started anyway.");

        return true;
    }

    private static bool IsAlive(nint process) =>
        Kernel32.GetExitCodeProcess(process, out var code) && code == Kernel32.STILL_ACTIVE;

    private static void StopWorker()
    {
        if (_worker == 0) return;

        // Asked first, so it leaves the way Quit does: the stream ended and the screen put back.
        // Killed outright, it leaves the desktop at whatever size and scale the client asked for.
        WorkerStop.Ask();

        // A moment to leave on its own: asked, or already on its way out after Quit or a shutdown.
        // Only one still there afterwards is ended; it has no window.
        if (Kernel32.WaitForMultipleObjects(1, new[] { _worker }, true, GracefulExitMs) != 0)
        {
            // Said here because the worker cannot say it: ended from outside, its own log stops
            // at whatever it wrote last, which reads as a crash to anybody who finds it.
            Log.Event($"the worker (pid {_workerPid}) had not left on its own after " +
                      $"{GracefulExitMs / 1000} s and is being ended by the service, because " +
                      $"{_stopReason}");
            Kernel32.TerminateProcess(_worker, 0);
            Kernel32.WaitForMultipleObjects(1, new[] { _worker }, true, 5000);
        }

        Kernel32.CloseHandle(_worker);
        _worker = 0;
        _workerPid = 0;
    }

    // The code Windows ends a process with when it ends the session the process runs in.
    private const uint DbgTerminateProcess = 0x40010004;

    // What a process's exit code was, or zero when it cannot be read. Windows puts the reason a
    // process was ended here, and the reasons are distinguishable — see DescribeExit.
    private static uint ExitCodeOf(nint process) =>
        Kernel32.GetExitCodeProcess(process, out var code) ? code : 0;

    // The exit code in words. The high values are NTSTATUS: Windows ends a process with one of
    // these rather than with a number the process chose, and each names a different morning.
    private static string DescribeExit(uint code) => code switch
    {
        0 => "having closed itself (exit code 0)",
        1 => "refusing to start (exit code 1 — its own lines say what it could not open)",
        DbgTerminateProcess =>"because Windows ended it with the session it was in (DBG_TERMINATE_PROCESS)",
        0xC000013A => "because it was asked to close and did not (CTRL_C / STATUS_CONTROL_C_EXIT)",
        0xC0000005 => "by crashing on a bad memory access (STATUS_ACCESS_VIOLATION)",
        0xC0000409 => "by crashing with a corrupted stack (STATUS_STACK_BUFFER_OVERRUN)",
        0xC0000374 => "by crashing with a corrupted heap (STATUS_HEAP_CORRUPTION)",
        0xC00000FD => "by crashing out of stack (STATUS_STACK_OVERFLOW)",
        0xE0434352 => "on an exception nothing caught (a .NET exception; the stack is above)",
        _ when code >= 0xC0000000 => $"by crashing (exit code 0x{code:X8})",
        _ => $"with exit code {code} (0x{code:X8})",
    };

    private static uint Handler(uint control, uint eventType, nint eventData, nint context)
    {
        switch (control)
        {
            case Advapi32.SERVICE_CONTROL_STOP:
            case Advapi32.SERVICE_CONTROL_SHUTDOWN:
                _stopReason = control == Advapi32.SERVICE_CONTROL_SHUTDOWN
                    ? "Windows is shutting this machine down"
                    : "the service was asked to stop";

                Log.Event($"the service was told to stop: {_stopReason}");
                Report(Advapi32.SERVICE_STOP_PENDING, waitHintMs: 6000);
                Stopping.Set();
                return Advapi32.NO_ERROR;

            case Advapi32.SERVICE_CONTROL_SESSIONCHANGE:
                // Not acted on: the supervisor asks Windows where the console is and decides from that.
                // Logged as the only record of what moved and when, to explain a worker that ends.
                Log.Event($"session {Wtsapi32.SessionOf(eventData)}: " +
                          Wtsapi32.DescribeChange(eventType));

                SessionChanged.Set();
                return Advapi32.NO_ERROR;

            case Advapi32.SERVICE_CONTROL_POWEREVENT:
                // Not acted on: the worker hears it too and ends its own stream. Logged so the hours
                // either side of a sleep do not read as one unbroken run.
                Log.Event(Advapi32.DescribePowerEvent(eventType));

                // A wake can move the console (remote desktop wakes machines), so the supervisor
                // looks now rather than in a second. A suspend costs one harmless turn.
                SessionChanged.Set();
                return Advapi32.NO_ERROR;

            case Advapi32.SERVICE_CONTROL_INTERROGATE:
                Report(_status.CurrentState);
                return Advapi32.NO_ERROR;

            default:
                return Advapi32.ERROR_CALL_NOT_IMPLEMENTED;
        }
    }

    private static void Report(uint state, uint waitHintMs = 0)
    {
        _status.CurrentState = state;
        _status.WaitHint = waitHintMs;
        _status.CheckPoint = state is Advapi32.SERVICE_START_PENDING or Advapi32.SERVICE_STOP_PENDING
            ? _status.CheckPoint + 1
            : 0;

        if (_statusHandle != 0) Advapi32.SetServiceStatus(_statusHandle, ref _status);
    }
}
