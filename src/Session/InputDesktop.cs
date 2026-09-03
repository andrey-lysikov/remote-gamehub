//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using RemoteGameHub.App;
using RemoteGameHub.Native;

namespace RemoteGameHub.Session;

// Puts the calling thread on the desktop that is receiving input. A thread keeps the desktop it
// was created on, and SendInput and GetCursorInfo answer for that one — silently, if it is stale.
internal static class InputDesktop
{
    [ThreadStatic]
    private static nint _attached;

    // Named rather than compared by handle: OpenInputDesktop returns a new handle every call, for
    // the same desktop, so the handles say nothing about whether anything moved.
    [ThreadStatic]
    private static string? _attachedName;

    // When this thread last looked. The answer changes when the screen locks or a prompt appears,
    // and asking costs three win32k calls under the window-station lock.
    [ThreadStatic]
    private static int _lastLook;

    private const int LookAgainAfterMs = 200;

    // Attaches this thread to whatever desktop currently has the input, when it is not there
    // already. Called before a batch rather than once: the desktop changes under a running stream.
    internal static void Attach()
    {
        // Not on every call. This runs once per sent frame and once per input packet — six hundred
        // times a second between them — for an answer that changes when somebody locks the screen.
        var now = Environment.TickCount;
        if (_attachedName is not null && (uint)(now - _lastLook) < LookAgainAfterMs) return;
        _lastLook = now;

        var desktop = User32.OpenInputDesktop(0, false, User32.DESKTOP_ALL);
        if (desktop == 0) return;

        var name = NameOf(desktop);

        if (name is not null && name == _attachedName)
        {
            // Already on it. The handle is a new one whatever the answer, and has to be closed.
            User32.CloseDesktop(desktop);
            return;
        }

        if (!User32.SetThreadDesktop(desktop))
        {
            // The usual reason is a window belonging to this thread on the desktop it is leaving,
            // which Windows will not allow. Nothing in this server creates one.
            Log.WarnOccasionally("input desktop",
                "this thread could not be moved to the desktop that has the input; " +
                "keys and mouse movement may not reach it");

            User32.CloseDesktop(desktop);
            return;
        }

        var previous = _attached;
        _attached = desktop;
        _attachedName = name;

        if (previous != 0) User32.CloseDesktop(previous);

        Log.Info($"the input thread moved to the \"{name ?? "unnamed"}\" desktop, which is the one " +
                 "receiving input now");
    }

    private static string? NameOf(nint desktop)
    {
        var buffer = new char[64];

        if (!User32.GetUserObjectInformation(desktop, User32.UOI_NAME, buffer,
                                             (uint)(buffer.Length * sizeof(char)), out var needed))
        {
            return null;
        }

        // The length includes the terminator, in bytes.
        var characters = (int)(needed / sizeof(char));
        if (characters <= 1 || characters > buffer.Length) return null;

        return new string(buffer, 0, characters - 1);
    }
}
