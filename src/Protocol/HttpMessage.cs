//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace RemoteGameHub.Protocol;

// One request, as far as this server needs to understand it: the endpoints Moonlight uses are all
// GET with a query string and no body, so that is exactly what this parses.
internal sealed class HttpRequest
{
    private readonly Dictionary<string, string> _query = new(StringComparer.OrdinalIgnoreCase);

    internal string Method { get; private init; } = "GET";
    internal string Path { get; private init; } = "/";

    // What followed the headers, when the request said how much of it there was. Only the page
    // sends one, as the whole body of a request rather than as a multipart form.
    internal byte[] Body { get; private set; } = Array.Empty<byte>();

    internal string? Query(string name) => _query.TryGetValue(name, out var value) ? value : null;

    // Everything after the path, for the log. Truncated: some parameters are long.
    internal string QueryForLog =>
        _query.Count == 0
            ? string.Empty
            : " " + string.Join(" ", _query.Select(pair =>
                $"{pair.Key}={(pair.Value.Length > 24 ? pair.Value[..24] + "…" : pair.Value)}"));

    // A body larger than this is refused. The only body this server ever receives is one picture,
    // and a cover that will not fit in eight megabytes is not a cover.
    private const int MaxBodyBytes = 8 * 1024 * 1024;

    internal static async Task<HttpRequest?> ReadAsync(Stream stream, CancellationToken cancel)
    {
        var buffer = new byte[16384];
        var used = 0;

        while (used < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(used, buffer.Length - used), cancel);
            if (read == 0) return null;

            used += read;

            var text = Encoding.ASCII.GetString(buffer, 0, used);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0) continue;

            var request = Parse(text[..end]);
            if (request is null) return null;

            var length = ContentLength(text[..end]);
            if (length <= 0) return request;
            if (length > MaxBodyBytes) return null;

            // Whatever of the body already arrived alongside the headers, and then the rest.
            var body = new byte[length];
            var already = Math.Min(length, used - (end + 4));
            Array.Copy(buffer, end + 4, body, 0, already);

            var at = already;
            while (at < length)
            {
                var more = await stream.ReadAsync(body.AsMemory(at, length - at), cancel);
                if (more == 0) return null;

                at += more;
            }

            request.Body = body;
            return request;
        }

        return null;
    }

    private static int ContentLength(string head)
    {
        foreach (var line in head.Split("\r\n"))
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;

            if (!line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                continue;

            return int.TryParse(line[(colon + 1)..].Trim(), out var length) ? length : 0;
        }

        return 0;
    }

    private static HttpRequest? Parse(string head)
    {
        var firstLine = head.Split("\r\n")[0];
        var parts = firstLine.Split(' ');
        if (parts.Length < 2) return null;

        var target = parts[1];
        var question = target.IndexOf('?');

        var request = new HttpRequest
        {
            Method = parts[0],
            Path = question < 0 ? target : target[..question],
        };

        if (question >= 0)
        {
            foreach (var pair in target[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var equals = pair.IndexOf('=');
                if (equals < 0)
                {
                    request._query[Uri.UnescapeDataString(pair)] = string.Empty;
                    continue;
                }

                request._query[Uri.UnescapeDataString(pair[..equals])] =
                    Uri.UnescapeDataString(pair[(equals + 1)..]);
            }
        }

        return request;
    }
}

// Building the answer. Always XML, always closed afterwards.
internal static class HttpResponse
{
    internal static async Task WriteXmlAsync(Stream stream, string xml, CancellationToken cancel)
    {
        var body = Encoding.UTF8.GetBytes(xml);

        var head = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: application/xml\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            // Closed after every answer. Moonlight opens a connection per request, and keeping
            // them alive only leaves sockets waiting for a second request that never comes.
            "Connection: close\r\n" +
            "\r\n");

        await stream.WriteAsync(head, cancel);
        await stream.WriteAsync(body, cancel);
        await stream.FlushAsync(cancel);
    }

    // The one non-XML answer in the protocol: the box art fetched per game. The bytes are read by
    // the caller, so a file that disappears mid-request is a 404, not a truncated image.
    internal static async Task WriteImageAsync(Stream stream, byte[] image, string contentType,
                                               CancellationToken cancel)
    {
        var head = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {image.Length}\r\n" +
            "Connection: close\r\n" +
            "\r\n");

        await stream.WriteAsync(head, cancel);
        await stream.WriteAsync(image, cancel);
        await stream.FlushAsync(cancel);
    }

    internal static Task WriteNotFoundAsync(Stream stream, CancellationToken cancel)
    {
        var head = Encoding.ASCII.GetBytes(
            "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

        return stream.WriteAsync(head, cancel).AsTask();
    }
}
