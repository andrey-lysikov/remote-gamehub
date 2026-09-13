//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text.RegularExpressions;
using RemoteGameHub.App;

namespace RemoteGameHub.Protocol;

// The page's markup, styles and script, read out of the executable they are built into. They were
// string literals in WebConsole until they outgrew it: nothing checked their syntax, a mistake in
// the script showed up as silence in a browser rather than at build time, and three quarters of
// that file was markup. They are still shipped inside the one exe, as embedded resources.
internal static partial class WebAssets
{
    // Web/page.html is one document with the whole page in it, followed by the pieces that are
    // only sometimes drawn, each after a marker of its own.
    [GeneratedRegex(@"<!--part:([a-z]+)-->")]
    private static partial Regex PartMarker { get; }

    // What the server fills in, spelled {{name}} wherever it goes.
    [GeneratedRegex(@"\{\{([a-z]+)\}\}")]
    private static partial Regex Slot { get; }

    private static readonly Lazy<string> StyleText = new(() => Read("page.css"));
    private static readonly Lazy<string> ScriptText = new(() => Read("page.js"));
    private static readonly Lazy<Dictionary<string, string>> PageParts = new(ReadParts);

    internal static string Style => StyleText.Value;
    internal static string Script => ScriptText.Value;

    // One piece of the page: "page" is the whole document, the rest are the parts that depend on
    // how this machine is set up.
    internal static string Part(string name)
    {
        if (PageParts.Value.TryGetValue(name, out var part)) return part;

        Log.Error($"the page has no part called \"{name}\"; that much of it is missing. This is a " +
                  "fault in this build, not something a setting can change.");
        return string.Empty;
    }

    // Puts the values into their slots, in one pass: what goes in is never read for slots of its
    // own, so a game called "{{log}}" is a game called "{{log}}" and nothing else.
    internal static string Fill(string template, params (string Name, string Value)[] values) =>
        Slot.Replace(template, match =>
        {
            foreach (var (name, value) in values)
            {
                if (name == match.Groups[1].Value) return value;
            }

            Log.Warn($"the page asks for {{{{{match.Groups[1].Value}}}}} and nothing fills it; " +
                     "that much of the page is left empty");
            return string.Empty;
        });

    // The names a piece of the page asks to have filled. Only the tests call this: what it
    // answers is the contract between Web/page.html and the code that fills it, and a slot added
    // to the file and to nothing else should fail a build rather than leave a hole in the page.
    internal static IReadOnlyList<string> SlotsIn(string template) =>
        Slot.Matches(template).Select(match => match.Groups[1].Value).Distinct().ToArray();

    private static Dictionary<string, string> ReadParts()
    {
        var text = Read("page.html");
        var parts = new Dictionary<string, string>(StringComparer.Ordinal);
        var markers = PartMarker.Matches(text);

        for (var i = 0; i < markers.Count; i++)
        {
            var from = markers[i].Index + markers[i].Length;
            var to = i + 1 < markers.Count ? markers[i + 1].Index : text.Length;

            parts[markers[i].Groups[1].Value] = text[from..to].Trim();
        }

        return parts;
    }

    // The files are embedded under one name each, set in the csproj. A missing one is a build that
    // went wrong rather than anything a person did, and is said plainly rather than thrown: the
    // rest of the server — the streaming, which is the point of it — is unaffected.
    private static string Read(string name)
    {
        var resource = "RemoteGameHub.Web." + name;

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
        if (stream is null)
        {
            Log.Error($"the page's {name} is not in this executable ({resource}); the page is " +
                      "drawn without it. Everything else works.");
            return string.Empty;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
