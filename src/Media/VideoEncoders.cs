//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using RemoteGameHub.App;
using RemoteGameHub.Native;

namespace RemoteGameHub.Media;

// One encoder session on the graphics card, over the device and texture the capturer owns. NVENC
// and AMF, and deliberately no software third: the processor cannot hold this server's latency.
internal interface IVideoEncoder : IDisposable
{
    // The codec actually opened — never VideoCodec.Auto.
    VideoCodec Codec { get; }

    int Width { get; }
    int Height { get; }

    // The parameter sets the client needs before the first frame (SPS/PPS, and VPS for HEVC).
    ReadOnlyMemory<byte> Header { get; }

    // Encodes the texture the capturer just filled. Returns the access unit, or empty while the
    // pipeline fills — which these settings never let happen, but a caller must tolerate.
    ReadOnlyMemory<byte> Encode(nint texture, bool forceKeyFrame);
}

// What the startup probe learned: the encoder this machine will stream with and the codecs it can
// produce, or the reason nothing opened. /serverinfo answers from this.
internal sealed record EncoderCapabilities(
    VideoEncoder Encoder,
    bool H264,
    bool Hevc,
    // AV1, which the newest cards encode: NVIDIA from Ada, AMD from RDNA 3. Offered to the client,
    // which chooses; a client that cannot decode it never asks.
    bool Av1,
    // Whether the card can encode colour at full resolution rather than quartered. Asked per
    // codec because the answer is the card's for that codec, and offered per codec to the client.
    bool H264Yuv444,
    bool HevcYuv444,
    // Whether ten-bit HEVC is available, which is what high dynamic range needs. Only offered to
    // a client when this is true, and only used when the client says it wants it.
    bool Hdr,
    // The same question for AV1: NVENC only, and only on the cards new enough to have an AV1
    // encoder at all. AMF is not asked — there is no AMD card here to check it against.
    bool Av1Hdr,
    string? Refusal)
{
    internal bool CanStream => Refusal is null && (H264 || Hevc || Av1);

    // Whether this card can send high dynamic range in some codec, whichever the client and this
    // card agree on — the one question a refusal message or a status page needs.
    internal bool AnyHdr => Hdr || Av1Hdr;

    internal static EncoderCapabilities Refused(string refusal) =>
        new(VideoEncoder.Auto, false, false, false, false, false, false, false, refusal);
}

// The choice and the opening of the encoder. The implementation follows the adapter that owns the
// captured output unless [Video] Encoder names one; there is no fallback from a named one.
internal static unsafe class VideoEncoders
{
    // Opens and immediately closes an encoder at startup, so that /serverinfo reports what the card
    // can really do. On a device of its own: the capturer's does not exist yet.
    internal static EncoderCapabilities Probe(GraphicsAdapter adapter, AppConfig config)
    {
        var chosen = config.Encoder;
        if (chosen == VideoEncoder.Auto)
        {
            chosen = adapter.IsNvidia ? VideoEncoder.NvEnc
                : adapter.IsAmd ? VideoEncoder.Amf
                : VideoEncoder.Auto;
        }

        if (chosen == VideoEncoder.Auto)
        {
            // Preflight refuses non-NVIDIA/AMD adapters before this runs, so reaching here means
            // the rule there and the rule here have drifted apart — worth saying plainly.
            return EncoderCapabilities.Refused(
                $"adapter \"{adapter.Name}\" is neither NVIDIA nor AMD; there is no encoder for it.");
        }

        void* device = null;
        void* context = null;
        void* dxgiAdapter = null;

        try
        {
            var factory = Dxgi.CreateFactory();
            try
            {
                Com.Check(Dxgi.EnumAdapters1(factory, (uint)adapter.Index, out dxgiAdapter),
                    $"IDXGIFactory1::EnumAdapters1({adapter.Index})");
            }
            finally
            {
                Com.Release(factory);
            }

            D3D11.CreateDevice(dxgiAdapter, out device, out context, out _);

            var capabilities = chosen == VideoEncoder.NvEnc
                ? ProbeNvenc(device)
                : ProbeAmf(device);

            return capabilities;
        }
        catch (Exception error)
        {
            return EncoderCapabilities.Refused(
                $"the encoder probe failed: {error.Message}");
        }
        finally
        {
            Com.ReleaseAndClear(ref context);
            Com.ReleaseAndClear(ref device);
            Com.ReleaseAndClear(ref dxgiAdapter);
        }
    }

    // Opens the encoder for a stream, on the device the capturer owns. VideoCodec.Auto resolves to
    // H.264, the codec every client takes, until the RTSP negotiation can say otherwise.
    internal static IVideoEncoder Open(nint device, EncoderCapabilities capabilities,
                                       VideoCodec codec, int width, int height,
                                       int bitrateKbps, int fps, bool hdr = false,
                                       bool yuv444 = false,
                                       StreamQuality quality = StreamQuality.High)
    {
        if (!capabilities.CanStream)
        {
            throw new InvalidOperationException(
                capabilities.Refusal ?? "no encoder opened at startup.");
        }

        if (codec == VideoCodec.Auto)
            codec = capabilities.H264 ? VideoCodec.H264 :
                    capabilities.Hevc ? VideoCodec.Hevc : VideoCodec.Av1;

        var available = codec switch
        {
            VideoCodec.H264 => capabilities.H264,
            VideoCodec.Hevc => capabilities.Hevc,
            VideoCodec.Av1 => capabilities.Av1,
            _ => false,
        };

        if (!available)
        {
            throw new InvalidOperationException(
                $"the {Name(codec)} codec was asked for, but the startup probe found the card " +
                "cannot encode it.");
        }

        var hdrAvailable = codec switch
        {
            VideoCodec.Hevc => capabilities.Hdr,
            VideoCodec.Av1 => capabilities.Av1Hdr,
            _ => false,
        };

        if (hdr && !hdrAvailable)
        {
            throw new InvalidOperationException(
                $"high dynamic range was asked for, but the startup probe found no ten-bit " +
                $"{Name(codec)} encoder on this card.");
        }

        if (yuv444 &&
            !(codec == VideoCodec.H264 ? capabilities.H264Yuv444 : capabilities.HevcYuv444))
        {
            throw new InvalidOperationException(
                "4:4:4 colour was asked for, but the startup probe found the card cannot encode " +
                $"it in {Name(codec)}.");
        }

        return capabilities.Encoder == VideoEncoder.NvEnc
            ? NvencEncoder.Open(device, codec, width, height, bitrateKbps, fps, hdr, yuv444, quality)
            : AmfEncoder.Open(device, codec, width, height, bitrateKbps, fps, hdr, quality);
    }

