//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Concentus.Enums;
using Concentus.Structs;
using RemoteGameHub.App;
using RemoteGameHub.Native;
using RemoteGameHub.Protocol;

namespace RemoteGameHub.Media;

// The sound of this machine on its way to the client: WASAPI loopback capture, Opus through
// Concentus, and RTP on 48000/udp with four-data-two-parity forward error correction.
internal sealed class AudioStream : IDisposable
{
    private const int SampleRate = 48000;

    // RTP payload types: 97 is Opus audio, 127 marks a parity packet.
    private const byte PayloadTypeAudio = 97;
    private const byte PayloadTypeFec = 127;

    private const int RtpHeaderBytes = 12;
    private const int FecHeaderBytes = 12;

    // The block shape is fixed by the protocol: four data packets, two parity.
    private const int DataShards = 4;
    private const int ParityShards = 2;

    // An Opus packet at these bitrates is far smaller; this is only the ceiling.
    private const int MaxPacketBytes = 1400;

    private readonly int _port;
    private readonly string _bindAddress;
    private readonly string _wantedDevice;
    private readonly int _channels;
    private readonly uint _channelMask;
    private readonly bool _highQuality;
    private readonly int _frameSizeSamples;
    private readonly int _bitrate;
    private readonly bool _encrypt;
    private readonly byte[] _riKey;
    private readonly uint _riKeyId;

    private readonly ReedSolomon _fec = ReedSolomon.ForAudio();
    private readonly byte[][] _shards = new byte[DataShards + ParityShards][];

    private Socket? _socket;
    private Thread? _capture;
    private Thread? _pings;
    private volatile bool _running;
    private EndPoint? _peer;

    // The cipher for the audio stream, made on first use. See Encrypt.
    private Aes? _aes;

    private Aes CreateAes()
    {
        var aes = Aes.Create();
        aes.Key = _riKey;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        return aes;
    }

    // Where Fit lays the frames out when the endpoint's channel count is not the negotiated one.
    // Owned by the capture thread, which is the only one that touches it.
    private short[] _fitted = Array.Empty<short>();

    private ushort _sequenceNumber;
    private uint _timestamp;
    private ushort _blockBaseSequence;
    private uint _blockBaseTimestamp;
    private int _blockShardSize;

    internal AudioStream(int port, string bindAddress, string device,
                         StreamNegotiation negotiation, byte[] riKey, uint riKeyId)
    {
        _port = port;
        _bindAddress = bindAddress;
        _wantedDevice = device;
        _channels = negotiation.AudioChannels;
        _channelMask = (uint)negotiation.AudioChannelMask;
        _highQuality = negotiation.AudioHighQuality;
        _frameSizeSamples = negotiation.AudioPacketDurationMs * SampleRate / 1000;
        _riKey = riKey;
        _riKeyId = riKeyId;

        // Audio encryption is never offered in DESCRIBE, so this is only ever on for a client
        // that turned it on through the legacy feature flag. SS_ENC_AUDIO is bit 0x04.
        _encrypt = (negotiation.EncryptionFlags & 0x04) != 0;

        // This server's own choice — see the note on StreamNegotiation.AudioBitrateKbps. The
        // wire carries no bitrate anywhere; the client simply decodes what arrives.
        _bitrate = negotiation.AudioBitrateKbps * 1000;

        for (var i = 0; i < _shards.Length; i++) _shards[i] = new byte[MaxPacketBytes];
    }

    internal void Start()
    {
        _socket = VideoStream.UdpBind(_port, _bindAddress);
        _running = true;

        // Same priority as the "stream" thread (see StreamSession.Start): audio sharing a core
        // with it at only Normal would be the one this server itself starves to fix video.
        _pings = new Thread(PingLoop)
            { IsBackground = true, Name = "audio-ping", Priority = ThreadPriority.Highest };
        _pings.Start();

        _capture = new Thread(CaptureLoop)
            { IsBackground = true, Name = "audio", Priority = ThreadPriority.Highest };
        _capture.Start();

        Log.Info($"audio stream ready on port {_port}: {_channels} channel(s) at " +
                 $"{_bitrate / 1000} kbit/s, {_frameSizeSamples * 1000 / SampleRate} ms packets" +
                 (_encrypt ? ", encrypted" : string.Empty));
    }

