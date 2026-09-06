//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using RemoteGameHub.Native;

namespace RemoteGameHub.App;

// Whether Windows signs the person in by itself after a restart. Without that the server has
// nobody to run for until somebody comes to the machine, and a client finds nothing to connect to.
internal static class AutoLogon
{
    private const string WinlogonKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";

    // Windows 11 hides netplwiz's "must enter a user name and password" box while this is 2.
    private const string PasswordLessKey =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device";
    private const string PasswordLessValue = "DevicePasswordLessBuildVersion";

    // How long the page and the log wait for the switch to be made after the settings were opened.
    private static readonly TimeSpan SwitchPatience = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SwitchPoll = TimeSpan.FromSeconds(5);

    internal enum State
    {
        // Windows signs the account in by itself.
        On,
        // It does not, and the account has a password, so the sign-in screen waits for somebody.
        Off,
        // The account has no password: Windows signs in by itself when it is the only one.
        NoPassword,
        // Could not be established; Why says what refused.
        Unknown,
    }

    // The answer, with the account it is about as "MACHINE\somebody" and, for Unknown, why.
    internal sealed record Check(State State, string Account, string Why);

    // The last full check, for the page: it is drawn many times, and the check asks Windows about
    // the password, which is a sign-in attempt and an audit line each time.
    internal static Check? Last { get; private set; }

    private static TrayIcon? _tray;

    // The full check: the registry, then, only when it is off there, whether the account has a
    // password at all. A machine with one account and no password signs in by itself anyway.
    internal static Check Inspect()
    {
        var account = Person();

        try
        {
            if (IsOn(out var forWhom)) return Last = new Check(State.On, forWhom ?? account ?? "?", string.Empty);
        }
        catch (Exception error)
        {
            return Last = new Check(State.Unknown, account ?? "?",
                $"the sign-in settings could not be read: {error.Message}");
        }

        if (account is null)
            return Last = new Check(State.Unknown, "?", "the signed-in account could not be identified");

        var state = HasPassword(account, out var why);
        return Last = new Check(state, account, why);
    }

    // The registry alone: AutoAdminLogon, as netplwiz and Sysinternals Autologon set it. Cheap,
    // for the page to ask again and again while the person is in the settings window.
    internal static bool IsOn(out string? forWhom)
    {
        forWhom = null;

        using var key = Registry.LocalMachine.OpenSubKey(WinlogonKey);
        if (key is null) return false;

        forWhom = key.GetValue("DefaultUserName") as string;

        // A string "1" from Windows' own tools, a number from some others: both count.
        return key.GetValue("AutoAdminLogon")?.ToString() == "1";
    }

    // Asked at every start. What it finds goes to the log; only the one case that leaves the host
    // unreachable after a restart is also said in a notification, which opens the page.
    internal static void NoticeAtStart(TrayIcon tray, Action openPage)
    {
        _tray = tray;

        var check = Inspect();

        switch (check.State)
        {
            case State.On:
                Log.Info($"automatic sign-in is on for {check.Account}; this host comes back on its " +
                         "own after a restart");
                return;

            case State.NoPassword:
                Log.Info($"{check.Account} has no password, so Windows signs in by itself after a " +
                         "restart when it is the only account");
                return;

            case State.Unknown:
                Log.Info($"whether Windows signs in by itself could not be established: {check.Why}");
                return;
        }

        Log.Warn(Notice(check.Account));

        tray.Notify(AppParameters.Identity.DisplayName,
            "Your account has a password and Windows does not sign in by itself, so this host " +
            "cannot be reached after a restart until somebody signs in at the machine. Click " +
            "here for a way to change that.",
            isError: false, onClick: openPage);
    }

    // The whole of it, for the log: what is wrong, what can be done, and what it costs. The page
    // says the same in its own words, and the balloon only that there is something to read.
    private static string Notice(string account) =>
        $"Windows does not sign {account} in by itself, and the account has a password. After a\n" +
        "restart this host is unreachable until somebody signs in at the machine.\n" +
        "This server can open Windows' own sign-in settings, where automatic sign-in is switched\n" +
        "on and the password is typed into Windows itself: this server never sees or keeps it.\n" +
        "Understand the risk first. Anybody who switches the machine on lands on that desktop\n" +
        "without a password, and a BitLocker drive with no start-up PIN unlocks on its own with\n" +
        "it. Locking the screen right after sign-in keeps the machine shut while this host stays\n" +
        "reachable: the lock screen is streamed and can be unlocked from the client.\n" +
        "The status page has the button that opens the settings.";

