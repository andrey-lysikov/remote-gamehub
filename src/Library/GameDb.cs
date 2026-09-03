//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Net.Http;
using System.Text.Json;
using RemoteGameHub.App;

namespace RemoteGameHub.Library;

// The second catalogue: LizardByte's GameDB, which is IGDB published as static files and so needs
// no key and no account. Held for one pass of the search — a bucket of names is up to a megabyte.
internal sealed class GameDb
{
    // One name as a bucket lists it.
    private sealed record Entry(long Id, string Name);

    private readonly HttpClient _http;
    private readonly Dictionary<string, IReadOnlyList<Entry>> _buckets =
        new(StringComparer.OrdinalIgnoreCase);

    internal GameDb(HttpClient http) => _http = http;

    // The best match's cover, for the search nobody watches: the same rule the store's side uses,
    // an exact name after normalising or one name beginning the other.
    internal async Task<string?> CoverAddressAsync(string title, CancellationToken cancel)
    {
        var wanted = CoverArt.Normalise(title);
        if (wanted.Length == 0) return null;

        foreach (var entry in await MatchesAsync(title, cancel))
        {
            var name = CoverArt.Normalise(entry.Name);
            if (name == wanted ||
                name.StartsWith(wanted, StringComparison.Ordinal) ||
                wanted.StartsWith(name, StringComparison.Ordinal))
            {
                var cover = await CoverOfAsync(entry.Id, cancel);
                if (cover is not null) return cover;
            }
        }

        return null;
    }

    // Everything that looks like the title, for a person to choose from. Each answer costs one
    // small request for the record, so the list is cut before they are asked for.
    internal async Task<IReadOnlyList<CoverArt.Candidate>> SearchAllAsync(string title, int most,
                                                                          CancellationToken cancel)
    {
        var found = new List<CoverArt.Candidate>();

        foreach (var entry in (await MatchesAsync(title, cancel)).Take(most))
        {
            var cover = await CoverOfAsync(entry.Id, cancel);
            if (cover is not null) found.Add(new CoverArt.Candidate(entry.Name, cover));
        }

        return found;
    }

    // The bucket's names that contain the title, closest first: the whole name, then one that
    // begins with it, then one that merely holds it somewhere.
    private async Task<IReadOnlyList<Entry>> MatchesAsync(string title, CancellationToken cancel)
    {
        var wanted = CoverArt.Normalise(title);
        if (wanted.Length == 0) return Array.Empty<Entry>();

        var bucket = await BucketAsync(BucketOf(title), cancel);

        return bucket
            .Select(entry => (Entry: entry, Name: CoverArt.Normalise(entry.Name)))
            .Where(pair => pair.Name.Length > 0 &&
                           (pair.Name.Contains(wanted, StringComparison.Ordinal) ||
                            wanted.StartsWith(pair.Name, StringComparison.Ordinal)))
            .OrderBy(pair => pair.Name == wanted ? 0
                           : pair.Name.StartsWith(wanted, StringComparison.Ordinal) ? 1 : 2)
            .ThenBy(pair => pair.Name.Length)
            .Select(pair => pair.Entry)
            .ToList();
    }

    // Which file a name is listed in: its first two letters or digits, lowered. A name whose
    // second character is a space is filed under the first alone, and anything else under "@".
    private static string BucketOf(string title)
    {
        var name = title.Trim();
        if (name.Length == 0 || !char.IsLetterOrDigit(name[0])) return "@";

        var first = char.ToLowerInvariant(name[0]);
        if (name.Length == 1 || name[1] == ' ') return first.ToString();

        return char.IsLetterOrDigit(name[1]) ? $"{first}{char.ToLowerInvariant(name[1])}" : "@";
    }

    // One bucket, read once. A bucket that answers with anything but names is remembered as empty
    // rather than asked for again for every title beginning the same way.
    private async Task<IReadOnlyList<Entry>> BucketAsync(string bucket, CancellationToken cancel)
    {
        if (_buckets.TryGetValue(bucket, out var already)) return already;

        var entries = new List<Entry>();

        try
        {
            var address = $"{AppParameters.Artwork.GameDbBuckets}{Uri.EscapeDataString(bucket)}.json";

            using var response = await _http.GetAsync(address, cancel);
            if (response.IsSuccessStatusCode)
            {
                await using var body = await response.Content.ReadAsStreamAsync(cancel);
                using var json = await JsonDocument.ParseAsync(body, cancellationToken: cancel);

                foreach (var item in json.RootElement.EnumerateObject())
                {
                    if (!long.TryParse(item.Name, out var id)) continue;
                    if (!item.Value.TryGetProperty("name", out var name)) continue;

                    var text = name.GetString();
                    if (!string.IsNullOrEmpty(text)) entries.Add(new Entry(id, text));
                }
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            Log.Info($"the catalogue's \"{bucket}\" list could not be read: {error.Message}");
        }

        _buckets[bucket] = entries;
        return entries;
    }

    // The picture of one record, at the size a client draws rather than the thumbnail the record
    // names. The address has no scheme in the file — it begins at the host.
    private async Task<string?> CoverOfAsync(long id, CancellationToken cancel)
    {
        try
        {
            var address = $"{AppParameters.Artwork.GameDbGames}{id}.json";

            using var response = await _http.GetAsync(address, cancel);
            if (!response.IsSuccessStatusCode) return null;

            await using var body = await response.Content.ReadAsStreamAsync(cancel);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: cancel);

            if (!json.RootElement.TryGetProperty("cover", out var cover) ||
                !cover.TryGetProperty("url", out var url))
            {
                return null;
            }

            var picture = url.GetString();
            if (string.IsNullOrEmpty(picture)) return null;

            if (picture.StartsWith("//", StringComparison.Ordinal)) picture = "https:" + picture;

            return picture.Replace(AppParameters.Artwork.GameDbThumbSize,
                                   AppParameters.Artwork.GameDbCoverSize, StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            Log.Info($"the catalogue's record {id} could not be read: {error.Message}");
            return null;
        }
    }
}
