//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace RemoteGameHub.App;

// Asks GitHub whether a newer release exists: one check a few minutes after the start, one a day
// after that. Nothing is downloaded — the answer is a version number and the page to get it from.
internal sealed class UpdateChecker : IDisposable
{
    // GitHub refuses a request without a User-Agent, and the header is also how the traffic is
    // recognised on their side.
    private static readonly HttpClient Http = new() { Timeout = AppParameters.Updates.RequestTimeout };

    static UpdateChecker()
    {
        Http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue(AppParameters.Identity.FileBase, Program.Version));

        // Without it the API answers with whatever it feels like; this pins the shape of the JSON.
        Http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    private readonly CancellationTokenSource _stopping = new();
    private volatile string? _newer;
    private volatile string? _link;

    // The newer version, once one is known; null until then.
    internal string? Newer => _newer;

    // The page of that release, as GitHub named it; the fixed "latest" address until one is known.
    internal string Link => _link ?? AppParameters.Links.LatestRelease;

    // Raised once per newer version found — not at every daily check that finds the same one
    // again, because a balloon a day about the same release is nagging.
    internal event Action<string>? Found;

    // Starts the checks on a thread of their own. Nothing is asked right away.
    internal void Start()
    {
        _ = Task.Run(async () =>
        {
            var wait = AppParameters.Updates.FirstCheckDelay;

            while (!_stopping.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(wait, _stopping.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                await CheckAsync();
                wait = AppParameters.Updates.CheckPeriod;
            }
        });
    }

    // One check. Returns the newer version, or null when there is none or the question could not
    // be asked — no network, a rate limit, a rewritten API.
    internal async Task<string?> CheckAsync()
    {
        try
        {
            using var answer = await Http.GetAsync(AppParameters.Links.LatestReleaseApi, _stopping.Token);

            // Nothing published yet: that is not a failure, it is "no updates".
            if (answer.StatusCode == HttpStatusCode.NotFound)
            {
                Log.Info("update check: no releases published yet");
                return null;
            }

            answer.EnsureSuccessStatusCode();
            var json = await answer.Content.ReadAsStringAsync(_stopping.Token);

            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("tag_name", out var tag)) return null;

            var latest = (tag.GetString() ?? string.Empty).TrimStart('v', 'V');
            if (latest.Length == 0) return null;

            // The release's own page, checked against the project: an answer from anywhere else
            // (a renamed or redirected repository) is not news about this server and is dropped.
            var page = document.RootElement.TryGetProperty("html_url", out var url) ? url.GetString() : null;
            if (page is not null &&
                !page.StartsWith(AppParameters.Links.Project + "/", StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn($"update check: {AppParameters.Links.LatestReleaseApi} answered with a " +
                         $"release of another project ({page}); it is ignored");
                return null;
            }

            var current = Program.Version;
            Log.Info($"update check: running {current}, latest {latest} ({page ?? "no page named"})");

            if (!IsNewer(latest, current)) return null;

            if (_newer != latest)
            {
                _newer = latest;
                _link = page;
                Found?.Invoke(latest);
            }

            return latest;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception error)
        {
            Log.Info($"the update check did not go through: {error.Message}");
            return null;
        }
    }

    // Compared as numbers rather than as text: "0.10" is newer than "0.9", though it sorts
    // before it. Anything that will not parse is treated as no news.
    internal static bool IsNewer(string latest, string current) =>
        Version.TryParse(latest, out var l) &&
        Version.TryParse(current, out var c) &&
        l > c;

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }
}
