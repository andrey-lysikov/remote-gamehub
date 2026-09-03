//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using RemoteGameHub.App;
using RemoteGameHub.Native;

namespace RemoteGameHub.Media;

// Finding playback devices and asking what each mixes into. The enumerator belongs to the caller;
// every device handed back here is the caller's to release.
internal static unsafe class AudioEndpoints
{
    // The default playback device for the console role, which is the one a person means.
    internal static void* Default(void* enumerator)
    {
        Wasapi.GetDefaultAudioEndpoint(enumerator, Wasapi.EDataFlowRender,
            Wasapi.ERoleConsole, out var device);
        return device;
    }

    // The first active device whose name contains namePart, or null.
    internal static void* Find(void* enumerator, string namePart)
    {
        if (Wasapi.EnumAudioEndpoints(enumerator, Wasapi.EDataFlowRender,
                Wasapi.DEVICE_STATE_ACTIVE, out var collection) < 0 || collection is null)
            return null;

        try
        {
            var count = Wasapi.GetDeviceCount(collection);
            for (uint i = 0; i < count; i++)
            {
                if (Wasapi.GetDeviceAt(collection, i, out var candidate) < 0 || candidate is null)
                    continue;

                var name = Wasapi.GetDeviceName(candidate);
                if (name is not null && name.Contains(namePart, StringComparison.OrdinalIgnoreCase))
                    return candidate;

                Com.Release(candidate);
            }
        }
        finally
        {
            Com.Release(collection);
        }

        return null;
    }

    // One endpoint's mix channel count, or zero when it will not say. Nothing is initialised and
    // nothing starts, so this does not take the device away from whatever is playing through it.
    internal static int MixChannelsOf(void* device)
    {
        void* client = null;

        try
        {
            if (Wasapi.Activate(device, Wasapi.IID_IAudioClient, out client) < 0 || client is null)
                return 0;

            if (Wasapi.GetMixFormat(client, out var mix) < 0 || mix is null) return 0;

            try
            {
                return mix->Channels;
            }
            finally
            {
                Wasapi.CoTaskMemFree(mix);
            }
        }
        finally
        {
            Com.Release(client);
        }
    }

    // Widens a device's own shared-mode format to carry more channels, keeping its rate, its bit
    // depth and its sample type exactly as they were — only the channel count and the speaker mask
    // change. Returns the format as it was, to be handed back to SetDeviceFormat afterwards; null
    // when nothing was changed, whether because it did not need to be or because it would not.
    internal static byte[]? Widen(void* device, int channels)
    {
        var mask = channels switch
        {
            6 => Wasapi.KSAUDIO_SPEAKER_5POINT1,
            8 => Wasapi.KSAUDIO_SPEAKER_7POINT1_SURROUND,
            _ => 0u,
        };
        if (mask == 0) return null;

        var was = Wasapi.GetDeviceFormat(device);
        var size = Marshal.SizeOf<WaveFormatExtensible>();
        if (was is null || was.Length != size) return null;

        var format = MemoryMarshal.Read<WaveFormatExtensible>(was);
        if (format.Channels >= channels) return null;

        format.Channels = (ushort)channels;
        format.ChannelMask = mask;
        format.BlockAlign = (ushort)(channels * (format.BitsPerSample / 8));
        format.AverageBytesPerSecond = format.SamplesPerSecond * format.BlockAlign;

        var wider = new byte[size];
        MemoryMarshal.Write(wider, in format);

        return Wasapi.SetDeviceFormat(device, wider) ? was : null;
    }

