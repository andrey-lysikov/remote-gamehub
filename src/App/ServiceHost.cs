//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Runtime.InteropServices;
using RemoteGameHub.Native;

namespace RemoteGameHub.App;

// The LocalSystem half. It draws nothing, streams nothing and holds no ports: its whole job is to
// keep one copy of the server running as SYSTEM on the console session, and to move it when the
// console does. Running the server as SYSTEM is what lets it capture and type into the secure
// desktop — the UAC prompt and the lock screen — which an ordinary elevated copy cannot touch.
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

    // How long the worker is given to close itself down before it is ended. Long enough for the
    // listeners and the stream to be let go of, short enough that Windows shutting down does not
    // wait on this service. WAIT_OBJECT_0 is zero, which is what a worker that left answers with.
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

    // Keeps exactly one worker alive on the console session for as long as somebody is signed in,
    // and moves it when the console session changes. A poll rather than a wait on handles: the set
    // of things to watch — the worker, the stop request, the session — is small and changes slowly.
    private static void Supervise()
    {
        var currentSession = Wtsapi32.NoSession;

        // When the worker was last started, and how long to wait before starting it again. A
        // worker that refuses to run — no screen, no encoder, a configuration it will not read —
        // exits in under a second, and starting it once a second would fill the log and the
        // process table with the same refusal. Each quick exit doubles the wait, up to a minute.
        var startedAt = DateTime.MinValue;
        var backoff = TimeSpan.Zero;
        var maximumBackoff = TimeSpan.FromMinutes(1);

        // Quick exits in a row. A worker that refuses to run refuses for a reason that will not
        // change by being asked again — no screen to capture, no encoder, a configuration file
        // with a mistake in it — and it says so in a balloon every time. After this many the
        // service stops itself rather than showing that balloon for ever; the worker's own log
        // has the reason, and starting the application again is what tries once more.
        const int GiveUpAfter = 5;
        var failures = 0;

        // What was last said about whether there is a session to stream for, so that the answer
        // is written when it changes and not once a second for as long as it does not.
        var wasReady = false;
        var lastWhy = string.Empty;

        while (!Stopping.WaitOne(0))
        {
            var console = Wtsapi32.WTSGetActiveConsoleSessionId();

            // A different session really is a different desktop, and the worker on the old one can
            // capture nothing. Fast user switching and a remote desktop connection both land here.
            if (console != currentSession)
            {
                if (_worker != 0)
                {
                    Log.Event($"the console session changed from {currentSession} to {console}; " +
                              "the worker is being moved to it");
                    StopWorker();
                }

                currentSession = console;

                // Whatever went wrong in the old session says nothing about this one.
                failures = 0;
                backoff = TimeSpan.Zero;
                startedAt = DateTime.MinValue;
            }

            if (_worker != 0 && !IsAlive(_worker))
            {
                var lived = DateTime.UtcNow - startedAt;
                var quickly = lived < TimeSpan.FromSeconds(20);

                backoff = quickly
                    ? TimeSpan.FromSeconds(Math.Min(maximumBackoff.TotalSeconds,
                                                    Math.Max(2, backoff.TotalSeconds * 2)))
                    : TimeSpan.Zero;

                failures = quickly ? failures + 1 : 0;

                Kernel32.CloseHandle(_worker);
                _worker = 0;

                if (failures >= GiveUpAfter)
                {
                    Log.Warn(
                        $"The server would not stay running: {failures} attempts, each of them " +
                        "over in seconds.\n" +
                        "The service is stopping rather than starting it again and again. Why it " +
                        "refuses is in its own log, which is beside its configuration file.\n" +
                        "Starting the application again is what tries once more.");

                    Stopping.Set();
                    break;
                }

                Log.Warn($"the worker ended after " +
                         $"{lived.TotalSeconds:0} s; it will be started again" +
                         (backoff > TimeSpan.Zero ? $" in {backoff.TotalSeconds:0} s" : " now") +
                         ". Its own log says why it stopped.");
            }

            // Whether there is anybody to stream for. Said only when the answer changes: this
            // runs once a second, and a machine sitting at its sign-in screen would otherwise
            // write a line a second all night.
            // Written out rather than short-circuited, so that the reason is always set: the two
            // ways of having nothing to start the server for read very differently in a log.
            var ready = false;
            string why;

            if (console == Wtsapi32.NoSession)
            {
                why = "no session is attached to the console";
            }
            else
            {
                ready = SomeoneSignedIn(console, out why);
            }

            if (ready != wasReady || (!ready && why != lastWhy))
            {
                wasReady = ready;
                lastWhy = why;

                Log.Event(ready
                    ? $"session {console} is the console and somebody is signed in to it; " +
                      "the server belongs there"
                    : $"nothing to start the server for: {why}. The service waits, and starts it " +
                      "as soon as that changes.");
            }

            if (_worker == 0 && ready && DateTime.UtcNow - startedAt >= backoff)
            {
                startedAt = DateTime.UtcNow;
                _worker = SessionLauncher.StartServerAsSystem(console, "--worker");

                if (_worker == 0)
                {
                    // Nothing to do but wait and try again; a tight retry would fill the log.
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
                             "service is stopping; this service's own log has the reason.");

                    Stopping.Set();
                    break;
                }
            }

            // A turn a second, and sooner when Windows says a session moved. Most of what raises
            // that changes nothing here — locking the screen, unlocking it, answering a prompt all
            // leave the console where it was — which is why the answer above is read again rather
            // than acted on: restarting the worker for those would break a stream at exactly the
            // moments this service exists to carry it through.
            WaitHandle.WaitAny(Wakes, 1000);
        }
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

        // Anything else is a fault on this side rather than an empty session — the privilege this
        // call needs not switched on, most likely — and it says nothing about whether there is
        // somebody there. Answering "no" to it would leave the server unstarted for ever over a
        // question that was never really asked, so the launch goes ahead and reports for itself.
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

        // Given a moment to leave on its own first. Quit in its tray menu is the ordinary way this
        // service is stopped at all: the worker shuts its listeners and its session down, asks the
        // service to stop, and is already on its way out by the time this runs. Windows shutting
        // down reaches here the same way. Only a worker that is still there afterwards is ended,
        // which is the case this cannot be polite about: it has no window to close.
        if (Kernel32.WaitForMultipleObjects(1, new[] { _worker }, true, GracefulExitMs) != 0)
        {
            Kernel32.TerminateProcess(_worker, 0);
            Kernel32.WaitForMultipleObjects(1, new[] { _worker }, true, 5000);
        }

        Kernel32.CloseHandle(_worker);
        _worker = 0;
    }

    private static uint Handler(uint control, uint eventType, nint eventData, nint context)
    {
        switch (control)
        {
            case Advapi32.SERVICE_CONTROL_STOP:
            case Advapi32.SERVICE_CONTROL_SHUTDOWN:
                Report(Advapi32.SERVICE_STOP_PENDING, waitHintMs: 6000);
                Stopping.Set();
                return Advapi32.NO_ERROR;

            case Advapi32.SERVICE_CONTROL_SESSIONCHANGE:
                // Every reason is treated the same, and none of them is acted on here: the
                // supervisor asks Windows which session the console is now and decides from that.
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
