//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using RemoteGameHub.App;
using RemoteGameHub.Media;

namespace RemoteGameHub.Protocol;

// What the client asked for in its ANNOUNCE. The codec, the frame rate and the bitrate are its
// own choice — there is no setting that caps them — held only by the protocol's bounds.
internal sealed record StreamNegotiation(
    IPAddress ClientAddress,
    int Width,
    int Height,
    int Fps,
    int BitrateKbps,
    VideoCodec Codec,
    // Payload bytes per video packet, before the protocol's own headers.
    int PacketSize,
    // Reed-Solomon parity, as a percentage of the data packets in each frame.
    int FecPercentage,
    int MinRequiredFecPackets,
    int AudioChannels,
    int AudioChannelMask,
    int AudioPacketDurationMs,
    bool AudioHighQuality,
    // Whether the client would like high dynamic range. What the host does about it is decided
    // where the screen is configured, not here.
    bool HdrRequested,
    // Colour at full resolution instead of quartered: sharper text and smoother gradients for
    // several times the bits. The client's choice, from what /serverinfo offered.
    bool Yuv444,
    // The SS_ENC_* bits the client turned on. CONTROL_V2 (0x01) decides the control stream's
    // AES-GCM IV shape; AUDIO (0x04) would oblige the audio stream to encrypt.
    int EncryptionFlags)
{
    // The Opus bitrate this server encodes at; nothing on the wire carries the number. A coupled
    // stereo pair gets 192 kbit/s, a mono stream half that, high quality two and a half times.
    internal int AudioBitrateKbps => AudioBitrateFor(AudioChannels, AudioHighQuality);

    internal static int AudioBitrateFor(int channels, bool highQuality) => channels switch
    {
        // 5.1 as this server maps it: two coupled pairs and two mono streams.
        6 => highQuality ? 1440 : 576,
        // 7.1: three coupled pairs and two mono streams.
        8 => highQuality ? 1920 : 768,
        _ => highQuality ? 480 : 192,
    };
}

// The session negotiation: OPTIONS, DESCRIBE, one SETUP per stream, ANNOUNCE carrying the client's
// SDP, PLAY. One TCP connection per request, read to the close; 7.1.431 and newer never use ENet.
internal sealed class RtspServer : IAsyncDisposable
{
    // One request with headers and payload fits far below this. Roomy, because the SDP grows a
    // line every time a client learns a new trick.
    private const int MaxRequestBytes = 8192;

    // An arbitrary token in the shape clients parse: hex-looking, with a timeout tail they discard
    // at the semicolon. Nothing validates it; there is only ever one session being negotiated.
    private const string SessionId = "FEEDC0DECAFE;timeout = 90";

    private readonly AppConfig _config;
    private readonly EncoderCapabilities _encoder;
    private readonly CancellationTokenSource _stopping = new();

    private TcpListener? _listener;
    private Task? _accepting;

    // Raised on the connection's thread when an ANNOUNCE has been accepted.
    internal event Action<StreamNegotiation>? Negotiated;

    internal RtspServer(AppConfig config, EncoderCapabilities encoder)
    {
        _config = config;
        _encoder = encoder;
    }

    internal void Start()
    {
        // The same bind rules as the HTTP listeners: BindAddress restricts, "any" hears both
        // address families on one socket.
        if (!string.Equals(_config.BindAddress, "any", StringComparison.OrdinalIgnoreCase))
        {
            _listener = new TcpListener(IPAddress.Parse(_config.BindAddress), _config.RtspPort);
        }
        else
        {
            try
            {
                _listener = new TcpListener(IPAddress.IPv6Any, _config.RtspPort);
                _listener.Server.DualMode = true;
            }
            catch (Exception)
            {
                _listener = new TcpListener(IPAddress.Any, _config.RtspPort);
            }
        }

        _listener.Start();
        _accepting = Task.Run(AcceptLoop);

        Log.Info($"listening on port {_config.RtspPort} (rtsp)");
    }

