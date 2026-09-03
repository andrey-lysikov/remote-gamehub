//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace RemoteGameHub.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11Texture2DDesc
{
    internal uint Width;
    internal uint Height;
    internal uint MipLevels;
    internal uint ArraySize;
    internal uint Format;
    internal uint SampleCount;
    internal uint SampleQuality;
    internal uint Usage;
    internal uint BindFlags;
    internal uint CpuAccessFlags;
    internal uint MiscFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct D3D11MappedSubresource
{
    internal void* Data;
    internal uint RowPitch;
    internal uint DepthPitch;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11Viewport
{
    internal float TopLeftX;
    internal float TopLeftY;
    internal float Width;
    internal float Height;
    internal float MinDepth;
    internal float MaxDepth;
}

// A view of one plane of a texture: the format chooses the plane, which is how D3D11 addresses
// the luma and chroma halves of P010 — R16_UNORM is the first, R16G16_UNORM the second.
[StructLayout(LayoutKind.Sequential)]
internal struct D3D11RenderTargetViewDesc
{
    internal uint Format;
    internal uint ViewDimension;
    internal uint MipSlice;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11SamplerDesc
{
    internal uint Filter;
    internal uint AddressU;
    internal uint AddressV;
    internal uint AddressW;
    internal float MipLodBias;
    internal uint MaxAnisotropy;
    internal uint ComparisonFunc;
    internal float BorderColor0;
    internal float BorderColor1;
    internal float BorderColor2;
    internal float BorderColor3;
    internal float MinLod;
    internal float MaxLod;
}

// The slice of Direct3D 11 this server uses: the duplicated desktop texture handed to the encoder,
// and the one drawing there is — the shader that turns an HDR desktop into what an encoder takes.
internal static unsafe class D3D11
{
    internal static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    internal static readonly Guid IID_ID3D11Multithread = new("9b7e4e00-342c-4106-a19f-4f2704f689f0");

    internal const uint D3D_DRIVER_TYPE_UNKNOWN = 0;
    internal const uint D3D11_SDK_VERSION = 7;

    internal const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;

    internal const uint D3D_FEATURE_LEVEL_11_1 = 0xb100;
    internal const uint D3D_FEATURE_LEVEL_11_0 = 0xb000;

    internal const uint D3D11_USAGE_DEFAULT = 0;
    internal const uint D3D11_USAGE_STAGING = 3;

    internal const uint D3D11_BIND_SHADER_RESOURCE = 0x8;
    internal const uint D3D11_BIND_RENDER_TARGET = 0x20;

    internal const uint D3D11_CPU_ACCESS_READ = 0x20000;
    internal const uint D3D11_CPU_ACCESS_WRITE = 0x10000;

    internal const uint D3D11_MAP_READ = 1;
    internal const uint D3D11_MAP_WRITE = 2;

    internal const uint D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST = 4;

    // D3D11_RTV_DIMENSION_TEXTURE2D.
    internal const uint D3D11_RTV_DIMENSION_TEXTURE2D = 4;

    // D3D11_FILTER_MIN_MAG_MIP_LINEAR, and clamping at the edges: the chroma pass reads between
    // texels, and a wrapped read at the last column would fetch the first.
    internal const uint D3D11_FILTER_MIN_MAG_MIP_LINEAR = 0x15;
    internal const uint D3D11_TEXTURE_ADDRESS_CLAMP = 3;
    internal const uint D3D11_COMPARISON_NEVER = 1;

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        void* adapter, uint driverType, nint software, uint flags,
        uint* featureLevels, uint featureLevelCount, uint sdkVersion,
        void** device, uint* featureLevel, void** context);

    // Creates a device on one specific adapter. The driver type must then be UNKNOWN: a named driver
    // type together with an adapter is rejected with an E_INVALIDARG that does not say which was wrong.
    internal static void CreateDevice(void* adapter, out void* device, out void* context,
                                      out uint featureLevel)
    {
        var levels = stackalloc uint[2] { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };

        void* createdDevice;
        void* createdContext;
        uint level;

        Com.Check(D3D11CreateDevice(
                adapter, D3D_DRIVER_TYPE_UNKNOWN, 0, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                levels, 2, D3D11_SDK_VERSION,
                &createdDevice, &level, &createdContext),
            "D3D11CreateDevice");

        device = createdDevice;
        context = createdContext;
        featureLevel = level;
    }

    // ID3D11Device slot 5: CreateTexture2D
    internal static void* CreateTexture2D(void* device, in D3D11Texture2DDesc desc)
    {
        void* texture;
        fixed (D3D11Texture2DDesc* description = &desc)
            Com.Check(((delegate* unmanaged[Stdcall]<void*, D3D11Texture2DDesc*, void*, void**, int>)
                Com.VTable(device)[5])(device, description, null, &texture), "ID3D11Device::CreateTexture2D");
        return texture;
    }

    // ID3D11Device slot 7: CreateShaderResourceView. A null description is the whole resource in
    // its own format, which is what every view here wants.
    internal static void* CreateShaderResourceView(void* device, void* resource)
    {
        void* view;
        Com.Check(((delegate* unmanaged[Stdcall]<void*, void*, void*, void**, int>)
            Com.VTable(device)[7])(device, resource, null, &view),
            "ID3D11Device::CreateShaderResourceView");
        return view;
    }

    // ID3D11Device slot 9: CreateRenderTargetView
    internal static void* CreateRenderTargetView(void* device, void* resource,
                                                 in D3D11RenderTargetViewDesc desc)
    {
        void* view;
        fixed (D3D11RenderTargetViewDesc* description = &desc)
            Com.Check(((delegate* unmanaged[Stdcall]<void*, void*, D3D11RenderTargetViewDesc*, void**, int>)
                Com.VTable(device)[9])(device, resource, description, &view),
                "ID3D11Device::CreateRenderTargetView");
        return view;
    }

    // ID3D11Device slot 12: CreateVertexShader
    internal static void* CreateVertexShader(void* device, void* bytecode, nuint length)
    {
        void* shader;
        Com.Check(((delegate* unmanaged[Stdcall]<void*, void*, nuint, void*, void**, int>)
            Com.VTable(device)[12])(device, bytecode, length, null, &shader),
            "ID3D11Device::CreateVertexShader");
        return shader;
    }

    // ID3D11Device slot 15: CreatePixelShader
    internal static void* CreatePixelShader(void* device, void* bytecode, nuint length)
    {
        void* shader;
        Com.Check(((delegate* unmanaged[Stdcall]<void*, void*, nuint, void*, void**, int>)
            Com.VTable(device)[15])(device, bytecode, length, null, &shader),
            "ID3D11Device::CreatePixelShader");
        return shader;
    }

    // ID3D11Device slot 23: CreateSamplerState
    internal static void* CreateSamplerState(void* device, in D3D11SamplerDesc desc)
    {
        void* sampler;
        fixed (D3D11SamplerDesc* description = &desc)
            Com.Check(((delegate* unmanaged[Stdcall]<void*, D3D11SamplerDesc*, void**, int>)
                Com.VTable(device)[23])(device, description, &sampler),
                "ID3D11Device::CreateSamplerState");
        return sampler;
    }

    // ID3D11DeviceContext slot 8: PSSetShaderResources
    internal static void PSSetShaderResources(void* context, uint slot, void* view) =>
        ((delegate* unmanaged[Stdcall]<void*, uint, uint, void**, void>)Com.VTable(context)[8])(
            context, slot, 1, &view);

    // ID3D11DeviceContext slot 9: PSSetShader
    internal static void PSSetShader(void* context, void* shader) =>
        ((delegate* unmanaged[Stdcall]<void*, void*, void**, uint, void>)Com.VTable(context)[9])(
            context, shader, null, 0);

    // ID3D11DeviceContext slot 10: PSSetSamplers
    internal static void PSSetSamplers(void* context, void* sampler) =>
        ((delegate* unmanaged[Stdcall]<void*, uint, uint, void**, void>)Com.VTable(context)[10])(
            context, 0, 1, &sampler);

    // ID3D11DeviceContext slot 11: VSSetShader
    internal static void VSSetShader(void* context, void* shader) =>
        ((delegate* unmanaged[Stdcall]<void*, void*, void**, uint, void>)Com.VTable(context)[11])(
            context, shader, null, 0);

    // ID3D11DeviceContext slot 13: Draw
    internal static void Draw(void* context, uint vertices) =>
        ((delegate* unmanaged[Stdcall]<void*, uint, uint, void>)Com.VTable(context)[13])(
            context, vertices, 0);

    // ID3D11DeviceContext slot 24: IASetPrimitiveTopology
    internal static void IASetPrimitiveTopology(void* context, uint topology) =>
        ((delegate* unmanaged[Stdcall]<void*, uint, void>)Com.VTable(context)[24])(context, topology);

    // ID3D11DeviceContext slot 33: OMSetRenderTargets
    internal static void OMSetRenderTargets(void* context, void* view) =>
        ((delegate* unmanaged[Stdcall]<void*, uint, void**, void*, void>)Com.VTable(context)[33])(
            context, view is null ? 0u : 1u, &view, null);

    // ID3D11DeviceContext slot 50: ClearRenderTargetView
    internal static void ClearRenderTargetView(void* context, void* view, float* colour) =>
        ((delegate* unmanaged[Stdcall]<void*, void*, float*, void>)Com.VTable(context)[50])(
            context, view, colour);

    // ID3D11DeviceContext slot 44: RSSetViewports
    internal static void RSSetViewports(void* context, in D3D11Viewport viewport)
    {
        fixed (D3D11Viewport* one = &viewport)
            ((delegate* unmanaged[Stdcall]<void*, uint, D3D11Viewport*, void>)
                Com.VTable(context)[44])(context, 1, one);
    }

    // ID3D11Texture2D slot 10 (ID3D11Resource base): GetDesc

    // ID3D11DeviceContext slot 47: CopyResource
    internal static void CopyResource(void* context, void* destination, void* source) =>
        ((delegate* unmanaged[Stdcall]<void*, void*, void*, void>)Com.VTable(context)[47])(
            context, destination, source);

    // ID3D11DeviceContext slot 14: Map
    internal static int Map(void* context, void* resource, uint subresource, uint mapType,
                            out D3D11MappedSubresource mapped)
    {
        fixed (D3D11MappedSubresource* result = &mapped)
            return ((delegate* unmanaged[Stdcall]<void*, void*, uint, uint, uint, D3D11MappedSubresource*, int>)
                Com.VTable(context)[14])(context, resource, subresource, mapType, 0, result);
    }

    // ID3D11DeviceContext slot 15: Unmap
    internal static void Unmap(void* context, void* resource, uint subresource) =>
        ((delegate* unmanaged[Stdcall]<void*, void*, uint, void>)Com.VTable(context)[15])(
            context, resource, subresource);

    // Capture and the encoder run on different threads and share the device. Without this the
    // driver may corrupt its own state, surfacing later as a torn frame or a hung encode.
    internal static void EnableMultithreadProtection(void* device)
    {
        Com.Check(Com.QueryInterface(device, IID_ID3D11Multithread, out var multithread),
            "ID3D11Device::QueryInterface(ID3D11Multithread)");
        try
        {
            // ID3D11Multithread slot 5: SetMultithreadProtected
            ((delegate* unmanaged[Stdcall]<void*, int, int>)Com.VTable(multithread)[5])(multithread, 1);
        }
        finally
        {
            Com.Release(multithread);
        }
    }
}
