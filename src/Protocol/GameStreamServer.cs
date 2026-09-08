//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml;
using RemoteGameHub.Media;
using RemoteGameHub.App;
using RemoteGameHub.Library;
using RemoteGameHub.Session;

namespace RemoteGameHub.Protocol;

// The endpoints a Moonlight client talks to before a stream exists. Two listeners on two ports
// answering the same paths: a client pairs over plain HTTP, and everything after goes over TLS.
internal sealed class GameStreamServer : IAsyncDisposable
{
    private readonly AppConfig _config;
    private readonly HostIdentity _identity;
    private readonly DisplayOutput _output;
    private readonly ClientStore _clients;
    private readonly PairingManager _pairing;
    private readonly GameLibrary _games;
    private readonly EncoderCapabilities _encoder;
    private readonly SessionManager _sessions;
    private readonly CancellationTokenSource _stopping = new();

    private readonly List<TcpListener> _listeners = new();
    private readonly List<Task> _accepting = new();

    internal GameStreamServer(AppConfig config, HostIdentity identity, DisplayOutput output,
                              ClientStore clients, PairingManager pairing, GameLibrary games,
                              EncoderCapabilities encoder, SessionManager sessions)
    {
        _config = config;
        _identity = identity;
        _output = output;
        _clients = clients;
        _pairing = pairing;
        _games = games;
        _encoder = encoder;
        _sessions = sessions;
    }


    internal void Start()
    {
        Listen(_config.HttpPort, secure: false);
        Listen(_config.HttpsPort, secure: true);
    }

    private void Listen(int port, bool secure)
    {
        var listener = CreateListener(port);
        listener.Start();

        _listeners.Add(listener);
        _accepting.Add(Task.Run(() => AcceptLoop(listener, secure, port)));

        Log.Info($"listening on port {port} ({(secure ? "https" : "http")})");
    }

    // Binds so that both IPv4 and IPv6 clients are heard on one socket: a phone often finds the
    // host over IPv6 while a laptop uses IPv4, and one listener would miss half the house.
    private TcpListener CreateListener(int port)
    {
        if (!string.Equals(_config.BindAddress, "any", StringComparison.OrdinalIgnoreCase))
        {
            if (!IPAddress.TryParse(_config.BindAddress, out var address))
            {
                throw new FormatException(
                    $"[Network] BindAddress = \"{_config.BindAddress}\" is not an address. " +
                    "Write an address of this machine, or \"any\".");
            }

            return new TcpListener(address, port);
        }

        try
        {
            var listener = new TcpListener(IPAddress.IPv6Any, port);
            listener.Server.DualMode = true;
            return listener;
        }
        catch (Exception error)
        {
            // A machine with IPv6 switched off cannot bind that socket at all. IPv4 alone is a
            // good answer there, and worth a line so "the phone cannot see it" has a start.
            Log.Info($"IPv6 is unavailable ({error.Message}); listening on IPv4 only");
            return new TcpListener(IPAddress.Any, port);
        }
    }

    private async Task AcceptLoop(TcpListener listener, bool secure, int port)
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(_stopping.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception error)
            {
                if (_stopping.IsCancellationRequested) return;
                Log.Warn($"the listener on port {port} stopped accepting: {error.Message}");
                return;
            }

