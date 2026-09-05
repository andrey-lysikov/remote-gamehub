//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using RemoteGameHub.Native;

namespace RemoteGameHub.App;

// The service, from the application's own side: made if it is not there, pointed at this copy if it
// is there but stale, started when the application starts and stopped when it exits. Nobody has to
// type anything — this server already runs with administrator rights, which is all that installing
// a service needs.
//
// Through the service control API rather than sc.exe: sc prints in whatever language Windows was
// installed in, and reading a service's state out of translated text is not something to build on.
internal static class ServiceControl
{
    private const string Name = AppParameters.Identity.ServiceName;

    // How long to wait for the service to reach the state that was asked for. Starting means
    // loading this same executable again; stopping means letting go of a worker.
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);
    private const int PollMs = 100;

    // Everything the start-up path may have to do to it, deletion included: a service found
    // running another copy is taken away and made again rather than merely re-pointed.
    private const uint ServiceAccess =
        Advapi32.SERVICE_QUERY_STATUS | Advapi32.SERVICE_START | Advapi32.SERVICE_STOP |
        Advapi32.SERVICE_QUERY_CONFIG | Advapi32.DELETE;

    // What the command line has to be for the service to start this copy of the executable.
    private static string? CommandLine =>
        Environment.ProcessPath is { Length: > 0 } exe ? $"\"{exe}\" --service" : null;

    // Makes the service if it is absent, points it at this copy if it names another, and starts it.
    // Answers whether it is running afterwards; refusal tells the caller what to say and is never
    // fatal — the server can still run in this process, only without the secure desktop.
    internal static bool EnsureRunning(out string refusal)
    {
        refusal = string.Empty;

        var command = CommandLine;
        if (command is null)
        {
            refusal = "the path of this executable is not known";
            return false;
        }

        if (!PlatformGuard.IsElevated)
        {
            refusal = "this copy has no administrator rights, and a service cannot be made without them";
            return false;
        }

        var manager = Advapi32.OpenSCManager(null, null,
            Advapi32.SC_MANAGER_CONNECT | Advapi32.SC_MANAGER_CREATE_SERVICE);

        if (manager == 0)
        {
            refusal = $"the service control manager would not open: {LastError()}";
            return false;
        }

        try
        {
            var service = Advapi32.OpenService(manager, Name, ServiceAccess);

            if (service == 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != Advapi32.ERROR_SERVICE_DOES_NOT_EXIST)
                {
                    refusal = $"the service could not be opened: {Describe(error)}";
                    return false;
                }

                service = Create(manager, command, out refusal);
                if (service == 0) return false;
            }
            else if (InstalledCommand() is { Length: > 0 } installed &&
                     !string.Equals(installed, command, StringComparison.OrdinalIgnoreCase))
            {
                // The same name, running another copy of this executable: a portable folder that
                // was moved, an installed copy taking over from one run out of Downloads, or a
                // version left behind by an older release. Rewriting the command line alone would
                // not be enough — a service already running goes on running the executable it was
                // started with, and this launch would hand the machine to the old copy. It is
                // stopped, taking its worker with it, and made again from nothing.
                Log.Event($"the service runs another copy of this server ({installed}); " +
                          "it is being stopped and made again for this one");

                service = Recreate(manager, service, command, out refusal);
                if (service == 0) return false;
            }

            try
            {
                return Start(service, out refusal);
            }
            finally
            {
                Advapi32.CloseServiceHandle(service);
            }
        }
        finally
        {
            Advapi32.CloseServiceHandle(manager);
        }
    }

    // Stops the service, which ends the worker with it. Called when the server is on its way out,
    // so that nothing is left running on a machine whose owner has just quit the application.
    //
    // wait is for the one caller that has something to do afterwards: the uninstaller, which is
    // about to delete the executable the service is running. The server's own exit does not wait,
    // because what it is waiting for is this process being ended.
    internal static void StopIfRunning(bool wait = false)
    {
        var manager = Advapi32.OpenSCManager(null, null, Advapi32.SC_MANAGER_CONNECT);
        if (manager == 0) return;

        try
        {
            var service = Advapi32.OpenService(manager, Name,
                Advapi32.SERVICE_QUERY_STATUS | Advapi32.SERVICE_STOP);

            if (service == 0) return;

            try
            {
                StopOpenService(service, wait);
            }
            finally
            {
                Advapi32.CloseServiceHandle(service);
            }
        }
        finally
        {
            Advapi32.CloseServiceHandle(manager);
        }
    }

    // The same, for a handle the caller already holds and goes on using afterwards.
    private static void StopOpenService(nint service, bool wait)
    {
        var status = new ServiceStatus();
        if (!Advapi32.QueryServiceStatus(service, ref status)) return;
        if (status.CurrentState == Advapi32.SERVICE_STOPPED) return;

        if (!Advapi32.ControlService(service, Advapi32.SERVICE_CONTROL_STOP, ref status))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != Advapi32.ERROR_SERVICE_NOT_ACTIVE)
                Log.Info($"the service would not stop: {Describe(error)}");

            return;
        }

        // ControlService returns only once the service's own handler has run, so by here the
        // service already knows it is stopping and will not start another worker.
        Log.Event("the service was asked to stop");

        if (!wait) return;

        if (!WaitFor(service, Advapi32.SERVICE_STOPPED))
        {
            Log.Warn("the service did not report itself stopped within the time allowed; " +
                     "whatever it is holding may still be held");
            return;
        }

        // The service control manager says stopped the moment the service says so, which is a few
        // instructions before that process actually goes and lets go of the executable it was
        // running. Waiting the difference out here is what keeps the uninstaller from finding the
        // file in use, and what keeps a copy taking over from another out of its ports.
        Thread.Sleep(1000);
    }

    // Whether the server the service starts has actually appeared. The service runs the same
    // executable, so the name alone would find the service itself; the session tells them apart,
    // since a service is in session 0 and what it starts is in this one.
    //
    // Asked because the alternative is silence: the copy a person started hands the machine to the
    // service and exits, and if nothing comes up in its place there is nothing on screen and
    // nothing in this log to say so — the service's own log is somewhere else entirely.
    internal static bool WaitForServer(TimeSpan patience)
    {
        var deadline = DateTime.UtcNow + patience;
        var me = Environment.ProcessId;

        uint session;
        if (!Kernel32.ProcessIdToSessionId((uint)me, out session)) return true;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                foreach (var process in Process.GetProcessesByName(
                             Path.GetFileNameWithoutExtension(Environment.ProcessPath) ??
                             AppParameters.Identity.FileBase))
                {
                    using (process)
                    {
                        if (process.Id != me && (uint)process.SessionId == session) return true;
                    }
                }
            }
            catch (Exception error)
            {
                // Not worth failing a start over: this is a check, not a step.
                Log.Info($"the process list could not be read ({error.Message}); " +
                         "the server is taken to have started");
                return true;
            }

            Thread.Sleep(PollMs);
        }

        return false;
    }

    // Where the service writes what it did, which is not where the server writes what it did.
    // Beside the executable, because that is the one folder every part of this agrees on: the
    // service runs as LocalSystem and has no profile worth using, and the server's own log
    // follows the configuration into the profile of whoever is signed in.
    internal static string LogDirectory => AppContext.BaseDirectory;

    // Where it goes when the folder holding the executable cannot be written to — a read-only
    // installation, an unusual set of permissions. Never reached on an ordinary machine.
    internal static string LogFallbackDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        AppParameters.Identity.DataFolder);

    // The file itself, for the one message that has to send somebody to it.
    internal static string LogPath =>
        Path.Combine(LogDirectory, AppParameters.Identity.ServiceLogFile);

    // ------------------------------------------------------------------ the command-line verbs

    // Kept for the person who would rather set it up once by hand, or take it away for good. The
    // ordinary way is neither: the application does both for itself as it starts and exits.
    internal static int Install()
    {
        BorrowConsole();

        if (EnsureRunning(out var refusal))
        {
            Say($"The service \"{Name}\" is installed and running. It is started and stopped with " +
                "the application from now on, and there is nothing else to do.");
            return 0;
        }

        Say($"The service could not be installed: {refusal}", isError: true);
        return 1;
    }

    internal static int Uninstall()
    {
        BorrowConsole();

        if (!PlatformGuard.IsElevated)
        {
            Say("Removing the service needs administrator rights.", isError: true);
            return 1;
        }

        var manager = Advapi32.OpenSCManager(null, null, Advapi32.SC_MANAGER_CONNECT);
        if (manager == 0)
        {
            Say($"The service control manager would not open: {LastError()}", isError: true);
            return 1;
        }

        try
        {
            var service = Advapi32.OpenService(manager, Name,
                Advapi32.SERVICE_QUERY_STATUS | Advapi32.SERVICE_STOP | Advapi32.DELETE);

            if (service == 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == Advapi32.ERROR_SERVICE_DOES_NOT_EXIST)
                {
                    Say($"There is no service \"{Name}\" on this machine; nothing was removed.");
                    return 0;
                }

                Say($"The service could not be opened: {Describe(error)}", isError: true);
                return 1;
            }

            try
            {
                // Waited out: the file about to be deleted is the one the service was running, and
                // the worker it started is another process holding the same file. Both are gone by
                // the time this returns.
                StopIfRunning(wait: true);

                if (!Advapi32.DeleteService(service))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != Advapi32.ERROR_SERVICE_MARKED_FOR_DELETE)
                    {
                        Say($"The service could not be removed: {Describe(error)}", isError: true);
                        return 1;
                    }
                }

                Say($"The service \"{Name}\" was removed. The server still runs when started by " +
                    "hand, but a prompt for administrator rights will hold the picture still " +
                    "instead of being streamed, and the application will offer to make the " +
                    "service again at its next start.");
                return 0;
            }
            finally
            {
                Advapi32.CloseServiceHandle(service);
            }
        }
        finally
        {
            Advapi32.CloseServiceHandle(manager);
        }
    }

    // ------------------------------------------------------------------ the pieces

    private static nint Create(nint manager, string command, out string refusal)
    {
        refusal = string.Empty;

        var service = Advapi32.CreateService(manager, Name, AppParameters.Identity.DisplayName,
            Advapi32.SERVICE_QUERY_STATUS | Advapi32.SERVICE_START | Advapi32.SERVICE_STOP |
            Advapi32.SERVICE_CHANGE_CONFIG | Advapi32.SERVICE_QUERY_CONFIG,
            Advapi32.SERVICE_WIN32_OWN_PROCESS, Advapi32.SERVICE_DEMAND_START,
            Advapi32.SERVICE_ERROR_NORMAL, command, null, 0, null, "LocalSystem", null);

        if (service == 0)
        {
            refusal = $"the service could not be created: {LastError()}";
            return 0;
        }

        Describe(service,
            $"Runs {AppParameters.Identity.DisplayName} as SYSTEM on the console session, so that " +
            "a prompt for administrator rights, the sign-in screen and the lock screen can be " +
            "streamed and answered from a client. Started and stopped by the application itself.");

        Log.Event($"the service \"{Name}\" was created; it runs {command}");
        return service;
    }

    // The command line the installed service actually starts, or null when it cannot be read.
    // From the registry rather than QueryServiceConfig, which would be a two-call dance with a
    // sized buffer for one string that is written down in plain sight.
    private static string? InstalledCommand()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{Name}");

            return (key?.GetValue("ImagePath") as string)?.Trim();
        }
        catch (Exception error)
        {
            // Left alone rather than remade on a guess: this service is this application's own,
            // and taking away a working one because a registry read failed would be worse.
            Log.Info($"the service's command line could not be read ({error.Message}); " +
                     "it is taken to be this copy's");

            return null;
        }
    }

    // Stops the service, removes it, and makes it again against this copy. The handle is consumed:
    // a new one is returned on success, zero on failure with the reason.
    private static nint Recreate(nint manager, nint service, string command, out string refusal)
    {
        refusal = string.Empty;

        try
        {
            // First, because a service cannot be removed while it runs, and because the copy it
            // is running holds the ports this one is about to want.
            StopOpenService(service, wait: true);

            if (!Advapi32.DeleteService(service))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != Advapi32.ERROR_SERVICE_MARKED_FOR_DELETE)
                {
                    refusal = $"the old service could not be removed: {Describe(error)}";
                    return 0;
                }
            }
        }
        finally
        {
            // Before the wait below, and that is the point: a service marked for deletion is not
            // gone until the last handle to it is closed.
            Advapi32.CloseServiceHandle(service);
        }

        if (!WaitUntilGone(manager))
        {
            refusal = "the old service was removed but is still registered; " +
                      "a restart of this machine clears that";
            return 0;
        }

        return Create(manager, command, out refusal);
    }

    // Polls until the name is free. Deletion is not immediate: the service control manager keeps
    // the record until the service has stopped and nothing holds a handle to it.
    private static bool WaitUntilGone(nint manager)
    {
        var deadline = DateTime.UtcNow + Patience;

        while (DateTime.UtcNow < deadline)
        {
            var service = Advapi32.OpenService(manager, Name, Advapi32.SERVICE_QUERY_STATUS);
            if (service == 0) return Marshal.GetLastWin32Error() == Advapi32.ERROR_SERVICE_DOES_NOT_EXIST;

            Advapi32.CloseServiceHandle(service);
            Thread.Sleep(PollMs);
        }

        return false;
    }

    private static void Describe(nint service, string text)
    {
        var description = Marshal.StringToHGlobalUni(text);

        try
        {
            var info = new ServiceDescription { Description = description };
            Advapi32.ChangeServiceConfig2(service, Advapi32.SERVICE_CONFIG_DESCRIPTION, ref info);
        }
        finally
        {
            Marshal.FreeHGlobal(description);
        }
    }

    private static bool Start(nint service, out string refusal)
    {
        refusal = string.Empty;

        var status = new ServiceStatus();
        if (!Advapi32.QueryServiceStatus(service, ref status))
        {
            refusal = $"the service's state could not be read: {LastError()}";
            return false;
        }

        if (status.CurrentState == Advapi32.SERVICE_RUNNING) return true;

        // Caught mid-stop, which is what a restart looks like from here. Waited out rather than
        // started on top of, which the service control manager refuses anyway.
        if (status.CurrentState == Advapi32.SERVICE_STOP_PENDING)
            WaitFor(service, Advapi32.SERVICE_STOPPED);

        if (!Advapi32.StartService(service, 0, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == Advapi32.ERROR_SERVICE_ALREADY_RUNNING) return true;

            refusal = $"the service would not start: {Describe(error)}";
            return false;
        }

        if (WaitFor(service, Advapi32.SERVICE_RUNNING)) return true;

        refusal = "the service was started but did not report itself running";
        return false;
    }

    // Polls until the state is reached or the patience runs out. The service control manager has
    // no wait of its own, and the checkpoint a service reports is advice rather than a signal.
    private static bool WaitFor(nint service, uint state)
    {
        var deadline = DateTime.UtcNow + Patience;
        var status = new ServiceStatus();

        while (DateTime.UtcNow < deadline)
        {
            if (!Advapi32.QueryServiceStatus(service, ref status)) return false;
            if (status.CurrentState == state) return true;

            Thread.Sleep(PollMs);
        }

        return false;
    }

    // ------------------------------------------------------------------ saying so

    // This executable is a windowed one and has no console of its own, so the verbs borrow the
    // prompt they were typed at. Everything is written to the log as well: started from Explorer
    // or a script there is no console to borrow, and the answer must still be somewhere.
    internal static void BorrowConsole() => Kernel32.AttachConsole(Kernel32.ATTACH_PARENT_PROCESS);

    private static void Say(string message, bool isError = false)
    {
        if (isError) Log.Warn(message); else Log.Event(message);

        try
        {
            var writer = isError ? Console.Error : Console.Out;
            writer.WriteLine(message);
            writer.Flush();
        }
        catch (Exception)
        {
            // No console to write to. The log has it.
        }
    }

    private static string LastError() => Describe(Marshal.GetLastWin32Error());

    private static string Describe(int error) =>
        $"{new Win32Exception(error).Message} (Win32 {error})";
}
