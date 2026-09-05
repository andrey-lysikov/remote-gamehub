//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Text;

namespace RemoteGameHub.Native;

// winsvc.h: what the service control manager is told and tells back.
[StructLayout(LayoutKind.Sequential)]
internal struct ServiceStatus
{
    internal uint ServiceType;
    internal uint CurrentState;
    internal uint ControlsAccepted;
    internal uint Win32ExitCode;
    internal uint ServiceSpecificExitCode;
    internal uint CheckPoint;
    internal uint WaitHint;
}

// SERVICE_DESCRIPTION, the one thing ChangeServiceConfig2 is asked to set here: the sentence the
// services list shows under the name.
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct ServiceDescription
{
    internal nint Description;
}

// One row of the table StartServiceCtrlDispatcher takes; the last row is all zeros.
[StructLayout(LayoutKind.Sequential)]
internal struct ServiceTableEntry
{
    internal nint Name;
    internal nint Proc;
}

// STARTUPINFOW. Desktop is the one field this server sets: the process is put on the
// interactive desktop of the session the token names.
[StructLayout(LayoutKind.Sequential)]
internal struct StartupInfo
{
    internal uint Size;
    internal nint Reserved;
    internal nint Desktop;
    internal nint Title;
    internal uint X;
    internal uint Y;
    internal uint XSize;
    internal uint YSize;
    internal uint XCountChars;
    internal uint YCountChars;
    internal uint FillAttribute;
    internal uint Flags;
    internal ushort ShowWindow;
    internal ushort Reserved2;
    internal nint Reserved3;
    internal nint StdInput;
    internal nint StdOutput;
    internal nint StdError;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ProcessInformation
{
    internal nint Process;
    internal nint Thread;
    internal uint ProcessId;
    internal uint ThreadId;
}

// TOKEN_PRIVILEGES with exactly one privilege in it, which is all this server ever adjusts at once.
// The LUID it carries is the one declared in Dxgi.cs: the same two thirty-two-bit halves Windows
// uses for an adapter's identifier and for a privilege's, and there is no second shape of it.
[StructLayout(LayoutKind.Sequential)]
internal struct TokenPrivileges
{
    internal uint Count;
    internal Luid Luid;
    internal uint Attributes;
}

// Access tokens and the service control manager. Both are needed for one thing only: running
// the server as LocalSystem inside the interactive session, which is what lets it capture the
// secure desktop — the UAC prompt and the lock screen — and type into it.
internal static class Advapi32
{
    internal const uint TOKEN_ALL_ACCESS = 0x000F01FF;

    // SECURITY_IMPERSONATION_LEVEL and TOKEN_TYPE.
    internal const int SecurityImpersonation = 2;
    internal const int TokenPrimary = 1;

    // TOKEN_INFORMATION_CLASS.
    internal const int TokenSessionId = 12;

    internal const uint SE_PRIVILEGE_ENABLED = 0x00000002;

    // The three privileges LocalSystem holds but does not start with switched on, and which
    // moving a token into another session and starting a process under it need.
    internal const string SE_TCB_NAME = "SeTcbPrivilege";
    internal const string SE_ASSIGNPRIMARYTOKEN_NAME = "SeAssignPrimaryTokenPrivilege";
    internal const string SE_INCREASE_QUOTA_NAME = "SeIncreaseQuotaPrivilege";

    internal const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    // For the cmd.exe that stands in for ShellExecute when a game is started: without it a console
    // window flashes on the player's screen, and on a stream that is a black rectangle mid-frame.
    internal const uint CREATE_NO_WINDOW = 0x08000000;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DuplicateTokenEx(nint token, uint desiredAccess, nint attributes,
                                                 int impersonationLevel, int tokenType,
                                                 out nint duplicate);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetTokenInformation(nint token, int informationClass,
                                                    ref uint information, uint length);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode,
               EntryPoint = "LookupPrivilegeValueW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AdjustTokenPrivileges(nint token,
                                                      [MarshalAs(UnmanagedType.Bool)] bool disableAll,
                                                      ref TokenPrivileges newState, uint length,
                                                      nint previousState, nint returnedLength);

    // The command line is a StringBuilder and not a string on purpose: the call is allowed to
    // write into it, and a managed string is handed over by reference, not copied.
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode,
               EntryPoint = "CreateProcessAsUserW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcessAsUser(nint token, string? application,
                                                    StringBuilder? commandLine,
                                                    nint processAttributes, nint threadAttributes,
                                                    [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
                                                    uint creationFlags, nint environment,
                                                    string? currentDirectory,
                                                    ref StartupInfo startupInfo,
                                                    out ProcessInformation processInformation);

