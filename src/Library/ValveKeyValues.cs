//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

namespace RemoteGameHub.Library;

// Valve's KeyValues text format, as far as libraryfolders.vdf and appmanifest_*.acf use it: nested
// "key" "value" pairs and "key" { … } blocks, // comments and backslash escapes in quoted strings.
internal sealed class ValveKeyValues
{
    // Steam's own files are inconsistent about case — "AppState" here, "appid" there — and they
    // are read case-insensitively by Steam itself.
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ValveKeyValues> _blocks = new();

    internal string Name { get; }

    private ValveKeyValues(string name) => Name = name;

    internal string? Value(string key) => _values.TryGetValue(key, out var value) ? value : null;

    internal IEnumerable<KeyValuePair<string, string>> Values => _values;

    internal IReadOnlyList<ValveKeyValues> Blocks => _blocks;

    // Parses one document: a single named root block.
    internal static ValveKeyValues Parse(string text)
    {
        var position = 0;

        var name = ReadToken(text, ref position);
        if (name is not { Kind: TokenKind.Text })
            throw new FormatException("the file does not begin with a named block");

        var open = ReadToken(text, ref position);
        if (open is not { Kind: TokenKind.Open })
            throw new FormatException($"\"{name.Value.Text}\" is not followed by an opening brace");

        var root = new ValveKeyValues(name.Value.Text);
        root.ReadBody(text, ref position);
        return root;
    }

    private void ReadBody(string text, ref int position)
    {
        while (true)
        {
            var key = ReadToken(text, ref position)
                ?? throw new FormatException("the file ends inside a block");

            if (key.Kind == TokenKind.Close) return;
            if (key.Kind == TokenKind.Open)
                throw new FormatException("a block appears without a name");

            var next = ReadToken(text, ref position)
                ?? throw new FormatException($"\"{key.Text}\" has no value");

            switch (next.Kind)
            {
                case TokenKind.Open:
                    var child = new ValveKeyValues(key.Text);
                    child.ReadBody(text, ref position);
                    _blocks.Add(child);
                    break;

                case TokenKind.Close:
                    throw new FormatException($"\"{key.Text}\" has no value");

                default:
                    _values[key.Text] = next.Text;
                    break;
            }
        }
    }

    private enum TokenKind { Text, Open, Close }

    private readonly record struct Token(TokenKind Kind, string Text);

    private static Token? ReadToken(string text, ref int position)
    {
        while (position < text.Length)
        {
            var c = text[position];

            if (char.IsWhiteSpace(c))
            {
                position++;
                continue;
            }

            if (c == '/' && position + 1 < text.Length && text[position + 1] == '/')
            {
                while (position < text.Length && text[position] != '\n') position++;
                continue;
            }

            if (c == '{') { position++; return new Token(TokenKind.Open, "{"); }
            if (c == '}') { position++; return new Token(TokenKind.Close, "}"); }

            if (c == '"') return new Token(TokenKind.Text, ReadQuoted(text, ref position));

            // An unquoted token, read to the next delimiter. The two files this parser exists for
            // quote everything, but Steam's writer has been known to leave bare numbers.
            var start = position;
            while (position < text.Length && !char.IsWhiteSpace(text[position])
                   && text[position] is not ('{' or '}' or '"'))
            {
                position++;
            }
            return new Token(TokenKind.Text, text[start..position]);
        }

        return null;
    }

    private static string ReadQuoted(string text, ref int position)
    {
        position++;   // the opening quote

        var value = new System.Text.StringBuilder();
        while (position < text.Length)
        {
            var c = text[position++];

            if (c == '"') return value.ToString();

            // Windows paths in libraryfolders.vdf arrive as "C:\\Games", so the escapes are not
            // optional decoration: an unescaped read doubles every separator.
            if (c == '\\' && position < text.Length)
            {
                var escaped = text[position++];
                value.Append(escaped switch
                {
                    'n' => '\n',
                    't' => '\t',
                    _ => escaped,
                });
                continue;
            }

            value.Append(c);
        }

        throw new FormatException("a quoted string is not closed");
    }
}
