//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using RemoteGameHub.App;

namespace RemoteGameHub.Native;

// The NVENC interface of the NVIDIA driver, through nvEncodeAPI64.dll. Everything here is from
// nvEncodeAPI.h of Video Codec SDK 11.1: the driver refuses an API newer than itself, never older.
internal static unsafe class NvEnc
{
    // nvEncodeAPI.h line 121: NVENCAPI_VERSION = major | (minor << 24), for 11.1.
    internal const uint ApiVersion = 0x0100000B;

    // The per-struct version constants, each NVENCAPI_STRUCT_VERSION(n), with bit 31 set where the
    // header says so. A wrong one is NV_ENC_ERR_INVALID_VERSION and nothing more descriptive.
    internal const uint CapsParamVer = 0x7101000B;
    internal const uint CreateBitstreamBufferVer = 0x7101000B;
    internal const uint ConfigVer = 0xF107000B;
    internal const uint InitializeParamsVer = 0xF105000B;
    internal const uint PresetConfigVer = 0xF104000B;
    internal const uint PicParamsVer = 0xF104000B;
    internal const uint LockBitstreamVer = 0x7101000B;
    internal const uint MapInputResourceVer = 0x7104000B;
    internal const uint RegisterResourceVer = 0x7103000B;
    internal const uint SequenceParamPayloadVer = 0x7101000B;
    internal const uint OpenSessionParamsVer = 0x7101000B;
    internal const uint FunctionListVer = 0x7102000B;

    // nvEncodeAPI.h lines 143-149 and 157-159, 244-246.
    internal static readonly Guid CodecH264 = new("6BC82762-4E63-4CA4-AA85-1E50F321F6BF");
    internal static readonly Guid CodecHevc = new("790CDC88-4522-4D7B-9425-BDA9975F7603");
    internal static readonly Guid CodecAv1 = new("0A352289-0AA7-4759-862D-5D15CD16D254");
    internal static readonly Guid ProfileAutoselect = new("BFD6F8E7-233C-4341-8B3E-4818523803F4");

    // nvEncodeAPI.h line 194: the ten-bit HEVC profile, which HDR needs.
    internal static readonly Guid ProfileHevcMain10 = new("FA4D2B6C-3A5B-411A-8018-0A3F5E3C9BE5");

    // AV1 has one profile this server can use, and the automatic choice does not cover it.
    internal static readonly Guid ProfileAv1Main = new("5F2A39F5-F14E-4F95-9A9E-B76D568FCF97");

    // Full-resolution colour. The automatic profile is a 4:2:0 one, so asking for 4:4:4 means
    // naming the profile that carries it: High 4:4:4 in H.264, range extensions in HEVC.
    internal static readonly Guid ProfileH264High444 = new("7AC663CB-A598-4960-B844-339B261A7D52");
    internal static readonly Guid ProfileHevcFrext = new("51EC32B5-1B4C-453C-9CBD-B616BD621341");
    // The speed-against-quality scale, P1 fastest to P7 slowest. P1 stepped across a gradient at
    // any bitrate; P4 is the middle and still finishes a 1440p frame in a fraction of its slot.
    internal static readonly Guid PresetP1 = new("FC0A8D3E-45F8-4CF8-80C7-298871590EBF");
    internal static readonly Guid PresetP4 = new("90A7B826-DF06-4862-B9D2-CD6D73A08681");
    internal static readonly Guid PresetP6 = new("8E75C279-6299-4AB6-8302-0B215A335CF5");
    internal static readonly Guid PresetP7 = new("84848C12-6F71-4C13-931B-53E283F57974");

    internal const uint InfiniteGopLength = 0xFFFFFFFF;    // NVENC_INFINITE_GOPLENGTH
    internal const int MaxSequenceHeaderBytes = 512;       // NV_MAX_SEQ_HDR_LEN

    // NV_ENC_DEVICE_TYPE_DIRECTX. An ID3D11Device counts as "directx" here; the enum's comment
    // says directx9 only because it predates 11.
    internal const int DeviceTypeDirectX = 0;

