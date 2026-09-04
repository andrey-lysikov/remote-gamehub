//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using RemoteGameHub.Protocol;
using Xunit;

namespace RemoteGameHub.Tests;

// The two hand-rolled parsers behind ANNOUNCE: the request line and headers, and the SDP body's
// a=name:value lines. Both are read by moonlight-common-c's own generator, not a library's.
public class RtspServerTests
{
    [Fact]
    public void The_head_carries_the_command_the_target_and_every_header()
    {
        var request = RtspServer.ParseHead(
            "ANNOUNCE streamid=1 RTSP/1.0\r\nCSeq: 3\r\nX-ss-general.encryptionSupported: 1\r\n");

        Assert.NotNull(request);
        Assert.Equal("ANNOUNCE", request.Command);
        Assert.Equal("streamid=1", request.Target);
        Assert.Equal("3", request.CSeq);
        Assert.Equal("1", request.Options["x-ss-general.encryptionSupported"]);
    }

    [Fact]
    public void A_request_line_that_is_not_RTSP_1_0_is_refused()
    {
        Assert.Null(RtspServer.ParseHead("ANNOUNCE streamid=1 HTTP/1.1\r\n"));
        Assert.Null(RtspServer.ParseHead("ANNOUNCE streamid=1\r\n"));
        Assert.Null(RtspServer.ParseHead(string.Empty));
    }

    [Fact]
    public void A_header_without_a_colon_is_skipped_rather_than_thrown_on()
    {
        var request = RtspServer.ParseHead("OPTIONS * RTSP/1.0\r\nnonsense\r\nCSeq: 7\r\n");

        Assert.NotNull(request);
        Assert.Equal("7", request.CSeq);
        Assert.False(request.Options.ContainsKey("nonsense"));
    }

    [Fact]
    public void CSeq_stays_the_default_when_the_header_is_absent()
    {
        var request = RtspServer.ParseHead("OPTIONS * RTSP/1.0\r\n");

        Assert.NotNull(request);
        Assert.Equal("0", request.CSeq);
    }

    [Theory]
    [InlineData("abc\r\ndef", -1)]
    [InlineData("abc\r\n\r\ndef", 3)]
    [InlineData("\r\n\r\n", 0)]
    public void The_blank_line_ending_the_headers_is_found_by_its_four_bytes(string text, int index)
    {
        var buffer = System.Text.Encoding.ASCII.GetBytes(text);
        Assert.Equal(index, RtspServer.IndexOfBlankLine(buffer, buffer.Length));
    }

    [Fact]
    public void Sdp_attributes_are_read_by_name_with_the_trailing_space_some_clients_leave_stripped()
    {
        var attributes = RtspServer.ParseSdpAttributes(
            "v=0\n" +
            "a=x-nv-video[0].clientViewportWd:1920 \n" +
            "a=x-ss-video[0].chromaSamplingType:1\n" +
            "not-an-attribute-line\n" +
            "a=no-colon-here\n");

        Assert.Equal("1920", attributes["x-nv-video[0].clientViewportWd"]);
        Assert.Equal("1", attributes["x-ss-video[0].chromaSamplingType"]);
        Assert.Equal(2, attributes.Count);
    }

    [Fact]
    public void A_carriage_return_before_the_newline_does_not_become_part_of_the_value()
    {
        var attributes = RtspServer.ParseSdpAttributes("a=x-nv-video[0].height:1080\r\n");

        Assert.Equal("1080", attributes["x-nv-video[0].height"]);
    }
}
