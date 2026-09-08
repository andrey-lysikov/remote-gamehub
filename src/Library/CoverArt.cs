//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Net.Http;
using System.Text.Json;
using RemoteGameHub.App;

namespace RemoteGameHub.Library;

// The pictures shown beside each game's name: what little is on this machine, then Steam's public
// catalogue, then GameDB. Runs in the background after the listeners open, never at start.
internal static class CoverArt
{
    // How many of the catalogue's answers the picker is offered. Each one costs a request for the
    // record behind it, and a page of a hundred portraits is not a choice but a wall.
    private const int MostFromCatalogue = 8;

    // A game searched for and not found is not searched for again for 30 days: a delay, not a
    // refusal, because titles do appear later.
    private static readonly TimeSpan RetryAfter = TimeSpan.FromDays(30);

    // Nothing here is urgent, and a server working through fifty titles as fast as it can looks
    // like something worth rate-limiting.
    private static readonly TimeSpan BetweenRequests = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    // Short names match too many things; below this length nothing is searched for.
    private const int ShortestSearchableTitle = 4;

    internal const string Folder = "covers";

    // 3:4, the box art shape moonlight-qt and moonlight-android size their own grids to. A cover
    // of another shape — Steam's is 2:3 — is padded to this (PadToAspect), never cropped.
    private const double CoverAspect = 3.0 / 4.0;

