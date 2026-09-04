//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using RemoteGameHub.App;
using RemoteGameHub.Media;
using RemoteGameHub.Library;
using RemoteGameHub.Session;

namespace RemoteGameHub.Protocol;

// One page at the machine's own address, showing what this server is doing and taking the four
// digits a pairing client shows. Its own port, private addresses only, and never forwarded.
internal sealed class WebConsole : IAsyncDisposable
{
    // How much of the log the page shows. Enough to cover a stream starting and failing, which is
    // what anyone opening this page is usually looking at, and little enough to send in one go.
    private const int LogTailBytes = 64 * 1024;

    private readonly AppConfig _config;
    private readonly HostIdentity _identity;
    private readonly PairingManager _pairing;
    private readonly ClientStore _clients;
    private readonly GameLibrary _games;
    private readonly SessionManager _sessions;
    private readonly GamepadHub _gamepads;
    private readonly EncoderCapabilities _encoder;
    private readonly UpdateChecker _updates;
    private readonly string _directory;
    private readonly DateTimeOffset _started = DateTimeOffset.Now;
    private readonly CancellationTokenSource _stopping = new();

    private TcpListener? _listener;
    private Task? _accepting;

    // What the Rescan button asks for, set after construction because the page is opened before
    // the scan exists. Takes the reason for the log line and returns at once.
    internal Action<string>? Rescan { get; set; }

    internal WebConsole(AppConfig config, HostIdentity identity, PairingManager pairing,
                        ClientStore clients, GameLibrary games, SessionManager sessions,
                        EncoderCapabilities encoder, UpdateChecker updates,
                        GamepadHub gamepads, string directory)
    {
        _config = config;
        _gamepads = gamepads;
        _identity = identity;
        _pairing = pairing;
        _clients = clients;
        _games = games;
        _sessions = sessions;
        _encoder = encoder;
        _updates = updates;
        _directory = directory;
    }

