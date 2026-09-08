//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using RemoteGameHub.App;
using RemoteGameHub.Native;

namespace RemoteGameHub.Media;

// The NVENC session: BGRA in, H.264, HEVC or AV1 access units out. Synchronous (enableEncodeAsync = 0),
// driver picture types (enablePTD = 1), preset P4, CBR with a one-frame VBV, infinite GOP, no B.
internal sealed unsafe class NvencEncoder : IVideoEncoder
{
    // The four levels as this card takes them: the preset is time spent looking, the quantiser
    // ceiling is the floor under the quality (0 lossless, 51 unwatchable), the last is AQ strength.
    private static (Guid Preset, uint MaxQp, uint AqStrength) Settings(StreamQuality quality) =>
        quality switch
        {
            StreamQuality.Low => (NvEnc.PresetP1, 34u, 4u),
            StreamQuality.Medium => (NvEnc.PresetP4, 28u, 8u),
            StreamQuality.Lossless => (NvEnc.PresetP7, 16u, 12u),
            _ => (NvEnc.PresetP6, 24u, 8u),
        };

    private readonly NvEnc.FunctionList* _api;
    private void* _session;
    private void* _bitstream;
    // The access unit, grown once and reused. Allocated per frame it was 2.5 MB a second, and at
    // 4K every key frame is over the 85 KB the large object heap starts at.
    private byte[] _unit = Array.Empty<byte>();

    private void* _registered;
    private nint _registeredTexture;
    private readonly bool _hdr;
    // Whether the ten-bit samples use the whole range or the studio one. The shader that made
    // them and this flag must agree, or the client stretches what was never compressed.
    private readonly bool _fullRange;
    private readonly bool _yuv444;
    private readonly StreamQuality _quality;
    private byte[] _header = Array.Empty<byte>();
    private ulong _frameIndex;
    private bool _disposed;

    public VideoCodec Codec { get; }
    public int Width { get; }
    public int Height { get; }
    public ReadOnlyMemory<byte> Header => _header;

    private NvencEncoder(NvEnc.FunctionList* api, VideoCodec codec, int width, int height,
                         bool hdr, bool fullRange, bool yuv444, StreamQuality quality)
    {
        _api = api;
        Codec = codec;
        Width = width;
        Height = height;
        _hdr = hdr;
        _fullRange = fullRange;
        _yuv444 = yuv444;
        _quality = quality;
    }

    // What the input texture is: the desktop as it was captured, or — for high dynamic range —
    // the ten-bit BT.2020 PQ that ColourConverter wrote, where the matrix is ours and not the card's.
    private int InputFormat => _hdr ? NvEnc.BufferFormatYuv420Ten : NvEnc.BufferFormatArgb;

    // Opens a session on the device the capturer owns. Throws with the driver's own words when it
    // refuses; the caller decides whether that ends the stream or the choice of encoder.
    internal static NvencEncoder Open(nint device, VideoCodec codec, int width, int height,
                                      int bitrateKbps, int fps, bool hdr = false, bool yuv444 = false,
                                      StreamQuality quality = StreamQuality.High,
                                      bool fullRange = false)
    {
        var api = NvEnc.Api();
        if (api is null)
        {
            throw new InvalidOperationException(
                "NVENC is not available: nvEncodeAPI64.dll could not be loaded. " +
                "It ships with the NVIDIA display driver.");
        }

        if (codec == VideoCodec.Auto) codec = VideoCodec.H264;

        // Ten bits are sent in HEVC and AV1 and nothing else here, so asking for high dynamic
        // range in H.264 is a mistake worth naming rather than silently ignoring.
        if (hdr && codec != VideoCodec.Hevc && codec != VideoCodec.Av1)
        {
            throw new InvalidOperationException(
                $"high dynamic range needs HEVC or AV1; {VideoEncoders.Name(codec)} is not sent " +
                "in ten bits by this server.");
        }

        var encoder = new NvencEncoder(api, codec, width, height, hdr, fullRange, yuv444, quality);
        try
        {
            encoder.OpenSession(device);
            encoder.Initialize(bitrateKbps, fps);
            encoder.ReadSequenceHeader();
            return encoder;
        }
        catch
        {
            encoder.Dispose();
            throw;
        }
    }

