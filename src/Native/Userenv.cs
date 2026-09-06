//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace RemoteGameHub.Native;

// The environment and folders of a person other than the one this process runs as: needed while
// the server is LocalSystem, for games and for the configuration in the player's profile.
internal static class Userenv
{
    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateEnvironmentBlock(out nint environment, nint token,
                                                       [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyEnvironmentBlock(nint environment);

    // FOLDERID_LocalAppData, from knownfolders.h.
    internal static readonly Guid LocalAppData = new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(in Guid folder, uint flags, nint token, out nint path);

    // The known folder as the token's owner sees it, redirections included. Null when Windows
    // will not say, which is answered by falling back to a folder every account can reach.
    internal static string? KnownFolder(in Guid folder, nint token)
    {
        if (SHGetKnownFolderPath(folder, 0, token, out var path) < 0 || path == 0) return null;

        try
        {
            return Marshal.PtrToStringUni(path);
        }
        finally
        {
            Marshal.FreeCoTaskMem(path);
        }
    }
}