    internal void Start()
    {
        try
        {
            var listener = new TcpListener(IPAddress.IPv6Any, _config.WebPort);
            listener.Server.DualMode = true;
            listener.Start();

            _listener = listener;
        }
        catch (Exception error)
        {
            // Never fatal: port 80 is the setting most likely to be taken by something else, and
            // without the page a client can still be paired and the log still opened.
            Log.Warn(
                $"The page on port {_config.WebPort} could not be opened: {error.Message}\n" +
                "Something else is probably using that port. Set [Network] WebPort to a free one —\n" +
                $"for example {_config.PortBase + AppParameters.Ports.WebOffset} — and restart.\n" +
                "Everything else works; only the page is missing.");
            return;
        }

        _accepting = Task.Run(AcceptLoop);
        Log.Info($"the page is at http://{LocalAddress()}" +
                 (_config.WebPort == 80 ? string.Empty : $":{_config.WebPort}") + "/");
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
                Log.Warn($"the page stopped accepting connections: {error.Message}");
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                var peer = Peer.Plain((client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None);

                await using var stream = client.GetStream();

                // Refused before the request is read: this page pairs devices and shows the log,
                // and a refusal before parsing is one that cannot be talked around.
                if (!IsPrivate(peer))
                {
                    Log.Warn($"the page refused {Peer.Describe(peer)}: it is " +
                             "not on a private network. Only the streaming ports are meant to be " +
                             "reachable from outside.");

                    await WriteAsync(stream, 403, "text/plain",
                        "This page answers only on the local network.");
                    return;
                }

                var request = await HttpRequest.ReadAsync(stream, _stopping.Token);
                if (request is null) return;

                await RespondAsync(stream, request);
            }
        }
        catch (Exception error)
        {
            Log.Info($"a page request ended early ({error.GetType().Name}: {error.Message})");
        }
    }

    private async Task RespondAsync(Stream stream, HttpRequest request)
    {
        // Everything is at "/", asked for through the query string: one address to remember, and
        // one to keep off the internet.
        if (request.Query("pin") is { } pin)
        {
            var digits = pin.Length == 4 && pin.All(char.IsAsciiDigit);

            // The name is the person's to choose and is kept as typed, within reason: a name is
            // one line, and the list it appears in has room for about this much of one.
            var name = (request.Query("name") ?? string.Empty).Trim();
            if (name.Length > 48) name = name[..48];

            var answer = !digits
                ? "A code is four digits."
                : _pairing.SupplyPin(pin, name)
                    ? "Sent. The client should finish pairing in a moment."
                    : "Nothing is waiting for a code. Press Pair on the client and type the " +
                      "digits it shows then; the ones before are of no use now.";

            await WriteAsync(stream, 200, "text/plain", answer);
            return;
        }

        // Cancel, from the box that asks for the digits: the client waits five minutes for a PIN,
        // and there is no other way to end an attempt started by accident.
        if (request.Query("cancelpair") is not null)
        {
            await WriteAsync(stream, 200, "text/plain",
                _pairing.CancelWaiting()
                    ? "Pairing cancelled."
                    : "Nothing is waiting to pair.");
            return;
        }

        // The same scan the tray menu asks for; it re-reads [Games] first, so a folder added to
        // the file is scanned without a restart. Returns at once.
        if (request.Query("rescan") is not null)
        {
            var rescan = Rescan;
            rescan?.Invoke("asked for from the page");

            await WriteAsync(stream, 200, "text/plain",
                rescan is null ? "The scan is not ready yet." : "Scanning…");
            return;
        }

        if (request.Query("waiting") is not null)
        {
            await WriteAsync(stream, 200, "text/plain", _pairing.WaitingFor ?? string.Empty);
            return;
        }

        // Which of the two palettes the page wears: this machine's application theme rather than
        // the browser's, so it changes when that is flipped.
        if (request.Query("theme") is not null)
        {
            await WriteAsync(stream, 200, "text/plain", ThemeName());
            return;
        }

        // The header's two lines and which row is running, in one answer rather than three
        // requests a second: none of the three can contain a newline of its own.
        if (request.Query("status") is not null)
        {
            var runningId = RunningGameId();
            await WriteAsync(stream, 200, "text/html; charset=utf-8",
                HostLine() + "\n" + Status() + "\n" +
                (runningId != 0 ? runningId.ToString(CultureInfo.InvariantCulture) : string.Empty));
            return;
        }

        if (request.Query("log") is not null)
        {
            await WriteAsync(stream, 200, "text/plain", ReadLogTail());
            return;
        }

        if (request.Query("games") is not null)
        {
            await WriteAsync(stream, 200, "text/html; charset=utf-8", GamesList());
            return;
        }

        // The picture itself, for the list. Served from here rather than from the protocol's own
        // box-art endpoint so that the page never has to know the offset the protocol adds.
        if (request.Query("cover") is { } coverId && long.TryParse(coverId, out var forCover))
        {
            var path = _games.BoxArtPath(forCover);
            if (path is null || !File.Exists(path))
            {
                await WriteAsync(stream, 404, "text/plain", "no cover");
                return;
            }

            var kind = Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)
                ? "image/png"
                : "image/jpeg";

            await HttpResponse.WriteImageAsync(stream, await File.ReadAllBytesAsync(path, _stopping.Token),
                                               kind, _stopping.Token);
            return;
        }

        // The application's own icon, from inside the executable: the page has no folder of files
        // beside it, and one exe was promised.
        if (request.Query("icon") is not null)
        {
            await using var icon = System.Reflection.Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("RemoteGameHub.Icons.RemoteGameHub.ico");

            if (icon is null)
            {
                await WriteAsync(stream, 404, "text/plain", "no icon");
                return;
            }

            using var bytes = new MemoryStream();
            await icon.CopyToAsync(bytes, _stopping.Token);

            await HttpResponse.WriteImageAsync(stream, bytes.ToArray(), "image/x-icon",
                                               _stopping.Token);
            return;
        }

        if (request.Query("stop") == "1")
        {
            _sessions.Cancel();
            await WriteAsync(stream, 200, "text/plain", "Stopped.");
            return;
        }

        if (request.Query("remove") is { } removeId && long.TryParse(removeId, out var toRemove))
        {
            _games.Remove(toRemove);
            await WriteAsync(stream, 200, "text/plain", "Removed.");
            return;
        }

        if (request.Query("save") is { } saveId && long.TryParse(saveId, out var toSave))
        {
            var title = (request.Query("title") ?? string.Empty).Trim();
            var command = (request.Query("command") ?? string.Empty).Trim();
            var folder = (request.Query("folder") ?? string.Empty).Trim();

            if (title.Length == 0 || command.Length == 0)
            {
                await WriteAsync(stream, 200, "text/plain",
                    "A game needs a name and something to start.");
                return;
            }

            // Save answers with the row's identifier, which is the new one when a game is being
            // added: the pointer switch belongs to that row and is written straight after.
            var saved = _games.Save(toSave, title, command, folder);
            _games.RecordPointer(saved, request.Query("pointer") == "1");
            _games.RecordShowCard(saved, request.Query("card") != "0");

            if (int.TryParse(request.Query("quality"), out var level) &&
                Enum.IsDefined(typeof(StreamQuality), level))
            {
                _games.RecordQuality(saved, (StreamQuality)level);
            }

            await WriteAsync(stream, 200, "text/plain", "Saved.");
            return;
        }

        // What the catalogue answers for a name, as data rather than drawn here: the page lays the
        // pictures out and fetches them from the catalogue's address, not through this server.
        if (request.Query("artlist") is not null)
        {
            var title = (request.Query("title") ?? string.Empty).Trim();
            var found = await CoverArt.SearchAllAsync(title, _config, _stopping.Token);

            var json = "[" + string.Join(",", found.Select(c =>
                $"{{\"name\":{JsonText(c.Name)},\"src\":{JsonText(c.Address)}}}")) + "]";

            await WriteAsync(stream, 200, "application/json", json);
            return;
        }

        if (request.Query("upload") is { } uploadId && long.TryParse(uploadId, out var forUpload))
        {
            if (request.Body.Length == 0)
            {
                await WriteAsync(stream, 200, "text/plain", "Nothing arrived.");
                return;
            }

            var stored = CoverArt.Store(_games, forUpload, _directory, request.Body);
            await WriteAsync(stream, 200, "text/plain",
                stored ? "Uploaded." : "That file is not a picture this machine can read.");
            return;
        }

        // A picture found in a browser, given as its address. The one answer to a game the search
        // does not know, which is most games that were never sold on Steam.
        if (request.Query("arturl") is { } urlId && long.TryParse(urlId, out var forUrl))
        {
            var address = (request.Query("url") ?? string.Empty).Trim();
            if (address.Length == 0)
            {
                await WriteAsync(stream, 200, "text/plain", "Paste the address of a picture.");
                return;
            }

            var said = await CoverArt.FetchFromUrlAsync(_games, forUrl, address, _directory,
                                                        _stopping.Token);
            await WriteAsync(stream, 200, "text/plain", said);
            return;
        }

        if (request.Query("clients") is not null)
        {
            await WriteAsync(stream, 200, "text/html; charset=utf-8", ClientsList());
            return;
        }

        if (request.Query("forget") is { } forgetId && long.TryParse(forgetId, out var toForget))
        {
            var gone = _clients.Forget(toForget);
            await WriteAsync(stream, 200, "text/plain",
                gone ? "Forgotten." : "There is no such device.");
            return;
        }

        await WriteAsync(stream, 200, "text/html; charset=utf-8", Page());
    }

    // Which row is streaming right now, as the identifier the protocol uses less the offset games
    // are numbered from. Zero is the desktop, and no tile carries that, so zero means none.
    private long RunningGameId()
    {
        var appId = _sessions.CurrentAppId;
        return appId > AppParameters.Protocol.GameAppIdOffset
            ? appId - AppParameters.Protocol.GameAppIdOffset
            : 0;
    }

    // The games, as the page shows them. Rendered here rather than sent as data: the server
    // already has the list in that shape.
    private string GamesList()
    {
        var html = new StringBuilder();
        var games = _games.Details();
        var running = RunningGameId();

        foreach (var game in games)
        {
            // The title and the command travel with the tile, so that opening the editor needs no
            // second request: the page already has everything the window asks about.
            html.Append($"<article class=\"game{(game.Id == running ? " running" : string.Empty)}\" " +
                        $"data-id={game.Id} " +
                        $"data-title=\"{Escape(game.Title)}\" " +
                        $"data-command=\"{Escape(game.LaunchCommand)}\" " +
                        $"data-folder=\"{Escape(game.InstallPath ?? string.Empty)}\" " +
                        $"data-pointer={(game.Pointer ? 1 : 0)} " +
                        $"data-quality={(int)game.Quality} " +
                        $"data-card={(game.ShowCard ? 1 : 0)}>");

            // A container of its own, so the overlays below position against the poster alone,
            // not against the whole tile — taller by the title and source line under it.
            html.Append("<div class=poster>");

            // The stamp is not read by the server: it is there so that a cover just replaced is
            // fetched again instead of being taken from the browser's cache under the same address.
            html.Append(game.ArtStamp != 0
                ? $"<img class=art src=\"/?cover={game.Id}&amp;v={game.ArtStamp}\" alt=\"\" loading=lazy>"
                : "<div class=\"art none\"><span>no cover</span></div>");

            // Always in the markup; the poll that follows the running one toggles the article's
            // class, and CSS alone decides whether this badge is seen.
            html.Append("<span class=running>Running</span>");

            // Centred on the poster rather than among the small tools below: stopping the one
            // game that is running is the one action here worth not having to aim for.
            html.Append($"<button data-do=stop title=\"Stop\" class=stop>{StopIcon}</button>");

            html.Append("<div class=tools>" +
                        $"<button data-do=edit title=\"Edit\">{PencilIcon}</button>" +
                        $"<button data-do=remove title=\"Remove\" class=danger>{TrashIcon}</button>" +
                        "</div>");

            html.Append("</div>");

            html.Append($"<h3>{Escape(game.Title)}</h3>");
            // Where the game came from, and whether anybody has touched it since: "xbox (changed)"
            // says more than "by hand", which loses how the game is started.
            var changed = game.Manual || game.ArtManual;
            html.Append($"<p class=q>{Escape(game.Source)}" +
                        (changed && game.Source != "by hand" ? " (changed)" : string.Empty) +
                        "</p>");
            html.Append("</article>");
        }

        // Last in the grid, and the same shape as the tiles beside it: a game is added where the
        // games are, not from a form somewhere else on the page.
        html.Append($"<button class=\"game add\" id=addtile>{PlusIcon}<span>Add a game</span></button>");

        return html.ToString();
    }

    // The devices that have paired, and nothing at all when none have. Eight characters of the
    // SHA-256 fingerprint are enough to tell two devices of the same name apart by eye.
    private string ClientsList()
    {
        var clients = _clients.All();
        if (clients.Count == 0) return string.Empty;

        // The heading is part of the list rather than of the section around it, so that it comes
        // and goes with the rows when the page replaces them.
        var html = new StringBuilder("<h2>Paired devices</h2>");

        foreach (var client in clients)
        {
            html.Append($"<div class=client data-id={client.Id} " +
                        $"data-name=\"{Escape(client.Name)}\">");
            html.Append($"<b>{Escape(client.Name)}</b>");
            html.Append($"<span class=q>last seen {client.LastSeenAt.LocalDateTime:yyyy-MM-dd HH:mm}" +
                        $" · paired {client.PairedAt.LocalDateTime:yyyy-MM-dd}" +
                        $" · {client.Fingerprint[..8].ToLowerInvariant()}</span>");
            html.Append($"<button data-do=forget title=\"Forget\" class=danger>{TrashIcon}</button>");
            html.Append("</div>");
        }

        return html.ToString();
    }

    // ------------------------------------------------------------------ the page

    private string Page()
    {
        var waiting = _pairing.WaitingFor;

        var body = new StringBuilder();
        body.Append($"<!doctype html><html data-theme=\"{ThemeName()}\"><meta charset=\"utf-8\">");
        body.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        body.Append($"<title>{AppParameters.Identity.DisplayName}</title>");
        body.Append(Style);
        // The header is the icon beside two lines: the name, the version and the project's
        // address, and under them what the server is doing now. The icon is as tall as both.
        body.Append("<header><img id=mark src=\"/?icon=1\" alt=\"\"><div><div class=titlerow><h1>" +
                    $"{AppParameters.Identity.DisplayName} <span class=v>v{Program.Version}</span>" +
                    "</h1><span class=meta id=host>");
        body.Append(HostLine());
        body.Append("</span><span class=\"meta gh\">" +
                    $"<a href=\"{AppParameters.Links.Project}\" target=_blank rel=noopener>GitHub</a>" +
                    "</span></div><p id=status>");
        body.Append(Status());
        body.Append("</p></div></header>");

        // Written into every page, shown or hidden: a client can start pairing a second after the
        // page was drawn. Both fields are sent together on Save, and the name is what is listed.
        body.Append($"<section id=pair class=\"card{(waiting is null ? " off" : string.Empty)}\">");
        body.Append("<p id=who>");
        body.Append(waiting is null ? string.Empty : WhoIsPairing(waiting));
        body.Append("</p>");
        body.Append("<label>What to call this device" +
                    "<input id=devname maxlength=48 autocomplete=off placeholder=\"Living-room TV, " +
                    "Anna's phone…\"></label>");
        body.Append("<label>The four digits it is showing" +
                    "<input id=pin inputmode=numeric maxlength=4 autocomplete=off></label>");
        body.Append("<div class=row><button id=pairsave class=primary>Save</button>" +
                    "<button id=paircancel>Cancel</button>" +
                    "<span id=said class=q></span></div></section>");

        // Above the list, because it is about the whole list rather than about any tile in it.
        // The same thing the tray menu does, put where somebody looking at the games already is.
        body.Append("<div class=\"row end\" id=gamestools>" +
                    "<span id=scansaid class=q></span>" +
                    "<button id=rescan>Rescan games</button></div>");

        body.Append("<div id=games>");
        body.Append(GamesList());
        body.Append("</div>");

        // The paired devices, under the games: this server admits the first client that asks, so
        // the list is the only account of who can reach this screen and the only place to end it.
        body.Append("<section id=clientsbox><div id=clients>");
        body.Append(ClientsList());
        body.Append("</div></section>");

        // One window for adding and editing, and a real dialog rather than a panel: the browser
        // takes care of the backdrop, of Escape, and of keeping the keyboard inside it.
        body.Append("<dialog id=editor><form method=dialog>" +
                    "<h2 id=editortitle>Edit</h2>" +
                    "<label>Name<input id=title placeholder=\"As it should appear\"></label>" +
                    "<label>Starts with<input id=command placeholder=\"A file, a shortcut, or " +
                    "steam://rungameid/…\"></label>" +
                    "<label>From this folder <span class=hint>optional — many games look for their " +
                    "own files beside it</span>" +
                    "<input id=folder placeholder=\"C:\\Games\\Something\"></label>" +

                    // On for the few games that draw no pointer of their own; not offered at all
                    // once the virtual cursor is off in the configuration file.
                    (_config.VirtualMouse
                        ? "<label class=switch><input type=checkbox id=pointer>" +
                          "<span>Enable software mouse cursor</span></label>"
                        : string.Empty) +

                    // On by default, unlike the pointer above. Named startcard: the pairing
                    // section below already claims the id "card" for itself.
                    "<label class=switch><input type=checkbox id=startcard checked>" +
                    "<span>Show splash screen when game is starting</span></label>" +

                    // A fast game wants the encoder to finish early; a quiet one can afford the
                    // time. A client from outside this network is given one level less than this.
                    "<label>Quality <span class=hint>lower for fast games, higher for quiet " +
                    "ones — a remote client drops one level</span>" +
                    "<select id=quality><option value=0>Low</option>" +
                    "<option value=1>Medium</option><option value=2>High</option>" +
                    "<option value=3>Lossless</option></select></label>" +
                    "<div id=coverbox><label>Cover" +
                    "<span class=row><button type=button id=find>Find by name</button>" +
                    "<label class=upload>Upload<input type=file accept=\"image/*\" hidden id=file>" +
                    "</label></span></label>" +

                    "<label>Or the address of a picture " +
                    "<span class=hint>any format — it is fetched and converted here</span>" +
                    "<span class=row><input id=arturl placeholder=\"https://…/poster.webp\">" +
                    "<button type=button id=fetch>Fetch</button></span></label></div>" +
                    "<p id=editorsaid class=q></p>" +
                    "<div class=\"row end\"><button type=button id=cancel>Cancel</button>" +
                    "<button type=button id=save class=primary>Save</button></div>" +
                    "</form></dialog>");

        // The picker, over the editor: the name stays editable, because a game is installed under
        // one name and sold under another. A click chooses; a click outside closes.
        body.Append("<dialog id=picker>" +
                    "<div class=row><input id=pickname placeholder=\"The name to look for\">" +
                    "<button type=button id=picksearch class=primary>Search</button></div>" +
                    "<p id=picksaid class=q></p>" +
                    "<div id=pickgrid></div>" +
                    "</dialog>");

        // A drawer along the bottom, shut to begin with and opening upwards over the full width,
        // leaving the page where it was rather than pushing it about.
        body.Append("<footer id=logbox><pre id=log>");
        body.Append(Escape(ReadLogTail()));
        body.Append("</pre><button id=logtoggle>Log</button></footer>");

        body.Append(Script);
        return body.ToString();
    }

    // The palette the page wears, from this machine's theme for applications. Read on every
    // request, because the point is to follow the registry value when it changes.
    private static string ThemeName()
    {
        try
        {
            return ThemeIcons.AppsAreDark() ? "dark" : "light";
        }
        catch (Exception)
        {
            return "dark";
        }
    }

    // The line above the two fields. The client's own name for itself is quoted rather than
    // used, because on Moonlight it is the same word on every device.
    private static string WhoIsPairing(string clientName) =>
        $"A device calling itself <b>{Escape(clientName)}</b> wants to pair with this machine.";

    // What is happening on this machine, which is the whole of the line under the heading: one
    // client at a time, and its stream's own numbers while there is one.
    private string Status()
    {
        var stream = _sessions.Status;

        return stream.Streaming
            ? $"<b class=live>streaming</b> to {stream.Client} " +
              $"<span class=dot>·</span> {stream.Detail}"
            : "<b>waiting for a client</b>";
    }

    // What this machine is, on the heading's line: the host name, the encoder, the sizes of the
    // two lists and how long the server has been up.
    private string HostLine()
    {
        var uptime = DateTimeOffset.Now - _started;

        var codecs = _encoder.Refusal is null
            ? string.Join("/", new[]
            {
                _encoder.H264 ? "H.264" : null,
                _encoder.Hevc ? "HEVC" : null,
                _encoder.Av1 ? "AV1" : null,
            }.Where(codec => codec is not null))
            : "no encoder";

        // What the card can encode. Whether a stream really goes out in it depends on the screen
        // being in HDR at the time, which only a running stream knows; the log has the reasons.
        var hdr = _encoder.AnyHdr ? "HDR" : "no HDR";

        var machine = new List<string>
        {
            Escape(_identity.HostName),
            $"{Escape(_encoder.Encoder.ToString())} {Escape(codecs)}",
            Escape(hdr),
            $"{_games.Count()} games",
            // Which controller bus is presenting the pads, or that there is none: a client whose
            // controller does nothing has one question, and this is its answer.
            _gamepads.IsAvailable
                ? $"{Escape(_gamepads.Driver)} enabled"
                : "no controller bus",
            $"{_clients.Count()} paired",
            $"up {Escape(Describe(uptime))}",
        };

        machine.Add(_config.Upnp ? "<b class=warn>uPnP enabled</b>" : "uPnP disabled");

        // A newer release, when the daily check has found one. Last on this line, because it is
        // news about the server rather than about the machine.
        if (_updates.Newer is { } newer)
        {
            machine.Add($"<a class=update href=\"{AppParameters.Links.LatestRelease}\" target=_blank " +
                        $"rel=noopener>version {Escape(newer)} is out — download</a>");
        }

        return string.Join(" <span class=dot>·</span> ", machine);
    }

    private static string Describe(TimeSpan span) => span.TotalDays >= 1
        ? $"{(int)span.TotalDays} d {span.Hours} h"
        : span.TotalHours >= 1
            ? $"{(int)span.TotalHours} h {span.Minutes} min"
            : $"{(int)span.TotalMinutes} min";

    // The end of the log file, read while the server is still writing to it — hence the sharing
    // flags. Cut at the first line break, so the page never opens on half a line.
    private static string ReadLogTail()
    {
        var path = Log.Path;
        if (path is null) return "There is no log file.";

        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                                            FileShare.ReadWrite | FileShare.Delete);

            var from = Math.Max(0, file.Length - LogTailBytes);
            file.Seek(from, SeekOrigin.Begin);

            using var reader = new StreamReader(file, Encoding.UTF8);
            var text = reader.ReadToEnd();

            if (from > 0)
            {
                var firstBreak = text.IndexOf('\n');
                if (firstBreak >= 0) text = text[(firstBreak + 1)..];
            }

            return text;
        }
        catch (Exception error)
        {
            return $"The log could not be read: {error.Message}";
        }
    }

    // ------------------------------------------------------------------ who may ask

    // Whether an address is on a private network — the test that decides who may see this page.
    // Written out rather than left to a library so that anything unrecognised is refused.
    internal static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;

        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var octets = address.GetAddressBytes();

            return octets[0] switch
            {
                10 => true,                                        // 10.0.0.0/8
                127 => true,                                       // loopback, again after mapping
                172 => octets[1] >= 16 && octets[1] <= 31,          // 172.16.0.0/12
                192 => octets[1] == 168,                            // 192.168.0.0/16
                169 => octets[1] == 254,                            // link-local
                100 => octets[1] >= 64 && octets[1] <= 127,         // carrier-grade NAT
                _ => false,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal) return true;

            // Unique local addresses, fc00::/7 — the IPv6 equivalent of the ranges above.
            return (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
        }

        return false;
    }

    private static string LocalAddress()
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect("192.168.1.1", 9);
            return (probe.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? "this machine";
        }
        catch (Exception)
        {
            return "this machine";
        }
    }

    // ------------------------------------------------------------------ plumbing

    private async Task WriteAsync(Stream stream, int status, string contentType, string text)
    {
        var body = Encoding.UTF8.GetBytes(text);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Forbidden")}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n" +
            "\r\n");

        await stream.WriteAsync(head, _stopping.Token);
        await stream.WriteAsync(body, _stopping.Token);
        await stream.FlushAsync(_stopping.Token);
    }

    private static string Escape(string text) => WebUtility.HtmlEncode(text);

    // A string as a JSON literal, quotes included.
    private static string JsonText(string text) => System.Text.Json.JsonSerializer.Serialize(text);

    // The marks on the tiles, drawn rather than written: a glyph from a font would be a
    // different shape on every machine, and none of these need a language.
    private const string PencilIcon =
        "<svg viewBox=\"0 0 24 24\" aria-hidden=true><path d=\"M4 20h4L19 9l-4-4L4 16v4z\"/>" +
        "<path d=\"M14 6l4 4\"/></svg>";

    private const string TrashIcon =
        "<svg viewBox=\"0 0 24 24\" aria-hidden=true><path d=\"M5 7h14M10 7V5h4v2M6 7l1 13h10l1-13\"/>" +
        "<path d=\"M10 11v6M14 11v6\"/></svg>";

    private const string StopIcon =
        "<svg viewBox=\"0 0 24 24\" aria-hidden=true><rect x=6 y=6 width=12 height=12 rx=2/></svg>";

    private const string PlusIcon =
        "<svg viewBox=\"0 0 24 24\" class=big aria-hidden=true><path d=\"M12 5v14M5 12h14\"/></svg>";

    // The stylesheet, in two palettes: every colour is a variable, written only in the two sets
    // below, and the root's data-theme attribute chooses between them.
    private const string Style =
        "<style>" +
        // Light: Windows 11's own greys, a white field on a grey ground, green for what is live.
        ":root{color-scheme:light;--bg:#f3f3f3;--panel:#fbfbfb;--line:#e2e2e2;--ink:#1a1a1a;" +
        "--dim:#5f5f5f;--faint:#8a8a8a;--fainter:#c4c4c4;--field:#fff;--field-line:#c9c9c9;" +
        "--button:#fbfbfb;--button-hover:#eaeaea;--button-line:#cfcfcf;--button-line-hover:#a8a8a8;" +
        "--glass:rgba(251,251,251,.85);--primary:#dff0e2;--primary-line:#8cc79a;--primary-hover:#cfe8d4;" +
        "--danger-line:#d99;--danger-bg:#fbe9e9;--live:#2a7f3f;--warn:#b8620a;--green-line:#7fb98a;" +
        "--update-hover:#e6f4e9;--log-bg:#fafafa;--log-ink:#333;--none-ink:#9a9a9a;" +
        "--pin-field:#fff;--pin-line:#b0b0b0;--pin-ink:#111;--backdrop:rgba(0,0,0,.35);" +
        "--add-hover-ink:#333;--meta-link:#6a6a6a}" +
        // Dark: the palette the page was first drawn in.
        ":root[data-theme=dark]{color-scheme:dark;--bg:#101010;--panel:#161616;--line:#262626;" +
        "--ink:#ddd;--dim:#8a8a8a;--faint:#666;--fainter:#444;--field:#111;--field-line:#3a3a3a;" +
        "--button:#1c1c1c;--button-hover:#262626;--button-line:#3a3a3a;--button-line-hover:#555;" +
        "--glass:rgba(16,16,16,.82);--primary:#2c4a33;--primary-line:#3d6b47;--primary-hover:#35603d;" +
        "--danger-line:#844;--danger-bg:#2a1a1a;--live:#7c6;--warn:#d95;--green-line:#3a5;" +
        "--update-hover:#1a2a1e;--log-bg:#0c0c0c;--log-ink:#bbb;--none-ink:#555;" +
        "--pin-field:#222;--pin-line:#555;--pin-ink:#eee;--backdrop:rgba(0,0,0,.6);" +
        "--add-hover-ink:#aaa;--meta-link:#888}" +
        "*{box-sizing:border-box}" +
        "body{font:15px/1.5 system-ui,sans-serif;margin:0 auto;padding:1.25rem 1.25rem 4rem;" +
        // Wide enough for five posters and their gaps, and no wider: past that the grid keeps
        // adding columns until a poster is a thumbnail again.
        "max-width:960px;background:var(--bg);color:var(--ink)}" +

        // The header: one line, and it stays put while the grid scrolls under it.
        "header{position:sticky;top:0;z-index:5;background:var(--bg);padding:.25rem 0 .75rem;" +
        "border-bottom:1px solid var(--line);margin-bottom:1.25rem;display:flex;gap:.7rem;" +
        "align-items:center}" +
        // As tall as the two lines beside it, and not a pixel of the width they need: the icon is
        // the one thing here that is decoration, so it gives way.
        "#mark{width:2.7rem;height:2.7rem;flex:none;image-rendering:auto}" +
        "header>div{min-width:0;flex:1}" +
        "h1{font-size:1.05rem;margin:0;font-weight:600}" +
        ".v{color:var(--faint);font-weight:400}" +
        ".meta{margin-left:.6rem;font-size:12px;font-weight:400;color:var(--faint)}" +
        ".meta a{color:var(--meta-link);text-decoration:none}" +
        ".meta a:hover{color:var(--ink);text-decoration:underline}" +
        "#status{margin:.35rem 0 0;color:var(--dim);font-size:13px;" +
        "white-space:nowrap;overflow:auto;scrollbar-width:none}" +
        // Title, host facts and the GitHub link on one row; the facts between the two fixed ends
        // give way, scrolling rather than wrapping the link onto a line of its own.
        ".titlerow{display:flex;align-items:baseline;min-width:0}" +
        ".titlerow h1{flex:none}" +
        "#host{flex:1;min-width:0;overflow:auto;scrollbar-width:none;white-space:nowrap}" +
        ".titlerow .gh{flex:none}" +
        "#status b{color:var(--ink);font-weight:600}" +
        "#status b.live{color:var(--live)}" +
        "#status b.warn{color:var(--warn)}" +
        // The one link in the status line, and the one thing on the page in a colour of its own:
        // a newer version is news, and news is allowed to stand out.
        "#status a.update{color:var(--live);font-weight:600;text-decoration:none;" +
        "border:1px solid var(--green-line);border-radius:.3rem;padding:.05rem .45rem}" +
        "#status a.update:hover{background:var(--update-hover)}" +
        ".dot{color:var(--fainter);margin:0 .15rem}" +

        // The grid: auto-fill with a minimum of about a hundred and forty pixels lands on five or
        // six columns at an ordinary window and on two on a telephone.
        "#gamestools{align-items:center;margin:0 0 .6rem}" +
        "#games{display:grid;gap:1.25rem;grid-template-columns:repeat(auto-fill,minmax(168px,1fr))}" +
        ".game{background:none;border:0;padding:0;color:inherit;text-align:left;font:inherit}" +
        // Positions everything pinned to the poster (the tools, the stop button, the running
        // badge) against the image alone, not against the title and source line under it too.
        ".poster{position:relative}" +
        // 3:4, matching the shape CoverArt.ToPng pads every cover to. No border or rounding: the
        // poster is the tile, not a picture framed inside one.
        ".art{width:100%;aspect-ratio:3/4;object-fit:cover;display:block;background:transparent}" +
        ".art.none{display:grid;place-items:center;color:var(--none-ink);font-size:11px}" +
        ".game h3{font-size:13px;margin:.5rem 0 0;font-weight:500;line-height:1.3;" +
        "overflow:hidden;display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical}" +
        ".game .q{margin:.1rem 0 0;font-size:11px;color:var(--faint)}" +

        // The two marks sit on the poster and appear when it is pointed at. On a touch screen
        // there is no pointing, so there they are simply always there.
        ".tools{position:absolute;top:.5rem;right:.5rem;display:flex;gap:.35rem;opacity:0;" +
        "transition:opacity .12s}" +
        ".game:hover .tools,.game:focus-within .tools{opacity:1}" +
        "@media (hover:none){.tools{opacity:1}}" +
        // Present in every tile, shown only on the running one via a class the poll toggles.
        // Qualified with the tag: a bare .running also matched the article and hid the tile.
        "span.running{display:none;position:absolute;right:.4rem;bottom:.4rem;" +
        "padding:.22rem .55rem;" +
        "border-radius:.4rem;font-size:15px;font-weight:600;letter-spacing:.02em;" +
        "background:var(--live);color:var(--bg);box-shadow:0 1px 4px rgba(0,0,0,.35)}" +
        ".game.running span.running{display:inline-block}" +
        ".tools button{padding:.3rem;line-height:0;border-radius:.35rem;" +
        "background:var(--glass);border:1px solid var(--button-line);backdrop-filter:blur(4px)}" +
        ".tools button:hover{background:var(--button-hover);border-color:var(--button-line-hover)}" +
        ".tools .danger:hover{border-color:var(--danger-line);background:var(--danger-bg)}" +
        // Centred on the poster and about three times the size of the small tools. Grey glass at
        // rest, like them, and only turns to danger red on approach.
        ".stop{display:none;position:absolute;top:50%;left:50%;transform:translate(-50%,-50%);" +
        "width:3.6rem;height:3.6rem;align-items:center;justify-content:center;padding:0;" +
        "border-radius:50%;border:1px solid var(--button-line);background:var(--glass);" +
        "color:var(--ink);backdrop-filter:blur(4px);opacity:0;" +
        "transition:opacity .12s,background .12s,border-color .12s,color .12s}" +
        // Filled, unlike the other outline icons — the inherited stroke of the same colour is
        // turned off here, or it blurs into the fill instead of reading as a crisp square.
        ".stop svg{width:28px;height:28px;fill:currentColor;stroke:none}" +
        ".game.running .stop{display:flex}" +
        ".game:hover .stop,.game:focus-within .stop{opacity:1}" +
        "@media (hover:none){.game.running .stop{opacity:1}}" +
        ".stop:hover{background:var(--danger-bg);border-color:var(--danger-line);" +
        "color:var(--danger-line)}" +
        "svg{width:15px;height:15px;fill:none;stroke:currentColor;stroke-width:1.8;" +
        "stroke-linecap:round;stroke-linejoin:round;display:block}" +
        "svg.big{width:30px;height:30px;stroke-width:1.5}" +

        // The tile that adds one, shaped like the posters it sits among but with nothing framing
        // it: no border, no background, on the same bare ground as everything else on the page.
        ".game.add{display:grid;place-content:center;gap:.4rem;justify-items:center;" +
        "aspect-ratio:3/4;border:0;background:none;color:var(--faint);cursor:pointer}" +
        ".game.add:hover{color:var(--add-hover-ink)}" +
        ".game.add span{font-size:12px}" +

        // The paired devices: rows rather than tiles, because a device is a name and two dates
        // and nothing worth a picture.
        "#clients:not(:empty){display:block;margin-top:2rem}" +
        "#clients h2{font-size:13px;font-weight:500;color:var(--dim);margin:0 0 .6rem}" +
        ".client{display:flex;align-items:center;gap:.6rem;padding:.55rem .75rem;" +
        "border:1px solid var(--line);border-radius:.45rem;margin-bottom:.4rem;" +
        "background:var(--panel)}" +
        ".client b{font-weight:500;font-size:13px}" +
        ".client .q{flex:1;font-size:11px;min-height:0}" +
        ".client button{padding:.3rem;line-height:0}" +

        // Buttons and fields, shared by the dialogs and the pairing card.
        "button,.upload{font:inherit;font-size:13px;padding:.35rem .8rem;border-radius:.35rem;" +
        "border:1px solid var(--button-line);background:var(--button);color:var(--ink);cursor:pointer}" +
        "button:hover,.upload:hover{background:var(--button-hover)}" +
        "button.primary{background:var(--primary);border-color:var(--primary-line)}" +
        "button.primary:hover{background:var(--primary-hover)}" +
        ".row{display:flex;gap:.4rem;flex-wrap:wrap;align-items:center}" +
        ".row.end{justify-content:flex-end;margin-top:.25rem}" +
        ".q{color:var(--dim);min-height:1.2em;margin:0}" +

        // The pairing card: green, because it is the one thing on this page that is urgent.
        ".card{border:1px solid var(--green-line);border-radius:.5rem;padding:1rem;margin-bottom:1.25rem}" +
        ".card.off{display:none}" +
        ".card label{display:block;font-size:12px;color:var(--dim);margin:.6rem 0}" +
        "#devname{display:block;width:min(24rem,100%);margin-top:.3rem;font:inherit;font-size:14px;" +
        "padding:.45rem .6rem;border-radius:.35rem;border:1px solid var(--field-line);background:var(--field);" +
        "color:var(--ink)}" +
        // Wide enough for four digits with room to spare: the letter spacing is charged after the
        // last digit as well, so a box measured to fit exactly hides the fourth.
        "#pin{display:block;font:inherit;font-size:2rem;width:10.5rem;margin-top:.3rem;padding:.4rem;" +
        "text-align:center;letter-spacing:.4em;border-radius:.4rem;border:1px solid var(--pin-line);" +
        "background:var(--pin-field);color:var(--pin-ink)}" +
        "#said{margin-left:.5rem}" +

        "dialog{border:1px solid var(--line);border-radius:.6rem;background:var(--panel);" +
        "color:var(--ink);padding:1.25rem;width:min(30rem,calc(100vw - 2rem))}" +
        "dialog::backdrop{background:var(--backdrop)}" +
        "dialog h2{font-size:1rem;margin:0 0 1rem}" +
        "dialog label{display:block;font-size:12px;color:var(--dim);margin-bottom:.9rem}" +
        // The browser's own [hidden] loses to the rule above on specificity, and a field that
        // will not hide is worse than one that was never written.
        "dialog label[hidden]{display:none}" +
        ".hint{color:var(--faint)}" +
        "dialog label.upload{display:inline-block;margin:0}" +
        // The one switch in the window: the box beside its words rather than above them, which is
        // what every other checkbox on this machine looks like.
        "dialog label.switch{display:flex;gap:.5rem;align-items:flex-start;cursor:pointer}" +
        "dialog label.switch input{width:auto;margin:.15rem 0 0}" +
        "dialog label.switch span{color:var(--ink)}" +
        "dialog input[type=text],dialog input:not([type]),dialog select{display:block;width:100%;" +
        "margin-top:.3rem;font:inherit;font-size:14px;padding:.45rem .6rem;border-radius:.35rem;" +
        "border:1px solid var(--field-line);background:var(--field);color:var(--ink)}" +

        // A field standing beside a button rather than on its own line: it takes what is left.
        ".row input{flex:1 1 8rem;width:auto;margin-top:0}" +
        ".row{margin-top:.3rem}" +

        // The picker: wider than the editor for four posters side by side, no taller than the
        // window, and four columns exactly whatever the width.
        "#picker{width:min(46rem,calc(100vw - 2rem));max-height:calc(100vh - 2rem)}" +
        // Only on the open form: any display on the element itself outranks the display:none a
        // browser gives a closed dialog, and the window then stands on the page, empty.
        "#picker[open]{display:flex;flex-direction:column}" +
        "#picker .row{margin-top:0}" +
        "#pickname{flex:1;font:inherit;font-size:14px;padding:.45rem .6rem;border-radius:.35rem;" +
        "border:1px solid var(--field-line);background:var(--field);color:var(--ink)}" +
        // minmax(0,1fr) rather than 1fr, and min-width on the item for the same reason: a plain
        // 1fr column may not become narrower than the widest picture, and the grid went off screen.
        "#pickgrid{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:.75rem;" +
        "overflow-y:auto;overflow-x:hidden;min-height:0;padding:.25rem .1rem;margin-top:.5rem}" +
        ".pick{cursor:pointer;background:none;border:0;padding:0;color:inherit;font:inherit;" +
        "text-align:left;min-width:0}" +
        ".pick img{width:100%;aspect-ratio:2/3;object-fit:cover;border-radius:.4rem;display:block;" +
        "border:2px solid transparent;background:var(--panel)}" +
        ".pick:hover img,.pick:focus-visible img{border-color:var(--live)}" +
        ".pick span{display:block;font-size:11px;color:var(--dim);margin-top:.3rem;overflow:hidden;" +
        "white-space:nowrap;text-overflow:ellipsis}" +

        // The log: a drawer along the bottom that grows upwards. The page keeps room for the bar
        // at all times, so nothing is ever hidden behind it.
        "#logbox{position:fixed;left:0;right:0;bottom:0;z-index:10;display:flex;" +
        "flex-direction:column;background:var(--log-bg);border-top:1px solid var(--line)}" +
        "#log{height:0;overflow:auto;margin:0;padding:0 1rem;font-size:12px;color:var(--log-ink);" +
        "white-space:pre-wrap;word-break:break-word;transition:height .18s ease}" +
        "#logbox.open #log{height:45vh;padding:1rem}" +
        "#logtoggle{border:0;border-radius:0;background:none;color:var(--dim);text-align:left;" +
        "padding:.5rem 1rem;width:100%}" +
        "#logtoggle:hover{background:var(--panel);color:var(--ink)}" +
        "#logtoggle::before{content:\"▲ \";font-size:9px;vertical-align:middle}" +
        "#logbox.open #logtoggle::before{content:\"▼ \"}" +
        "</style>";

    // The page keeps itself current without reloading: a reload would throw away half-typed
    // digits, and the one moment this page matters is while somebody is typing them.
    private const string Script =
        "<script>" +
        "const $=id=>document.getElementById(id);" +

        // --- pairing ---
        "const pin=$('pin'),devname=$('devname'),said=$('said'),card=$('pair'),who=$('who')," +
        "clients=$('clients');" +
        "async function reloadClients(){clients.innerHTML=await (await fetch('/?clients=1')).text();}" +
        "async function sendPin(){" +
        "if(!/^\\d{4}$/.test(pin.value)){said.textContent='A code is four digits.';pin.focus();return;}" +
        "said.textContent='sending…';" +
        "const r=await fetch('/?pin='+encodeURIComponent(pin.value)+'&name='+encodeURIComponent(devname.value));" +
        "said.textContent=await r.text();pin.value='';" +
        // The device appears once the client has finished its side. Asked for twice: the exchange
        // takes a few hundred milliseconds, and one reload can be too early.
        "setTimeout(reloadClients,1500);setTimeout(reloadClients,5000);}" +
        "$('pairsave').addEventListener('click',sendPin);" +
        // Cancel ends the attempt at the host's side. The box goes on its own a moment later,
        // when the poll below finds nothing waiting any more, so nothing is hidden here.
        "$('paircancel').addEventListener('click',async()=>{" +
        "said.textContent='cancelling…';pin.value='';" +
        "said.textContent=await (await fetch('/?cancelpair=1')).text();});" +
        "pin.addEventListener('keydown',e=>{if(e.key==='Enter')sendPin();});" +
        "devname.addEventListener('keydown',e=>{if(e.key==='Enter')pin.focus();});" +
        "let wasWaiting=!card.classList.contains('off');" +
        "setInterval(async()=>{" +
        "const name=await (await fetch('/?waiting=1')).text();" +
        "const wanted=name.length>0;" +
        "if(wanted!==wasWaiting){" +
        "card.classList.toggle('off',!wanted);" +
        "if(wanted){said.textContent='';devname.focus();}" +
        // The box has just gone: the attempt ended, one way or the other, and the list below is
        // where the outcome shows.
        "else reloadClients();" +
        "wasWaiting=wanted;}" +
        "if(wanted)who.innerHTML='A device calling itself <b>'+name.replace(/&/g,'&amp;').replace(/</g,'&lt;')+'</b> wants to pair with this machine.';" +
        "const both=(await (await fetch('/?status=1')).text()).split('\\n');" +
        "$('host').innerHTML=both[0];$('status').innerHTML=both[1]||'';" +
        // The badge follows without a page reload: every tile's class is set from the one row
        // the server says is running, which is cheap enough to do on every tick of this poll.
        "document.querySelectorAll('#games .game').forEach(el=>" +
        "el.classList.toggle('running',el.dataset.id===(both[2]||'')));" +
        // The palette follows the machine, not the browser: the attribute the stylesheet keys on
        // is refreshed with the rest, so a theme flipped in Settings reaches the page in a second.
        "const theme=(await (await fetch('/?theme=1')).text()).trim();" +
        "if(theme&&document.documentElement.dataset.theme!==theme)document.documentElement.dataset.theme=theme;" +
        "},1000);" +
        "if(!card.classList.contains('off'))devname.focus();" +

        // --- the log drawer ---
        "const logbox=$('logbox'),log=$('log');" +
        "$('logtoggle').addEventListener('click',()=>{logbox.classList.toggle('open');" +
        "if(logbox.classList.contains('open'))log.scrollTop=log.scrollHeight;});" +
        // Only while it is open: sixty-four kilobytes every three seconds into a shut drawer is
        // work for nobody.
        "setInterval(async()=>{if(!logbox.classList.contains('open'))return;" +
        "const atEnd=log.scrollTop+log.clientHeight>=log.scrollHeight-8;" +
        "log.textContent=await (await fetch('/?log=1')).text();" +
        "if(atEnd)log.scrollTop=log.scrollHeight;},3000);" +

        // --- the editor ---
        "const games=$('games'),editor=$('editor'),title=$('title'),command=$('command'),folder=$('folder')," +
        "editorsaid=$('editorsaid'),coverbox=$('coverbox'),arturl=$('arturl');" +
        "let editing=0;" +
        "async function reload(){games.innerHTML=await (await fetch('/?games=1')).text();}" +
        // Rescan. The answer says only that the scan started, so the list is fetched twice
        // afterwards: a library of a hundred games takes a few seconds.
        "const scansaid=$('scansaid');" +
        "$('rescan').addEventListener('click',async e=>{const b=e.target;b.disabled=true;" +
        "scansaid.textContent=await (await fetch('/?rescan=1')).text();" +
        "setTimeout(reload,2000);" +
        "setTimeout(()=>{reload();scansaid.textContent='';b.disabled=false;},6000);});" +
        "function open(id,name,starts,from,pointerOn,level,cardOn){editing=id;" +
        "$('editortitle').textContent=id?'Edit game':'Add a game';" +
        "title.value=name||'';command.value=starts||'';folder.value=from||'';" +
        "quality.value=level===undefined?2:level;" +
        "if(window.pointer)pointer.checked=pointerOn==='1';" +
        "startcard.checked=cardOn!=='0';" +
        "editorsaid.textContent='';" +
        // A game that does not exist yet has nowhere to put a cover, so that half of the window is
        // shown only once there is a row to attach one to.
        "coverbox.style.display=id?'':'none';" +
        "arturl.value='';" +
        "editor.showModal();title.focus();}" +
        "$('cancel').addEventListener('click',()=>editor.close());" +
        "$('save').addEventListener('click',async()=>{" +
        "if(!title.value.trim()||!command.value.trim()){" +
        "editorsaid.textContent='A game needs a name and something to start.';return;}" +
        "const r=await fetch('/?save='+editing+'&title='+encodeURIComponent(title.value)+" +
        "'&command='+encodeURIComponent(command.value)+" +
        "'&folder='+encodeURIComponent(folder.value)+" +
        "'&pointer='+(window.pointer&&pointer.checked?1:0)+'&quality='+quality.value+" +
        "'&card='+(startcard.checked?1:0));" +
        "editorsaid.textContent=await r.text();await reload();editor.close();});" +
        // --- the cover picker ---
        // Searched at once for the editor's name; a portrait that does not exist falls back once.
        "const picker=$('picker'),pickname=$('pickname'),pickgrid=$('pickgrid')," +
        "picksaid=$('picksaid');" +
        "async function search(){const name=pickname.value.trim();" +
        "if(!name){picksaid.textContent='Type a name to look for.';pickname.focus();return;}" +
        "picksaid.textContent='looking…';pickgrid.innerHTML='';" +
        "const found=await (await fetch('/?artlist=1&title='+encodeURIComponent(name))).json();" +
        "const none='Nothing with a picture was found under \"'+name+'\". " +
        "Try the name a store would use.';" +
        "if(!found.length){picksaid.textContent=none;return;}" +
        // Counted again whenever one drops out, because the count is written before a single
        // picture has loaded and the answer is only true once they have.
        "const count=()=>{const n=pickgrid.children.length;" +
        "picksaid.textContent=n?n+' found — click the right one':none;};" +
        "count();" +
        "for(const c of found){const b=document.createElement('button');b.type='button';b.className='pick';" +
        "b.title=c.name;" +
        "const img=document.createElement('img');img.loading='lazy';img.alt='';img.src=c.src;" +
        // Out of the window rather than shown as an empty frame: an entry whose portrait answers
        // 404 has no cover to choose, and there is nothing else here worth offering instead.
        "img.onerror=()=>{b.remove();count();};" +
        "const cap=document.createElement('span');cap.textContent=c.name;" +
        "b.append(img,cap);pickgrid.append(b);}" +
        "count();}" +
        "$('find').addEventListener('click',()=>{pickname.value=title.value.trim();" +
        "picksaid.textContent='';pickgrid.innerHTML='';picker.showModal();search();});" +
        "$('picksearch').addEventListener('click',search);" +
        "pickname.addEventListener('keydown',e=>{if(e.key==='Enter'){e.preventDefault();search();}});" +
        // A click on a picture chooses it; a click on the backdrop — the dialog element itself,
        // outside its box — closes the window.
        "pickgrid.addEventListener('click',async e=>{const b=e.target.closest('.pick');if(!b)return;" +
        "const img=b.querySelector('img');if(!img)return;" +
        "picksaid.textContent='fetching…';" +
        // The address of the picture on screen, not the entry's number: after the fallback above
        // the two can differ, and what is stored should be the picture that was clicked.
        "const r=await fetch('/?arturl='+editing+'&url='+" +
        "encodeURIComponent(img.currentSrc||img.src));" +
        "editorsaid.textContent=await r.text();picker.close();await reload();});" +
        "picker.addEventListener('click',e=>{if(e.target===picker)picker.close();});" +
        "$('fetch').addEventListener('click',async()=>{" +
        "editorsaid.textContent='fetching…';" +
        "const r=await fetch('/?arturl='+editing+'&url='+encodeURIComponent(arturl.value));" +
        "editorsaid.textContent=await r.text();await reload();});" +
        "$('file').addEventListener('change',async e=>{" +
        "if(!e.target.files.length)return;editorsaid.textContent='uploading…';" +
        "const r=await fetch('/?upload='+editing,{method:'POST',body:e.target.files[0]});" +
        "editorsaid.textContent=await r.text();e.target.value='';await reload();});" +

        // Every tile is handled here rather than each on its own, so that the grid can be replaced
        // whole after a change without anything being wired up again.
        "games.addEventListener('click',async e=>{" +
        "if(e.target.closest('#addtile')){open(0);return;}" +
        "const button=e.target.closest('button[data-do]');if(!button)return;" +
        "const tile=button.closest('.game');" +
        "if(button.dataset.do==='edit'){open(tile.dataset.id,tile.dataset.title,tile.dataset.command," +
        "tile.dataset.folder,tile.dataset.pointer,tile.dataset.quality,tile.dataset.card);return;}" +
        "if(button.dataset.do==='remove'){" +
        "if(!confirm('Remove '+tile.dataset.title+' from the list?'))return;" +
        "await fetch('/?remove='+tile.dataset.id);await reload();return;}" +
        "if(button.dataset.do==='stop'){" +
        "if(!confirm('Stop '+tile.dataset.title+'?'))return;" +
        "await fetch('/?stop=1');await reload();}});" +

        // The paired devices, the same way. Forgetting one is asked about first: it is the one
        // thing here that somebody else has to undo, by pairing their device again.
        "clients.addEventListener('click',async e=>{" +
        "const button=e.target.closest('button[data-do=forget]');if(!button)return;" +
        "const row=button.closest('.client');" +
        "if(!confirm('Forget '+row.dataset.name+'? It will have to pair again.'))return;" +
        "await fetch('/?forget='+row.dataset.id);" +
        "await reloadClients();});" +
        "</script>";

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener?.Stop();

        try
        {
            if (_accepting is not null) await _accepting.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
        }

        _stopping.Dispose();
    }
}