    private void OpenSession(nint device)
    {
        var open = new NvEnc.OpenSessionParams
        {
            Version = NvEnc.OpenSessionParamsVer,
            DeviceType = NvEnc.DeviceTypeDirectX,
            Device = (void*)device,
            ApiVersion = NvEnc.ApiVersion,
        };

        void* session;
        NvEnc.Check(_api->OpenSessionEx(&open, &session), "NvEncOpenEncodeSessionEx");
        _session = session;
    }

    private Guid CodecGuid => Codec switch
    {
        VideoCodec.Hevc => NvEnc.CodecHevc,
        VideoCodec.Av1 => NvEnc.CodecAv1,
        _ => NvEnc.CodecH264,
    };

    private void Initialize(int bitrateKbps, int fps)
    {
        // The driver's own preset is the starting point and only what has to differ is touched:
        // building the config from zero means owning every default the driver chose better.
        var preset = new NvEnc.PresetConfig { Version = NvEnc.PresetConfigVer };
        preset.Preset.Version = NvEnc.ConfigVer;

        // The tuning is the same at every level and is not a quality axis: it decides whether the
        // encoder may hold frames back, which is latency, and this server never trades that.
        var settings = Settings(_quality);

        NvEnc.Check(_api->GetPresetConfigEx(_session, CodecGuid, settings.Preset,
                NvEnc.TuningInfoUltraLowLatency, &preset),
            "NvEncGetEncodePresetConfigEx");

        var config = preset.Preset;
        config.ProfileGuid =
            // AV1 has one profile for both eight and ten bits — the bit depth is config fields
            // below, not a different profile — so this must be checked before _hdr picks HEVC's.
            Codec == VideoCodec.Av1 ? NvEnc.ProfileAv1Main :
            _hdr ? NvEnc.ProfileHevcMain10 :
            _yuv444 && Codec == VideoCodec.Hevc ? NvEnc.ProfileHevcFrext :
            _yuv444 ? NvEnc.ProfileH264High444 : NvEnc.ProfileAutoselect;
        config.GopLength = NvEnc.InfiniteGopLength;
        config.FrameIntervalP = 1;   // IPPP…: a B frame is a frame held back, which is latency

        var bits = (uint)Math.Min((long)bitrateKbps * 1000, uint.MaxValue);

        config.Rc.RateControlMode = NvEnc.RateControlCbr;
        config.Rc.AverageBitRate = bits;
        config.Rc.MaxBitRate = bits;
        // A VBV of one frame: the rate control may never save up for a burst taking several frame
        // times to send, because a frame that arrives late is a frame the client shows late.
        config.Rc.VbvBufferSize = bits / (uint)fps;
        config.Rc.VbvInitialDelay = config.Rc.VbvBufferSize;
        config.Rc.Flags |= NvEnc.RcFlagZeroReorderDelay;

        // Adaptive quantisation, spatial and temporal, above the driver's own choice: it moves
        // bits into the flat colour and slow gradients a desktop is mostly made of.
        config.Rc.Flags |= NvEnc.RcFlagEnableAq | NvEnc.RcFlagEnableTemporalAq;
        config.Rc.Flags = (config.Rc.Flags & ~(15u << NvEnc.RcAqStrengthShift))
                          | (settings.AqStrength << NvEnc.RcAqStrengthShift);

        // And a floor under the quality, which is what a gradient needs: the bitrate is a ceiling
        // the rate control stays far under — four megabits of fifty — rather than spend the room.
        config.Rc.Flags |= NvEnc.RcFlagEnableMaxQp;
        config.Rc.MaxQpInterP = settings.MaxQp;
        config.Rc.MaxQpInterB = settings.MaxQp;
        config.Rc.MaxQpIntra = settings.MaxQp;

        if (Codec == VideoCodec.Av1)
        {
            // The sequence header goes out with every key frame, so a client recovering from a loss
            // can decode the next.
            config.Av1IdrPeriod = NvEnc.InfiniteGopLength;
            config.Av1Flags |= NvEnc.Av1FlagRepeatSeqHdr;

            // Ten bits, input and output alike: the same P010 texture a ten-bit HEVC session gets.
            // 4:4:4 is not supported for AV1 by this card, so chromaFormatIDC is left as preset.
            config.Av1OutputBitDepth = _hdr ? NvEnc.Av1BitDepth10 : NvEnc.Av1BitDepth8;
            config.Av1InputBitDepth = _hdr ? NvEnc.Av1BitDepth10 : NvEnc.Av1BitDepth8;

            // What the picture is, said in the stream itself. Without it a client has ten-bit
            // samples and no idea they are BT.2020 PQ, and shows them as washed-out Rec. 709.
            if (_hdr)
            {
                config.Av1ColourPrimaries = NvEnc.ColourPrimariesBt2020;
                config.Av1TransferCharacteristics = NvEnc.TransferCharacteristicSmpte2084;
                config.Av1MatrixCoefficients = NvEnc.ColourMatrixBt2020Ncl;
                config.Av1ColorRange = _fullRange ? 1u : 0u;
            }
        }
        else if (Codec == VideoCodec.Hevc)
        {
            config.HevcIdrPeriod = NvEnc.InfiniteGopLength;
            config.HevcFlags |= NvEnc.HevcFlagRepeatSpsPps;
            // chromaFormatIDC is a 2-bit field inside the bitfield word: 1 = 4:2:0, 3 = 4:4:4.
            config.HevcFlags = (config.HevcFlags & ~(3u << NvEnc.HevcChromaFormatShift))
                               | ((_yuv444 ? 3u : 1u) << NvEnc.HevcChromaFormatShift);

            // pixelBitDepthMinus8, in the three bits after it: 2 for the ten-bit profile.
            config.HevcFlags = (config.HevcFlags & ~(7u << NvEnc.HevcBitDepthShift))
                               | ((_hdr ? 2u : 0u) << NvEnc.HevcBitDepthShift);

            // What the picture is, said in the stream itself. Without it a client has ten-bit
            // samples and no idea they are BT.2020 PQ, and shows them as washed-out Rec. 709.
            if (_hdr)
            {
                config.HevcVideoSignalTypePresentFlag = 1;
                config.HevcColourDescriptionPresentFlag = 1;
                config.HevcVideoFullRangeFlag = _fullRange ? 1u : 0u;
                config.HevcColourPrimaries = NvEnc.ColourPrimariesBt2020;
                config.HevcTransferCharacteristics = NvEnc.TransferCharacteristicSmpte2084;
                config.HevcColourMatrix = NvEnc.ColourMatrixBt2020Ncl;
            }
        }
        else
        {
            config.H264IdrPeriod = NvEnc.InfiniteGopLength;
            config.H264Flags |= NvEnc.H264FlagRepeatSpsPps;
            config.H264ChromaFormatIdc = _yuv444 ? 3u : 1u;   // 3 = 4:4:4, 1 = 4:2:0
        }

        var init = new NvEnc.InitializeParams
        {
            Version = NvEnc.InitializeParamsVer,
            EncodeGuid = CodecGuid,
            PresetGuid = settings.Preset,
            EncodeWidth = (uint)Width,
            EncodeHeight = (uint)Height,
            DarWidth = (uint)Width,
            DarHeight = (uint)Height,
            FrameRateNum = (uint)fps,
            FrameRateDen = 1,
            EnableEncodeAsync = 0,
            EnablePtd = 1,
            EncodeConfig = &config,
            MaxEncodeWidth = (uint)Width,
            MaxEncodeHeight = (uint)Height,
            TuningInfo = NvEnc.TuningInfoUltraLowLatency,
        };

        var status = _api->InitializeEncoder(_session, &init);
        if (status != NvEnc.StatusSuccess)
        {
            // The one failure worth the driver's own words: everything about the configuration
            // funnels through this call, and "INVALID_PARAM" alone names none of forty fields.
            var detail = NvEnc.LastError(_session);
            throw new InvalidOperationException(
                $"NvEncInitializeEncoder failed: {status} ({NvEnc.Describe(status)})" +
                (detail.Length > 0 ? $" — {detail}" : string.Empty));
        }

        var create = new NvEnc.CreateBitstreamBuffer { Version = NvEnc.CreateBitstreamBufferVer };
        NvEnc.Check(_api->CreateBitstream(_session, &create), "NvEncCreateBitstreamBuffer");
        _bitstream = create.BitstreamBuffer;

        Log.Info($"NVENC session open: {VideoEncoders.Name(Codec)}" +
                 (_hdr ? " Main10 (high dynamic range)" : string.Empty) +
                 $", {Width}x{Height}, {bitrateKbps} kbit/s CBR at {fps} fps, " +
                 (_yuv444 ? "4:4:4 colour, " : string.Empty) +
                 $"{_quality.ToString().ToLowerInvariant()} quality, ultra-low-latency, " +
                 $"adaptive quantisation at {settings.AqStrength}, " +
                 $"quantiser capped at {settings.MaxQp}");
    }

