//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using RemoteGameHub.Native;

namespace RemoteGameHub.App;

// The signed-in person, seen from a server that runs as LocalSystem. Their profile folder, so the
// configuration and log sit where they set them, and their registry, so the game scan reads the
// launchers they installed rather than SYSTEM's empty ones. Does nothing when not run as SYSTEM.
internal static class UserContext
{
    // The console user's LocalAppData, resolved through their own token so redirected profiles
    // land in the right place. Null when nobody is signed in or the token cannot be had.
    internal static string? ConsoleUserLocalAppData()
    {
        var token = OpenConsoleUserToken();
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

    // Runs the work as the console user, so Registry.CurrentUser and the like answer for them.
    // Runs it as-is (no impersonation) when there is no user token to borrow — an ordinary
    // elevated run, where the process already is the user.
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

    // The primary token of whoever is signed in to the console, or zero. The caller owns it.
    private static nint OpenConsoleUserToken()
    {
        // Only LocalSystem may ask, and only it needs to: an ordinary run is already the user.
        if (!PlatformGuard.IsSystem) return 0;

        // WTSQueryUserToken is refused outright without SeTcbPrivilege. LocalSystem holds it, but
        // holding a privilege and having it switched on are different things, and this is asked
        // for on the way in rather than assumed. Without it the call quietly answers nothing, and
        // "nothing" here means the configuration and the log move to SYSTEM's own profile under
        // System32 — where they are read from and written to correctly, and where nobody would
        // ever think to look for them.
        SessionLauncher.EnablePrivileges();

        var session = Wtsapi32.WTSGetActiveConsoleSessionId();
        if (session == Wtsapi32.NoSession) return 0;

        if (Wtsapi32.WTSQueryUserToken(session, out var token)) return token;

        Log.Info($"the account signed in to session {session} could not be identified: " +
                 $"{new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message}");

        return 0;
    }
}