    // Finds and stores the missing pictures. Never throws: a picture is the least important thing
    // this server has, and no failure here may disturb a stream.
    internal static async Task FetchAsync(GameLibrary library, string directory, AppConfig config,
                                          CancellationToken cancel)
    {
        var folder = Path.Combine(directory, Folder);

        try
        {
            Directory.CreateDirectory(folder);
            Prune(library, folder);

            var forgotten = library.ForgetMissingArtwork();
            if (forgotten > 0)
                Log.Info($"{forgotten} cover picture(s) were gone from disk and will be looked " +
                         "up again");

            if (!config.GamesArtwork) return;

            var candidates = library.NeedingArtwork(RetryAfter);
            if (candidates.Count == 0) return;

            Log.Info($"looking for cover art for {candidates.Count} game(s)");

            using var http = new HttpClient { Timeout = RequestTimeout };
            // Some of Steam's front ends answer a request with no agent string with a redirect to
            // a page rather than the data.
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"{AppParameters.Identity.FileBase}/{Program.Version}");

            // One catalogue for the whole pass: it keeps the lists it reads, and fifty titles
            // beginning with the same two letters are then one download rather than fifty.
            var catalogue = new GameDb(http);

            var found = 0;
            foreach (var candidate in candidates)
            {
                if (cancel.IsCancellationRequested) return;

                var path = await FetchOneAsync(http, catalogue, candidate, folder, config, cancel);
                library.RecordArtwork(candidate.Id, path);

                if (path is not null)
                {
                    found++;
                    Log.Info($"    cover art found for \"{candidate.Title}\"");
                }

                await Task.Delay(BetweenRequests, cancel);
            }

            Log.Info($"cover art: {found} of {candidates.Count} game(s) now have a picture");
        }
        catch (OperationCanceledException)
        {
            // The server is closing. Whatever was found is already recorded.
        }
        catch (Exception error)
        {
            Log.Info($"the search for cover art stopped: {error.GetType().Name}: {error.Message}");
        }
    }

    // One answer, as the page shows it: the name and where its portrait is. Only the portrait —
    // the catalogue's wide headers among the covers filled the picker with the wrong shape.
    internal sealed record Candidate(string Name, string Address);

    // Everything the store answers for a title, unfiltered, for a person to choose from; the
    // automatic search filters because nobody is watching it.
    internal static async Task<IReadOnlyList<Candidate>> SearchAllAsync(string title, AppConfig config,
                                                                        CancellationToken cancel)
    {
        var found = new List<Candidate>();
        if (title.Trim().Length == 0) return found;

        using var http = new HttpClient { Timeout = RequestTimeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"{AppParameters.Identity.FileBase}/{Program.Version}");

        try
        {
            var address = $"{AppParameters.Artwork.SteamSearch}?term={Uri.EscapeDataString(title)}&cc=us&l=en";

            using var response = await http.GetAsync(address, cancel);
            if (!response.IsSuccessStatusCode)
            {
                Log.Info($"the cover search answered {(int)response.StatusCode} for \"{title}\"");
                return found;
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancel);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: cancel);

            if (!json.RootElement.TryGetProperty("items", out var items)) return found;

            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var type) || type.GetString() != "app") continue;
                if (!item.TryGetProperty("id", out var id)) continue;
                if (!item.TryGetProperty("name", out var name)) continue;

                var appId = id.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);

                found.Add(new Candidate(name.GetString() ?? string.Empty,
                                        $"{AppParameters.Artwork.SteamPictures}{appId}/library_600x900.jpg"));
            }
        }
        catch (Exception error)
        {
            Log.Info($"the cover search for \"{title}\" failed: {error.Message}");
        }

        // The other catalogue after the store's answers rather than mixed among them: the store
        // knows what people mostly stream, and this one knows the rest.
        try
        {
            found.AddRange(await new GameDb(http).SearchAllAsync(title, MostFromCatalogue, cancel));
        }
        catch (Exception error)
        {
            Log.Info($"the catalogue search for \"{title}\" failed: {error.Message}");
        }

        return found;
    }

    // Fetches a picture from an address: one typed, or the one behind the picture clicked in the
    // picker. Returns what the page should say; the format is converted, not trusted.
    internal static async Task<string> FetchFromUrlAsync(GameLibrary library, long gameId,
                                                         string address, string directory,
                                                         CancellationToken cancel)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "That is not an http or https address.";
        }

        try
        {
            using var http = new HttpClient { Timeout = RequestTimeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"{AppParameters.Identity.FileBase}/{Program.Version}");

            using var response = await http.GetAsync(uri, cancel);
            if (!response.IsSuccessStatusCode)
                return $"The address answered {(int)response.StatusCode}.";

            var bytes = await response.Content.ReadAsByteArrayAsync(cancel);
            if (bytes.Length == 0) return "The address answered with nothing.";

            return Store(library, gameId, directory, bytes)
                ? "Fetched."
                : "What came back is not a picture this machine can read.";
        }
        catch (Exception error)
        {
            Log.Info($"a cover could not be fetched from {uri}: {error.Message}");
            return $"It could not be fetched: {error.Message}";
        }
    }

    // Stores a picture somebody chose, as PNG; false when the bytes are not a picture at all.
    internal static bool Store(GameLibrary library, long gameId, string directory, byte[] picture)
    {
        var folder = Path.Combine(directory, Folder);
        Directory.CreateDirectory(folder);

        var png = ToPng(picture);
        if (png is null) return false;

        var path = Path.Combine(folder, $"{gameId}.png");
        File.WriteAllBytes(path, png);

        // A cover from before every picture here was written in one format.
        var other = Path.Combine(folder, $"{gameId}.jpg");
        if (File.Exists(other)) File.Delete(other);

        // By hand, always: everything that reaches Store came from the page — uploaded, fetched
        // from an address, or clicked in the picker. The background search writes its own file.
        library.RecordArtwork(gameId, path, byHand: true);
        Log.Info($"a cover of {png.Length} bytes was stored for game {gameId}");
        return true;
    }

    // Any picture Windows can decode, re-expressed as a PNG since that is the one format every
    // Moonlight client's box art reader is built to expect; null when it cannot be decoded.
    private static byte[]? ToPng(byte[] picture)
    {
        try
        {
            using var source = new MemoryStream(picture);
            using var image = System.Drawing.Image.FromStream(source);
            using var padded = PadToAspect(image, CoverAspect);

            using var output = new MemoryStream();
            padded.Save(output, System.Drawing.Imaging.ImageFormat.Png);
            return output.ToArray();
        }
        catch (Exception error)
        {
            Log.Info($"a picture could not be converted: {error.GetType().Name}: {error.Message}");
            return null;
        }
    }

    // Adds transparent margin on whichever side is short, so the canvas is exactly the target
    // aspect and every pixel of the original survives untouched at its native resolution.
    internal static System.Drawing.Bitmap PadToAspect(System.Drawing.Image original, double aspect)
    {
        var width = original.Width;
        var height = original.Height;

        var canvasWidth = width;
        var canvasHeight = height;

        if (width / (double)height > aspect)
            canvasHeight = (int)Math.Round(width / aspect);
        else
            canvasWidth = (int)Math.Round(height * aspect);

        var canvas = new System.Drawing.Bitmap(canvasWidth, canvasHeight,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        using var g = System.Drawing.Graphics.FromImage(canvas);
        g.Clear(System.Drawing.Color.Transparent);
        g.DrawImage(original, (canvasWidth - width) / 2, (canvasHeight - height) / 2, width, height);

        return canvas;
    }

    private static async Task<string?> FetchOneAsync(HttpClient http, GameDb catalogue,
                                                     ArtworkCandidate candidate,
                                                     string folder, AppConfig config,
                                                     CancellationToken cancel)
    {
        try
        {
            // A Steam game knows its own number, which is exact where a name match is a guess.
            var appId = candidate.Source == "steam" && candidate.ExternalId is not null
                ? candidate.ExternalId
                : await SearchAsync(http, candidate.Title, cancel);

            var picture = appId is null
                ? null
                : await DownloadAsync(http, appId, cancel);

            // Then the catalogue, which is where a game the store never sold is found.
            picture ??= await FromCatalogueAsync(http, catalogue, candidate.Title, cancel);

            if (picture is null) return null;

            var png = ToPng(picture);
            if (png is null) return null;

            // Named by the row rather than by the title: a title can contain anything a file name
            // cannot, and the row is what the request will arrive with anyway.
            var path = Path.Combine(folder, $"{candidate.Id}.png");
            await File.WriteAllBytesAsync(path, png, cancel);
            return path;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            // One title that cannot be found must not stop the rest.
            Log.Info($"no cover art for \"{candidate.Title}\": {error.Message}");
            return null;
        }
    }

    // Looks a title up in the store's search. The first answer is taken only when it plausibly is
    // the same game: the search returns something for almost any word.
    private static async Task<string?> SearchAsync(HttpClient http, string title,
                                                   CancellationToken cancel)
    {
        var wanted = Normalise(title);
        if (wanted.Length < ShortestSearchableTitle) return null;

        var address = $"{AppParameters.Artwork.SteamSearch}?term={Uri.EscapeDataString(title)}&cc=us&l=en";

        using var response = await http.GetAsync(address, cancel);
        if (!response.IsSuccessStatusCode) return null;

        await using var body = await response.Content.ReadAsStreamAsync(cancel);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: cancel);

        if (!json.RootElement.TryGetProperty("items", out var items)) return null;

        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var type) || type.GetString() != "app") continue;
            if (!item.TryGetProperty("id", out var id)) continue;
            if (!item.TryGetProperty("name", out var name)) continue;

            var candidate = Normalise(name.GetString() ?? string.Empty);
            if (candidate.Length == 0) continue;

            // Exact after normalising, or one name begins the other: "Hollow Knight" against
            // "Hollow Knight: Silksong", or a folder name with a version suffix against the title.
            if (candidate == wanted ||
                candidate.StartsWith(wanted, StringComparison.Ordinal) ||
                wanted.StartsWith(candidate, StringComparison.Ordinal))
            {
                return id.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    // The best the catalogue has for a title, already fetched. Short names are not looked for at
    // all: a bucket holds thousands, and three letters match half of them.
    private static async Task<byte[]?> FromCatalogueAsync(HttpClient http, GameDb catalogue,
                                                          string title, CancellationToken cancel)
    {
        if (Normalise(title).Length < ShortestSearchableTitle) return null;

        var address = await catalogue.CoverAddressAsync(title, cancel);
        if (address is null) return null;

        using var response = await http.GetAsync(address, cancel);
        if (!response.IsSuccessStatusCode) return null;

        var bytes = await response.Content.ReadAsByteArrayAsync(cancel);
        return bytes.Length > 0 ? bytes : null;
    }

    // The portrait, or the wide header when a game has none: some older titles never got one, and
    // a picture of the wrong shape beats a blank tile.
    private static async Task<byte[]?> DownloadAsync(HttpClient http, string appId,
                                                     CancellationToken cancel)
    {
        foreach (var name in new[] { "library_600x900.jpg", "header.jpg" })
        {
            using var response = await http.GetAsync($"{AppParameters.Artwork.SteamPictures}{appId}/{name}", cancel);
            if (!response.IsSuccessStatusCode) continue;

            var bytes = await response.Content.ReadAsByteArrayAsync(cancel);
            if (bytes.Length > 0) return bytes;
        }

        return null;
    }

    // Titles compared without what differs for no reason: case, spacing, punctuation and the
    // trademark marks stores put in names and installers do not.
    internal static string Normalise(string title)
    {
        var text = new System.Text.StringBuilder(title.Length);

        foreach (var c in title)
        {
            if (char.IsLetterOrDigit(c)) text.Append(char.ToLowerInvariant(c));
        }

        return text.ToString();
    }

    // Deletes stored pictures no game claims any more; uninstalled games would otherwise leave
    // their pictures behind for the life of the folder.
    private static void Prune(GameLibrary library, string folder)
    {
        var kept = new HashSet<string>(library.ArtworkPaths(), StringComparer.OrdinalIgnoreCase);
        var removed = 0;

        foreach (var file in Directory.EnumerateFiles(folder))
        {
            if (kept.Contains(file)) continue;

            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception)
            {
                // It will be tried again at the next start; a picture that will not delete is not
                // worth a word in the log.
            }
        }

        if (removed > 0) Log.Info($"{removed} cover picture(s) belonged to no game and were removed");
    }
}
