//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace RemoteGameHub.App;

// A plain [Section] / key = value file. Chosen over JSON so that every parameter can carry
// a comment above it explaining what it does, and it must stay hand-editable.
internal sealed class ConfFile
{
    // Section and key names are compared case-insensitively: the file is typed by a person.
    private readonly Dictionary<string, Dictionary<string, string>> _sections =
        new(StringComparer.OrdinalIgnoreCase);

    // Keys asked for and not found, in order — settings added since the file was last written,
    // not ones left blank on purpose. Read() asks for all of them, so this ends up the whole gap.
    private readonly List<(string Section, string Key)> _missing = new();

    internal IReadOnlyList<(string Section, string Key)> MissingKeys => _missing;

    internal static ConfFile Parse(string text)
    {
        var file = new ConfFile();
        Dictionary<string, string>? section = null;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var number = i + 1;
            var line = lines[i].Trim();

            if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                continue;

            if (line[0] == '[')
            {
                var close = line.IndexOf(']');
                if (close < 0)
                    throw new FormatException($"Line {number}: section \"{line}\" has no closing bracket.");

                var name = line[1..close].Trim();
                if (name.Length == 0)
                    throw new FormatException($"Line {number}: the section name is empty.");

                if (!file._sections.TryGetValue(name, out section))
                {
                    section = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    file._sections[name] = section;
                }
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals < 0)
                throw new FormatException($"Line {number}: \"{line}\" is neither a section nor a key = value pair.");

            var key = line[..equals].Trim();
            if (key.Length == 0)
                throw new FormatException($"Line {number}: the key is empty.");

            // A value before the first section has no place to go, and guessing one would put it
            // somewhere the user did not mean.
            if (section is null)
                throw new FormatException($"Line {number}: \"{key}\" appears before any section.");

            section[key] = line[(equals + 1)..].Trim();
        }

        return file;
    }

    internal bool Has(string section, string key) =>
        _sections.TryGetValue(section, out var values) && values.ContainsKey(key);

    private string? Raw(string section, string key)
    {
        if (_sections.TryGetValue(section, out var values) && values.TryGetValue(key, out var value))
            return value;

        _missing.Add((section, key));
        return null;
    }

    internal string Text(string section, string key, string fallback) =>
        Raw(section, key) is { Length: > 0 } value ? value : fallback;

    // true/false/1/0, case-insensitively. Anything else is an error,
    // not a silent false: a user who wrote yes meant yes.
    internal bool Bool(string section, string key, bool fallback)
    {
        var value = Raw(section, key);
        if (value is null or "") return fallback;

        return value.ToLowerInvariant() switch
        {
            "true" or "1" => true,
            "false" or "0" => false,
            _ => throw new FormatException(
                $"[{section}] {key} = \"{value}\": expected true, false, 1 or 0."),
        };
    }

    // Clamped, never thrown: a bitrate typed with one zero too many must not keep the server from
    // starting. The clamp is reported through onClamp so the log can say so.
    internal int Number(string section, string key, int fallback, int min, int max,
                        Action<string>? onClamp = null)
    {
        var value = Raw(section, key);
        if (value is null or "") return fallback;

        if (!int.TryParse(Clean(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            throw new FormatException($"[{section}] {key} = \"{value}\": expected a whole number.");

        if (parsed < min || parsed > max)
        {
            var clamped = Math.Clamp(parsed, min, max);
            onClamp?.Invoke($"[{section}] {key} = {parsed} is outside {min}..{max}; using {clamped}.");
            return clamped;
        }

        return parsed;
    }


    internal T Enum<T>(string section, string key, T fallback) where T : struct, Enum
    {
        var value = Raw(section, key);
        if (value is null or "") return fallback;

        if (System.Enum.TryParse<T>(value, ignoreCase: true, out var parsed))
            return parsed;

        var allowed = string.Join(", ", System.Enum.GetNames<T>()).ToLowerInvariant();
        throw new FormatException($"[{section}] {key} = \"{value}\": expected one of {allowed}.");
    }

    // Comma-separated. An empty value means an empty list, not "leave the default" — the
    // distinction matters when a user deliberately switches something off by clearing it.
    internal IReadOnlyList<string> List(string section, string key, IReadOnlyList<string> fallback)
    {
        var value = Raw(section, key);
        if (value is null) return fallback;
        if (value.Length == 0) return Array.Empty<string>();

        var items = new List<string>();
        var item = new StringBuilder();
        var closing = '\0';   // what would end the item being read, when it was opened with something

        foreach (var character in value)
        {
            if (closing != '\0')
            {
                // Inside quotes or brackets nothing separates: this is the whole point of them.
                if (character == closing) closing = '\0';
                else item.Append(character);

                continue;
            }

            switch (character)
            {
                // A folder may contain the separator, so a value may be wrapped. Three openers and
                // not one: quotes are the habit from a command line, brackets from everywhere else.
                case '"': closing = '"'; break;
                case '\'': closing = '\''; break;
                case '[': closing = ']'; break;
                case '(': closing = ')'; break;

                case ',':
                    Add(items, item);
                    break;

                default:
                    item.Append(character);
                    break;
            }
        }

        Add(items, item);
        return items;
    }

    private static void Add(List<string> items, StringBuilder item)
    {
        var text = item.ToString().Trim();
        item.Clear();

        if (text.Length > 0) items.Add(text);
    }

    private static string Clean(string value)
    {
        var text = value.Trim();
        if (text.EndsWith('%')) text = text[..^1].TrimEnd();
        return text.Replace(',', '.');
    }

    // Builds the file text. Used by ConfFormat, which owns the comments: the file is
    // rewritten whole on every save, so the comments the application writes are the documentation.
    internal sealed class Writer
    {
        private readonly StringBuilder _text = new();
        private bool _first = true;

        internal void Section(string name)
        {
            if (!_first) _text.AppendLine();
            _first = false;
            _text.Append('[').Append(name).AppendLine("]");
        }

        // An explanation written above the key.
        internal void Note(string text)
        {
            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
                _text.Append("# ").AppendLine(line);
        }

        internal void Key(string key, string value) => _text.Append(key).Append(" = ").AppendLine(value);

        internal void Key(string key, bool value) => Key(key, value ? "true" : "false");

        internal void Key(string key, int value) => Key(key, value.ToString(CultureInfo.InvariantCulture));

        // A list, comma-separated. A value containing a comma is written in quotes, which is what
        // the reader above expects: without them "Discs, old" came back as two folders.
        internal void Key(string key, IEnumerable<string> values) =>
            Key(key, string.Join(", ", values.Select(v => v.Contains(',') ? $"\"{v}\"" : v)));

        internal void Blank() => _text.AppendLine();

        public override string ToString() => _text.ToString();
    }
}
