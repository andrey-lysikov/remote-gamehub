//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace RemoteGameHub.Native;

// Sessions: which one is on the console, and the token of whoever is signed in to it. Asked by
// the service, to know where to put the server, and by the server itself when it runs as
// LocalSystem, to find out whose configuration to read and whom to start games as.
internal static class Wtsapi32
{
    // What WTSGetActiveConsoleSessionId answers while the console is between sessions.
    internal const uint NoSession = 0xFFFFFFFF;

    [DllImport("kernel32.dll")]
    internal static extern uint WTSGetActiveConsoleSessionId();

    // The primary token of the person signed in to the session. Only LocalSystem may ask, and
    // it fails while nobody is signed in — which is how "is anybody there" is answered.
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSQueryUserToken(uint sessionId, out nint token);
}
