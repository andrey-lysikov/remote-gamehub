//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using RemoteGameHub.App;
using RemoteGameHub.Native;

namespace RemoteGameHub.Media;

// The AMF session: the capturer's BGRA texture goes in, H.264 or HEVC access units come out, one
// frame at a time. Names and enum values come from VideoEncoderVCE.h and VideoEncoderHEVC.h.
internal sealed unsafe class AmfEncoder : IVideoEncoder
{
    // How long one frame may reasonably take before the encoder is declared stuck.
    private const int OutputDeadlineMs = 500;

    // The coarsest quantiser allowed at each level, on the 0-to-51 scale the AVC and HEVC encoders
    // use. The same numbers as NVENC's, so a level looks the same whichever card is in the machine.
    private static int MaxQpFor(StreamQuality quality) => quality switch
    {
        StreamQuality.Low => 34,
        StreamQuality.Medium => 28,
        StreamQuality.Lossless => 16,
        _ => 24,
    };

    // The quality preset, whose numbers AMF gives differently in every codec's header: AVC counts
    // BALANCED 0, SPEED 1, QUALITY 2, HIGH_QUALITY 3; HEVC 0/5/10/15; AV1 100/70/30/0 reversed.
    private int QualityPresetFor(StreamQuality quality) => Codec switch
    {
        VideoCodec.Hevc => quality switch
        {
            StreamQuality.Low => 10, StreamQuality.Medium => 5,
            StreamQuality.Lossless => 15, _ => 0,
        },
        VideoCodec.Av1 => quality switch
        {
            StreamQuality.Low => 100, StreamQuality.Medium => 70,
            StreamQuality.Lossless => 0, _ => 30,
        },
        _ => quality switch
        {
            StreamQuality.Low => 1, StreamQuality.Medium => 0,
            StreamQuality.Lossless => 3, _ => 2,
        },
    };

    private readonly bool _hdr;
    private readonly StreamQuality _quality;
    private void* _context;
    private void* _component;
    private byte[] _header = Array.Empty<byte>();
    // The access unit, grown once and reused. Allocated per frame it was 2.5 MB a second, and at
    // 4K every key frame is over the 85 KB the large object heap starts at.
    private byte[] _unit = Array.Empty<byte>();

    private bool _disposed;

    public VideoCodec Codec { get; }
    public int Width { get; }
    public int Height { get; }
    public ReadOnlyMemory<byte> Header => _header;

    private AmfEncoder(VideoCodec codec, int width, int height, bool hdr, StreamQuality quality)
    {
        Codec = codec;
        Width = width;
        Height = height;
        _hdr = hdr;
        _quality = quality;
    }

    internal static AmfEncoder Open(nint device, VideoCodec codec, int width, int height,
                                    int bitrateKbps, int fps, bool hdr = false,
                                    StreamQuality quality = StreamQuality.High)
    {
        var factory = Amf.Factory();
        if (factory is null)
        {
            throw new InvalidOperationException(
                "AMF is not available: amfrt64.dll could not be loaded. " +
                "It ships with the AMD display driver.");
        }

        if (codec == VideoCodec.Auto) codec = VideoCodec.H264;

        // Ten bits only exist in HEVC as far as this protocol is concerned.
        if (hdr && codec != VideoCodec.Hevc)
        {
            throw new InvalidOperationException(
                $"high dynamic range needs HEVC; {VideoEncoders.Name(codec)} is not sent in " +
                "ten bits by this server.");
        }

        var encoder = new AmfEncoder(codec, width, height, hdr, quality);
        try
        {
            encoder.OpenPipeline(factory, device, bitrateKbps, fps);
            encoder.ReadExtraData();
            return encoder;
        }
        catch
        {
            encoder.Dispose();
            throw;
        }
    }

    private bool Hevc => Codec == VideoCodec.Hevc;
    private bool Av1 => Codec == VideoCodec.Av1;

    // AMF names every property after the codec it belongs to: no prefix for AVC, "Hevc" and "Av1"
    // for the others. The few whose names differ by more than the prefix are written out below.
    private string P(string name) => Codec switch
    {
        VideoCodec.Hevc => "Hevc" + name,
        VideoCodec.Av1 => "Av1" + name,
        _ => name,
    };

    private void OpenPipeline(void* factory, nint device, int bitrateKbps, int fps)
    {
        Amf.Check(Amf.CreateContext(factory, out _context), "AMFFactory::CreateContext");
        Amf.Check(Amf.ContextInitDx11(_context, (void*)device), "AMFContext::InitDX11");

        // AVC ultra-low-latency usage is broken on some driver/AMF combinations (AMF issue 410),
        // so when Init refuses it the component is rebuilt once with plain low-latency usage.
        if (!TryOpenComponent(factory, bitrateKbps, fps, ultraLowLatency: true, out var refusal))
        {
            Log.Warn($"the AMF encoder refused ultra-low-latency usage ({refusal}); " +
                     "retrying with low-latency usage (AMF issue 410)");

            if (!TryOpenComponent(factory, bitrateKbps, fps, ultraLowLatency: false, out refusal))
                throw new InvalidOperationException($"AMFComponent::Init failed: {refusal}");
        }

        Log.Info($"AMF session open: {VideoEncoders.Name(Codec)}" +
                 (_hdr ? " Main10 (high dynamic range)" : string.Empty) +
                 $", {Width}x{Height}, {bitrateKbps} kbit/s CBR at {fps} fps, " +
                 $"{_quality.ToString().ToLowerInvariant()} quality");
    }