            // Each connection on its own, so that one client stuck mid-handshake cannot hold up
            // the next. Nothing here is shared but the configuration, which is read-only.
            _ = Task.Run(() => ServeAsync(client, secure));
        }
    }

    private async Task ServeAsync(TcpClient client, bool secure)
    {
        var peer = Peer.Describe((client.Client.RemoteEndPoint as IPEndPoint)?.Address);

        try
        {
            using (client)
            {
                await using var stream = await OpenAsync(client, secure);
                if (stream is null) return;

                var request = await HttpRequest.ReadAsync(stream, _stopping.Token);
                if (request is null) return;

                // Which client this is, when it can be known. Over TLS the certificate says so;
                // over plain HTTP the identifier a client sends is a constant compiled into it.
                var certificate = secure ? (stream as SslStream)?.RemoteCertificate : null;
                using var presented = certificate is null ? null : new X509Certificate2(certificate);
                var known = presented is null ? null : _clients.Find(presented);

                // A client arriving over TLS with a certificate this server has not seen is
                // answered as unpaired: it either finished the exchange or was forgotten on purpose.
                if (presented is not null && known is null)
                {
                    Log.Info($"{peer} arrived over TLS with a certificate this server does not " +
                             "know; it is told it is not paired");
                }

                if (known is not null) _clients.Touch(known.Fingerprint);

                Log.Info($"{peer} {(secure ? "https" : "http")} {request.Method} " +
                         $"{request.Path}{request.QueryForLog}" +
                         (known is null ? string.Empty : $"  [{known.Name}]"));


                // The one endpoint that answers with an image rather than a document, so it does
                // not go through the XML route below.
                if (request.Path == "/appasset")
                {
                    await ServeBoxArtAsync(stream, request);
                    return;
                }

                // A pairing answer is held while a person types four digits and a name; the
                // client's first request has no read timeout. The socket is watched meanwhile.
                var pairing = request.Path == "/pair";

                using var gone = new CancellationTokenSource();
                using var patience = pairing ? MeasurePatience(client, gone) : null;
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token, gone.Token);

                var answer = await RouteAsync(request, known, client.Client.LocalEndPoint,
                                              cancel.Token);
                if (answer is null)
                {
                    await HttpResponse.WriteNotFoundAsync(stream, _stopping.Token);
                    return;
                }

                // A client can reject a pairing answer and say nothing about why, so what was sent
                // is written out in full. Only for pairing: no other answer is worth this much log.
                if (pairing)
                {
                    // Whether the client is still there to hear it: writing to a socket whose peer
                    // has gone succeeds quietly, but a readable socket with nothing on it is closed.
                    var closed = client.Client.Poll(0, SelectMode.SelectRead) && client.Available == 0;

                    Log.Info($"answering the pairing request{(closed ? " — but the client has already " +
                        "closed the connection, so it will never see this" : string.Empty)}:\n{answer}");
                }


                await HttpResponse.WriteXmlAsync(stream, answer, _stopping.Token);
            }
        }
        catch (Exception error)
        {
            // A client that walks away mid-request is ordinary and constant. It is logged at the
            // level that is off by default, so that it does not bury what matters.
            Log.Info($"{peer}: the request ended early ({error.GetType().Name}: {error.Message})");
        }
    }


    // Watches a connection while its answer is being prepared, says how long the client waited
    // before giving up, and cancels gone. Disposing what it returns stops the watch.
    private static IDisposable MeasurePatience(TcpClient client, CancellationTokenSource gone)
    {
        var stop = new CancellationTokenSource();
        var since = Stopwatch.StartNew();

        _ = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await Task.Delay(200, stop.Token);

                    // Readable with nothing to read is a socket the other end has closed.
                    if (!client.Client.Poll(0, SelectMode.SelectRead) || client.Available != 0)
                        continue;

                    Log.Warn(
                        $"the client stopped waiting for its pairing answer after " +
                        $"{since.ElapsedMilliseconds} ms. A client that leaves within seconds is\n" +
                        "one whose pairing was cancelled on its own screen; a stock Moonlight\n" +
                        "waits for its first answer for as long as it takes.");

                    gone.Cancel();
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                // The answer was ready before the client gave up, which is the ordinary case.
            }
            catch (ObjectDisposedException)
            {
                // The connection was closed under the watch; there is nothing left to say.
            }
        }, stop.Token);

        return new StopOnDispose(stop);
    }

    // Cancels a token source when disposed, and only then disposes it: disposing a source does not
    // cancel it, and a watch left running against a disposed token throws on its own thread.
    private sealed class StopOnDispose(CancellationTokenSource source) : IDisposable
    {
        public void Dispose()
        {
            source.Cancel();
            source.Dispose();
        }
    }

    private async Task<Stream?> OpenAsync(TcpClient client, bool secure)
    {
        var raw = client.GetStream();
        if (!secure) return raw;

        var tls = new SslStream(raw, leaveInnerStreamOpen: false,
            // Every client certificate is accepted: refusing one here would be the approval step
            // deliberately left out. The client validates this machine's, got from the pairing.
            userCertificateValidationCallback: (_, _, _, _) => true);

        try
        {
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _identity.Certificate,
                // Asked for, never required: Moonlight offers one, and a client that does not is
                // not turned away for it.
                ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, _stopping.Token);

            return tls;
        }
        catch (Exception error)
        {
            Log.Info($"the TLS handshake failed: {error.Message}");
            await tls.DisposeAsync();
            return null;
        }
    }

    private async Task<string?> RouteAsync(HttpRequest request, KnownClient? known,
                                           EndPoint? reachedAt,
                                           CancellationToken cancel) => request.Path switch
    {
        "/serverinfo" => ServerInfo(known, reachedAt),
        "/applist" => AppList(),
        "/pair" => await _pairing.HandleAsync(request, cancel),
        "/launch" => Launch(request, reachedAt),
        "/resume" => Resume(request, reachedAt),
        "/cancel" => Cancel(),
        _ => null,
    };


    // ------------------------------------------------------------------ the answers

    // What the client reads to decide whether this machine is worth listing, what it can decode
    // from it, and whether it is free. Everything else the client does starts from here.
    private string ServerInfo(KnownClient? known, EndPoint? reachedAt) => BuildDocument(xml =>
    {
        xml.WriteElementString("hostname", _identity.HostName);
        xml.WriteElementString("appversion", AppParameters.Protocol.AppVersion);
        xml.WriteElementString("GfeVersion", AppParameters.Protocol.GfeVersion);
        xml.WriteElementString("uniqueid", _identity.UniqueId);
        xml.WriteElementString("HttpsPort", _config.HttpsPort.ToString());
        xml.WriteElementString("ExternalPort", _config.HttpPort.ToString());
        xml.WriteElementString("mac", MacAddress());
        // The address this very client reached, not a guess: a client builds its RTSP URL out of
        // this, and one told 0.0.0.0 negotiates against nothing.
        xml.WriteElementString("LocalIP", Peer.ThisMachine(reachedAt));

        // From the startup probe: what the card's encoder actually opened. The H.264 bit stays on
        // whatever the probe said — clients treat it as the floor — and the launch still refuses.
        var codecModes = AppParameters.Protocol.CodecH264
                         | (_encoder.Hevc ? AppParameters.Protocol.CodecHevc : 0)
                         // Ten-bit HEVC is what a client looks for before it will offer high
                         // dynamic range at all, so this bit is the whole of the offer.
                         | (_encoder.Hdr ? AppParameters.Protocol.CodecHevcMain10 : 0)
                         | (_encoder.Av1 ? AppParameters.Protocol.CodecAv1Main8 : 0)
                         | (_encoder.Av1Hdr ? AppParameters.Protocol.CodecAv1Main10 : 0)
                         // Colour at full resolution, per codec: the client asks for it with
                         // chromaSamplingType and only when one of these said it could.
                         | (_encoder.H264Yuv444 ? AppParameters.Protocol.CodecH264High8_444 : 0)
                         | (_encoder.HevcYuv444 ? AppParameters.Protocol.CodecHevcRext8_444 : 0);
        xml.WriteElementString("ServerCodecModeSupport",
            codecModes.ToString(CultureInfo.InvariantCulture));
        xml.WriteElementString("MaxLumaPixelsHEVC",
            (_encoder.Hevc ? AppParameters.Protocol.MaxLumaPixelsHevc : 0)
            .ToString(CultureInfo.InvariantCulture));

        // Answered from the certificate the client presented, so only ever 1 over TLS. Telling an
        // unpaired client it is paired sends it to the encrypted port to fail without a reason.
        xml.WriteElementString("PairStatus", known is null ? "0" : "1");

        // What is running, and whether this machine is busy. Clients read exactly these two to
        // choose between Start and Resume, so they must agree with each other.
        var currentApp = _sessions.CurrentAppId;
        xml.WriteElementString("currentgame", currentApp.ToString(CultureInfo.InvariantCulture));
        xml.WriteElementString("state", currentApp == 0
            ? AppParameters.Protocol.StateFree
            : AppParameters.Protocol.StateBusy);

        xml.WriteStartElement("SupportedDisplayMode");
        foreach (var (width, height, rate) in DisplayModes())
        {
            xml.WriteStartElement("DisplayMode");
            xml.WriteElementString("Width", width.ToString());
            xml.WriteElementString("Height", height.ToString());
            xml.WriteElementString("RefreshRate", rate.ToString());
            xml.WriteEndElement();
        }
        xml.WriteEndElement();
    });

    // The screen's own size first, then the common ones. The client picks from this list, and the
    // screen's real mode is the one that needs no scaling anywhere in the chain.
    private IEnumerable<(int Width, int Height, int Rate)> DisplayModes()
    {
        var native = (_output.Bounds.Width, _output.Bounds.Height, 60);

        yield return native;

        foreach (var mode in new[]
                 {
                     (3840, 2160, 60), (2560, 1440, 60), (1920, 1080, 60),
                     (1600, 900, 60), (1280, 720, 60),
                 })
        {
            if (mode.Item1 != native.Item1 || mode.Item2 != native.Item2)
                yield return mode;
        }
    }

    private string AppList() => BuildDocument(xml =>
    {
        foreach (var game in _games.List())
            WriteApp(xml, AppParameters.Protocol.GameAppIdOffset + game.Id, game.Title);

        // The desktop last, after the games: it always works whatever the scanners found, and the
        // games are what a person opened the client to look for.
        WriteApp(xml, AppParameters.Protocol.DesktopAppId, AppParameters.Protocol.DesktopAppTitle);
    });

    private void WriteApp(XmlWriter xml, long id, string title)
    {
        xml.WriteStartElement("App");
        xml.WriteElementString("IsHdrSupported", _encoder.AnyHdr ? "1" : "0");
        xml.WriteElementString("AppTitle", title);
        xml.WriteElementString("ID", id.ToString(CultureInfo.InvariantCulture));
        xml.WriteEndElement();
    }

    // The cover for "Desktop", read out of the executable once and kept: it is the same picture on
    // every machine and is asked for whenever a client opens the list.
    private static readonly Lazy<(byte[] Bytes, string Type)?> DesktopCover = new(() =>
    {
        using var source = System.Reflection.Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("RemoteGameHub.Art.desktop-cover.png");

        if (source is null)
        {
            Log.Info("the cover for \"Desktop\" is not embedded in this build; the client will " +
                     "draw its own placeholder");
            return null;
        }

        using var bytes = new MemoryStream();
        source.CopyTo(bytes);
        return (bytes.ToArray(), "image/png");
    });

    // Covers already read once, by path, and what the file looked like then: a client asks for
    // every cover on every list it draws, and rereading the same file each time is wasted work.
    private readonly Dictionary<string, (long Length, DateTime Written, byte[] Bytes, string Type)>
        _fittedCovers = new(StringComparer.OrdinalIgnoreCase);

    // The box art Moonlight fetches per game, at /appasset?appid=N&AssetType=2&AssetIdx=0. A
    // missing picture is a plain 404: the client draws its own placeholder.
    private async Task ServeBoxArtAsync(Stream stream, HttpRequest request)
    {
        // The desktop is not in the library — it is not something that was found on this machine,
        // it is the machine — so its cover comes from here rather than from the games table.
        if (request.Query("AssetType") == "2" &&
            request.Query("appid") == AppParameters.Protocol.DesktopAppId.ToString(CultureInfo.InvariantCulture) &&
            DesktopCover.Value is { } cover)
        {
            await HttpResponse.WriteImageAsync(stream, cover.Bytes, cover.Type, _stopping.Token);
            return;
        }

        var path = BoxArtFor(request);
        if (path is null)
        {
            await HttpResponse.WriteNotFoundAsync(stream, _stopping.Token);
            return;
        }

        byte[] image;
        string contentType;
        try
        {
            (image, contentType) = await FittedCoverAsync(path);
        }
        catch (Exception error)
        {
            // The path was checked a moment ago, but Steam prunes its cache on its own schedule.
            Log.Info($"box art {path} could not be read: {error.Message}");
            await HttpResponse.WriteNotFoundAsync(stream, _stopping.Token);
            return;
        }

        await HttpResponse.WriteImageAsync(stream, image, contentType, _stopping.Token);
    }

    // One cover, exactly as its file holds it, remembered until the file changes.
    private async Task<(byte[] Bytes, string Type)> FittedCoverAsync(string path)
    {
        var file = new FileInfo(path);

        lock (_fittedCovers)
        {
            if (_fittedCovers.TryGetValue(path, out var held) &&
                held.Length == file.Length && held.Written == file.LastWriteTimeUtc)
            {
                return (held.Bytes, held.Type);
            }
        }

        var image = await File.ReadAllBytesAsync(path, _stopping.Token);

        var type = System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            _ => "image/jpeg",
        };

        lock (_fittedCovers)
        {
            _fittedCovers[path] = (file.Length, file.LastWriteTimeUtc, image, type);
        }

        return (image, type);
    }

    private string? BoxArtFor(HttpRequest request)
    {
        // AssetType 2 is the box art; the other types are banners and logos this server does not
        // keep. Refusing them outright keeps the client on its placeholder path.
        if (request.Query("AssetType") != "2") return null;

        if (!long.TryParse(request.Query("appid"), NumberStyles.Integer,
                           CultureInfo.InvariantCulture, out var appId))
            return null;

        if (appId <= AppParameters.Protocol.GameAppIdOffset) return null;

        return _games.BoxArtPath(appId - AppParameters.Protocol.GameAppIdOffset);
    }

    // Starts a session. The client sends the key its input and control messages are encrypted
    // with, what it wants to run, and a picture shape — the picture is agreed later over RTSP.
    private string Launch(HttpRequest request, EndPoint? reachedAt)
    {
        var launch = ReadLaunchRequest(request);
        if (launch is null)
            return LaunchRefused(400, "The launch request is missing or malforming a parameter " +
                                      "this server needs.");

        if (!_sessions.Launch(launch, out var refusal))
            return LaunchRefused(503, $"This server cannot start the stream: {refusal}");

        return BuildDocument(xml =>
        {
            xml.WriteElementString("sessionUrl0", SessionUrl(reachedAt));
            xml.WriteElementString("gamesession", "1");
        });
    }

    // The parameters a launch and a resume both carry: what to run, and the key everything this
    // client sends afterwards will be encrypted with.
    private static LaunchRequest? ReadLaunchRequest(HttpRequest request)
    {
        var riKeyText = request.Query("rikey");
        var riKeyIdText = request.Query("rikeyid");

        if (riKeyText is null || riKeyIdText is null) return null;

        if (!int.TryParse(request.Query("appid"), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var appId))
        {
            // A resume names no application, because it is resuming whatever is there.
            appId = 0;
        }

        if (!int.TryParse(riKeyIdText, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var riKeyId))
            return null;

        byte[] riKey;
        try
        {
            riKey = Convert.FromHexString(riKeyText);
        }
        catch (FormatException)
        {
            return null;
        }

        if (riKey.Length != 16) return null;

        // surroundAudioInfo is the channel mask in the high sixteen bits and the count in the low
        // ones: 0x30002 is stereo, 0x3F0006 is 5.1. The earliest the server hears what is wanted.
        var channels = int.TryParse(request.Query("surroundAudioInfo"), NumberStyles.Integer,
                           CultureInfo.InvariantCulture, out var surround)
            ? surround & 0xFFFF
            : 2;

        // Signed on the wire, unsigned here — the bits are kept, since both ends derive IVs from
        // it. 5.1 and 7.1 both move the sound; anything else stays on two.
        var audioChannels = channels == 6 ? 6 : channels == 8 ? 8 : 2;

        // The same size, rate and range the RTSP ANNOUNCE repeats later, read here so the screen
        // moves before the game reads it. Zero/false, skipping that move, for an older client.
        var (width, height, fps) = ParseMode(request.Query("mode"));
        var hdrRequested = request.Query("hdrMode") == "1";

        return new LaunchRequest(appId, riKey, unchecked((uint)riKeyId), audioChannels,
            width, height, fps, hdrRequested);
    }

    // "1920x1080x60". Anything else, including no mode at all, comes back as zeros.
    private static (int Width, int Height, int Fps) ParseMode(string? mode)
    {
        if (mode is null) return (0, 0, 0);

        var parts = mode.Split('x');
        if (parts.Length != 3) return (0, 0, 0);

        if (int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) &&
            int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var fps) &&
            width > 0 && height > 0 && fps > 0)
        {
            return (width, height, fps);
        }

        return (0, 0, 0);
    }

    // Where the client is to negotiate the stream. Built from the address it reached this server
    // on: it dials whatever goes here, and 0.0.0.0 is a stream that never starts.
    private string SessionUrl(EndPoint? reachedAt) =>
        $"rtsp://{Peer.ThisMachine(reachedAt)}:{_config.RtspPort.ToString(CultureInfo.InvariantCulture)}";

    // A refusal a client will show rather than swallow. The gamesession element is what
    // tells it no session was created; without it a client waits for a stream that is not coming.
    private static string LaunchRefused(int code, string message) => BuildDocument(xml =>
    {
        xml.WriteElementString("status_message", message);
        xml.WriteElementString("gamesession", "0");
    }, code);

    // Reattaches to a session already running. This server reports a running application only
    // while it is actually streaming, so this is asked when a client's own stream dropped.
    private string Resume(HttpRequest request, EndPoint? reachedAt)
    {
        var resume = ReadLaunchRequest(request);
        if (resume is null)
        {
            return BuildDocument(xml =>
            {
                xml.WriteElementString("resume", "0");
                xml.WriteElementString("status_message",
                    "The resume request is missing a parameter this server needs.");
            }, 400);
        }

        if (!_sessions.Resume(resume, out var refusal))
        {
            return BuildDocument(xml =>
            {
                xml.WriteElementString("resume", "0");
                xml.WriteElementString("status_message", $"This stream cannot be resumed: {refusal}");
            }, 503);
        }

        return BuildDocument(xml =>
        {
            xml.WriteElementString("sessionUrl0", SessionUrl(reachedAt));
            xml.WriteElementString("resume", "1");
        });
    }

    private string Cancel()
    {
        _sessions.Cancel();
        return BuildDocument(xml => xml.WriteElementString("cancel", "1"));
    }

    // Every answer is a root element carrying a status code. The declaration is written by hand:
    // XmlWriter into a string declares utf-16 while the bytes that go on the wire are UTF-8.
    internal static string BuildDocument(Action<XmlWriter> body, int statusCode = 200)
    {
        var text = new StringBuilder();
        text.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");

        using (var xml = XmlWriter.Create(text, new XmlWriterSettings
               {
                   Indent = true,
                   OmitXmlDeclaration = true,
               }))
        {
            xml.WriteStartElement("root");
            xml.WriteAttributeString("status_code", statusCode.ToString());
            body(xml);
            xml.WriteEndElement();
            xml.Flush();
        }

        return text.ToString();
    }

    // ------------------------------------------------------------------ this machine on the network

    // The address of the interface that would actually carry the stream, from the routing table:
    // on a machine with a virtual switch or a VPN, the first interface is rarely the right one.
    private static string MacAddress()
    {
        var chosen = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n => n.GetPhysicalAddress().GetAddressBytes())
            .FirstOrDefault(bytes => bytes.Length == 6);

        return chosen is null
            ? "00:00:00:00:00:00"
            : string.Join(":", chosen.Select(b => b.ToString("x2")));
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();

        foreach (var listener in _listeners) listener.Stop();

        try
        {
            await Task.WhenAll(_accepting).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // Shutting down is not a place to wait indefinitely for a socket to notice.
        }

        _stopping.Dispose();
    }
}
