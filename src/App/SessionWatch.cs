//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using RemoteGameHub.Native;

namespace RemoteGameHub.App;

// Whether this machine can be streamed from at this moment, and notice when that changes. A remote
// desktop connection moves the session to the remote screen and draws it on the processor.
internal sealed class SessionWatch : IDisposable
{
    private readonly object _gate = new();
    private bool _suspended;
    private bool _disposed;

    // Raised when streaming becomes possible or stops being possible.
    internal event Action<SessionWatch>? Changed;

    // Raised with the reason when the machine is going to sleep, off, or signed out: the last
    // seconds in which a stream can be ended properly.
    internal event Action<string>? Leaving;

    internal SessionWatch()
    {
        _suspended = PlatformGuard.IsRemoteSession;

        // The session can be taken and given back while the server runs, and nothing else tells it
        // so.
        SystemEvents.SessionSwitch += OnSessionSwitch;

        // Sleep and shutdown take the screen away without any session switch to say so. Windows
        // asks first and stops running code a moment later, which is the whole of the warning.
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionEnding += OnSessionEnding;
    }

    // true while this session belongs to a remote desktop connection.
    internal bool IsRemote
    {
        get { lock (_gate) return _suspended; }
    }

    // What the tray icon says. Short: the shell truncates it at sixty-odd characters.
    internal string TrayState => IsRemote
        ? "waiting for a client — remote desktop session"
        : "waiting for a client";

    // The whole explanation, for the log, written at startup and at every change: a remote desktop
    // connection ending the moment a stream starts reads as a fault without this line.
    internal string Describe() => IsRemote
        ? "This session belongs to remote desktop, not to the console.\n" +
          "There is no screen to capture here: a remote desktop is drawn by a display adapter that\n" +
          "cannot be duplicated, and the card's own output is not attached to this session.\n" +
          "The console is therefore taken back as soon as a client asks to stream, which ends the\n" +
          $"remote desktop connection. To do it now: \"tscon {Wtsapi32.ServedSessionId()} " +
          "/dest:console\" from an elevated prompt."
        : "this session is on the console";

    // Hands this session back to the console, as "tscon <id> /dest:console" does by hand: a remote
    // desktop has no output DXGI can duplicate, so the connection is spent to get a picture at all.
    internal bool ReclaimConsole()
    {
        if (!IsRemote) return true;

        var own = Wtsapi32.ServedSessionId();
        var console = Wtsapi32.WTSGetActiveConsoleSessionId();

        if (own == Wtsapi32.NoSession || console == Wtsapi32.NoSession || own == 0)
        {
            Log.Warn("the console cannot be taken back: Windows names no session to move, or none " +
                     "holding the console. This passes on its own; the stream has no picture until " +
                     "it does.");
            return false;
        }

        if (own == console)
        {
            // Already this session's yet still reported remote: the move is under way, or a
            // shadowed session is looking on. Nothing to ask for twice.
            Settle("the console is already this session's");
            return !IsRemote;
        }

        // Only LocalSystem holds SE_TCB_NAME, without which Windows refuses the move rather than
        // asking for a password. Administrator rights are not enough.
        if (!PlatformGuard.IsSystem)
        {
            Log.Warn(
                "This stream has no picture: the session is on remote desktop, where there is no\n" +
                "screen to capture, and this server may not hand it back to the console.\n" +
                "Only a server running as LocalSystem can, which is what the installed service\n" +
                $"makes it: \"{Environment.ProcessPath}\" install-service\n" +
                $"What to do now: \"tscon {own} /dest:console\" from an elevated prompt.");
            return false;
        }

        Log.Event($"the stream needs a screen and this session has none: session {own} is being " +
                  $"handed back to the console (session {console} holds it now). The remote " +
                  "desktop connection ends as the session moves — that is what taking the console " +
                  "back means.");

        // The empty password is what tscon.exe passes, and the only thing that works: a null one
        // is dereferenced by the RPC stub before SE_TCB_NAME is looked at, and answers 1780.
        if (!Wtsapi32.WTSConnectSession(own, console, string.Empty, true))
        {
            var error = Marshal.GetLastWin32Error();
            Log.Warn(
                $"the session could not be handed to the console (WTSConnectSession failed with " +
                $"{error}: {new Win32Exception(error).Message}). The stream goes on without a " +
                $"picture until the console is taken back by hand: \"tscon {own} /dest:console\" " +
                "from an elevated prompt.");
            return false;
        }

        // The call returns when the session has moved, but the metric behind IsRemoteSession
        // follows a moment later, and everything after this would be told "none" too early.
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(AppParameters.Handover.SessionMs);

        while (PlatformGuard.IsRemoteSession && DateTime.UtcNow < deadline)
            Thread.Sleep(AppParameters.Handover.PollMs);

        Settle("the console was taken back");
        return !IsRemote;
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        // Logged whether or not it changed anything: the last of these is what explains a worker
        // that vanished a second later. The ones that change the picture say more, below.
        if (!Settle($"the session changed ({e.Reason})"))
            Log.Info($"the session changed ({e.Reason}); there is still " +
                     (IsRemote ? "no screen here to capture" : "a screen here to capture"));
    }

    // Sleep, and waking from it. A stream cannot survive a suspend: the encoder's device goes,
    // the network goes, and the client is left sending to a machine that answers nothing.
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                // A client told the stream ended puts its own screen away and comes back later. One
                // that is not told sits on a frozen picture until its timeout, blaming this server.
                Log.Event("this machine is going to sleep; anything streaming is ended first, " +
                          "while there is still a moment in which to say so.");
                Leaving?.Invoke("this machine is going to sleep");
                break;

            case PowerModes.Resume:
                // Screen, encoder and sound come back on their own. The console is asked again,
                // since a remote desktop connection can be what woke the machine.
                if (!Settle("this machine woke up"))
                    Log.Event("this machine woke up; " + Describe());

                break;
        }
    }

    // Signing out and shutting down. Windows ends every process in the session seconds after this,
    // this one included, so the stream is taken down here rather than left to be cut off.
    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        Leaving?.Invoke(e.Reason == SessionEndReasons.SystemShutdown
            ? "this machine is shutting down"
            : "this session is being signed out");
    }

    // Asks Windows again after any session switch, cheaper than reasoning from the cause, and
    // answers whether the answer turned over.
    private bool Settle(string what)
    {
        var suspended = PlatformGuard.IsRemoteSession;

        bool changed;
        lock (_gate)
        {
            changed = suspended != _suspended;
            _suspended = suspended;
        }

        if (!changed) return false;

        // An event, not a warning: this is what the server was asked to sit through, and it is
        // said only when the answer to "can the console be streamed" actually turned over.
        Log.Event($"{what}. {Describe()}");
        Changed?.Invoke(this);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;
    }
}
