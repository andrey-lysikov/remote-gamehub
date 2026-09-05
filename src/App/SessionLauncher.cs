//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using RemoteGameHub.Native;

namespace RemoteGameHub.App;

// Starting a process in the interactive console session with a chosen identity, which only a
// LocalSystem process can do. Two callers: the service, which starts the server here as SYSTEM on
// the console so it can capture and type into the secure desktop; and that server, which starts
// games here as the person signed in, so a game finds its profile and does not run as SYSTEM.
internal static class SessionLauncher
{
    private static bool _privilegesEnabled;

    // The three a LocalSystem process holds but does not begin with switched on. Assigning a token
    // to another session, and starting a process under a token, both need them.
    internal static void EnablePrivileges()
    {
        if (_privilegesEnabled) return;
        _privilegesEnabled = true;

        foreach (var name in new[]
                 {
                     Advapi32.SE_TCB_NAME,
                     Advapi32.SE_ASSIGNPRIMARYTOKEN_NAME,
                     Advapi32.SE_INCREASE_QUOTA_NAME,
                 })
        {
            if (!Advapi32.EnablePrivilege(name))
                Log.Info($"the privilege {name} could not be switched on; it may not be held");
        }
    }

    // Starts the server as LocalSystem on the interactive desktop of the given console session.
    // The returned handle is the process, for the service to watch; zero on failure, logged.
    internal static nint StartServerAsSystem(uint sessionId, string arguments)
    {
        EnablePrivileges();

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            Log.Warn("the server's own path is not known; the worker cannot be started");
            return 0;
        }

        // This process is the service, running as LocalSystem. Its own token, duplicated as a
        // primary token and moved into the console session, is what the worker runs under.
        if (!Advapi32.OpenProcessToken(Kernel32.GetCurrentProcess(), Advapi32.TOKEN_ALL_ACCESS,
                                       out var self))
        {
            Log.Warn($"the service's token could not be opened: {LastError()}");
            return 0;
        }

        try
        {
            if (!Advapi32.DuplicateTokenEx(self, Advapi32.TOKEN_ALL_ACCESS, 0,
                                           Advapi32.SecurityImpersonation, Advapi32.TokenPrimary,
                                           out var token))
            {
                Log.Warn($"the service's token could not be duplicated: {LastError()}");
                return 0;
            }

            try
            {
                var session = sessionId;
                if (!Advapi32.SetTokenInformation(token, Advapi32.TokenSessionId, ref session,
                                                  sizeof(uint)))
                {
                    Log.Warn($"the worker's token could not be moved to session {sessionId}: {LastError()}");
                    return 0;
                }

                return StartWith(token, exe, new StringBuilder($"\"{exe}\" {arguments}"), null,
                                 Advapi32.CREATE_UNICODE_ENVIRONMENT,
                                 "the worker (the server as SYSTEM on the console)");
            }
            finally
            {
                Kernel32.CloseHandle(token);
            }
        }
        finally
        {
            Kernel32.CloseHandle(self);
        }
    }

    // Starts a game as the person signed in to the console, through the shell so a steam:// URL or
    // a shell: path resolves exactly as a double-click would. Only reached when the server itself
    // runs as SYSTEM; otherwise the server starts games directly, as itself.
    internal static bool StartGameAsConsoleUser(string command, string? workingDirectory)
    {
        EnablePrivileges();

        var session = Wtsapi32.WTSGetActiveConsoleSessionId();
        if (session == Wtsapi32.NoSession)
        {
            Log.Warn("no console session is attached; the game cannot be started as the signed-in user");
            return false;
        }

        if (!Wtsapi32.WTSQueryUserToken(session, out var userToken))
        {
            Log.Warn($"the signed-in user's token could not be obtained: {LastError()}");
            return false;
        }

        try
        {
            // Through cmd's start, which is ShellExecute: the same reason the direct path uses
            // UseShellExecute, and the only way a URL or a shell: path runs at all. The empty
            // first quotes are start's title argument, which it insists on when a path is quoted.
            var line = new StringBuilder($"cmd.exe /c start \"\" \"{command}\"");
            var process = StartWith(userToken, null, line, workingDirectory,
                                    Advapi32.CREATE_UNICODE_ENVIRONMENT | Advapi32.CREATE_NO_WINDOW,
                                    $"\"{command}\" as the signed-in user");
            if (process == 0) return false;

            Kernel32.CloseHandle(process);
            return true;
        }
        finally
        {
            Kernel32.CloseHandle(userToken);
        }
    }

    // The shared tail of both: an environment block for the token, the interactive desktop, and
    // CreateProcessAsUser. Returns the process handle (the thread handle is closed here) or zero.
    private static nint StartWith(nint token, string? application, StringBuilder commandLine,
                                  string? workingDirectory, uint flags, string what)
    {
        var haveEnvironment = Userenv.CreateEnvironmentBlock(out var environment, token, false);
        if (!haveEnvironment) environment = 0;

        var desktop = Marshal.StringToHGlobalUni(@"winsta0\default");

        try
        {
            var startup = new StartupInfo
            {
                Size = (uint)Marshal.SizeOf<StartupInfo>(),
                Desktop = desktop,
            };

            var directory = !string.IsNullOrWhiteSpace(workingDirectory) &&
                            Directory.Exists(workingDirectory)
                ? workingDirectory
                : null;

            if (!Advapi32.CreateProcessAsUser(token, application, commandLine, 0, 0, false, flags,
                                              environment, directory, ref startup, out var info))
            {
                Log.Warn($"{what} could not be started: {LastError()}");
                return 0;
            }

            Kernel32.CloseHandle(info.Thread);
            Log.Event($"started {what} (pid {info.ProcessId})");
            return info.Process;
        }
        finally
        {
            Marshal.FreeHGlobal(desktop);
            if (environment != 0) Userenv.DestroyEnvironmentBlock(environment);
        }
    }

    private static string LastError()
    {
        var error = Marshal.GetLastWin32Error();
        return $"{new Win32Exception(error).Message} (Win32 {error})";
    }
}
