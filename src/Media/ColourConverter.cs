//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using RemoteGameHub.App;
using RemoteGameHub.Native;

namespace RemoteGameHub.Media;

// An HDR desktop, which Windows composes as linear scRGB half floats, turned into the ten-bit
// BT.2020 PQ the encoders take. Two draws per frame: the luma plane, then the chroma one.
internal sealed unsafe class ColourConverter : IDisposable
{
    // BT.2020 non-constant luminance, from H.273 table 4. Kg follows from the other two.
    private const double Kr = 0.2627;
    private const double Kb = 0.0593;

    // scRGB says 1.0 is eighty nits, and PQ is written against absolute luminance.
    private const double ScRgbWhiteNits = 80.0;

    private readonly void* _device;
    private readonly void* _context;

    private void* _vertexShader;
    private void* _lumaShader;
    private void* _chromaShader;
    private void* _sampler;
    private void* _sourceView;
    private void* _lumaTarget;
    private void* _chromaTarget;
    private void* _output;
    private nint _sourceTexture;

    // The pointer overlay handed to Convert, and its view: cached the same way as the source, by
    // the texture handle, because DesktopDuplicator hands back the same one every frame.
    private void* _overlayView;
    private nint _overlayTexture;

    // A single transparent pixel, bound instead of a real overlay when there is nothing to draw:
    // the shader always has something at t1, and never needs to know whether it is real.
    private void* _dummyOverlay;
    private void* _dummyOverlayView;

    private bool _disposed;

    internal int Width { get; }
    internal int Height { get; }

    // The P010 texture the encoder is handed. Its content is whatever the last Convert wrote.
    internal nint Output => (nint)_output;

    private ColourConverter(void* device, void* context, int width, int height)
    {
        _device = device;
        _context = context;
        Width = width;
        Height = height;
    }

    // Builds the shader for one stream. Width and height must be even: a chroma sample covers two
    // pixels each way, and the duplication never hands back an odd desktop.
    internal static ColourConverter Open(nint device, nint context, int width, int height,
                                         bool fullRange)
    {
        var converter = new ColourConverter((void*)device, (void*)context, width, height);

        try
        {
            converter.Build(fullRange);
            Log.Info($"the HDR desktop is converted to ten-bit BT.2020 PQ in " +
                     $"{(fullRange ? "full" : "limited")} range before it is encoded");
            return converter;
        }
        catch
        {
            converter.Dispose();
            throw;
        }
    }

    // The frame the shader reads. Held until the texture changes, which is once per duplication:
    // a view is a driver object, and one per frame at 240 a second is not free.
    internal void Source(nint texture)
    {
        if (texture == _sourceTexture && _sourceView is not null) return;

        Com.ReleaseAndClear(ref _sourceView);
        _sourceView = D3D11.CreateShaderResourceView(_device, (void*)texture);
        _sourceTexture = texture;
    }