    // Every playback device with what each mixes into, once at startup: surround is a property of
    // the endpoint, and a machine with no six-channel one cannot send it whatever is asked.
    internal static void ListToLog()
    {
        void* enumerator = null;
        void* collection = null;

        try
        {
            enumerator = Wasapi.CreateDeviceEnumerator();
            if (Wasapi.EnumAudioEndpoints(enumerator, Wasapi.EDataFlowRender,
                    Wasapi.DEVICE_STATE_ACTIVE, out collection) < 0 || collection is null)
                return;

            var found = new List<string>();
            var count = Wasapi.GetDeviceCount(collection);

            for (uint i = 0; i < count; i++)
            {
                if (Wasapi.GetDeviceAt(collection, i, out var device) < 0 || device is null) continue;

                try
                {
                    found.Add($"\"{Wasapi.GetDeviceName(device) ?? "unnamed device"}\": " +
                              $"{MixChannelsOf(device)} channel(s)");
                }
                finally
                {
                    Com.Release(device);
                }
            }

            if (found.Count > 0)
                Log.Event("playback devices and what each mixes into:\n" + string.Join("\n", found));
        }
        catch (Exception error)
        {
            Log.Info($"the playback devices could not be listed: {error.Message}");
        }
        finally
        {
            Com.Release(collection);
            Com.Release(enumerator);
        }
    }
}

// Moves the machine's sound onto a device that can carry surround for one stream and puts it back
// after — Windows mixes down to the default device's channels long before loopback capture sees it.
internal sealed unsafe class AudioAdaptation : IDisposable
{
    // The virtual device Steam installs with itself: up to 7.1 whatever the real hardware is, and
    // the same name on every machine. Sunshine streams surround through it for the same reason.
    internal const string SurroundDevice = "Steam Streaming Speakers";

    // Where the sound was before, and null when nothing was moved and there is nothing to put back.
    private readonly string? _previousId;
    private readonly string _previousName;

    // The surround device's own format, widened for this stream, and what it was before that: set
    // only when Widen actually changed it, so there is something of this device's own to give back.
    private readonly string? _widenedId;
    private readonly byte[]? _previousFormat;

    private bool _restored;

    private AudioAdaptation(string? previousId = null, string previousName = "",
                            string? widenedId = null, byte[]? previousFormat = null)
    {
        _previousId = previousId;
        _previousName = previousName;
        _widenedId = widenedId;
        _previousFormat = previousFormat;
    }

    // Moves the sound to the surround device when the client asked for more than two channels.
    // Never throws: sound on the wrong device is fewer channels, not a stream that fails.
    internal static AudioAdaptation Apply(int channels)
    {
        // Stereo needs nothing. Every device does it, and moving the machine's sound for a stream
        // that would not use the extra channels is a change nobody asked for.
        if (channels <= 2) return new AudioAdaptation();

        var initialised = Wasapi.CoInitializeEx(0, Wasapi.COINIT_MULTITHREADED);

        void* enumerator = null;
        void* wanted = null;
        void* current = null;

        try
        {
            enumerator = Wasapi.CreateDeviceEnumerator();
            wanted = AudioEndpoints.Find(enumerator, SurroundDevice);

            if (wanted is null)
            {
                Log.Event(
                    $"The client asked for {channels} audio channels and \"{SurroundDevice}\" is not\n" +
                    "on this machine, so the sound stays where it is and goes out with the channels\n" +
                    "the default device mixes into — two, on an HDMI output to a screen.\n" +
                    "What to do: that device comes with Steam, so installing Steam adds it. Any other\n" +
                    "device set to 5.1 in Windows' sound settings and made the default does as well.");
                return new AudioAdaptation();
            }

            var wantedId = Wasapi.GetDeviceId(wanted);
            if (wantedId is null) return new AudioAdaptation();

            current = AudioEndpoints.Default(enumerator);
            var currentId = current is null ? null : Wasapi.GetDeviceId(current);
            var currentName = (current is null ? null : Wasapi.GetDeviceName(current))
                              ?? "the default device";

            if (string.Equals(currentId, wantedId, StringComparison.OrdinalIgnoreCase))
            {
                Log.Event($"the client asked for {channels} audio channels and the sound is already " +
                          $"on \"{SurroundDevice}\"; it is left there");

                var (already, restore) = WidenIfNeeded(wanted, wantedId, channels);
                return new AudioAdaptation(widenedId: already, previousFormat: restore);
            }

            if (!MoveTo(wantedId))
            {
                Log.Event($"the sound could not be moved to \"{SurroundDevice}\"; it stays on " +
                          $"\"{currentName}\" and goes out with that device's channels");
                return new AudioAdaptation();
            }

            var back = currentId is null
                ? " There was no default device to remember, so nothing is put back afterwards."
                : string.Empty;

            Log.Event($"the client asked for {channels} audio channels, so the sound is moved from " +
                      $"\"{currentName}\" to \"{SurroundDevice}\" for this stream." + back);

            var (widened, previous) = WidenIfNeeded(wanted, wantedId, channels);
            return new AudioAdaptation(currentId, currentName, widened, previous);
        }
        catch (Exception error)
        {
            Log.Warn($"the sound could not be moved for this stream: {error.Message}");
            return new AudioAdaptation();
        }
        finally
        {
            Com.Release(current);
            Com.Release(wanted);
            Com.Release(enumerator);

            // Balanced for every call that succeeded, S_FALSE included; only RPC_E_CHANGED_MODE,
            // which is the one failure, must be left alone.
            if (initialised >= 0) Wasapi.CoUninitialize();
        }
    }