    // Opens Windows' own dialog for it, as the person signed in: the password is typed into
    // Windows and kept by Windows, and this server never sees it. Returns what to tell the page.
    internal static string OpenWindowsSettings()
    {
        ShowNetplwizBox();

        var opened = PlatformGuard.IsSystem
            ? SessionLauncher.StartAsConsoleUser("netplwiz.exe", null)
            : StartHere("netplwiz.exe");

        if (!opened)
        {
            return "Windows' sign-in settings could not be opened; the log says why. At the " +
                   "machine, press Win+R and run netplwiz.";
        }

        Log.Event("Windows' sign-in settings (netplwiz) were opened on the machine's screen, as " +
                  "asked from the status page; the password, if typed, goes to Windows alone");

        _ = Task.Run(WatchForSwitch);

        return "Opened on the machine's screen. In that window untick \"Users must enter a user " +
               "name and password to use this computer\", press OK, and type the Windows " +
               "password into the box Windows shows. This page notices when it is done.";
    }

    // Windows 11 hides the box behind a "passwordless" flag; shown again, since without it the
    // window has nothing to untick. A flag of the settings dialog only, not of sign-in itself.
    private static void ShowNetplwizBox()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PasswordLessKey, writable: true);
            if (key is null || Equals(key.GetValue(PasswordLessValue), 0)) return;

            key.SetValue(PasswordLessValue, 0, RegistryValueKind.DWord);
            Log.Event($"{PasswordLessValue} was set to 0, so that netplwiz shows the box that " +
                      "switches automatic sign-in on");
        }
        catch (Exception error)
        {
            Log.Warn($"the box in netplwiz may stay hidden: {PasswordLessValue} could not be " +
                     $"changed ({error.Message})");
        }
    }

    // Looks for the switch being made after the settings were opened, and says so once: the
    // person is at the machine's screen, and the log is what says whether it took.
    private static void WatchForSwitch()
    {
        var deadline = DateTime.UtcNow + SwitchPatience;

        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(SwitchPoll);

            try
            {
                if (!IsOn(out _)) continue;
            }
            catch (Exception)
            {
                continue;
            }

            Inspect();
            Log.Event("automatic sign-in is on now; this host comes back on its own after a restart");

            _tray?.Notify(AppParameters.Identity.DisplayName,
                "Automatic sign-in is on. This host will be reachable after a restart.",
                isError: false);
            return;
        }

        Log.Info("the sign-in settings were opened but automatic sign-in is still off; " +
                 "the status page offers it again at the next start");
    }

    // Whether the account has a password, asked of Windows with an empty one: accepted or refused
    // for being blank means none; refused as wrong means there is one. An audit line, once a start.
    private static State HasPassword(string account, out string why)
    {
        why = string.Empty;

        var slash = account.IndexOf('\\');
        var domain = slash > 0 ? account[..slash] : null;
        var user = slash > 0 ? account[(slash + 1)..] : account;

        if (Advapi32.LogonUser(user, domain, string.Empty, Advapi32.LOGON32_LOGON_INTERACTIVE,
                               Advapi32.LOGON32_PROVIDER_DEFAULT, out var token))
        {
            Kernel32.CloseHandle(token);
            return State.NoPassword;
        }

        var error = Marshal.GetLastWin32Error();

        switch (error)
        {
            case Advapi32.ERROR_ACCOUNT_RESTRICTION:
                return State.NoPassword;

            case Advapi32.ERROR_LOGON_FAILURE:
            case Advapi32.ERROR_PASSWORD_EXPIRED:
            case Advapi32.ERROR_PASSWORD_MUST_CHANGE:
                return State.Off;

            default:
                why = $"Windows answered {new Win32Exception(error).Message} (Win32 {error}) " +
                      $"when asked about {account}'s password";
                return State.Unknown;
        }
    }

    // The account this is about: the person at the console when this server is SYSTEM, and
    // whoever runs it otherwise. Null when nobody is signed in.
    private static string? Person()
    {
        if (PlatformGuard.IsSystem) return UserContext.ConsoleUserName();

        using var identity = WindowsIdentity.GetCurrent();
        return identity.Name;
    }

    private static bool StartHere(string command)
    {
        try
        {
            Process.Start(new ProcessStartInfo(command) { UseShellExecute = true });
            return true;
        }
        catch (Exception error)
        {
            Log.Warn($"{command} could not be started: {error.Message}");
            return false;
        }
    }
}