    // Draws the two planes, the pointer blended in from cursorOverlay if there is one. Nothing
    // here waits for the card: the encode that reads Output next is ordered behind it already.
    internal void Convert(nint texture, nint cursorOverlay = 0)
    {
        Source(texture);

        void* overlay;
        if (cursorOverlay != 0)
        {
            if (cursorOverlay != _overlayTexture || _overlayView is null)
            {
                Com.ReleaseAndClear(ref _overlayView);
                _overlayView = D3D11.CreateShaderResourceView(_device, (void*)cursorOverlay);
                _overlayTexture = cursorOverlay;
            }

            overlay = _overlayView;
        }
        else
        {
            overlay = _dummyOverlayView;
        }

        D3D11.IASetPrimitiveTopology(_context, D3D11.D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        D3D11.VSSetShader(_context, _vertexShader);
        D3D11.PSSetShaderResources(_context, 0, _sourceView);
        D3D11.PSSetShaderResources(_context, 1, overlay);
        D3D11.PSSetSamplers(_context, _sampler);

        Plane(_lumaShader, _lumaTarget, Width, Height);
        Plane(_chromaShader, _chromaTarget, Width / 2, Height / 2);

        // The views are left bound otherwise, and the next frame's write into those same textures
        // would find them still in use as a shader input.
        D3D11.OMSetRenderTargets(_context, null);
    }

    private void Plane(void* shader, void* target, int width, int height)
    {
        var viewport = new D3D11Viewport
        {
            Width = width,
            Height = height,
            MaxDepth = 1,
        };

        D3D11.OMSetRenderTargets(_context, target);
        D3D11.RSSetViewports(_context, viewport);
        D3D11.PSSetShader(_context, shader);

        // Three vertices, no buffer: the shader makes a triangle that covers the target from the
        // vertex number alone, which is cheaper than a quad and needs no input layout.
        D3D11.Draw(_context, 3);
    }

    private void Build(bool fullRange)
    {
        var source = Hlsl(fullRange);

        var vertex = D3DCompiler.Compile(source, "vs", "vs_5_0");
        var luma = D3DCompiler.Compile(source, "luma", "ps_5_0");
        var chroma = D3DCompiler.Compile(source, "chroma", "ps_5_0");

        fixed (byte* bytes = vertex) _vertexShader = D3D11.CreateVertexShader(_device, bytes, (nuint)vertex.Length);
        fixed (byte* bytes = luma) _lumaShader = D3D11.CreatePixelShader(_device, bytes, (nuint)luma.Length);
        fixed (byte* bytes = chroma) _chromaShader = D3D11.CreatePixelShader(_device, bytes, (nuint)chroma.Length);

        _sampler = D3D11.CreateSamplerState(_device, new D3D11SamplerDesc
        {
            // Linear, so that one chroma sample is the average of the four pixels it covers: at
            // half the size, the centre of a target pixel falls exactly between four of the source.
            Filter = D3D11.D3D11_FILTER_MIN_MAG_MIP_LINEAR,
            AddressU = D3D11.D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressV = D3D11.D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressW = D3D11.D3D11_TEXTURE_ADDRESS_CLAMP,
            ComparisonFunc = D3D11.D3D11_COMPARISON_NEVER,
            MaxLod = float.MaxValue,
        });

        _output = D3D11.CreateTexture2D(_device, new D3D11Texture2DDesc
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Dxgi.DXGI_FORMAT_P010,
            SampleCount = 1,
            Usage = D3D11.D3D11_USAGE_DEFAULT,
            BindFlags = D3D11.D3D11_BIND_RENDER_TARGET | D3D11.D3D11_BIND_SHADER_RESOURCE,
        });

        _lumaTarget = D3D11.CreateRenderTargetView(_device, _output, new D3D11RenderTargetViewDesc
        {
            Format = Dxgi.DXGI_FORMAT_R16_UNORM,
            ViewDimension = D3D11.D3D11_RTV_DIMENSION_TEXTURE2D,
        });

        _chromaTarget = D3D11.CreateRenderTargetView(_device, _output, new D3D11RenderTargetViewDesc
        {
            Format = Dxgi.DXGI_FORMAT_R16G16_UNORM,
            ViewDimension = D3D11.D3D11_RTV_DIMENSION_TEXTURE2D,
        });

        BuildDummyOverlay();
    }

    // One pixel, transparent, bound at t1 when there is no pointer to draw. Zeroed through a
    // render target view rather than trusted to come up that way on its own.
    private void BuildDummyOverlay()
    {
        _dummyOverlay = D3D11.CreateTexture2D(_device, new D3D11Texture2DDesc
        {
            Width = 1,
            Height = 1,
            MipLevels = 1,
            ArraySize = 1,
            Format = Dxgi.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleCount = 1,
            Usage = D3D11.D3D11_USAGE_DEFAULT,
            BindFlags = D3D11.D3D11_BIND_SHADER_RESOURCE | D3D11.D3D11_BIND_RENDER_TARGET,
        });

        var view = D3D11.CreateRenderTargetView(_device, _dummyOverlay, new D3D11RenderTargetViewDesc
        {
            Format = Dxgi.DXGI_FORMAT_B8G8R8A8_UNORM,
            ViewDimension = D3D11.D3D11_RTV_DIMENSION_TEXTURE2D,
        });

        try
        {
            var clear = stackalloc float[4];
            D3D11.ClearRenderTargetView(_context, view, clear);
        }
        finally
        {
            Com.Release(view);
        }

        _dummyOverlayView = D3D11.CreateShaderResourceView(_device, _dummyOverlay);
    }