    private async Task AcceptLoop()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(_stopping.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception error)
            {
                if (_stopping.IsCancellationRequested) return;
                Log.Warn($"the RTSP listener stopped accepting: {error.Message}");
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        var peer = Peer.Plain((client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None);

        try
        {
            using (client)
            {
                client.NoDelay = true;
                var stream = client.GetStream();

                var request = await ReadRequestAsync(stream);
                if (request is null)
                {
                    // Said rather than dropped in silence: an unreadable request and one the
                    // client never sent look the same in the log and want opposite fixes.
                    Log.Info($"{peer} rtsp: a connection carried nothing this server could read");
                    return;
                }

                Log.Info($"{peer} rtsp {request.Command} {request.Target}");

                var response = request.Command switch
                {
                    "OPTIONS" => Ok(request),
                    "DESCRIBE" => Describe(request),
                    "SETUP" => Setup(request),
                    "ANNOUNCE" => Announce(request, peer),
                    "PLAY" => Ok(request),
                    _ => Respond(request, 404, "NOT FOUND"),
                };

                await stream.WriteAsync(response, _stopping.Token);
                await stream.FlushAsync(_stopping.Token);

                // The close is the framing, and only the sending half of it: closing both while
                // anything is unread sends a reset, which throws away the unacknowledged answer.
                client.Client.Shutdown(SocketShutdown.Send);
                await DrainAsync(stream);
            }
        }
        catch (Exception error)
        {
            Log.Info($"{peer}: the RTSP exchange ended early ({error.GetType().Name}: {error.Message})");
        }
    }

    // Reads whatever the client still had in flight, so that disposing the socket is a finish and
    // not a reset. Bounded: a client that never closes must not hold a thread.
    private static async Task DrainAsync(NetworkStream stream)
    {
        var buffer = new byte[512];

        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        try
        {
            while (await stream.ReadAsync(buffer, patience.Token) > 0)
            {
            }
        }
        catch (OperationCanceledException)
        {
            // Two seconds and the client had still not closed. Said, because anything left unread
            // when this socket is disposed brings the reset back that this method exists to avoid.
            Log.Info("an RTSP connection did not close within two seconds and is being dropped");
        }
        catch (Exception)
        {
            // The client closed first, which is the ordinary end of an exchange.
        }
    }

    // ------------------------------------------------------------------ reading

    internal sealed class RtspRequest
    {
        internal string Command = string.Empty;
        internal string Target = string.Empty;
        internal string CSeq = "0";
        internal readonly Dictionary<string, string> Options = new(StringComparer.OrdinalIgnoreCase);
        internal string Payload = string.Empty;
    }

    // Reads one request: headers to the blank line, then as many payload bytes as Content-length
    // announced — the client keeps its half open, so there is no reading to idle.
    private async Task<RtspRequest?> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[MaxRequestBytes];
        var used = 0;
        int headerEnd;

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(used, buffer.Length - used), _stopping.Token);
            if (read == 0) return null;
            used += read;

