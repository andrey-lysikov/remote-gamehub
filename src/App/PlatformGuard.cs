//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Security.Principal;

namespace RemoteGameHub.App;

// The two conditions not worth starting without, checked in code rather than declared in the
// project file: a versioned target framework would pull in the Windows SDK projections.
internal static class PlatformGuard
{
    // Windows 11 21H2. Nothing older is supported, and nothing older is tested.
    internal const int MinimumBuild = 22000;

    internal static bool IsWindows11OrNewer =>
        OperatingSystem.IsWindows() && Environment.OSVersion.Version.Build >= MinimumBuild;

    // Checked although the manifest already asks for administrator rights: a manifest is a request,
    // and a scheduled task, a service wrapper or a debugger can arrive here without them.
    internal static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    // Whether this process runs inside a remote desktop session. It does not stop the server: the
    // session is watched from SessionWatch instead, because it changes while the server runs.
    internal static bool IsRemoteSession =>
        Native.User32.GetSystemMetrics(Native.User32.SM_REMOTESESSION) != 0;

    // Windows 11 still reports itself as major version 10; only the build number tells the two
    // apart.
    internal static string DescribeWindows()
    {
        var build = Environment.OSVersion.Version.Build;
        var name = build >= MinimumBuild ? "Windows 11" : $"Windows {Environment.OSVersion.Version.Major}";

        return $"{name} build {build}, {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}";
    }
}
