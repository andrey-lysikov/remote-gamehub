//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace RemoteGameHub.Native;

// Sessions: which is on the console, which exist and in what state, and the token of whoever is
// signed in to one. Asked by the service to place the server, and by the server as LocalSystem.
internal static class Wtsapi32
{
    // What WTSGetActiveConsoleSessionId answers while the console is between sessions.
    internal const uint NoSession = 0xFFFFFFFF;

    [DllImport("kernel32.dll")]
    internal static extern uint WTSGetActiveConsoleSessionId();

    // The session the person this process serves is in: the worker's own, since the service put
    // it where the person is; the console only for a process outside any session, like the service.
    internal static uint ServedSessionId()
    {
        if (Kernel32.ProcessIdToSessionId((uint)Environment.ProcessId, out var own) && own != 0)
            return own;

        return WTSGetActiveConsoleSessionId();
    }

    // The primary token of the person signed in to the session. Only LocalSystem may ask, and
    // it fails while nobody is signed in — which is how "is anybody there" is answered.
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSQueryUserToken(uint sessionId, out nint token);

    // Hands one session to the winstation of another — what tscon.exe does. The password is empty,
    // never null: the RPC stub dereferences it before privileges are looked at and answers 1780.
    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode,
               EntryPoint = "WTSConnectSessionW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSConnectSession(uint logonId, uint targetLogonId,
                                                  [MarshalAs(UnmanagedType.LPWStr)] string password,
                                                  [MarshalAs(UnmanagedType.Bool)] bool wait);

    // WTS_CONNECTSTATE_CLASS. Active is a session somebody is looking at, on the console or over
    // remote desktop; Disconnected is one they left without signing out.
    internal const int WTSActive = 0;
    internal const int WTSConnected = 1;
    internal const int WTSConnectQuery = 2;
    internal const int WTSShadow = 3;
    internal const int WTSDisconnected = 4;
    internal const int WTSIdle = 5;
    internal const int WTSListen = 6;
    internal const int WTSReset = 7;
    internal const int WTSDown = 8;
    internal const int WTSInit = 9;

    // WTS_CURRENT_SERVER_HANDLE: this machine.
    private const nint CurrentServer = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SessionInfo
    {
        internal uint SessionId;
        internal nint WinStationName;
        internal int State;
    }

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "WTSEnumerateSessionsW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessions(nint server, uint reserved, uint version,
                                                    out nint sessions, out uint count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(nint memory);

    // Every session on this machine and its state, or an empty list when the question could not be
    // asked. The list is short: the console, session 0, a remote connection or two, the listeners.
    internal static IReadOnlyList<(uint Id, int State)> Sessions()
    {
        if (!WTSEnumerateSessions(CurrentServer, 0, 1, out var buffer, out var count) || buffer == 0)
            return Array.Empty<(uint, int)>();

        try
        {
            var size = Marshal.SizeOf<SessionInfo>();
            var list = new List<(uint, int)>((int)count);

            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<SessionInfo>(buffer + i * size);
                list.Add((info.SessionId, info.State));
            }

            return list;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    // The state of one session, or null when there is no such session any more.
    internal static int? StateOf(uint sessionId)
    {
        foreach (var (id, state) in Sessions())
        {
            if (id == sessionId) return state;
        }

        return null;
    }

    internal static string DescribeState(int? state) => state switch
    {
        WTSActive => "active",
        WTSConnected => "connected",
        WTSConnectQuery => "connecting",
        WTSShadow => "shadowed",
        WTSDisconnected => "disconnected",
        WTSIdle => "idle",
        WTSListen => "listening",
        WTSReset => "resetting",
        WTSDown => "down",
        WTSInit => "initialising",
        null => "gone",
        _ => $"state {state}",
    };
}
