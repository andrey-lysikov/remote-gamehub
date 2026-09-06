//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace RemoteGameHub.Native;

// A wave format in the extensible shape every modern endpoint speaks: the base 18 bytes, then a
// channel mask and a sub-format telling PCM from float, which wFormatTag alone could not.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct WaveFormatExtensible
{
    internal ushort FormatTag;
    internal ushort Channels;
    internal uint SamplesPerSecond;
    internal uint AverageBytesPerSecond;
    internal ushort BlockAlign;
    internal ushort BitsPerSample;
    internal ushort ExtraSize;

    // Present only when ExtraSize says so; the struct is always allocated at full size here
    // because the only formats this server asks for are extensible ones.
    internal ushort ValidBitsPerSample;
    internal uint ChannelMask;
    internal Guid SubFormat;
}

// The slice of WASAPI this server needs: open the playback endpoint for loopback capture and read
// the mix. Slots are the member's position in mmdeviceapi.h, audioclient.h and propsys.h.
internal static unsafe class Wasapi
{
    // mmdeviceapi.h DEFINE_GUID lines, and audioclient.h / propsys.h for the rest.
    internal static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    internal static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    internal static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    internal static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    // ksmedia.h: the sub-format that means plain integer PCM.
    internal static readonly Guid KSDATAFORMAT_SUBTYPE_PCM =
        new("00000001-0000-0010-8000-00AA00389B71");

    // ksmedia.h: 32-bit float samples, which is what a mix format usually is.
    internal static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT =
        new("00000003-0000-0010-8000-00AA00389B71");

    internal const int CLSCTX_ALL = 0x17;
    internal const uint COINIT_MULTITHREADED = 0;

    // PolicyConfig, in no SDK header: the interface every audio switcher uses, Sunshine included.
    // GUIDs from its reverse-engineered PolicyConfig.h.
    internal static readonly Guid CLSID_CPolicyConfigClient =
        new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");

    internal static readonly Guid IID_IPolicyConfig = new("F8679F50-850A-41CF-9C72-430F290290C8");

    internal const int EDataFlowRender = 0;    // eRender
    internal const int DEVICE_STATE_ACTIVE = 0x1;

    // mmdeviceapi.h ERole. A default device is three defaults, and Windows keeps them apart.
    internal const int ERoleConsole = 0;
    internal const int ERoleMultimedia = 1;
    internal const int ERoleCommunications = 2;
    internal const int ERoleCount = 3;

    internal const uint AUDCLNT_SHAREMODE_SHARED = 0;

    // audiosessiontypes.h.
    internal const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    internal const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
    internal const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;

    internal const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    // AUDCLNT_ERR(n) = MAKE_HRESULT(SEVERITY_ERROR, FACILITY_AUDCLNT, n), and winerror.h gives
    // FACILITY_AUDCLNT = 2185 (0x889).

    internal const ushort WAVE_FORMAT_EXTENSIBLE = 0xFFFE;

    // ksmedia.h speaker masks, for the two channel counts Moonlight ever asks for above stereo:
    // front L/R/centre, LFE, back L/R for 6; adding the sides is what 7.1 is over that.
    internal const uint KSAUDIO_SPEAKER_5POINT1 = 0x3F;
    internal const uint KSAUDIO_SPEAKER_7POINT1_SURROUND = 0x63F;

    // propsys.h / functiondiscoverykeys_devpkey.h: the name a person would recognise.
    internal static readonly Guid PKEY_Device_FriendlyName_Format =
        new("A45C254E-DF1C-4EFD-8020-67D146A850E0");
    internal const uint PKEY_Device_FriendlyName_Id = 14;

    // mmdeviceapi.h: the shared-mode format every WASAPI client gets handed. Writable — the same
    // value the Sound control panel's "Default Format" changes, by the same means.
    internal static readonly Guid PKEY_AudioEngine_DeviceFormat_Format =
        new("F19F064D-082C-4E27-BC73-6882A1BB8E4C");
    internal const uint PKEY_AudioEngine_DeviceFormat_Id = 0;