    private void ReadSequenceHeader()
    {
        var buffer = stackalloc byte[NvEnc.MaxSequenceHeaderBytes];
        uint written = 0;

        var payload = new NvEnc.SequenceParamPayload
        {
            Version = NvEnc.SequenceParamPayloadVer,
            InBufferSize = NvEnc.MaxSequenceHeaderBytes,
            SpsPpsBuffer = buffer,
            OutPayloadSize = &written,
        };

        NvEnc.Check(_api->GetSequenceParams(_session, &payload), "NvEncGetSequenceParams");

        _header = new byte[written];
        new ReadOnlySpan<byte>(buffer, (int)written).CopyTo(_header);
    }

    // The capturer's frame texture is registered once and re-registered only when it changes —
    // after a mode change rebuilt the duplication, when this encoder is rebuilt too if it resized.
    private void* RegisteredInput(nint texture)
    {
        if (_registered is not null && texture == _registeredTexture) return _registered;

        if (_registered is not null)
        {
            _api->Unregister(_session, _registered);
            _registered = null;
        }

        var register = new NvEnc.RegisterResource
        {
            Version = NvEnc.RegisterResourceVer,
            ResourceType = NvEnc.ResourceTypeDirectX,
            Width = (uint)Width,
            Height = (uint)Height,
            Pitch = 0,   // the header: 0 for DirectX resources
            ResourceToRegister = (void*)texture,
            BufferFormat = InputFormat,
            BufferUsage = NvEnc.BufferUsageInputImage,
        };

        NvEnc.Check(_api->Register(_session, &register), "NvEncRegisterResource");

        _registered = register.RegisteredResource;
        _registeredTexture = texture;
        return _registered;
    }

