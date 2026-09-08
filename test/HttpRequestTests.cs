//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Text;
using RemoteGameHub.Protocol;
using Xunit;

namespace RemoteGameHub.Tests;

// The one shape of request the server reads: a line, a query string, sometimes a body.
public class HttpRequestTests
{
    private static Task<HttpRequest?> Read(string text, byte[]? body = null)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        if (body is not null) bytes = bytes.Concat(body).ToArray();

        return HttpRequest.ReadAsync(new MemoryStream(bytes), CancellationToken.None);
    }

    [Fact]
    public async Task Path_and_query_are_split_and_decoded()
    {
        var request = await Read("GET /pair?uniqueid=0123&devicename=Living%20room&flag HTTP/1.1\r\nHost: x\r\n\r\n");

        Assert.NotNull(request);
        Assert.Equal("GET", request.Method);
        Assert.Equal("/pair", request.Path);
        Assert.Equal("0123", request.Query("uniqueid"));
        Assert.Equal("Living room", request.Query("DeviceName"));
        Assert.Equal(string.Empty, request.Query("flag"));
        Assert.Null(request.Query("absent"));
        Assert.Empty(request.Body);
    }

    [Fact]
    public async Task A_body_is_read_to_its_declared_length()
    {
        var picture = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();
        var request = await Read($"POST /?upload=3 HTTP/1.1\r\nContent-Length: {picture.Length}\r\n\r\n", picture);

        Assert.NotNull(request);
        Assert.Equal("3", request.Query("upload"));
        Assert.Equal(picture, request.Body);
    }

    [Fact]
    public async Task A_connection_that_ends_early_is_null_not_an_error()
    {
        Assert.Null(await Read("GET /serverinfo HTTP/1.1\r\nHost: x\r\n"));
        Assert.Null(await Read("POST / HTTP/1.1\r\nContent-Length: 10\r\n\r\n", new byte[3]));
        Assert.Null(await Read(string.Empty));
    }

    [Fact]
    public async Task The_query_is_shortened_for_the_log()
    {
        var request = await Read("GET /pair?clientcert=" + new string('a', 100) + " HTTP/1.1\r\n\r\n");

        Assert.NotNull(request);
        Assert.Contains("clientcert=" + new string('a', 24) + "…", request.QueryForLog);
        Assert.DoesNotContain(new string('a', 25), request.QueryForLog);
    }

    [Fact]
    public void The_answer_declares_utf8_and_carries_the_status()
    {
        var xml = GameStreamServer.BuildDocument(x => x.WriteElementString("paired", "1"), 503);

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml);
        Assert.Contains("<root status_code=\"503\">", xml);
        Assert.Contains("<paired>1</paired>", xml);
    }
}
