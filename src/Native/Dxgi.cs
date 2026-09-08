//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace RemoteGameHub.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct Luid
{
    internal uint LowPart;
    internal int HighPart;

    public override string ToString() => $"{HighPart:X8}:{LowPart:X8}";
}

[StructLayout(LayoutKind.Sequential)]
internal struct Rect
{
    internal int Left, Top, Right, Bottom;

    internal int Width => Right - Left;
    internal int Height => Bottom - Top;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Point
{
    internal int X, Y;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct DxgiAdapterDesc1
{
    internal fixed char Description[128];
    internal uint VendorId;
    internal uint DeviceId;
    internal uint SubSysId;
    internal int Revision;
    internal nuint DedicatedVideoMemory;
    internal nuint DedicatedSystemMemory;
    internal nuint SharedSystemMemory;
    internal Luid AdapterLuid;
    internal uint Flags;

    internal string Name
    {
        get { fixed (char* text = Description) return new string(text); }
    }
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct DxgiOutputDesc
{
    internal fixed char DeviceName[32];
    internal Rect DesktopCoordinates;
    internal int AttachedToDesktop;
    internal uint Rotation;
    internal nint Monitor;

    internal string Name
    {
        get { fixed (char* text = DeviceName) return new string(text); }
    }
}

// The same, plus what the screen says about its colour. The primaries and the white point are
// chromaticities; the luminances are nits, and MaxFullFrame is the brightest a whole white screen.
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DxgiOutputDesc1
{
    internal fixed char DeviceName[32];
    internal Rect DesktopCoordinates;
    internal int AttachedToDesktop;
    internal uint Rotation;
    internal nint Monitor;
    internal uint BitsPerColor;
    internal uint ColorSpace;
    internal float RedPrimaryX;
    internal float RedPrimaryY;
    internal float GreenPrimaryX;
    internal float GreenPrimaryY;
    internal float BluePrimaryX;
    internal float BluePrimaryY;
    internal float WhitePointX;
    internal float WhitePointY;
    internal float MinLuminance;
    internal float MaxLuminance;
    internal float MaxFullFrameLuminance;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DxgiModeDesc
{
    internal uint Width;
    internal uint Height;
    internal uint RefreshNumerator;
    internal uint RefreshDenominator;
    internal uint Format;
    internal uint ScanlineOrdering;
    internal uint Scaling;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DxgiOutduplDesc
{
    internal DxgiModeDesc ModeDesc;
    internal uint Rotation;
    // Set when the desktop is rendered on the processor (a basic display driver or a remote
    // session): the texture is then in system memory and cannot go to a hardware encoder.
    internal int DesktopImageInSystemMemory;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DxgiOutduplPointerPosition
{
    internal Point Position;
    internal int Visible;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DxgiOutduplFrameInfo
{
    internal long LastPresentTime;
    internal long LastMouseUpdateTime;
    // Zero means only the pointer moved. That wake-up is not a frame: encoding it again sends the
    // client a duplicate of what it already has.
    internal uint AccumulatedFrames;
    internal int RectsCoalesced;
    internal int ProtectedContentMaskedOut;
    internal DxgiOutduplPointerPosition PointerPosition;
    internal uint TotalMetadataBufferSize;
    internal uint PointerShapeBufferSize;
}

// What the pointer looks like, as the duplication hands it over: a monochrome AND/XOR pair of
// masks, a colour image with alpha, or one whose alpha is the AND mask rather than transparency.
[StructLayout(LayoutKind.Sequential)]
internal struct DxgiOutduplPointerShapeInfo
{
    internal uint Type;
    internal int Width;
    internal int Height;
    // Bytes per row of the buffer, which is not the width times four.
    internal int Pitch;
    internal Point HotSpot;
}

// The slice of DXGI this server uses: enumerate adapters and outputs, and duplicate one desktop.
// The slot numbers come from dxgi.h and dxgi1_2.h and must not be reordered.
internal static unsafe class Dxgi
{
    internal static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    internal static readonly Guid IID_IDXGIOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");

    // The output interface that takes a format list, which is how the desktop is duplicated in HDR.
    internal static readonly Guid IID_IDXGIOutput5 = new("80a07424-ab52-42eb-833c-0c42fd282d98");
    internal static readonly Guid IID_IDXGIOutput6 = new("068346e8-aaec-4b84-add7-137f513f77a1");
    internal static readonly Guid IID_IDXGISurface1 = new("4ae63092-6327-4c1b-80ae-bfe12ea32b86");

    // From dxgi.h. They are not consecutive and easy to transpose, and each steers the capture
    // loop down a different path, so check them against the header rather than memory.
    internal const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
    internal const int DXGI_ERROR_UNSUPPORTED = unchecked((int)0x887A0004);
    internal const int DXGI_ERROR_DEVICE_REMOVED = unchecked((int)0x887A0005);
    internal const int DXGI_ERROR_NOT_CURRENTLY_AVAILABLE = unchecked((int)0x887A0022);
    internal const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
    internal const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
    internal const int DXGI_ERROR_SESSION_DISCONNECTED = unchecked((int)0x887A0028);
    internal const int DXGI_ERROR_ACCESS_DENIED = unchecked((int)0x887A002B);
    internal const int E_ACCESSDENIED = unchecked((int)0x80070005);

    internal const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    internal const uint DXGI_FORMAT_R10G10B10A2_UNORM = 24;

    // What an HDR desktop is composed in: linear scRGB, where 1.0 is the eighty nits of white a
    // standard-range desktop is drawn at, and values above it are the highlights.
    internal const uint DXGI_FORMAT_R16G16B16A16_FLOAT = 10;

    // Ten-bit 4:2:0 in two planes, which is what the encoders take for high dynamic range. The
    // planes are addressed by the format of the view: R16_UNORM is luma, R16G16_UNORM is chroma.
    internal const uint DXGI_FORMAT_P010 = 104;
    internal const uint DXGI_FORMAT_R16_UNORM = 56;
    internal const uint DXGI_FORMAT_R16G16_UNORM = 35;

    // A high-dynamic-range desktop is really composited as linear half-floats with Rec.709
    // primaries. No encoder takes that, which is what the ten-bit request above exists to avoid.

    // Set on the Microsoft Basic Render Driver, which is never a card that can encode.
    internal const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;

    internal const uint VendorNvidia = 0x10DE;
    internal const uint VendorAmd = 0x1002;
    internal const uint VendorIntel = 0x8086;

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(Guid* iid, void** factory);

    internal static void* CreateFactory()
    {
        void* factory;
        fixed (Guid* iid = &IID_IDXGIFactory1)
            Com.Check(CreateDXGIFactory1(iid, &factory), "CreateDXGIFactory1");
        return factory;
    }

    // IDXGIFactory1 slot 12: EnumAdapters1
    internal static int EnumAdapters1(void* factory, uint index, out void* adapter)
    {
        fixed (void** output = &adapter)
            return ((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)Com.VTable(factory)[12])(
                factory, index, output);
    }

    // IDXGIAdapter1 slot 10: GetDesc1
    internal static DxgiAdapterDesc1 GetAdapterDesc(void* adapter)
    {
        DxgiAdapterDesc1 desc;
        Com.Check(((delegate* unmanaged[Stdcall]<void*, DxgiAdapterDesc1*, int>)Com.VTable(adapter)[10])(
            adapter, &desc), "IDXGIAdapter1::GetDesc1");
        return desc;
    }

    // IDXGIAdapter slot 7: EnumOutputs
    internal static int EnumOutputs(void* adapter, uint index, out void* output)
    {
        fixed (void** result = &output)
            return ((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)Com.VTable(adapter)[7])(
                adapter, index, result);
    }

    // IDXGIOutput slot 7: GetDesc
    internal static DxgiOutputDesc GetOutputDesc(void* output)
    {
        DxgiOutputDesc desc;
        Com.Check(((delegate* unmanaged[Stdcall]<void*, DxgiOutputDesc*, int>)Com.VTable(output)[7])(
            output, &desc), "IDXGIOutput::GetDesc");
        return desc;
    }

    // IDXGIOutput1 slot 22: DuplicateOutput
    internal static int DuplicateOutput(void* output1, void* device, out void* duplication)
    {
        fixed (void** result = &duplication)
            return ((delegate* unmanaged[Stdcall]<void*, void*, void**, int>)Com.VTable(output1)[22])(
                output1, device, result);
    }

    // IDXGIOutput5 slot 26: DuplicateOutput1. Unlike DuplicateOutput it takes a format list, which
    // is the only way to get an HDR desktop as it is rather than converted to eight-bit BGRA.
    internal static int DuplicateOutput1(void* output5, void* device, uint* formats,
                                         uint formatCount, out void* duplication)
    {
        duplication = null;
        fixed (void** result = &duplication)
            return ((delegate* unmanaged[Stdcall]<void*, void*, uint, uint, uint*, void**, int>)
                Com.VTable(output5)[26])(output5, device, 0, formatCount, formats, result);
    }

    // IDXGIOutput6 slot 27: GetDesc1. What the screen says about its own colour — the primaries
    // it can show and how bright it goes — which is what a client is told about an HDR stream.
    internal static bool GetOutputDesc1(void* output, out DxgiOutputDesc1 desc)
    {
        desc = default;

        if (Com.QueryInterface(output, IID_IDXGIOutput6, out var output6) < 0) return false;

        try
        {
            fixed (DxgiOutputDesc1* result = &desc)
                return ((delegate* unmanaged[Stdcall]<void*, DxgiOutputDesc1*, int>)
                    Com.VTable(output6)[27])(output6, result) >= 0;
        }
        finally
        {
            Com.Release(output6);
        }
    }

    // IDXGIOutputDuplication slot 7: GetDesc
    internal static DxgiOutduplDesc GetDuplicationDesc(void* duplication)
    {
        DxgiOutduplDesc desc;
        ((delegate* unmanaged[Stdcall]<void*, DxgiOutduplDesc*, void>)Com.VTable(duplication)[7])(
            duplication, &desc);
        return desc;
    }

    // IDXGIOutputDuplication slot 8: AcquireNextFrame
    internal static int AcquireNextFrame(void* duplication, uint timeoutMs,
                                         out DxgiOutduplFrameInfo info, out void* resource)
    {
        fixed (DxgiOutduplFrameInfo* frame = &info)
        fixed (void** result = &resource)
            return ((delegate* unmanaged[Stdcall]<void*, uint, DxgiOutduplFrameInfo*, void**, int>)
                Com.VTable(duplication)[8])(duplication, timeoutMs, frame, result);
    }

    // IDXGIOutputDuplication slot 11: GetFramePointerShape. Handed out only when the shape has
    // changed, so what comes back is kept until the next one arrives.
    internal static int GetFramePointerShape(void* duplication, uint bufferSize, void* buffer,
                                             out uint written, out DxgiOutduplPointerShapeInfo info)
    {
        fixed (uint* size = &written)
        fixed (DxgiOutduplPointerShapeInfo* shape = &info)
            return ((delegate* unmanaged[Stdcall]<void*, uint, void*, uint*,
                                                  DxgiOutduplPointerShapeInfo*, int>)
                Com.VTable(duplication)[11])(duplication, bufferSize, buffer, size, shape);
    }

    // IDXGIOutputDuplication slot 14: ReleaseFrame
    internal static int ReleaseFrame(void* duplication) =>
        ((delegate* unmanaged[Stdcall]<void*, int>)Com.VTable(duplication)[14])(duplication);

    // IDXGISurface1 slot 11: GetDC. The false means "keep what is already there": the desktop was
    // just copied into this texture, and discarding it would hand back a blank surface.
    internal static int GetDC(void* surface1, out nint dc)
    {
        fixed (nint* result = &dc)
            return ((delegate* unmanaged[Stdcall]<void*, int, nint*, int>)Com.VTable(surface1)[11])(
                surface1, 0, result);
    }

    // IDXGISurface1 slot 12: ReleaseDC. The null rectangle means the whole surface changed.
    internal static int ReleaseDC(void* surface1) =>
        ((delegate* unmanaged[Stdcall]<void*, void*, int>)Com.VTable(surface1)[12])(surface1, null);

    internal static string DescribeVendor(uint vendorId) => vendorId switch
    {
        VendorNvidia => "NVIDIA",
        VendorAmd => "AMD",
        VendorIntel => "Intel",
        _ => $"vendor 0x{vendorId:X4}",
    };
}