    // The whole shader, written with this stream's numbers in it rather than fed a constant
    // buffer: it is built once per stream, and the arithmetic is then plain to read in the source.
    internal static string Hlsl(bool fullRange)
    {
        var (y, u, v) = Vectors(fullRange);

        return $$"""
            Texture2D<float4> source : register(t0);

            // The pointer, drawn by GDI onto its own eight-bit BGRA texture the same size as
            // source (alpha zero where there is nothing); a one-pixel stand-in when there is none.
            Texture2D<float4> overlay : register(t1);

            SamplerState blend : register(s0);

            struct vertex { float4 position : SV_Position; float2 texel : TEXCOORD0; };

            // One triangle larger than the target, from the vertex number: no buffer, no layout.
            vertex vs(uint id : SV_VertexID)
            {
                vertex pixel;
                pixel.texel = float2((id << 1) & 2, id & 2);
                pixel.position = float4(pixel.texel * float2(2, -2) + float2(-1, 1), 0, 1);
                return pixel;
            }

            // SMPTE ST 2084, on absolute luminance in nits.
            float3 pq(float3 nits)
            {
                static const float m1 = 2610.0 / 4096.0 / 4;
                static const float m2 = 2523.0 / 4096.0 * 128;
                static const float c1 = 3424.0 / 4096.0;
                static const float c2 = 2413.0 / 4096.0 * 32;
                static const float c3 = 2392.0 / 4096.0 * 32;

                float3 l = pow(saturate(nits / 10000.0), m1);
                return pow((c1 + c2 * l) / (1 + c3 * l), m2);
            }

            // scRGB is linear on Rec. 709 primaries; Rec. 2100 wants Rec. 2020 primaries and PQ.
            float3 encode(float3 rgb)
            {
                static const float3x3 toRec2020 =
                {
                    0.627402, 0.329292, 0.043306,
                    0.069095, 0.919544, 0.011360,
                    0.016394, 0.088028, 0.895578
                };

                return pq(mul(toRec2020, rgb) * {{Number(ScRgbWhiteNits)}});
            }

            // GDI drew the pointer in plain sRGB, with no notion of nits. Undoing the sRGB curve
            // puts it on the same Rec. 709 linear scale scRGB already uses for the desktop.
            float3 fromSrgb(float3 c)
            {
                return c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4);
            }

            // The desktop with the pointer blended over it, in the linear space both are already
            // in: alpha zero leaves the desktop untouched, which is what the one-pixel stand-in is.
            float3 picture(float4 desktop, float4 pointer)
            {
                return lerp(desktop.rgb, fromSrgb(pointer.rgb), pointer.a);
            }

            float luma(vertex pixel) : SV_Target
            {
                float4 desktop = source.Load(int3(pixel.position.xy, 0));
                float4 pointer = overlay.Load(int3(pixel.position.xy, 0));
                float3 c = encode(picture(desktop, pointer));
                return dot(float3({{Number(y.R)}}, {{Number(y.G)}}, {{Number(y.B)}}), c) + {{Number(y.Add)}};
            }

            float2 chroma(vertex pixel) : SV_Target
            {
                float4 desktop = source.Sample(blend, pixel.texel);
                float4 pointer = overlay.Sample(blend, pixel.texel);
                float3 c = encode(picture(desktop, pointer));
                return float2(
                    dot(float3({{Number(u.R)}}, {{Number(u.G)}}, {{Number(u.B)}}), c) + {{Number(u.Add)}},
                    dot(float3({{Number(v.R)}}, {{Number(v.G)}}, {{Number(v.B)}}), c) + {{Number(v.Add)}});
            }
            """;
    }

    internal readonly record struct Vector(double R, double G, double B, double Add);

    // The matrix of H.273 section 8.3, for ten bits written into a unorm target: the codes are
    // scaled by 1023 rather than by 65535, which is what puts them in the top ten bits of P010.
    internal static (Vector Y, Vector U, Vector V) Vectors(bool fullRange)
    {
        const double kg = 1.0 - Kr - Kb;
        const double codes = (1 << 10) - 1;

        var lumaScale = (fullRange ? codes : 4 * 219) / codes;
        var lumaOffset = (fullRange ? 0 : 4 * 16) / codes;
        var chromaScale = (fullRange ? codes : 4 * 224) / codes;
        var chromaOffset = (fullRange ? 1 << 9 : 4 * 128) / codes;

        return (
            new Vector(Kr * lumaScale, kg * lumaScale, Kb * lumaScale, lumaOffset),
            new Vector(-0.5 * Kr / (1.0 - Kb) * chromaScale, -0.5 * kg / (1.0 - Kb) * chromaScale,
                       0.5 * chromaScale, chromaOffset),
            new Vector(0.5 * chromaScale, -0.5 * kg / (1.0 - Kr) * chromaScale,
                       -0.5 * Kb / (1.0 - Kr) * chromaScale, chromaOffset));
    }

    private static string Number(double value) =>
        value.ToString("0.#########", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Com.ReleaseAndClear(ref _lumaTarget);
        Com.ReleaseAndClear(ref _chromaTarget);
        Com.ReleaseAndClear(ref _output);
        Com.ReleaseAndClear(ref _sourceView);
        Com.ReleaseAndClear(ref _overlayView);
        Com.ReleaseAndClear(ref _dummyOverlayView);
        Com.ReleaseAndClear(ref _dummyOverlay);
        Com.ReleaseAndClear(ref _sampler);
        Com.ReleaseAndClear(ref _vertexShader);
        Com.ReleaseAndClear(ref _lumaShader);
        Com.ReleaseAndClear(ref _chromaShader);
    }
}