    public ReadOnlyMemory<byte> Encode(nint texture, bool forceKeyFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var map = new NvEnc.MapInputResource
        {
            Version = NvEnc.MapInputResourceVer,
            RegisteredResource = RegisteredInput(texture),
        };
        NvEnc.Check(_api->MapResource(_session, &map), "NvEncMapInputResource");

        try
        {
            var pic = new NvEnc.PicParams
            {
                Version = NvEnc.PicParamsVer,
                InputWidth = (uint)Width,
                InputHeight = (uint)Height,
                InputPitch = (uint)Width,
                EncodePicFlags = forceKeyFrame
                    ? NvEnc.PicFlagForceIdr | NvEnc.PicFlagOutputSpsPps
                    : 0,
                InputTimeStamp = _frameIndex++,
                InputBuffer = map.MappedResource,
                OutputBitstream = _bitstream,
                BufferFormat = map.MappedBufferFormat,
                PictureStruct = NvEnc.PicStructFrame,
            };

            var status = _api->EncodePicture(_session, &pic);

            // With no B frames and PTD on this should never be asked for, but the header allows
            // it and answering wrongly would mean locking a bitstream that holds nothing.
            if (status == NvEnc.StatusNeedMoreInput) return ReadOnlyMemory<byte>.Empty;

            NvEnc.Check(status, "NvEncEncodePicture");

            var @lock = new NvEnc.LockBitstream
            {
                Version = NvEnc.LockBitstreamVer,
                OutputBitstream = _bitstream,
            };
            NvEnc.Check(_api->LockBitstreamBuffer(_session, &@lock), "NvEncLockBitstream");

            try
            {
                var size = (int)@lock.BitstreamSizeInBytes;
                if (_unit.Length < size) _unit = new byte[size];

                new ReadOnlySpan<byte>(@lock.BitstreamBufferPtr, size).CopyTo(_unit);
                return _unit.AsMemory(0, size);
            }
            finally
            {
                _api->UnlockBitstreamBuffer(_session, _bitstream);
            }
        }
        finally
        {
            _api->UnmapResource(_session, map.MappedResource);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_session is null) return;

        if (_registered is not null) _api->Unregister(_session, _registered);
        if (_bitstream is not null) _api->DestroyBitstream(_session, _bitstream);
        _api->DestroyEncoder(_session);

        _registered = null;
        _bitstream = null;
        _session = null;
    }
}