    internal const ushort VT_LPWSTR = 31;
    internal const ushort VT_BLOB = 65;

    internal const uint STGM_READ = 0;

    // ------------------------------------------------------------------ the runtime

    [DllImport("ole32.dll")]
    internal static extern int CoInitializeEx(nint reserved, uint model);

    [DllImport("ole32.dll")]
    internal static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(Guid* rclsid, void* outer, uint context,
                                               Guid* riid, void** result);

    [DllImport("ole32.dll")]
    internal static extern void CoTaskMemFree(void* memory);

    // Creates the device enumerator, the entry point to everything else here.
    internal static void* CreateDeviceEnumerator()
    {
        void* enumerator;
        fixed (Guid* clsid = &CLSID_MMDeviceEnumerator)
        fixed (Guid* iid = &IID_IMMDeviceEnumerator)
            Com.Check(CoCreateInstance(clsid, null, CLSCTX_ALL, iid, &enumerator),
                "CoCreateInstance(MMDeviceEnumerator)");
        return enumerator;
    }

    // Creates the policy configuration object, which is what moves the default playback device.
    internal static void* CreatePolicyConfig()
    {
        void* policy;
        fixed (Guid* clsid = &CLSID_CPolicyConfigClient)
        fixed (Guid* iid = &IID_IPolicyConfig)
            Com.Check(CoCreateInstance(clsid, null, CLSCTX_ALL, iid, &policy),
                "CoCreateInstance(CPolicyConfigClient)");
        return policy;
    }

    // ------------------------------------------------------------------ IPolicyConfig

    // Slot 13: SetDefaultEndpoint(PCWSTR deviceId, ERole). PolicyConfig.h's eleventh method after
    // IUnknown: four format calls, two period, two share-mode and two property calls come first.
    internal static int SetDefaultEndpoint(void* policy, string deviceId, int role)
    {
        fixed (char* id = deviceId)
            return ((delegate* unmanaged[Stdcall]<void*, char*, int, int>)
                Com.VTable(policy)[13])(policy, id, role);
    }

    // ------------------------------------------------------------------ IMMDeviceEnumerator

    // Slot 3: EnumAudioEndpoints(EDataFlow, DWORD stateMask, IMMDeviceCollection**)
    internal static int EnumAudioEndpoints(void* enumerator, int dataFlow, uint stateMask,
                                           out void* collection)
    {
        collection = null;
        fixed (void** result = &collection)
            return ((delegate* unmanaged[Stdcall]<void*, int, uint, void**, int>)
                Com.VTable(enumerator)[3])(enumerator, dataFlow, stateMask, result);
    }

    // Slot 4: GetDefaultAudioEndpoint(EDataFlow, ERole, IMMDevice**)
    internal static int GetDefaultAudioEndpoint(void* enumerator, int dataFlow, int role,
                                               out void* device)
    {
        device = null;
        fixed (void** result = &device)
            return ((delegate* unmanaged[Stdcall]<void*, int, int, void**, int>)
                Com.VTable(enumerator)[4])(enumerator, dataFlow, role, result);
    }

    // ------------------------------------------------------------------ IMMDeviceCollection

    // Slot 3: GetCount(UINT*)
    internal static uint GetDeviceCount(void* collection)
    {
        uint count;
        Com.Check(((delegate* unmanaged[Stdcall]<void*, uint*, int>)Com.VTable(collection)[3])(
            collection, &count), "IMMDeviceCollection::GetCount");
        return count;
    }

    // Slot 4: Item(UINT, IMMDevice**)
    internal static int GetDeviceAt(void* collection, uint index, out void* device)
    {
        device = null;
        fixed (void** result = &device)
            return ((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)
                Com.VTable(collection)[4])(collection, index, result);
    }

    // ------------------------------------------------------------------ IMMDevice

