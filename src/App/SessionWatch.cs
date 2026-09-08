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

    internal SessionWatch()
    {
        _suspended = PlatformGuard.IsRemoteSession;

        // The session can be taken and given back while the server runs, and nothing else tells it
        // so.
        SystemEvents.SessionSwitch += OnSessionSwitch;
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
        Settle($"the session changed ({e.Reason})");
    }

    // Asks Windows again and says so when the answer turned over. Every session switch can change
    // it, and asking is cheaper than working out from the reason which of them did.
    private void Settle(string what)
    {
        var suspended = PlatformGuard.IsRemoteSession;

        bool changed;
        lock (_gate)
        {
            changed = suspended != _suspended;
            _suspended = suspended;
        }

        if (!changed) return;

        // An event, not a warning: this is what the server was asked to sit through, and it is
        // said only when the answer to "can the console be streamed" actually turned over.
        Log.Event($"{what}. {Describe()}");
        Changed?.Invoke(this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        SystemEvents.SessionSwitch -= OnSessionSwitch;
    }
}