    // The client pings this port from the socket it will listen on, which is the only way to
    // learn the source port its NAT chose. Nothing is sent before the first one arrives.
    private void PingLoop()
    {
        var buffer = new byte[64];
        var any = new IPEndPoint(
            _socket!.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);

        while (_running)
        {
            try
            {
                EndPoint sender = any;
                if (_socket.ReceiveFrom(buffer, ref sender) <= 0) continue;

                if (_peer is null) Log.Info($"audio: the client pings from {Peer.Describe(sender)}");
                _peer = sender;
            }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionReset)
            {
                // ICMP from one of this socket's own sends; the pings keep the truth current.
            }
            catch (Exception)
            {
                if (!_running) return;
            }
        }
    }

    // ------------------------------------------------------------------ capture and encode

    private void CaptureLoop()
    {
        // Multithreaded apartment: this thread owns its COM objects and never pumps messages.
        // The tray icon's thread is the STA one and shares nothing with this.
        Wasapi.CoInitializeEx(0, Wasapi.COINIT_MULTITHREADED);

        // Raised for as long as a stream runs and lowered below. Measured here: Sleep(2) takes
        // 15.57 ms at the default resolution and 2.66 ms with this, and the loop sleeps in twos.
        var raised = WinMm.RaiseTimerResolution();
        if (!raised) Log.Info("the timer resolution could not be raised; audio will be sent in bursts");

        try
        {
            // 5.1/7.1 as the client decodes them: front L/R, centre, LFE, rear L/R, then (7.1)
            // the two side channels — the order Windows already uses, so nothing is shuffled.
            var mapping = _channels switch
            {
                8 => new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 },
                6 => new byte[] { 0, 1, 2, 3, 4, 5 },
                _ => new byte[] { 0, 1 },
            };

            // The shape DESCRIBE advertised and the client decodes in — 5.1: 4 streams/2 coupled
            // normal, 6/0 high; 7.1: 5/3 normal, 8/0 high; stereo: 1/1. Wrong here is noise.
            var streams = _channels switch { 8 => _highQuality ? 8 : 5, 6 => _highQuality ? 6 : 4, _ => 1 };
            var coupled = _channels switch { 8 => _highQuality ? 0 : 3, 6 => _highQuality ? 0 : 2, _ => 1 };

            // The mapping is the identity one on purpose, and it is deliberately not the mapping
            // the DESCRIBE above advertises for these same layouts: see the surround-params lines
            // in RtspServer, which rotate it. Both halves are what the reference server does, and
            // the client is built for the pair.
            //
            // Concentus deprecates this call in favour of OpusCodecFactory, and this is the one
            // place the factory cannot stand in for it. All it offers for several streams is the
            // surround form, which picks the layout itself from a mapping family and hands back a
            // mapping of its own, built for input in Vorbis speaker order. Asked for six channels
            // it answers four streams and two coupled — the counts wanted here — with the mapping
            // [0,4,1,2,3,5], which reads its input as front left, centre, front right, rears,
            // then low frequency. What arrives here is what Windows captures: front left, front
            // right, centre, low frequency, then the rears. Eight channels differ the same way.
            // Only the deprecated call takes a layout and a mapping and uses them unchanged.
#pragma warning disable CS0618
            var encoder = OpusMSEncoder.Create(SampleRate, _channels, streams, coupled, mapping,
                OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
#pragma warning restore CS0618

            encoder.Bitrate = _bitrate;
            // Constant bitrate, as the reference server uses: every packet in a FEC block must be
            // the same length for the block to have one shard size.
            encoder.UseVBR = false;

            var pending = new short[_frameSizeSamples * _channels];
            var pendingSamples = 0;
            var packet = new byte[MaxPacketBytes];
            var report = new PacingReport();

            while (_running)
            {
                using var device = LoopbackDevice.Open(_wantedDevice, _channels, _channelMask);
                if (device is null)
                {
                    // Nothing to capture from — no endpoint, or one that refused. The stream
                    // stays silent rather than ending: a device can come back.
                    Thread.Sleep(1000);
                    continue;
                }

                // The endpoint cannot fill what the client agreed to decode: the spare channels
                // go out silent, which is the answer to "5.1 is on and only the front pair plays".
                if (device.MixChannels > 0 && device.MixChannels < _channels)
                {
                    Log.WarnOccasionally("surround from a smaller device",
                        $"The client asked for {_channels} audio channels and \"{device.Name}\" mixes\n" +
                        $"into {device.MixChannels}. The stream carries {_channels}, with the front pair\n" +
                        "holding the sound and the rest silent: Windows mixes down before this server\n" +
                        "sees it.\n" +
                        "What to do: install Steam, which brings the \"" + AudioAdaptation.SurroundDevice +
                        "\" device this\nserver moves the sound to; or set a 5.1 device as the default " +
                        "in Windows' sound\nsettings; or set the client to stereo.");
                }

                if (device.Channels != _channels)
                {
                    Log.Info($"the device is giving {device.Channels} channel(s) and the client " +
                             $"agreed {_channels}; the frames are being laid out again on the way");
                }

                while (_running && device.IsHealthy)
                {
                    report.Turn();

                    if (!device.Read(out var captured))
                    {
                        Thread.Sleep(2);
                        continue;
                    }

                    var samples = Fit(captured, device.Channels);

                    var offset = 0;
                    while (offset < samples.Length)
                    {
                        var take = Math.Min(pending.Length - pendingSamples, samples.Length - offset);
                        samples.Slice(offset, take).CopyTo(pending.AsSpan(pendingSamples));
                        pendingSamples += take;
                        offset += take;

                        if (pendingSamples < pending.Length) continue;
                        pendingSamples = 0;

                        var encoded = encoder.EncodeMultistream(pending, _frameSizeSamples,
                            packet, packet.Length);

                        if (encoded > 0)
                        {
                            Send(packet.AsSpan(0, encoded));
                            report.Sent();
                        }
                    }
                }
            }
        }
        catch (Exception error)
        {
            // The audio path ending must not take the stream with it: a session with a picture
            // and no sound is far better than no session.
            Log.Error("the audio stream stopped", error);
        }
        finally
        {
            if (raised) WinMm.LowerTimerResolution();
            Wasapi.CoUninitialize();
        }
    }

    // The endpoint's frames laid out in the channel count the client decodes, for the driver that
    // refuses the conversion Activate asks for and hands back its own mix instead.
    private ReadOnlySpan<short> Fit(ReadOnlySpan<short> source, int sourceChannels)
    {
        if (sourceChannels == _channels || sourceChannels <= 0) return source;

        var frames = source.Length / sourceChannels;
        var needed = frames * _channels;
        if (_fitted.Length < needed) _fitted = new short[needed];

        for (var frame = 0; frame < frames; frame++)
        {
            var from = source.Slice(frame * sourceChannels, sourceChannels);
            var to = _fitted.AsSpan(frame * _channels, _channels);
            to.Clear();

            if (sourceChannels == 6 && _channels == 2)
            {
                // The usual fold: the centre and each rear channel into the front pair, three
                // decibels down, which is where the low frequency channel is also left out.
                to[0] = Clip(from[0] + Attenuated(from[2]) + Attenuated(from[4]));
                to[1] = Clip(from[1] + Attenuated(from[2]) + Attenuated(from[5]));
                continue;
            }

            // Stereo into 5.1 among others: the channels both layouts have, and silence for the
            // rest. Nothing here invents a centre channel out of a stereo mix.
            var shared = Math.Min(sourceChannels, _channels);
            for (var channel = 0; channel < shared; channel++) to[channel] = from[channel];
        }

        return _fitted.AsSpan(0, needed);
    }

    // Three decibels down, the coefficient a downmix folds a channel in at.
    private static int Attenuated(short sample) => (int)(sample * 0.7071);

    private static short Clip(int value) => (short)Math.Clamp(value, short.MinValue, short.MaxValue);

    // ------------------------------------------------------------------ sending

    private void Send(ReadOnlySpan<byte> opus)
    {
        var peer = _peer;
        if (peer is null || _socket is null) return;

        ReadOnlySpan<byte> payload = opus;
        if (_encrypt) payload = Encrypt(opus, _sequenceNumber);

        if (payload.Length > MaxPacketBytes)
        {
            // The bitrates in use put an Opus packet around a kilobyte; anything past the shard
            // buffers would corrupt the FEC block rather than merely be large.
            Log.Warn($"an audio packet of {payload.Length} bytes exceeds the {MaxPacketBytes} " +
                     "byte limit and was dropped");
            return;
        }

        // The data packet goes out first, then it becomes shard (seq % 4) of the current block.
        var packet = new byte[RtpHeaderBytes + payload.Length];
        packet[0] = 0x80;
        packet[1] = PayloadTypeAudio;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), _sequenceNumber);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), _timestamp);
        // Bytes 8..11 are the SSRC, and it is zero on this protocol.
        payload.CopyTo(packet.AsSpan(RtpHeaderBytes));

        var shardIndex = _sequenceNumber % DataShards;
        if (shardIndex == 0)
        {
            _blockBaseSequence = _sequenceNumber;
            _blockBaseTimestamp = _timestamp;
            _blockShardSize = payload.Length;
        }
        else if (payload.Length != _blockShardSize)
        {
            // Constant bitrate should make this impossible; the block would be unrecoverable at
            // the client, so it is worth a line rather than parity that protects nothing.
            Log.WarnOccasionally("audio shard size",
                $"an audio packet of {payload.Length} bytes differs from its FEC block's " +
                $"{_blockShardSize}; recovery for such blocks will not work");
        }

        // Zeroed first: a shorter packet must not leave the tail of an older one under the
        // parity, where it would be reconstructed as sound that was never played.
        Array.Clear(_shards[shardIndex]);
        payload.CopyTo(_shards[shardIndex]);

        try
        {
            _socket.SendTo(packet, peer);
        }
        catch (Exception error)
        {
            Log.WarnOccasionally("audio send", $"audio send failed: {error.Message}");
            return;
        }

        var lastSequence = _sequenceNumber;
        _sequenceNumber++;
        // The timestamp advances by the packet's duration in milliseconds, which is what the
        // reference server sends; the client uses it to line blocks up, not to clock playback.
        _timestamp += (uint)(_frameSizeSamples * 1000 / SampleRate);

        if (shardIndex != DataShards - 1) return;

        _fec.Encode(_shards, _blockShardSize);

        for (var x = 0; x < ParityShards; x++)
        {
            var parity = new byte[RtpHeaderBytes + FecHeaderBytes + _blockShardSize];
            parity[0] = 0x80;
            parity[1] = PayloadTypeFec;
            // The two sequence numbers after the block's last data packet. They collide with the
            // next block's, harmlessly: the client tells them apart by payload type.
            BinaryPrimitives.WriteUInt16BigEndian(parity.AsSpan(2), (ushort)(lastSequence + x + 1));
            // Timestamp and SSRC stay zero in a parity packet.

            parity[12] = (byte)x;
            parity[13] = PayloadTypeAudio;
            BinaryPrimitives.WriteUInt16BigEndian(parity.AsSpan(14), _blockBaseSequence);
            BinaryPrimitives.WriteUInt32BigEndian(parity.AsSpan(16), _blockBaseTimestamp);
            // Bytes 20..23 are the SSRC again, zero.

            _shards[DataShards + x].AsSpan(0, _blockShardSize)
                .CopyTo(parity.AsSpan(RtpHeaderBytes + FecHeaderBytes));

            try
            {
                _socket.SendTo(parity, peer);
            }
            catch (Exception error)
            {
                Log.WarnOccasionally("audio parity send",
                    $"audio parity send failed: {error.Message}");
                return;
            }
        }
    }

    // AES-CBC, as this protocol encrypts audio: the initialisation vector is the key id plus the
    // sequence number, big-endian in the first four bytes and zero after.
    private byte[] Encrypt(ReadOnlySpan<byte> plaintext, ushort sequenceNumber)
    {
        Span<byte> iv = stackalloc byte[16];
        iv.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(iv, _riKeyId + sequenceNumber);

        // One instance for the stream: the key never changes, and creating one per packet was two
        // hundred key schedules and two hundred CNG handles a second.
        _aes ??= CreateAes();

        return _aes.EncryptCbc(plaintext, iv);
    }

    public void Dispose()
    {
        _running = false;
        _socket?.Close();
        _socket = null;
        _aes?.Dispose();
        _aes = null;
        _capture?.Join(1000);
        _pings?.Join(500);
    }

    // ------------------------------------------------------------------ the pacing

    // How evenly the sound is leaving, under Debug only: packets in threes every fifteen
    // milliseconds is the fault, one every five the cure, and nothing else here can show it.
    private sealed class PacingReport
    {
        private static readonly TimeSpan Every = TimeSpan.FromSeconds(10);

        private readonly System.Diagnostics.Stopwatch _clock =
            System.Diagnostics.Stopwatch.StartNew();

        private TimeSpan _since;
        private TimeSpan _lastTurn;
        private int _turns;
        private int _packets;
        private double _gapSum;
        private double _gapMax;

        internal void Sent() => _packets++;

        internal void Turn()
        {
            var now = _clock.Elapsed;

            if (_turns > 0)
            {
                var gap = (now - _lastTurn).TotalMilliseconds;
                _gapSum += gap;
                if (gap > _gapMax) _gapMax = gap;
            }

            _lastTurn = now;
            _turns++;

            var elapsed = now - _since;
            if (elapsed < Every) return;

            var seconds = Math.Max(1.0, elapsed.TotalSeconds);
            var mean = _turns > 1 ? _gapSum / (_turns - 1) : 0;

            Log.Info($"audio: the capture loop woke {_turns / seconds:0} times a second and sent " +
                     $"{_packets / seconds:0} packet(s) a second; " +
                     $"{mean.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} ms " +
                     $"between turns on average, " +
                     $"{_gapMax.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} ms " +
                     "at the worst");

            _since = now;
            _turns = 0;
            _packets = 0;
            _gapSum = 0;
            _gapMax = 0;
        }
    }

    // ------------------------------------------------------------------ the endpoint

    // One open loopback capture. A class of its own so that losing the device is a matter of
    // disposing this and opening another, rather than unwinding the encode loop.
    private sealed unsafe class LoopbackDevice : IDisposable
    {
        // A hundred milliseconds of buffer, in 100 ns units. Latency does not depend on it, but a
        // small buffer drops samples when Windows schedules this thread late.
        private const long BufferDuration100Ns = 100 * 10_000;

        // How often the default endpoint is re-checked while following it.
        private const int DefaultDeviceCheckMs = 2000;

        private void* _enumerator;
        private void* _device;
        private void* _client;
        private void* _capture;

        private short[] _samples = Array.Empty<short>();
        private int _channels;
        private bool _isFloat;

        private readonly bool _followsDefault;
        private readonly string? _deviceId;
        private int _lastDefaultCheck;

        internal bool IsHealthy { get; private set; } = true;

        // How many channels this endpoint mixes into, which is the ceiling on what can be
        // captured from it: Windows mixes down to this long before loopback sees the sound.
        internal int MixChannels { get; private set; }

        // The endpoint's name, for the lines that have to say which device is meant.
        internal string Name { get; private set; } = "unnamed device";

        // How many channels the open capture hands back: what was asked for when Windows accepted
        // the conversion, the endpoint's own count when it did not.
        internal int Channels => _channels;

        private LoopbackDevice(bool followsDefault, string? deviceId)
        {
            _followsDefault = followsDefault;
            _deviceId = deviceId;
            _lastDefaultCheck = Environment.TickCount;
        }

        // Opens the endpoint named by [Audio] Device, or the default one. Returns null with the
        // reason logged: a missing sound device is a stream without sound, not one that ends.
        internal static LoopbackDevice? Open(string wanted, int channels, uint channelMask)
        {
            void* enumerator = null;
            void* device = null;

            try
            {
                enumerator = Wasapi.CreateDeviceEnumerator();
                var followsDefault = string.Equals(wanted, "auto", StringComparison.OrdinalIgnoreCase);

                device = followsDefault
                    ? AudioEndpoints.Default(enumerator)
                    : AudioEndpoints.Find(enumerator, wanted);

                if (device is null)
                {
                    Log.Warn(followsDefault
                        ? "there is no default playback device; the stream will have no sound"
                        : $"no playback device matches [Audio] Device = \"{wanted}\"; " +
                          "the stream will have no sound");
                    return null;
                }

                var name = Wasapi.GetDeviceName(device) ?? "unnamed device";
                var opened = new LoopbackDevice(followsDefault, Wasapi.GetDeviceId(device))
                {
                    _enumerator = enumerator,
                    _device = device,
                    Name = name,
                };

                // Ownership moves in before anything can fail. Nulling the locals afterwards
                // instead had the failure paths release each interface twice, Dispose and finally.
                enumerator = null;
                device = null;

                // Read before the client is initialised: what Activate hands back is what Windows
                // converted to, which says nothing about what the endpoint really has.
                opened.MixChannels = AudioEndpoints.MixChannelsOf(opened._device);

                if (!opened.Activate(channels, channelMask))
                {
                    opened.Dispose();
                    return null;
                }

                Log.Info($"capturing the sound of \"{name}\"" +
                         (followsDefault ? " (the default device)" : string.Empty));

                return opened;
            }
            catch (Exception error)
            {
                Log.Warn($"the playback device could not be opened: {error.Message}");
                return null;
            }
            finally
            {
                // Only reached with these still set when the object was never built.
                Com.Release(device);
                Com.Release(enumerator);
            }
        }

        // Opens the audio client for loopback, asking for 48 kHz 16-bit in the negotiated channel
        // count with conversion allowed; where it is refused, the mix format is taken as it comes.
        private bool Activate(int channels, uint channelMask)
        {
            if (Wasapi.Activate(_device, Wasapi.IID_IAudioClient, out _client) < 0 || _client is null)
            {
                Log.Warn("the playback device would not open an audio client");
                return false;
            }

            var wanted = new WaveFormatExtensible
            {
                FormatTag = Wasapi.WAVE_FORMAT_EXTENSIBLE,
                Channels = (ushort)channels,
                SamplesPerSecond = SampleRate,
                BitsPerSample = 16,
                BlockAlign = (ushort)(channels * 2),
                AverageBytesPerSecond = (uint)(SampleRate * channels * 2),
                ExtraSize = 22,
                ValidBitsPerSample = 16,
                ChannelMask = channelMask != 0 ? channelMask : channels switch
                {
                    8 => Wasapi.KSAUDIO_SPEAKER_7POINT1_SURROUND,
                    6 => Wasapi.KSAUDIO_SPEAKER_5POINT1,
                    _ => 0x3u,
                },
                SubFormat = Wasapi.KSDATAFORMAT_SUBTYPE_PCM,
            };

            var flags = Wasapi.AUDCLNT_STREAMFLAGS_LOOPBACK |
                        Wasapi.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM |
                        Wasapi.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;

            var hr = Wasapi.Initialize(_client, Wasapi.AUDCLNT_SHAREMODE_SHARED, flags,
                BufferDuration100Ns, &wanted);

            if (hr >= 0)
            {
                _channels = channels;
                _isFloat = false;
            }
            else
            {
                // Whether a loopback client accepts the automatic conversion has varied between
                // Windows builds, so the mix format is the fallback rather than the failure.
                Log.Info($"the device refused 48 kHz {channels}-channel capture " +
                         $"(0x{hr:X8}); using its own mix format instead");

                if (!ActivateWithMixFormat()) return false;
            }

            if (Wasapi.GetService(_client, Wasapi.IID_IAudioCaptureClient, out _capture) < 0 ||
                _capture is null)
            {
                Log.Warn("the audio client would not give a capture service");
                return false;
            }

            if (Wasapi.Start(_client) < 0)
            {
                Log.Warn("the loopback capture would not start");
                return false;
            }

            return true;
        }

        private bool ActivateWithMixFormat()
        {
            // Activating again: an audio client that refused a format cannot be initialised a
            // second time, so a fresh one is taken from the same device.
            Com.ReleaseAndClear(ref _client);

            if (Wasapi.Activate(_device, Wasapi.IID_IAudioClient, out _client) < 0 || _client is null)
                return false;

            if (Wasapi.GetMixFormat(_client, out var mix) < 0 || mix is null)
            {
                Log.Warn("the audio client would not say what format it mixes in");
                return false;
            }

            try
            {
                if (mix->SamplesPerSecond != SampleRate)
                {
                    // There is no resampler here: a device not at 48 kHz is refused with a
                    // line saying so rather than streamed at the wrong rate.
                    Log.Warn(
                        $"The playback device mixes at {mix->SamplesPerSecond} Hz, and this server\n" +
                        "sends 48000 Hz. The stream will have no sound.\n" +
                        "What to do: set the device to 48000 Hz in Windows' sound settings\n" +
                        "(Sound control panel, the device's properties, Advanced).");
                    return false;
                }

                _channels = mix->Channels;
                _isFloat = mix->BitsPerSample == 32 &&
                           (mix->FormatTag != Wasapi.WAVE_FORMAT_EXTENSIBLE ||
                            mix->SubFormat == Wasapi.KSDATAFORMAT_SUBTYPE_IEEE_FLOAT);

                if (!_isFloat && mix->BitsPerSample != 16)
                {
                    Log.Warn($"the playback device mixes in {mix->BitsPerSample}-bit samples, " +
                             "which this server cannot read; the stream will have no sound");
                    return false;
                }

                var hr = Wasapi.Initialize(_client, Wasapi.AUDCLNT_SHAREMODE_SHARED,
                    Wasapi.AUDCLNT_STREAMFLAGS_LOOPBACK, BufferDuration100Ns, mix);

                if (hr < 0)
                {
                    Log.Warn($"loopback capture could not be initialised (0x{hr:X8}); " +
                             "the stream will have no sound");
                    return false;
                }

                return true;
            }
            finally
            {
                Wasapi.CoTaskMemFree(mix);
            }
        }

        // Reads whatever the endpoint has ready, as interleaved 16-bit samples. False when there
        // is nothing yet, which is most calls; the caller waits a couple of milliseconds.
        internal bool Read(out ReadOnlySpan<short> samples)
        {
            samples = default;

            if (_followsDefault && Environment.TickCount - _lastDefaultCheck > DefaultDeviceCheckMs)
            {
                _lastDefaultCheck = Environment.TickCount;
                if (DefaultDeviceChanged())
                {
                    Log.Info("the default playback device changed; the capture follows it");
                    IsHealthy = false;
                    return false;
                }
            }

            if (Wasapi.GetNextPacketSize(_capture, out var available) < 0)
            {
                IsHealthy = false;
                return false;
            }

            if (available == 0) return false;

            var hr = Wasapi.GetBuffer(_capture, out var data, out var frames, out var flags);
            if (hr < 0)
            {
                // The device was invalidated — unplugged, or its format changed under us.
                IsHealthy = false;
                return false;
            }

            var total = (int)frames * _channels;
            if (_samples.Length < total) _samples = new short[total];

            if ((flags & Wasapi.AUDCLNT_BUFFERFLAGS_SILENT) != 0)
            {
                // The buffer is silence and its contents are undefined. Silence is still sent: a
                // stream that stops in a quiet moment looks to the client like one that broke.
                Array.Clear(_samples, 0, total);
            }
            else if (_isFloat)
            {
                var source = new ReadOnlySpan<float>(data, total);
                for (var i = 0; i < total; i++)
                {
                    var value = source[i];
                    _samples[i] = value >= 1f ? short.MaxValue
                        : value <= -1f ? short.MinValue
                        : (short)(value * 32767f);
                }
            }
            else
            {
                new ReadOnlySpan<short>(data, total).CopyTo(_samples);
            }

            Wasapi.ReleaseBuffer(_capture, frames);

            samples = _samples.AsSpan(0, total);
            return true;
        }

        private bool DefaultDeviceChanged()
        {
            var current = AudioEndpoints.Default(_enumerator);
            if (current is null) return false;

            try
            {
                return !string.Equals(Wasapi.GetDeviceId(current), _deviceId, StringComparison.Ordinal);
            }
            finally
            {
                Com.Release(current);
            }
        }

        public void Dispose()
        {
            if (_client is not null) Wasapi.Stop(_client);

            Com.ReleaseAndClear(ref _capture);
            Com.ReleaseAndClear(ref _client);
            Com.ReleaseAndClear(ref _device);
            Com.ReleaseAndClear(ref _enumerator);
        }
    }
}