    // Slot 3: Activate(REFIID, DWORD clsCtx, PROPVARIANT* params, void** out)
    internal static int Activate(void* device, in Guid iid, out void* result)
    {
        result = null;
        fixed (Guid* id = &iid)
        fixed (void** output = &result)
            return ((delegate* unmanaged[Stdcall]<void*, Guid*, uint, void*, void**, int>)
                Com.VTable(device)[3])(device, id, CLSCTX_ALL, null, output);
    }

    // Slot 4: OpenPropertyStore(DWORD access, IPropertyStore**). Read-only: nothing is written
    // through the property store any more, PolicyConfig's own SetDeviceFormat is what changes one.
    internal static int OpenPropertyStore(void* device, out void* store)
    {
        store = null;
        fixed (void** result = &store)
            return ((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)
                Com.VTable(device)[4])(device, STGM_READ, result);
    }

    // Slot 5: GetId(LPWSTR*)
    internal static string? GetDeviceId(void* device)
    {
        char* id;
        if (((delegate* unmanaged[Stdcall]<void*, char**, int>)Com.VTable(device)[5])(
                device, &id) < 0)
            return null;

        try
        {
            return Marshal.PtrToStringUni((nint)id);
        }
        finally
        {
            CoTaskMemFree(id);
        }
    }

    // The endpoint's friendly name through its property store. Returns null rather than throwing:
    // a device that will not say its name can still be captured.
    internal static string? GetDeviceName(void* device)
    {
        if (OpenPropertyStore(device, out var store) < 0 || store is null) return null;

        try
        {
            // PROPVARIANT on x64: a two-byte type, six bytes of padding and reserved fields,
            // then the value union — so the string pointer sits at offset 8.
            var variant = stackalloc byte[24];
            new Span<byte>(variant, 24).Clear();

            var key = new PropertyKey
            {
                FormatId = PKEY_Device_FriendlyName_Format,
                PropertyId = PKEY_Device_FriendlyName_Id,
            };

            // IPropertyStore slot 5: GetValue(REFPROPERTYKEY, PROPVARIANT*)
            if (((delegate* unmanaged[Stdcall]<void*, PropertyKey*, byte*, int>)
                    Com.VTable(store)[5])(store, &key, variant) < 0)
                return null;

            // Cleared whatever the type turned out to be: the value belongs to the store until then.
            try
            {
                if (*(ushort*)variant != VT_LPWSTR) return null;
                return Marshal.PtrToStringUni(*(nint*)(variant + 8));
            }
            finally
            {
                PropVariantClear(variant);
            }
        }
        finally
        {
            Com.Release(store);
        }
    }

    // The device's shared-mode format, as the raw WAVEFORMATEXTENSIBLE bytes the property carries;
    // null when it will not say. Read with the store opened for reading, which is enough for this.
    internal static byte[]? GetDeviceFormat(void* device)
    {
        if (OpenPropertyStore(device, out var store) < 0 || store is null) return null;

        try
        {
            var variant = stackalloc byte[24];
            new Span<byte>(variant, 24).Clear();

            var key = new PropertyKey
            {
                FormatId = PKEY_AudioEngine_DeviceFormat_Format,
                PropertyId = PKEY_AudioEngine_DeviceFormat_Id,
            };

            if (((delegate* unmanaged[Stdcall]<void*, PropertyKey*, byte*, int>)
                    Com.VTable(store)[5])(store, &key, variant) < 0)
                return null;

            try
            {
                if (*(ushort*)variant != VT_BLOB) return null;

                // The BLOB union member: a byte count at offset 8, then — past the padding that
                // aligns the pointer after it — the bytes themselves.
                var size = *(uint*)(variant + 8);
                var data = *(byte**)(variant + 16);
                if (size == 0 || data is null) return null;

                var bytes = new byte[size];
                new Span<byte>(data, (int)size).CopyTo(bytes);
                return bytes;
            }
            finally
            {
                PropVariantClear(variant);
            }
        }
        finally
        {
            Com.Release(store);
        }
    }

