//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using Microsoft.Win32;

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

    // The whole explanation, for the log, written at startup and at every change. Duplication does
    // work in a remote session; without the line a picture of the wrong desktop reads as a fault.
    internal string Describe() => IsRemote
        ? "This session belongs to remote desktop, not to the console.\n" +
          "What is captured is therefore that session's desktop — which is what a client will see,\n" +
          "and it is not the screen a monitor is plugged into. To stream the console's screen, hand\n" +
          "the session back to it: \"tscon 1 /dest:console\" from an elevated prompt."
        : "this session is on the console";

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        // Every one of these can change the answer, and asking Windows again is cheaper than
        // working out from the reason which of them did.
        var suspended = PlatformGuard.IsRemoteSession;

        bool changed;
        lock (_gate)
        {
            changed = suspended != _suspended;
            _suspended = suspended;
        }

        if (!changed) return;

        Log.Warn($"the session changed ({e.Reason}). {Describe()}");
        Changed?.Invoke(this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        SystemEvents.SessionSwitch -= OnSessionSwitch;
    }
}