    private bool TryOpenComponent(void* factory, int bitrateKbps, int fps, bool ultraLowLatency,
                                  out string refusal)
    {
        if (_component is not null)
        {
            Amf.ComponentTerminate(_component);
            Amf.ReleaseAndClear(ref _component);
        }

        var result = Amf.CreateComponent(factory, _context, Codec switch
        {
            VideoCodec.Hevc => "AMFVideoEncoderHW_HEVC",
            VideoCodec.Av1 => "AMFVideoEncoderHW_AV1",
            _ => "AMFVideoEncoderVCE_AVC",
        }, out _component);
        if (result != Amf.Ok)
        {
            refusal = $"CreateComponent: {Amf.Describe(result)}";
            return false;
        }

        var bits = Math.Min((long)bitrateKbps * 1000, int.MaxValue);

        // Usage first: it "fully configures parameter set". The enumerations disagree — ULTRA_LOW
        // is 1 for AVC and HEVC and 2 for AV1, where 1 is plain LOW_LATENCY.
        var ultraLowLatencyValue = Av1 ? 2 : 1;
        var lowLatencyValue = Av1 ? 1 : 2;
        Amf.SetInt64(_component, P("Usage"), ultraLowLatency ? ultraLowLatencyValue : lowLatencyValue);

        // CBR is a different number per codec: 1 in the AVC enum, 3 in the HEVC and AV1 ones.
        Amf.SetInt64(_component, P("RateControlMethod"), Hevc || Av1 ? 3 : 1);
        Amf.SetInt64(_component, P("TargetBitrate"), bits);
        Amf.SetInt64(_component, P("PeakBitrate"), bits);
        // A VBV of one frame, for the same reason as NVENC: a burst that takes several frame
        // times to send is a frame the client shows late.
        Amf.SetInt64(_component, P("VBVBufferSize"), bits / fps);
        Amf.SetRate(_component, P("FrameRate"), (uint)fps, 1);

        // Key frames only when asked for. AVC counts an IDR period in frames; HEVC and AV1 count
        // GOPs — there a GOP of near-forever with one key frame per GOP amounts to the same thing.
        if (Hevc || Av1) Amf.SetInt64(_component, P("GOPSize"), int.MaxValue);
        else Amf.SetInt64(_component, "IDRPeriod", int.MaxValue);

        // QueryOutput blocks up to this long instead of the loop below spinning on AMF_REPEAT.
        Amf.SetInt64(_component, P("QueryTimeout"), 50);

        // Variance-based adaptive quantisation, AMD's answer to the steps a gradient shows. Set
        // unchecked: an encoder without the property answers "not found" and nothing happens.
        Amf.SetBool(_component, P("EnableVBAQ"), true);

        Amf.SetInt64(_component, P("QualityPreset"), QualityPresetFor(_quality));

        // The same floor under the quality that NVENC gets: a desktop stays so far under the
        // bitrate that a gradient is stepped rather than smoothed. Unchecked, like the one above.
        var maxQp = MaxQpFor(_quality);

        if (Av1)
        {
            // AV1 counts its quantiser 0 to 255 and names the fields apart, so the scale has to be
            // carried over rather than the number: five q-index steps to one of the others.
            Amf.SetInt64(_component, "Av1MaxQIndex_Intra", maxQp * 5);
            Amf.SetInt64(_component, "Av1MaxQIndex_Inter", maxQp * 5);
        }
        else
        {
            Amf.SetInt64(_component, P("MaxQP"), maxQp);
        }

        if (_hdr)
        {
            // Ten bits per colour, and the profile that can carry them. Both have to be set
            // before Init, which is what fixes the shape of everything after it.
            Amf.SetInt64(_component, "HevcColorBitDepth", 10);
            Amf.SetInt64(_component, "HevcProfile", 2);   // AMF_VIDEO_ENCODER_HEVC_PROFILE_MAIN_10

            // What arrives is already BT.2020 PQ from the colour shader, and it goes out as it
            // came: nothing here asks the card to convert anything, only to say what it has.
            Amf.SetInt64(_component, "HevcInColorProfile", Amf.ColorProfile2020);
            Amf.SetInt64(_component, "HevcInColorPrimaries", Amf.ColorPrimariesBt2020);
            Amf.SetInt64(_component, "HevcInColorTransferChar", Amf.ColorTransferSmpte2084);
            Amf.SetInt64(_component, "HevcOutColorProfile", Amf.ColorProfile2020);
            Amf.SetInt64(_component, "HevcOutColorPrimaries", Amf.ColorPrimariesBt2020);
            Amf.SetInt64(_component, "HevcOutColorTransferChar", Amf.ColorTransferSmpte2084);
        }

        result = Amf.ComponentInit(_component,
            _hdr ? Amf.SurfaceFormatP010 : Amf.SurfaceFormatBgra, Width, Height);
        if (result != Amf.Ok)
        {
            refusal = $"Init: {Amf.Describe(result)}";
            return false;
        }

        refusal = string.Empty;
        return true;
    }