            headerEnd = IndexOfBlankLine(buffer, used);
            if (headerEnd >= 0) break;
            if (used == buffer.Length) return null;
        }

        var head = Encoding.UTF8.GetString(buffer, 0, headerEnd);
        var request = ParseHead(head);
        if (request is null) return null;

        var contentLength = 0;
        if (request.Options.TryGetValue("Content-length", out var lengthText))
            _ = int.TryParse(lengthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLength);

        if (contentLength > 0)
        {
            var payloadStart = headerEnd + 4;
            if (payloadStart + contentLength > buffer.Length) return null;

            while (used < payloadStart + contentLength)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(used, buffer.Length - used), _stopping.Token);
                if (read == 0) break;
                used += read;
            }

            request.Payload = Encoding.UTF8.GetString(
                buffer, payloadStart, Math.Min(contentLength, used - payloadStart));
        }

        return request;
    }

    internal static int IndexOfBlankLine(byte[] buffer, int used)
    {
        for (var i = 0; i + 3 < used; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' &&
                buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                return i;
        }
        return -1;
    }

    internal static RtspRequest? ParseHead(string head)
    {
        var lines = head.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length < 3 || parts[2] != "RTSP/1.0") return null;

        var request = new RtspRequest { Command = parts[0], Target = parts[1] };

        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            request.Options[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        if (request.Options.TryGetValue("CSeq", out var seq)) request.CSeq = seq;
        return request;
    }

    // ------------------------------------------------------------------ answering

    // A response as moonlight-common-c's parser expects it: the status line, CSeq first among the
    // options, a blank line, then the payload — with no Content-length, which it does not read.
    private static byte[] Respond(RtspRequest request, int status, string statusText,
                                  IReadOnlyList<(string Name, string Value)>? options = null,
                                  string payload = "")
    {
        var text = new StringBuilder();
        text.Append("RTSP/1.0 ").Append(status).Append(' ').Append(statusText).Append("\r\n");
        text.Append("CSeq: ").Append(request.CSeq).Append("\r\n");

        if (options is not null)
        {
            foreach (var (name, value) in options)
                text.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        text.Append("\r\n");
        text.Append(payload);

        return Encoding.UTF8.GetBytes(text.ToString());
    }

    private static byte[] Ok(RtspRequest request) => Respond(request, 200, "OK");

    // What this host offers, as SDP fragments: sprop-parameter-sets=AAAAAU, the base64 prefix of an
    // HEVC VPS, is the only HEVC signal; surround-params per channel count, normal then high.
    private byte[] Describe(RtspRequest request)
    {
        var sdp = new StringBuilder();

        if (_encoder.Hevc) sdp.Append("sprop-parameter-sets=AAAAAU\n");

        // SS_ENC_CONTROL_V2 (bit 0x01): a client that sees it uses the 12-byte AES-GCM IV .NET can
        // decrypt, not the old 16-byte NVIDIA one. Video and audio encryption are not offered.
        sdp.Append("a=x-ss-general.encryptionSupported:1\n");
        sdp.Append("a=x-ss-general.encryptionRequested:1\n");

        // Sunshine's digit strings (audio.cpp): channels, streams, coupled streams, then the mapping.
        // The normal-quality surround mappings are rotated on purpose: clients undo GFE's old bug.
        sdp.Append("a=fmtp:97 surround-params=21101\n");
        sdp.Append("a=fmtp:97 surround-params=21101\n");
        sdp.Append("a=fmtp:97 surround-params=642012453\n");
        sdp.Append("a=fmtp:97 surround-params=660012345\n");
        sdp.Append("a=fmtp:97 surround-params=85301245367\n");
        sdp.Append("a=fmtp:97 surround-params=88001234567\n");

        return Respond(request, 200, "OK", payload: sdp.ToString());
    }

    // The port for the stream named in the target — streamid=audio/0/0, video/0/0, control/13/0 —
    // and the session id. The client believes SETUP, so the ports must match /serverinfo's.
    private byte[] Setup(RtspRequest request)
    {
        var target = request.Target;
        var equals = target.IndexOf('=');
        if (equals < 0) return Respond(request, 404, "NOT FOUND");

        var name = target[(equals + 1)..];
        var slash = name.IndexOf('/');
        if (slash >= 0) name = name[..slash];

        int port;
        switch (name)
        {
            case "audio": port = _config.AudioPort; break;
            case "video": port = _config.VideoPort; break;
            case "control": port = _config.ControlPort; break;
            default: return Respond(request, 404, "NOT FOUND");
        }

        return Respond(request, 200, "OK", new[]
        {
            ("Session", SessionId),
            ("Transport", $"server_port={port.ToString(CultureInfo.InvariantCulture)}"),
        });
    }

    // The ANNOUNCE payload is SDP whose a= attributes carry the client's stream configuration.
    // The names and the defaults are the client's side; what is decided from them is this server's.
    private byte[] Announce(RtspRequest request, IPAddress peer)
    {
        var attributes = ParseSdpAttributes(request.Payload);

        // Every attribute the client sent, once per stream under Debug: the only full statement of
        // what a client is prepared to be told, of which this server reads a handful.
        Log.Info($"the client's stream request, {attributes.Count} attributes:\n" +
                 string.Join("\n", attributes
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                     .Select(pair => $"    {pair.Key} = {pair.Value}")));

        int Value(string name, int fallback = int.MinValue)
        {
            if (attributes.TryGetValue(name, out var text) &&
                int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;

            if (fallback == int.MinValue)
                throw new FormatException($"the ANNOUNCE is missing {name}");

            return fallback;
        }

        try
        {
            var width = Value("x-nv-video[0].clientViewportWd");
            var height = Value("x-nv-video[0].clientViewportHt");
            var fps = Value("x-nv-video[0].maxFPS");
            var packetSize = Value("x-nv-video[0].packetSize");
            var bitrateKbps = Value("x-nv-vqos[0].bw.maximumBitrateKbps");
            var videoFormat = Value("x-nv-vqos[0].bitStreamFormat", 0);
            var fecPercentage = Value("x-nv-vqos[0].fec.repairPercent", 20);
            var minRequiredFec = Value("x-nv-vqos[0].fec.minRequiredFecPackets", 0);
            var audioChannels = Value("x-nv-audio.surround.numChannels");
            var audioMask = Value("x-nv-audio.surround.channelMask");
            var audioDuration = Value("x-nv-aqos.packetDuration", 5);
            var audioHighQuality = Value("x-nv-audio.surround.AudioQuality", 0) != 0;
            var configuredBitrateKbps = Value("x-ml-video.configuredBitrateKbps", 0);
            var encryptionFlags = Value("x-ss-general.encryptionEnabled", 0);

            // Legacy clients ask for audio encryption through a bit in the NVIDIA feature flags
            // rather than the Sunshine attribute; recorded the same way Sunshine records it.
            if ((Value("x-nv-general.featureFlags", 0x87) & 0x20) != 0)
                encryptionFlags |= 0x04;

            // The control stream is AES-GCM, and only the 12-byte-IV shape SS_ENC_CONTROL_V2
            // selects. A client too old for it is refused rather than left in silence.
            if ((encryptionFlags & 0x01) == 0)
            {
                Log.Warn("the client did not enable control-stream encryption v2; it is too old " +
                         "for this server, and the negotiation is refused");
                return Respond(request, 400, "BAD REQUEST");
            }

            // Stereo, 5.1 and 7.1 are the three layouts this server encodes. Anything else is
            // refused, because there is no Opus configuration here to send it in.
            if (audioChannels != 2 && audioChannels != 6 && audioChannels != 8)
            {
                Log.Warn(
                    $"The client asked for {audioChannels} audio channels. This server sends\n" +
                    "stereo, 5.1 or 7.1 and nothing else, so the negotiation is refused.\n" +
                    "What to do: set the client's audio to stereo, 5.1 or 7.1.");
                return Respond(request, 400, "BAD REQUEST");
            }

            // For stereo the quality wish rides in the Host header of all places: the client
            // sends 0.0.0.0 when it wants the host to hold audio to the low bitrate.
            if (audioChannels == 2 && request.Options.TryGetValue("Host", out var host))
                audioHighQuality = !host.Contains("0.0.0.0");

            // The client's budget, less parity — B/(1 + fec/100), since parity is charged on the
            // video — less 44 header bytes per packet and the audio, floored at half the budget.
            if (configuredBitrateKbps > 0)
            {
                var afterFec = (int)((long)configuredBitrateKbps * 100 / (100 + fecPercentage));

                var overheadShare = 100 - (44 * 100 / Math.Max(512, packetSize + 44));
                var afterOverhead = (int)((long)afterFec * overheadShare / 100);

                var video = afterOverhead - StreamNegotiation.AudioBitrateFor(audioChannels, audioHighQuality);

                bitrateKbps = Math.Max(video, configuredBitrateKbps / 2);
            }

            var codec = videoFormat switch
            {
                AppParameters.Protocol.BitStreamH264 => VideoCodec.H264,
                AppParameters.Protocol.BitStreamHevc => VideoCodec.Hevc,
                AppParameters.Protocol.BitStreamAv1 => VideoCodec.Av1,
                _ => VideoCodec.Auto,   // Anything beyond: not offered, so never a valid ask
            };

            // Colour at full resolution, asked for only when /serverinfo offered it. Refused rather
            // than quietly downgraded: told 4:4:4 and shown 4:2:0 is the opposite of what it asked.
            var yuv444 = Value("x-ss-video[0].chromaSamplingType", 0) ==
                         AppParameters.Protocol.ChromaSampling444;

            if (yuv444 && !(codec == VideoCodec.H264 ? _encoder.H264Yuv444 : _encoder.HevcYuv444))
            {
                Log.Warn($"the client asked for 4:4:4 colour in {VideoEncoders.Name(codec)}, " +
                         "which this card cannot encode; the negotiation is refused");
                return Respond(request, 400, "BAD REQUEST");
            }

            if (codec == VideoCodec.Auto ||
                (codec == VideoCodec.Hevc && !_encoder.Hevc) ||
                (codec == VideoCodec.Av1 && !_encoder.Av1) ||
                (codec == VideoCodec.H264 && !_encoder.H264))
            {
                Log.Warn($"the client asked for bitStreamFormat {videoFormat}, which this " +
                         "server did not offer; the negotiation is refused");
                return Respond(request, 400, "BAD REQUEST");
            }

            // What the client asked for, held only by the protocol's own bounds. Neither the rate
            // nor the bitrate has a setting: it knows what its screen and its network can do.
            var negotiation = new StreamNegotiation(
                ClientAddress: peer,
                Width: width,
                Height: height,
                Fps: Math.Clamp(fps, AppParameters.Limits.MinFps, AppParameters.Limits.MaxFps),
                BitrateKbps: Math.Clamp(bitrateKbps, AppParameters.Limits.MinBitrateKbps,
                                        AppParameters.Limits.MaxBitrateKbps),
                Codec: codec,
                PacketSize: packetSize,
                FecPercentage: fecPercentage,
                MinRequiredFecPackets: minRequiredFec,
                AudioChannels: audioChannels,
                AudioChannelMask: audioMask,
                AudioPacketDurationMs: audioDuration,
                AudioHighQuality: audioHighQuality,
                HdrRequested: Value("x-nv-video[0].dynamicRangeMode", 0) != 0,
                Yuv444: yuv444,
                EncryptionFlags: encryptionFlags);

            Log.Event($"negotiated with {peer}: {negotiation.Width}x{negotiation.Height} at " +
                     $"{negotiation.Fps} fps, {negotiation.BitrateKbps} kbit/s " +
                     $"{VideoEncoders.Name(negotiation.Codec)}, " +
                     $"packet size {negotiation.PacketSize}, FEC {negotiation.FecPercentage}%, " +
                     $"audio {negotiation.AudioChannels} channel(s) " +
                     $"{(negotiation.AudioHighQuality ? "high" : "normal")} quality");

            Negotiated?.Invoke(negotiation);

            return Ok(request);
        }
        catch (FormatException error)
        {
            Log.Warn($"the ANNOUNCE could not be read: {error.Message}");
            return Respond(request, 400, "BAD REQUEST");
        }
    }

    // a=name:value lines, plus a trailing space stripped from the value — the client's
    // SDP generator leaves one behind some values, and Sunshine strips it the same way.
    internal static Dictionary<string, string> ParseSdpAttributes(string payload)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var raw in payload.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith("a=", StringComparison.Ordinal)) continue;

            var colon = line.IndexOf(':');
            if (colon < 0) continue;

            var value = line[(colon + 1)..];
            if (value.EndsWith(' ')) value = value[..^1];

            attributes[line[2..colon]] = value;
        }

        return attributes;
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener?.Stop();

        if (_accepting is not null)
        {
            try
            {
                await _accepting.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // Shutting down is not a place to wait indefinitely for a socket to notice.
            }
        }

        _stopping.Dispose();
    }
}
