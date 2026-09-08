//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using RemoteGameHub.Native;

namespace RemoteGameHub.App;

// The signed-in person, seen from a server running as LocalSystem: their profile folder for the
// configuration and log, their registry for the game scan. Does nothing when not run as SYSTEM.
internal static class UserContext
{
    // The console user's LocalAppData, resolved through their own token so redirected profiles
    // land in the right place. Null when nobody is signed in or the token cannot be had.
    internal static string? ConsoleUserLocalAppData() => LocalAppDataOf(Wtsapi32.ServedSessionId());

    // The same for whoever is signed in to the given session: the service asks for the session it
    // is about to start the worker in, so its log lands in the same profile as the worker's.
    internal static string? LocalAppDataOf(uint session)
    {
        var token = OpenSessionToken(session);
        if (token == 0) return null;

        try
        {
            return Userenv.KnownFolder(Userenv.LocalAppData, token);
        }
        finally
        {
            Kernel32.CloseHandle(token);
        }
    }

    // The console user's account name, as "MACHINE\\somebody". Null when nobody is signed in or
    // this process is not SYSTEM, in which case the caller already is the person it wants to name.
    internal static string? ConsoleUserName()
    {
        var token = OpenConsoleUserToken();
        if (token == 0) return null;

        try
        {
            // WindowsIdentity duplicates the token it is handed, so the one above is still this
            // method's to close.
            using var identity = new WindowsIdentity(token);
            return identity.Name;
        }
        catch (Exception error)
        {
            Log.Info($"the signed-in account could not be named: {error.Message}");
            return null;
        }
        finally
        {
            Kernel32.CloseHandle(token);
        }
    }

    // The console user's identifier, found once: the worker lives and dies with one session, and
    // the page asks for the theme every second, which was a token opened every second.
    private static string? _consoleUserSid;
    private static bool _fallbackSaid;

    // The console user's security identifier as text, or null for the same reasons as above.
    internal static string? ConsoleUserSid()
    {
        if (_consoleUserSid is not null) return _consoleUserSid;

        var token = OpenConsoleUserToken();
        if (token == 0) return null;

        try
        {
            using var identity = new WindowsIdentity(token);
            return _consoleUserSid = identity.User?.Value;
        }
        catch (Exception error)
        {
            Log.Info($"the signed-in account could not be identified: {error.Message}");
            return null;
        }
        finally
        {
            Kernel32.CloseHandle(token);
        }
    }

    // A value from the signed-in person's registry, reached through HKEY_USERS by their SID under
    // SYSTEM (whose own HKCU has no theme or accent). Null when the key or value is not there.
    internal static object? ReadUserSetting(string subKey, string name)
    {
        try
        {
            if (PlatformGuard.IsSystem)
            {
                if (ConsoleUserSid() is { } sid)
                    return Registry.GetValue($@"HKEY_USERS\{sid}\{subKey}", name, null);

                // Said once: SYSTEM's own profile has never been personalised, so what follows is
                // the default theme and the default accent, whatever the person chose.
                if (!_fallbackSaid)
                {
                    _fallbackSaid = true;
                    Log.Warn("the signed-in account could not be identified, so the theme and the " +
                             "accent colour are read from SYSTEM's own profile: light, unpersonalised");
                }
            }

            return Registry.GetValue($@"HKEY_CURRENT_USER\{subKey}", name, null);
        }
        catch (Exception error)
        {
            Log.Info($"the setting {subKey}\\{name} could not be read: {error.Message}");
            return null;
        }
    }

    // Runs the work as the console user, so Registry.CurrentUser and the like answer for them;
    // as-is when there is no token to borrow, which is an ordinary run that already is the user.
    internal static void AsConsoleUser(Action work)
    {
        var raw = OpenConsoleUserToken();
        if (raw == 0)
        {
            work();
            return;
        }

        // The safe handle takes ownership of what it is given and closes it when disposed, so the
        // raw handle must not be closed here as well.
        using var token = new SafeAccessTokenHandle(raw);

        try
        {
            WindowsIdentity.RunImpersonated(token, work);
        }
        catch (Exception error)
        {
            Log.Info($"the console user could not be impersonated ({error.Message}); " +
                     "the work runs as the service instead");
            work();
        }
    }

    // The token of whoever is signed in to this process's own session, not the console's: over
    // remote desktop the console is an empty sign-in screen, and the worker sits with the person.
    private static nint OpenConsoleUserToken() => OpenSessionToken(Wtsapi32.ServedSessionId());

    // The primary token of whoever is signed in to the session, or zero. The caller owns it.
    private static nint OpenSessionToken(uint session)
    {
        // Only LocalSystem may ask, and only it needs to: an ordinary run is already the user.
        if (!PlatformGuard.IsSystem) return 0;

        // WTSQueryUserToken is refused without SeTcbPrivilege switched on, and quietly: without this
        // the configuration and the log would move to SYSTEM's profile under System32.
        SessionLauncher.EnablePrivileges();

        if (session == Wtsapi32.NoSession) return 0;

        if (Wtsapi32.WTSQueryUserToken(session, out var token)) return token;

        Log.Info($"the account signed in to session {session} could not be identified: " +
                 $"{new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message}");

        return 0;
    }
}