    // PolicyConfig.h slot 6. Writing PKEY_AudioEngine_DeviceFormat through the property store
    // alone left clients refused with AUDCLNT_E_UNSUPPORTED_FORMAT; this call actually reconfigures.
    internal static int SetDeviceFormat(void* policy, string deviceId, byte[] format)
    {
        fixed (char* id = deviceId)
        fixed (byte* bytes = format)
            return ((delegate* unmanaged[Stdcall]<void*, char*, byte*, byte*, int>)
                Com.VTable(policy)[6])(policy, id, bytes, bytes);
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(byte* variant);

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        internal Guid FormatId;
        internal uint PropertyId;
    }

    // ------------------------------------------------------------------ IAudioClient

    // Slot 3: Initialize(AUDCLNT_SHAREMODE, DWORD flags, REFERENCE_TIME buffer,
    //                    REFERENCE_TIME periodicity, const WAVEFORMATEX*, LPCGUID session)
    internal static int Initialize(void* client, uint shareMode, uint flags,
                                   long bufferDuration, WaveFormatExtensible* format)
    {
        return ((delegate* unmanaged[Stdcall]<void*, uint, uint, long, long,
                    WaveFormatExtensible*, void*, int>)Com.VTable(client)[3])(
            client, shareMode, flags, bufferDuration, 0, format, null);
    }

    // Slot 4: GetBufferSize(UINT32*)
    internal static uint GetBufferSize(void* client)
    {
        uint frames;
        Com.Check(((delegate* unmanaged[Stdcall]<void*, uint*, int>)Com.VTable(client)[4])(
            client, &frames), "IAudioClient::GetBufferSize");
        return frames;
    }

    // Slot 8: GetMixFormat(WAVEFORMATEX**) — the caller owns the buffer.
    internal static int GetMixFormat(void* client, out WaveFormatExtensible* format)
    {
        WaveFormatExtensible* result;
        var hr = ((delegate* unmanaged[Stdcall]<void*, WaveFormatExtensible**, int>)
            Com.VTable(client)[8])(client, &result);
        format = hr >= 0 ? result : null;
        return hr;
    }

    // Slot 10: Start
    internal static int Start(void* client) =>
        ((delegate* unmanaged[Stdcall]<void*, int>)Com.VTable(client)[10])(client);

    // Slot 11: Stop
    internal static int Stop(void* client) =>
        ((delegate* unmanaged[Stdcall]<void*, int>)Com.VTable(client)[11])(client);

    // Slot 14: GetService(REFIID, void**)
    internal static int GetService(void* client, in Guid iid, out void* service)
    {
        service = null;
        fixed (Guid* id = &iid)
        fixed (void** result = &service)
            return ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)
                Com.VTable(client)[14])(client, id, result);
    }

    // ------------------------------------------------------------------ IAudioCaptureClient

    // Slot 3: GetBuffer(BYTE**, UINT32* frames, DWORD* flags, UINT64* pos, UINT64* qpc)
    internal static int GetBuffer(void* capture, out byte* data, out uint frames, out uint flags)
    {
        byte* buffer;
        uint frameCount;
        uint bufferFlags;

        var hr = ((delegate* unmanaged[Stdcall]<void*, byte**, uint*, uint*, ulong*, ulong*, int>)
            Com.VTable(capture)[3])(capture, &buffer, &frameCount, &bufferFlags, null, null);

        data = buffer;
        frames = frameCount;
        flags = bufferFlags;
        return hr;
    }

    // Slot 4: ReleaseBuffer(UINT32)
    internal static int ReleaseBuffer(void* capture, uint frames) =>
        ((delegate* unmanaged[Stdcall]<void*, uint, int>)Com.VTable(capture)[4])(capture, frames);

    // Slot 5: GetNextPacketSize(UINT32*)
    internal static int GetNextPacketSize(void* capture, out uint frames)
    {
        uint count;
        var hr = ((delegate* unmanaged[Stdcall]<void*, uint*, int>)Com.VTable(capture)[5])(
            capture, &count);
        frames = count;
        return hr;
    }
}