    // The SPS/PPS (and VPS) buffer the encoder built during Init, exposed as the
    // read-only ExtraData property: a variant holding an AMFBuffer.
    private void ReadExtraData()
    {
        Amf.Check(Amf.GetProperty(_component, P("ExtraData"), out var variant),
            "AMFComponent::GetProperty(ExtraData)");

        if (variant.Type != Amf.VariantInterface || variant.InterfaceValue is null)
            throw new InvalidOperationException("the encoder returned no ExtraData buffer");

        var raw = variant.InterfaceValue;
        try
        {
            Amf.Check(Amf.QueryInterface(raw, Amf.BufferIid, out var buffer),
                "ExtraData::QueryInterface(AMFBuffer)");
            try
            {
                _header = new byte[Amf.BufferSize(buffer)];
                new ReadOnlySpan<byte>(Amf.BufferNative(buffer), _header.Length).CopyTo(_header);
            }
            finally
            {
                Amf.Release(buffer);
            }
        }
        finally
        {
            // The variant's reference. A property read hands out an acquired interface.
            Amf.Release(raw);
        }
    }

    public ReadOnlyMemory<byte> Encode(nint texture, bool forceKeyFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Amf.Check(Amf.CreateSurfaceFromDx11(_context, (void*)texture, out var surface),
            "AMFContext::CreateSurfaceFromDX11Native");

        try
        {
            if (forceKeyFrame)
            {
                // Per-submission properties ride on the surface, and the headers are re-sent with
                // the key frame so a client recovering from a loss can decode it.
                if (Av1)
                {
                    // AV1 names the field a frame type rather than a picture type, and counts KEY
                    // as 1 where the other two count IDR as 2.
                    Amf.SetInt64(surface, "Av1ForceFrameType", 1);
                    Amf.SetBool(surface, "Av1ForceInsertSequenceHeader", true);
                }
                else if (Hevc)
                {
                    Amf.SetInt64(surface, "HevcForcePictureType", 2);
                    Amf.SetBool(surface, "HevcInsertHeader", true);
                }
                else
                {
                    Amf.SetInt64(surface, "ForcePictureType", 2);
                    Amf.SetBool(surface, "InsertSPS", true);
                    Amf.SetBool(surface, "InsertPPS", true);
                }
            }

            var submitted = Amf.SubmitInput(_component, surface);
            if (submitted != Amf.Ok && submitted != Amf.InputFull)
                Amf.Check(submitted, "AMFComponent::SubmitInput");

            // One frame in, one frame out before this returns: the surface wraps the capturer's own
            // texture, which must not be written again while the encoder is still reading it.
            var output = WaitForOutput();

            if (submitted == Amf.InputFull)
            {
                // The queue should never fill when it is drained every frame; if it did, this
                // frame was dropped and the fact belongs in the log, not in silence.
                Log.Warn("the AMF encoder's input queue was full and a frame was dropped");
            }

            return output;
        }
        finally
        {
            Amf.Release(surface);
        }
    }

    private ReadOnlyMemory<byte> WaitForOutput()
    {
        // Timestamps rather than a Stopwatch: this runs once a frame and the class was an
        // allocation for a deadline two longs answer.
        var started = Stopwatch.GetTimestamp();
        var deadline = OutputDeadlineMs * Stopwatch.Frequency / 1000;

        while (true)
        {
            var result = Amf.QueryOutput(_component, out var data);

            if (result == Amf.Repeat || (result == Amf.Ok && data is null))
            {
                if (Stopwatch.GetTimestamp() - started > deadline)
                    throw new InvalidOperationException(
                        $"the AMF encoder produced nothing for {OutputDeadlineMs} ms");
                continue;   // QueryTimeout makes QueryOutput itself do the waiting
            }

            Amf.Check(result, "AMFComponent::QueryOutput");

            try
            {
                Amf.Check(Amf.QueryInterface(data, Amf.BufferIid, out var buffer),
                    "output::QueryInterface(AMFBuffer)");
                try
                {
                    var size = (int)Amf.BufferSize(buffer);
                    if (_unit.Length < size) _unit = new byte[size];

                    new ReadOnlySpan<byte>(Amf.BufferNative(buffer), size).CopyTo(_unit);
                    return _unit.AsMemory(0, size);
                }
                finally
                {
                    Amf.Release(buffer);
                }
            }
            finally
            {
                Amf.Release(data);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_component is not null)
        {
            Amf.ComponentTerminate(_component);
            Amf.ReleaseAndClear(ref _component);
        }

        if (_context is not null)
        {
            Amf.ContextTerminate(_context);
            Amf.ReleaseAndClear(ref _context);
        }
    }
}