    // The codec as it is written everywhere a person reads it.
    internal static string Name(VideoCodec codec) => codec switch
    {
        VideoCodec.Hevc => "HEVC",
        VideoCodec.Av1 => "AV1",
        _ => "H.264",
    };

    // ------------------------------------------------------------------ the probes

    private static EncoderCapabilities ProbeNvenc(void* device)
    {
        var api = NvEnc.Api();
        if (api is null)
        {
            return EncoderCapabilities.Refused(
                "NVENC is not available: nvEncodeAPI64.dll could not be loaded. It ships with " +
                "the NVIDIA display driver; installing or updating the driver is the fix.");
        }

        var open = new NvEnc.OpenSessionParams
        {
            Version = NvEnc.OpenSessionParamsVer,
            DeviceType = NvEnc.DeviceTypeDirectX,
            Device = device,
            ApiVersion = NvEnc.ApiVersion,
        };

        void* session;
        var status = api->OpenSessionEx(&open, &session);
        if (status != NvEnc.StatusSuccess)
        {
            return EncoderCapabilities.Refused(
                $"NVENC refused to open a session: {NvEnc.Describe(status)}. " +
                "Updating the NVIDIA driver is the usual fix.");
        }

        try
        {
            uint count = 0;
            NvEnc.Check(api->GetEncodeGuidCount(session, &count), "NvEncGetEncodeGUIDCount");

            var guids = stackalloc Guid[(int)count];
            uint returned = 0;
            NvEnc.Check(api->GetEncodeGuids(session, guids, count, &returned), "NvEncGetEncodeGUIDs");

            bool h264 = false, hevc = false, av1 = false;
            for (var i = 0; i < returned; i++)
            {
                if (guids[i] == NvEnc.CodecH264) h264 = true;
                if (guids[i] == NvEnc.CodecHevc) hevc = true;
                if (guids[i] == NvEnc.CodecAv1) av1 = true;
            }

            // Asked per codec: HEVC and AV1 are the two this server can send HDR in, and a card
            // new enough for one is not necessarily new enough for the other.
            var tenBit = hevc && Supports(api, session, NvEnc.CodecHevc,
                NvEnc.CapsSupport10BitEncode);
            var av1TenBit = av1 && Supports(api, session, NvEnc.CodecAv1,
                NvEnc.CapsSupport10BitEncode);

            return new EncoderCapabilities(VideoEncoder.NvEnc, h264, hevc, av1,
                h264 && Supports(api, session, NvEnc.CodecH264, NvEnc.CapsSupportYuv444Encode),
                hevc && Supports(api, session, NvEnc.CodecHevc, NvEnc.CapsSupportYuv444Encode),
                tenBit, av1TenBit, null);
        }
        finally
        {
            api->DestroyEncoder(session);
        }
    }

    private static EncoderCapabilities ProbeAmf(void* device)
    {
        var factory = Amf.Factory();
        if (factory is null)
        {
            return EncoderCapabilities.Refused(
                "AMF is not available: amfrt64.dll could not be loaded. It ships with the AMD " +
                "display driver; installing or updating the driver is the fix.");
        }

        void* context = null;
        try
        {
            Amf.Check(Amf.CreateContext(factory, out context), "AMFFactory::CreateContext");
            Amf.Check(Amf.ContextInitDx11(context, device), "AMFContext::InitDX11");

            // Creating the component is the capability test: the runtime refuses to create one
            // the hardware has no engine for. Nothing is initialised, so this is cheap.
            var h264 = TryComponent(factory, context, "AMFVideoEncoderVCE_AVC");
            var hevc = TryComponent(factory, context, "AMFVideoEncoderHW_HEVC");
            var av1 = TryComponent(factory, context, "AMFVideoEncoderHW_AV1");

            // Every part that encodes HEVC does ten bits, none do 4:4:4, and AV1 in ten bits is
            // not offered: nothing has checked it against real AMD hardware, unlike HEVC above.
            return new EncoderCapabilities(VideoEncoder.Amf, h264, hevc, av1, false, false, hevc,
                false, null);
        }
        finally
        {
            if (context is not null)
            {
                Amf.ContextTerminate(context);
                Amf.Release(context);
            }
        }
    }

    // One capability question, answered as a plain yes or no.
    private static bool Supports(NvEnc.FunctionList* api, void* session, Guid codec, int capability)
    {
        var query = new NvEnc.CapsParam
        {
            Version = NvEnc.CapsParamVer,
            CapsToQuery = capability,
        };

        int value;
        return api->GetEncodeCaps(session, codec, &query, &value) == NvEnc.StatusSuccess &&
               value != 0;
    }

    private static bool TryComponent(void* factory, void* context, string id)
    {
        if (Amf.CreateComponent(factory, context, id, out var component) != Amf.Ok) return false;
        Amf.Release(component);
        return true;
    }

}