    // NV_ENC_BUFFER_FORMAT_ARGB: "32-bit word with B in the lowest 8 bits" — which is the byte
    // order of DXGI_FORMAT_B8G8R8A8_UNORM, the format the capturer produces.
    internal const int BufferFormatArgb = 0x01000000;

    // NV_ENC_BUFFER_FORMAT_ABGR10: R in the lowest 10 bits, the bit order of
    // DXGI_FORMAT_R10G10B10A2_UNORM. Its sibling ARGB10 (0x02000000) puts B lowest and swaps red.
    internal const int BufferFormatAbgr10 = 0x20000000;

    // NV_ENC_BUFFER_FORMAT_YUV420_10BIT: luma then interleaved chroma, two bytes a sample with the
    // ten bits at the top — DXGI_FORMAT_P010, which is what the colour shader writes.
    internal const int BufferFormatYuv420Ten = 0x00010000;

    internal const int BufferUsageInputImage = 0;          // NV_ENC_INPUT_IMAGE
    internal const int ResourceTypeDirectX = 0;            // NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX

    internal const int TuningInfoUltraLowLatency = 3;      // NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY
    internal const int RateControlCbr = 0x2;               // NV_ENC_PARAMS_RC_CBR
    internal const int PicStructFrame = 0x01;              // NV_ENC_PIC_STRUCT_FRAME

    // NV_ENC_PIC_FLAGS.
    internal const uint PicFlagForceIdr = 0x2;
    internal const uint PicFlagOutputSpsPps = 0x4;

    // The NVENCSTATUS values that steer the encode loop; the rest are named in Describe below.
    internal const int StatusSuccess = 0;
    internal const int StatusNeedMoreInput = 17;
    internal const int StatusInvalidVersion = 15;

    // The NVENCSTATUS names in the header's order; the enum has no explicit values.
    internal static string Describe(int status) => status switch
    {
        0 => "NV_ENC_SUCCESS",
        1 => "NV_ENC_ERR_NO_ENCODE_DEVICE",
        2 => "NV_ENC_ERR_UNSUPPORTED_DEVICE",
        3 => "NV_ENC_ERR_INVALID_ENCODERDEVICE",
        4 => "NV_ENC_ERR_INVALID_DEVICE",
        5 => "NV_ENC_ERR_DEVICE_NOT_EXIST",
        6 => "NV_ENC_ERR_INVALID_PTR",
        7 => "NV_ENC_ERR_INVALID_EVENT",
        8 => "NV_ENC_ERR_INVALID_PARAM",
        9 => "NV_ENC_ERR_INVALID_CALL",
        10 => "NV_ENC_ERR_OUT_OF_MEMORY",
        11 => "NV_ENC_ERR_ENCODER_NOT_INITIALIZED",
        12 => "NV_ENC_ERR_UNSUPPORTED_PARAM",
        13 => "NV_ENC_ERR_LOCK_BUSY",
        14 => "NV_ENC_ERR_NOT_ENOUGH_BUFFER",
        15 => "NV_ENC_ERR_INVALID_VERSION",
        16 => "NV_ENC_ERR_MAP_FAILED",
        17 => "NV_ENC_ERR_NEED_MORE_INPUT",
        18 => "NV_ENC_ERR_ENCODER_BUSY",
        19 => "NV_ENC_ERR_EVENT_NOT_REGISTERD",
        20 => "NV_ENC_ERR_GENERIC",
        21 => "NV_ENC_ERR_INCOMPATIBLE_CLIENT_KEY",
        22 => "NV_ENC_ERR_UNIMPLEMENTED",
        23 => "NV_ENC_ERR_RESOURCE_REGISTER_FAILED",
        24 => "NV_ENC_ERR_RESOURCE_NOT_REGISTERED",
        25 => "NV_ENC_ERR_RESOURCE_NOT_MAPPED",
        _ => "unknown",
    };

    internal static void Check(int status, string call)
    {
        if (status == StatusSuccess) return;
        throw new InvalidOperationException($"{call} failed: {status} ({Describe(status)}).");
    }