    // "Steam Streaming Speakers" is a virtual device and often stuck at whatever it first
    // negotiated — commonly stereo — until something asks it for more. Widened here rather than
    // left to the capture's own warning, which only says the extra channels went nowhere.
    private static (string? WidenedId, byte[]? Previous) WidenIfNeeded(void* device, string deviceId,
                                                                        int channels)
    {
        if (AudioEndpoints.MixChannelsOf(device) >= channels) return (null, null);

        var previous = AudioEndpoints.Widen(device, channels);
        if (previous is null)
        {
            Log.Info($"\"{SurroundDevice}\" could not be widened to {channels} channels; " +
                     "it stays as it was");
            return (null, null);
        }

        Log.Event($"\"{SurroundDevice}\" is set to {channels} channels for this stream");
        return (deviceId, previous);
    }

    // The default playback device, set for all three of Windows' roles — console, multimedia and
    // communications — because "the default device" means all three to the person who set it.
    private static bool MoveTo(string deviceId)
    {
        void* policy = null;

        try
        {
            policy = Wasapi.CreatePolicyConfig();

            for (var role = 0; role < Wasapi.ERoleCount; role++)
                if (Wasapi.SetDefaultEndpoint(policy, deviceId, role) < 0) return false;

            return true;
        }
        catch (Exception error)
        {
            Log.Info($"the default playback device could not be set: {error.Message}");
            return false;
        }
        finally
        {
            Com.Release(policy);
        }
    }

    public void Dispose()
    {
        if (_restored || (_previousId is null && _widenedId is null)) return;
        _restored = true;

        var initialised = Wasapi.CoInitializeEx(0, Wasapi.COINIT_MULTITHREADED);
        void* enumerator = null;
        void* widened = null;

        try
        {
            // The format first, while the device is still known by an id that need not be the
            // default any more once the line after this puts the sound back where it came from.
            if (_widenedId is not null && _previousFormat is not null)
            {
                enumerator = Wasapi.CreateDeviceEnumerator();
                if (Wasapi.GetDevice(enumerator, _widenedId, out widened) >= 0 && widened is not null)
                {
                    Log.Event(Wasapi.SetDeviceFormat(widened, _previousFormat)
                        ? $"\"{SurroundDevice}\" is back to the format it had"
                        : $"\"{SurroundDevice}\" could not be put back to the format it had");
                }
            }

            if (_previousId is not null)
            {
                Log.Event(MoveTo(_previousId)
                    ? $"the sound is back on \"{_previousName}\""
                    : $"the sound could not be put back on \"{_previousName}\" and is still on " +
                      $"\"{SurroundDevice}\"; change it in Windows' sound settings");
            }
        }
        finally
        {
            Com.Release(widened);
            Com.Release(enumerator);
            if (initialised >= 0) Wasapi.CoUninitialize();
        }
    }
}
