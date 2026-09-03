//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace RemoteGameHub.Native;

// The multimedia timer's resolution, the shortest a Sleep on this process can be: 15.6 ms by
// default. Since Windows 10 2004 the request affects only this process, not the machine.
internal static class WinMm
{
    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint milliseconds);

    private const uint Ok = 0;

    // Raised while something needs it and lowered after, paired in a finally. Answers whether the
    // request was granted, so the caller does not lower a resolution it never raised.
    internal static bool RaiseTimerResolution() => timeBeginPeriod(1) == Ok;

    internal static void LowerTimerResolution() => timeEndPeriod(1);
}