    // ------------------------------------------------------------------ structs

    // Numbers in the capability enumeration, which has no explicit values and is therefore counted
    // by the compiler: YUV444_ENCODE is the thirty-fourth entry and 10BIT_ENCODE the fortieth.
    internal const int CapsSupportYuv444Encode = 33;
    internal const int CapsSupport10BitEncode = 39;

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    internal struct CapsParam
    {
        [FieldOffset(0)] internal uint Version;
        [FieldOffset(4)] internal int CapsToQuery;
    }

    [StructLayout(LayoutKind.Explicit, Size = 1552)]
    internal struct OpenSessionParams
    {
        [FieldOffset(0)] internal uint Version;
        [FieldOffset(4)] internal int DeviceType;
        [FieldOffset(8)] internal void* Device;
        [FieldOffset(24)] internal uint ApiVersion;
    }

    [StructLayout(LayoutKind.Explicit, Size = 128)]
    internal struct RcParams
    {
        [FieldOffset(0)] internal uint Version;
        [FieldOffset(4)] internal int RateControlMode;
        // constQP, three uints, sits at 8..20 and is left as the preset set it.
        [FieldOffset(20)] internal uint AverageBitRate;
        [FieldOffset(24)] internal uint MaxBitRate;
        [FieldOffset(28)] internal uint VbvBufferSize;
        [FieldOffset(32)] internal uint VbvInitialDelay;
        // Bits from 0: enableMinQP, enableMaxQP, enableInitialRCQP, enableAQ, reserved, lookahead,
        // disableIadapt, disableBadapt, temporalAQ, zeroReorderDelay, nonRefP, strictGOP, aqStrength.
        [FieldOffset(36)] internal uint Flags;

        // NV_ENC_QP is three uints — inter P, inter B, intra — and minQP follows the word above.
        [FieldOffset(40)] internal uint MinQpInterP;
        [FieldOffset(44)] internal uint MinQpInterB;
        [FieldOffset(48)] internal uint MinQpIntra;
        [FieldOffset(52)] internal uint MaxQpInterP;
        [FieldOffset(56)] internal uint MaxQpInterB;
        [FieldOffset(60)] internal uint MaxQpIntra;
    }

    internal const uint RcFlagZeroReorderDelay = 1u << 9;

    // Adaptive quantisation: bits spent where the eye sees them, which in a flat gradient is the
    // steps between shades. Spatial is bit 3, temporal bit 8, the strength four bits from 12.
    internal const uint RcFlagEnableAq = 1u << 3;
    internal const uint RcFlagEnableTemporalAq = 1u << 8;
    internal const int RcAqStrengthShift = 12;

    // The ceiling on how coarse the quantiser may become. Constant bitrate is a budget to stay
    // under, and a still desktop stays far under it while still stepping across a gradient.
    internal const uint RcFlagEnableMaxQp = 1u << 1;

    [StructLayout(LayoutKind.Explicit, Size = 3584)]
    internal struct Config
    {
        [FieldOffset(0)] internal uint Version;
        [FieldOffset(4)] internal Guid ProfileGuid;
        [FieldOffset(20)] internal uint GopLength;
        [FieldOffset(24)] internal int FrameIntervalP;
        [FieldOffset(40)] internal RcParams Rc;

        // The codec config union begins at 168. The H264 and HEVC views below overlap on purpose,
        // exactly as the union does; only the one matching the session's codec may be touched.

        // H264 bitfield word, in the header's order from bit 0 (enableTemporalSVC, enableStereoMVC,
        // the hierarchical and SEI flags): repeatSPSPPS is bit 12.
        [FieldOffset(168)] internal uint H264Flags;
        [FieldOffset(176)] internal uint H264IdrPeriod;
        [FieldOffset(360)] internal uint H264ChromaFormatIdc;

        // HEVC bitfield word (at +16 inside the union, after level, tier and the two CU sizes), in
        // the header's order: repeatSPSPPS bit 7, chromaFormatIDC 9-10, pixelBitDepthMinus8 11-13.
        [FieldOffset(184)] internal uint HevcFlags;
        [FieldOffset(188)] internal uint HevcIdrPeriod;

