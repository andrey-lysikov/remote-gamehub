//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace RemoteGameHub.Native;

// Interfaces are reached by indexing their virtual tables rather than through [ComImport], which
// would need every method declared in order and a marshaller an ahead-of-time build cannot use.
internal static unsafe class Com
{
    internal static void** VTable(void* self) => *(void***)self;

    internal static int QueryInterface(void* self, in Guid iid, out void* result)
    {
        fixed (Guid* id = &iid)
        fixed (void** output = &result)
            return ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)VTable(self)[0])(self, id, output);
    }

    internal static uint Release(void* self) =>
        self is null ? 0 : ((delegate* unmanaged[Stdcall]<void*, uint>)VTable(self)[2])(self);

    // Releases and clears in one step, so a failure path cannot release twice.
    internal static void ReleaseAndClear(ref void* self)
    {
        if (self is null) return;
        Release(self);
        self = null;
    }

    // Throws naming the call that failed: an HRESULT on its own sends the reader to a search engine,
    // and the answer usually depends on which call it came from.
    internal static void Check(int hr, string call)
    {
        if (hr >= 0) return;
        throw new COMException($"{call} failed: 0x{hr:X8} ({Describe(hr)}).", hr);
    }

    internal static string Describe(int hr) => unchecked((uint)hr) switch
    {
        0x887A0004 => "DXGI_ERROR_UNSUPPORTED",
        0x887A0005 => "DXGI_ERROR_DEVICE_REMOVED",
        0x887A0006 => "DXGI_ERROR_DEVICE_HUNG",
        0x887A0007 => "DXGI_ERROR_DEVICE_RESET",
        0x887A0001 => "DXGI_ERROR_INVALID_CALL",
        0x887A0002 => "DXGI_ERROR_NOT_FOUND",
        0x887A0003 => "DXGI_ERROR_MORE_DATA",
        0x887A000A => "DXGI_ERROR_WAS_STILL_DRAWING",
        0x887A0021 => "DXGI_ERROR_NONEXCLUSIVE",
        0x887A0022 => "DXGI_ERROR_NOT_CURRENTLY_AVAILABLE",
        0x887A0026 => "DXGI_ERROR_ACCESS_LOST",
        0x887A0027 => "DXGI_ERROR_WAIT_TIMEOUT",
        0x887A0028 => "DXGI_ERROR_SESSION_DISCONNECTED",
        0x887A002B => "DXGI_ERROR_ACCESS_DENIED",
        0x887A002C => "DXGI_ERROR_NAME_ALREADY_EXISTS",
        0x80070005 => "E_ACCESSDENIED",
        0x8007000E => "E_OUTOFMEMORY",
        0x80070057 => "E_INVALIDARG",
        0x80004001 => "E_NOTIMPL",
        0x80004002 => "E_NOINTERFACE",
        0x80004005 => "E_FAIL",
        _ => "unknown",
    };
}
