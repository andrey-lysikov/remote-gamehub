//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using RemoteGameHub.App;

namespace RemoteGameHub.Native;

// The AMD driver's AMF runtime through amfrt64.dll: C++ vtables by slot, each slot the member's
// offsetof over the pointer size in AMF 1.5.2. Refcounted: Acquire 0, Release 1, QueryInterface 2.
internal static unsafe class Amf
{
    // The version handed to AMFInit: 1.4.0.0, packed major<<48 | minor<<32 | release<<16 | build
    // (Version.h, AMF_MAKE_FULL_VERSION). Old deliberately: the runtime accepts up to its own.
    internal const ulong RequestedVersion = 0x0001000400000000;

    internal const int SurfaceFormatBgra = 3;   // AMF_SURFACE_BGRA — the capturer's byte order

    // AMF_SURFACE_P010: luma then interleaved chroma, ten bits in the upper part of each word —
    // the same DXGI_FORMAT_P010 the colour shader writes.
    internal const int SurfaceFormatP010 = 10;

    // The colour of what goes in and what comes out, from ColorSpace.h. The profile carries the
    // matrix and the range together: 2020 is BT.2020 in studio range.
    internal const long ColorProfile2020 = 2;
    internal const long ColorPrimariesBt2020 = 9;
    internal const long ColorTransferSmpte2084 = 16;
    internal const int Dx11VersionDefault = 110; // AMF_DX11_0

    // AMFBuffer's interface id, from Buffer.h AMF_DECLARE_IID. Same layout as a Windows GUID.
    internal static readonly Guid BufferIid = new(0xb04b7248, 0xb6f0, 0x4321,
        0xb6, 0x91, 0xba, 0xa4, 0x74, 0x0f, 0x9f, 0xcb);

    // The AMF_RESULT values that steer the loop, from the compiled header; the rest of the long
    // enum is named in Describe below.
    internal const int Ok = 0;
    internal const int Repeat = 24;    // QueryOutput: nothing ready yet
    internal const int InputFull = 25; // SubmitInput: drain first

    internal static string Describe(int result) => result switch
    {
        0 => "AMF_OK",
        1 => "AMF_FAIL",
        2 => "AMF_UNEXPECTED",
        3 => "AMF_INVALID_ARG",
        4 => "AMF_OUT_OF_MEMORY",
        5 => "AMF_NOT_SUPPORTED",
        6 => "AMF_NOT_FOUND",
        7 => "AMF_ALREADY_INITIALIZED",
        8 => "AMF_NOT_INITIALIZED",
        23 => "AMF_EOF",
        24 => "AMF_REPEAT",
        25 => "AMF_INPUT_FULL",
        44 => "AMF_NEED_MORE_INPUT",
        _ => "AMF_RESULT " + result,
    };

    internal static void Check(int result, string call)
    {
        if (result == Ok) return;
        throw new InvalidOperationException($"{call} failed: {result} ({Describe(result)}).");
    }

    // ------------------------------------------------------------------ the variant

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct Variant
    {
        [FieldOffset(0)] internal int Type;
        [FieldOffset(8)] internal long Int64Value;   // the union starts at 8; verified by gcc
        [FieldOffset(8)] internal byte BoolValue;    // amf_bool is one byte in both bindings
        [FieldOffset(8)] internal uint RateNum;      // AMFRate: two uint32s
        [FieldOffset(12)] internal uint RateDen;
        [FieldOffset(8)] internal void* InterfaceValue;
    }

    // AMF_VARIANT_TYPE values, from Variant.h.
    internal const int VariantBool = 1;
    internal const int VariantInt64 = 2;
    internal const int VariantRate = 7;
    internal const int VariantInterface = 12;

    // ------------------------------------------------------------------ loading

    private static readonly object Gate = new();
    private static void* _factory;
    private static bool _tried;

    // Loads amfrt64.dll and initialises the factory, once per process. Null, logged once, when the
    // library is missing. The factory is a runtime-owned singleton and is never released.
    internal static void* Factory()
    {
        lock (Gate)
        {
            if (_tried) return _factory;
            _tried = true;

            var library = Kernel32.LoadLibrary("amfrt64.dll");
            if (library == 0)
            {
                Log.Info("amfrt64.dll is not present; AMF is unavailable " +
                         "(it ships with the AMD display driver)");
                return null;
            }

            // Both entry points are cdecl (AMF_CDECL_CALL), which on x64 is the one calling
            // convention there is; the distinction matters only to a 32-bit build.
            var query = Kernel32.GetProcAddress(library, "AMFQueryVersion");
            if (query != 0)
            {
                ulong version = 0;
                if (((delegate* unmanaged[Cdecl]<ulong*, int>)query)(&version) == Ok)
                {
                    Log.Info($"AMF runtime {(version >> 48) & 0xFFFF}.{(version >> 32) & 0xFFFF}" +
                             $".{(version >> 16) & 0xFFFF}");
                }
            }

            var init = Kernel32.GetProcAddress(library, "AMFInit");
            if (init == 0)
            {
                Log.Warn("amfrt64.dll has no AMFInit; AMF is unavailable");
                return null;
            }

            void* factory = null;
            var result = ((delegate* unmanaged[Cdecl]<ulong, void**, int>)init)(RequestedVersion, &factory);
            if (result != Ok || factory is null)
            {
                Log.Warn($"AMFInit failed: {result} ({Describe(result)}); AMF is unavailable");
                return null;
            }

            _factory = factory;
            return _factory;
        }
    }