        // AV1's view of the same union, landing on the same two words: level, tier and the two
        // partition sizes are four uints. Named apart — different fields, different bits.
        [FieldOffset(184)] internal uint Av1Flags;
        [FieldOffset(188)] internal uint Av1IdrPeriod;

        // hevcVUIParameters, sixteen uints into the HEVC config: what the stream says its colour
        // is. Written for high dynamic range, where the client has no other way to know.
        [FieldOffset(240)] internal uint HevcVideoSignalTypePresentFlag;
        [FieldOffset(248)] internal uint HevcVideoFullRangeFlag;
        [FieldOffset(252)] internal uint HevcColourDescriptionPresentFlag;
        [FieldOffset(256)] internal uint HevcColourPrimaries;
        [FieldOffset(260)] internal uint HevcTransferCharacteristics;
        [FieldOffset(264)] internal uint HevcColourMatrix;

        // NV_ENC_CONFIG_AV1's own colour fields and bit depth, counted from the same union base
        // as Av1Flags — verified against the real SDK offsets, not guessed.
        [FieldOffset(236)] internal uint Av1ColourPrimaries;
        [FieldOffset(240)] internal uint Av1TransferCharacteristics;
        [FieldOffset(244)] internal uint Av1MatrixCoefficients;
        [FieldOffset(248)] internal uint Av1ColorRange;
        [FieldOffset(280)] internal uint Av1OutputBitDepth;
        [FieldOffset(284)] internal uint Av1InputBitDepth;
    }

    // NV_ENC_BIT_DEPTH: the values themselves, not an offset from eight the way HEVC's bitfield
    // counts them.
    internal const uint Av1BitDepth8 = 8;
    internal const uint Av1BitDepth10 = 10;

    // The three the high dynamic range stream declares, from H.273: Rec. 2020 primaries, the
    // perceptual quantiser, and Rec. 2020 non-constant luminance for the luma and chroma.
    internal const uint ColourPrimariesBt2020 = 9;
    internal const uint TransferCharacteristicSmpte2084 = 16;
    internal const uint ColourMatrixBt2020Ncl = 9;

    internal const uint H264FlagRepeatSpsPps = 1u << 12;
    internal const uint HevcFlagRepeatSpsPps = 1u << 7;
    internal const int HevcChromaFormatShift = 9;

    // pixelBitDepthMinus8, three bits after the two chroma-format ones: zero for eight-bit and
    // two for ten.
    internal const int HevcBitDepthShift = 11;

    // AV1's word counts from bit 0: outputAnnexBFormat, enableTimingInfo, enableDecoderModelInfo,
    // enableFrameIdNumbers, disableSeqHdr, repeatSeqHdr — all single bits, so this position holds.
    internal const uint Av1FlagRepeatSeqHdr = 1u << 5;

    [StructLayout(LayoutKind.Explicit, Size = 5128)]
    internal struct PresetConfig
    {
        [FieldOffset(0)] internal uint Version;
        [FieldOffset(8)] internal Config Preset;
    }

    [StructLayout(LayoutKind.Explicit, Size = 1808)]
    internal struct InitializeParams
    {
        [FieldOffset(0)] internal uint Version;
        [FieldOffset(4)] internal Guid EncodeGuid;
        [FieldOffset(20)] internal Guid PresetGuid;
        [FieldOffset(36)] internal uint EncodeWidth;
        [FieldOffset(40)] internal uint EncodeHeight;
        [FieldOffset(44)] internal uint DarWidth;
        [FieldOffset(48)] internal uint DarHeight;
        [FieldOffset(52)] internal uint FrameRateNum;
        [FieldOffset(56)] internal uint FrameRateDen;
        [FieldOffset(60)] internal uint EnableEncodeAsync;
        [FieldOffset(64)] internal uint EnablePtd;
        [FieldOffset(88)] internal Config* EncodeConfig;
        [FieldOffset(96)] internal uint MaxEncodeWidth;
        [FieldOffset(100)] internal uint MaxEncodeHeight;
        [FieldOffset(136)] internal int TuningInfo;
    }