    // Switches one privilege on in this process's own token. False when it is not held at all:
    // a process that is not LocalSystem has none of the three above, and says so once in the log.
    internal static bool EnablePrivilege(string name)
    {
        if (!OpenProcessToken(Kernel32.GetCurrentProcess(), TOKEN_ALL_ACCESS, out var token))
            return false;

        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid)) return false;

            var state = new TokenPrivileges
            {
                Count = 1,
                Luid = luid,
                Attributes = SE_PRIVILEGE_ENABLED,
            };

            if (!AdjustTokenPrivileges(token, false, ref state, (uint)Marshal.SizeOf<TokenPrivileges>(),
                                       0, 0))
            {
                return false;
            }

            // Succeeds even when nothing was assigned, and says so through the last error.
            return Marshal.GetLastWin32Error() == 0;
        }
        finally
        {
            Kernel32.CloseHandle(token);
        }
    }

    // ------------------------------------------------------------------ the service control manager

    internal const uint SERVICE_WIN32_OWN_PROCESS = 0x00000010;

    internal const uint SERVICE_STOPPED = 1;
    internal const uint SERVICE_START_PENDING = 2;
    internal const uint SERVICE_STOP_PENDING = 3;
    internal const uint SERVICE_RUNNING = 4;

    internal const uint SERVICE_ACCEPT_STOP = 0x00000001;
    internal const uint SERVICE_ACCEPT_SHUTDOWN = 0x00000004;
    internal const uint SERVICE_ACCEPT_SESSIONCHANGE = 0x00000080;

    internal const uint SERVICE_CONTROL_STOP = 1;
    internal const uint SERVICE_CONTROL_INTERROGATE = 4;
    internal const uint SERVICE_CONTROL_SHUTDOWN = 5;
    // Every reason it carries — a console connecting or disconnecting, a sign-in, a sign-out, a
    // lock, an unlock — is treated the same, so none of them is named here: each can change which
    // session the console is, and the supervisor asks Windows rather than working it out.
    internal const uint SERVICE_CONTROL_SESSIONCHANGE = 0x0000000E;

    internal const uint NO_ERROR = 0;
    internal const uint ERROR_CALL_NOT_IMPLEMENTED = 120;

    // ------------------------------------------------------------------ installing and driving it

    // Asked for by name rather than through sc.exe: sc prints in the language Windows is installed
    // in, and reading a service's state out of translated text is not something to build on.
    internal const uint SC_MANAGER_CONNECT = 0x0001;
    internal const uint SC_MANAGER_CREATE_SERVICE = 0x0002;

    internal const uint SERVICE_QUERY_CONFIG = 0x0001;
    internal const uint SERVICE_CHANGE_CONFIG = 0x0002;
    internal const uint SERVICE_QUERY_STATUS = 0x0004;
    internal const uint SERVICE_START = 0x0010;
    internal const uint SERVICE_STOP = 0x0020;
    internal const uint DELETE = 0x00010000;

    // Started when this application starts it and at no other time: the server exists for as long
    // as somebody is using it, and a service left running would keep a worker on an idle machine.
    internal const uint SERVICE_DEMAND_START = 3;
    internal const uint SERVICE_ERROR_NORMAL = 1;

    internal const uint SERVICE_CONFIG_DESCRIPTION = 1;

    internal const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
    internal const int ERROR_SERVICE_ALREADY_RUNNING = 1056;
    internal const int ERROR_SERVICE_MARKED_FOR_DELETE = 1072;
    internal const int ERROR_SERVICE_NOT_ACTIVE = 1062;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode,
               EntryPoint = "OpenSCManagerW")]
    internal static extern nint OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode,
               EntryPoint = "OpenServiceW")]
    internal static extern nint OpenService(nint manager, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode,
               EntryPoint = "CreateServiceW")]
    internal static extern nint CreateService(nint manager, string name, string displayName,
                                              uint access, uint serviceType, uint startType,
                                              uint errorControl, string binaryPath,
                                              string? loadOrderGroup, nint tagId,
                                              string? dependencies, string? account,
                                              string? password);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode,
               EntryPoint = "ChangeServiceConfig2W")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ChangeServiceConfig2(nint service, uint infoLevel,
                                                     ref ServiceDescription info);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteService(nint service);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode,
               EntryPoint = "StartServiceW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool StartService(nint service, uint argc, nint argv);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ControlService(nint service, uint control, ref ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryServiceStatus(nint service, ref ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseServiceHandle(nint handle);

    // Returns only when every service in the table has stopped; the table ends with a zero row.
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode,
               EntryPoint = "StartServiceCtrlDispatcherW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool StartServiceCtrlDispatcher(ServiceTableEntry[] table);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode,
               EntryPoint = "RegisterServiceCtrlHandlerExW")]
    internal static extern nint RegisterServiceCtrlHandlerEx(string serviceName, nint handler,
                                                             nint context);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetServiceStatus(nint handle, ref ServiceStatus status);
}