    // ------------------------------------------------------------------ calls, by slot

    // AMFFactory (not refcounted): slot 0 CreateContext, slot 1 CreateComponent. Every out pointer
    // is cleared first: the AMF C binding promises nothing, and stack garbage would be Released.
    internal static int CreateContext(void* factory, out void* context)
    {
        context = null;
        fixed (void** result = &context)
            return ((delegate* unmanaged[Stdcall]<void*, void**, int>)Com.VTable(factory)[0])(
                factory, result);
    }

    internal static int CreateComponent(void* factory, void* context, string id, out void* component)
    {
        component = null;
        fixed (char* name = id)
        fixed (void** result = &component)
            return ((delegate* unmanaged[Stdcall]<void*, void*, char*, void**, int>)Com.VTable(factory)[1])(
                factory, context, name, result);
    }

    // AMFInterface, common to every refcounted object: slot 1 Release, slot 2 QueryInterface.
    internal static void Release(void* obj)
    {
        if (obj is not null)
            ((delegate* unmanaged[Stdcall]<void*, int>)Com.VTable(obj)[1])(obj);
    }

    internal static void ReleaseAndClear(ref void* obj)
    {
        if (obj is null) return;
        Release(obj);
        obj = null;
    }

    internal static int QueryInterface(void* obj, in Guid iid, out void* result)
    {
        result = null;
        fixed (Guid* id = &iid)
        fixed (void** output = &result)
            return ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)Com.VTable(obj)[2])(
                obj, id, output);
    }

    // AMFPropertyStorage, on every object with properties: slot 3 SetProperty, 4 GetProperty. The
    // variant goes by value; being larger than a register, x64 passes a pointer to a copy.
    internal static int SetProperty(void* obj, string name, Variant value)
    {
        fixed (char* property = name)
            return ((delegate* unmanaged[Stdcall]<void*, char*, Variant, int>)Com.VTable(obj)[3])(
                obj, property, value);
    }

    internal static int GetProperty(void* obj, string name, out Variant value)
    {
        value = default;
        fixed (char* property = name)
        fixed (Variant* result = &value)
            return ((delegate* unmanaged[Stdcall]<void*, char*, Variant*, int>)Com.VTable(obj)[4])(
                obj, property, result);
    }

    internal static int SetInt64(void* obj, string name, long value) =>
        SetProperty(obj, name, new Variant { Type = VariantInt64, Int64Value = value });

    internal static int SetBool(void* obj, string name, bool value) =>
        SetProperty(obj, name, new Variant { Type = VariantBool, BoolValue = value ? (byte)1 : (byte)0 });

    internal static int SetRate(void* obj, string name, uint numerator, uint denominator) =>
        SetProperty(obj, name, new Variant { Type = VariantRate, RateNum = numerator, RateDen = denominator });

    // AMFContext: slot 13 Terminate, 18 InitDX11, 49 CreateSurfaceFromDX11Native.
    internal static int ContextInitDx11(void* context, void* device) =>
        ((delegate* unmanaged[Stdcall]<void*, void*, int, int>)Com.VTable(context)[18])(
            context, device, Dx11VersionDefault);

    internal static void ContextTerminate(void* context) =>
        ((delegate* unmanaged[Stdcall]<void*, int>)Com.VTable(context)[13])(context);

    internal static int CreateSurfaceFromDx11(void* context, void* texture, out void* surface)
    {
        surface = null;
        fixed (void** result = &surface)
            return ((delegate* unmanaged[Stdcall]<void*, void*, void**, void*, int>)Com.VTable(context)[49])(
                context, texture, result, null);
    }

    // AMFComponent: slot 17 Init, 19 Terminate, 21 Flush, 22 SubmitInput, 23 QueryOutput.
    internal static int ComponentInit(void* component, int format, int width, int height) =>
        ((delegate* unmanaged[Stdcall]<void*, int, int, int, int>)Com.VTable(component)[17])(
            component, format, width, height);

    internal static void ComponentTerminate(void* component) =>
        ((delegate* unmanaged[Stdcall]<void*, int>)Com.VTable(component)[19])(component);

    internal static int SubmitInput(void* component, void* data) =>
        ((delegate* unmanaged[Stdcall]<void*, void*, int>)Com.VTable(component)[22])(component, data);

    internal static int QueryOutput(void* component, out void* data)
    {
        data = null;
        fixed (void** result = &data)
            return ((delegate* unmanaged[Stdcall]<void*, void**, int>)Com.VTable(component)[23])(
                component, result);
    }

    // AMFBuffer: slot 24 GetSize, 25 GetNative.
    internal static nuint BufferSize(void* buffer) =>
        ((delegate* unmanaged[Stdcall]<void*, nuint>)Com.VTable(buffer)[24])(buffer);

    internal static void* BufferNative(void* buffer) =>
        ((delegate* unmanaged[Stdcall]<void*, void*>)Com.VTable(buffer)[25])(buffer);
}