    [StructLayout(LayoutKind.Explicit, Size = 1536)]
    internal struct RegisterResource
    {
        [FieldOffset(0)] internal uint Version;
        [FieldOffset(4)] internal int ResourceType;
        [FieldOffset(8)] internal uint Width;
        [FieldOffset(12)] internal uint Height;
        [FieldOffset(16)] internal uint Pitch;
        [FieldOffset(20)] internal uint SubResourceIndex;
        [FieldOffset(24)] internal void* ResourceToRegister;
        [FieldOffset(32)] internal void* RegisteredResource;
        [FieldOffset(40)] internal int BufferFormat;
        [FieldOffset(44)] internal int BufferUsage;
    }

    [StructLayout(LayoutKind.Explicit, Size = 1544)]
    internal struct MapInputResource
    {
        [FieldOffset(0)] internal uint Version;
        [FieldOffset(16)] internal void* RegisteredResource;
        [FieldOffset(24)] internal void* MappedResource;
        [FieldOffset(32)] internal int MappedBufferFormat;
    }

    [StructLayout(LayoutKind.Explicit, Size = 776)]
    internal struct CreateBitstreamBuffer
    {
        [FieldOffset(0)] internal uint Version;
        [FieldOffset(16)] internal void* BitstreamBuffer;
    }

    [StructLayout(LayoutKind.Explicit, Size = 3344)]
    internal struct PicParams
    {
        [FieldOffset(0)] internal uint Version;
        [FieldOffset(4)] internal uint InputWidth;
        [FieldOffset(8)] internal uint InputHeight;
        [FieldOffset(12)] internal uint InputPitch;
        [FieldOffset(16)] internal uint EncodePicFlags;
        [FieldOffset(24)] internal ulong InputTimeStamp;
        [FieldOffset(40)] internal void* InputBuffer;
        [FieldOffset(48)] internal void* OutputBitstream;
        [FieldOffset(64)] internal int BufferFormat;
        [FieldOffset(68)] internal int PictureStruct;
    }

    [StructLayout(LayoutKind.Explicit, Size = 1544)]
    internal struct LockBitstream
    {
        [FieldOffset(0)] internal uint Version;
        [FieldOffset(4)] internal uint Flags;                 // bit 0: doNotWait
        [FieldOffset(8)] internal void* OutputBitstream;
        [FieldOffset(36)] internal uint BitstreamSizeInBytes;
        [FieldOffset(56)] internal void* BitstreamBufferPtr;
        [FieldOffset(64)] internal int PictureType;
    }

    [StructLayout(LayoutKind.Explicit, Size = 1544)]
    internal struct SequenceParamPayload
    {
        [FieldOffset(0)] internal uint Version;
        [FieldOffset(4)] internal uint InBufferSize;
        [FieldOffset(16)] internal void* SpsPpsBuffer;
        [FieldOffset(24)] internal uint* OutPayloadSize;
    }

    // The table NvEncodeAPICreateInstance fills. Only the pointers this server calls are declared,
    // at their verified offsets; the total size covers the reserved tail.
    [StructLayout(LayoutKind.Explicit, Size = 2552)]
    internal struct FunctionList
    {
        [FieldOffset(0)] internal uint Version;

        [FieldOffset(16)] internal delegate* unmanaged[Stdcall]<void*, uint*, int> GetEncodeGuidCount;
        [FieldOffset(40)] internal delegate* unmanaged[Stdcall]<void*, Guid*, uint, uint*, int> GetEncodeGuids;
        [FieldOffset(64)] internal delegate* unmanaged[Stdcall]<void*, Guid, CapsParam*, int*, int> GetEncodeCaps;
        [FieldOffset(96)] internal delegate* unmanaged[Stdcall]<void*, InitializeParams*, int> InitializeEncoder;
        [FieldOffset(120)] internal delegate* unmanaged[Stdcall]<void*, CreateBitstreamBuffer*, int> CreateBitstream;
        [FieldOffset(128)] internal delegate* unmanaged[Stdcall]<void*, void*, int> DestroyBitstream;
        [FieldOffset(136)] internal delegate* unmanaged[Stdcall]<void*, PicParams*, int> EncodePicture;
        [FieldOffset(144)] internal delegate* unmanaged[Stdcall]<void*, LockBitstream*, int> LockBitstreamBuffer;
        [FieldOffset(152)] internal delegate* unmanaged[Stdcall]<void*, void*, int> UnlockBitstreamBuffer;
        [FieldOffset(184)] internal delegate* unmanaged[Stdcall]<void*, SequenceParamPayload*, int> GetSequenceParams;
        [FieldOffset(208)] internal delegate* unmanaged[Stdcall]<void*, MapInputResource*, int> MapResource;
        [FieldOffset(216)] internal delegate* unmanaged[Stdcall]<void*, void*, int> UnmapResource;
        [FieldOffset(224)] internal delegate* unmanaged[Stdcall]<void*, int> DestroyEncoder;
        [FieldOffset(240)] internal delegate* unmanaged[Stdcall]<OpenSessionParams*, void**, int> OpenSessionEx;
        [FieldOffset(248)] internal delegate* unmanaged[Stdcall]<void*, RegisterResource*, int> Register;
        [FieldOffset(256)] internal delegate* unmanaged[Stdcall]<void*, void*, int> Unregister;
        [FieldOffset(304)] internal delegate* unmanaged[Stdcall]<void*, sbyte*> GetLastErrorString;
        [FieldOffset(320)] internal delegate* unmanaged[Stdcall]<void*, Guid, Guid, int, PresetConfig*, int> GetPresetConfigEx;
    }

    // ------------------------------------------------------------------ loading

    private static readonly object Gate = new();
    private static FunctionList* _api;
    private static bool _tried;

    // Loads nvEncodeAPI64.dll and fills the function table, once per process. Returns null, and
    // logs why once, when the library or its entry point is missing (no NVIDIA driver).
    internal static FunctionList* Api()
    {
        lock (Gate)
        {
            if (_tried) return _api;
            _tried = true;

            var library = Kernel32.LoadLibrary("nvEncodeAPI64.dll");
            if (library == 0)
            {
                Log.Info("nvEncodeAPI64.dll is not present; NVENC is unavailable " +
                         "(it ships with the NVIDIA display driver)");
                return null;
            }

            var entry = Kernel32.GetProcAddress(library, "NvEncodeAPICreateInstance");
            if (entry == 0)
            {
                Log.Warn("nvEncodeAPI64.dll has no NvEncodeAPICreateInstance; NVENC is unavailable");
                return null;
            }

            // The table lives for the life of the process, so it is allocated on the native heap
            // rather than pinned managed memory.
            var table = (FunctionList*)NativeMemory.AllocZeroed((nuint)sizeof(FunctionList));
            table->Version = FunctionListVer;

            var create = (delegate* unmanaged[Stdcall]<FunctionList*, int>)entry;
            var status = create(table);
            if (status != StatusSuccess)
            {
                // INVALID_VERSION here means the driver is older than API 11.1 — pre-471.41.
                Log.Warn($"NvEncodeAPICreateInstance failed: {status} ({Describe(status)})." +
                         (status == StatusInvalidVersion
                             ? " The NVIDIA driver is older than the NVENC API this server uses " +
                               "(11.1, driver 471.41); update the driver."
                             : string.Empty));
                NativeMemory.Free(table);
                return null;
            }

            _api = table;
            return _api;
        }
    }

    // The driver's own description of the last failure, when it offers one.
    internal static string LastError(void* encoder)
    {
        var api = _api;
        if (api is null || encoder is null || api->GetLastErrorString is null) return string.Empty;

        var text = api->GetLastErrorString(encoder);
        return text is null ? string.Empty : Marshal.PtrToStringAnsi((nint)text) ?? string.Empty;
    }
}
