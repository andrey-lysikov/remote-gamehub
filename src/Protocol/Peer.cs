//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;

namespace RemoteGameHub.Protocol;

// How the other end of a connection is written down. Every listener binds one dual-mode IPv6
// socket, so an IPv4 client arrives as ::ffff:172.30.212.2; the prefix comes off when shown.
internal static class Peer
{
    // The address as a person would write it.
    internal static string Describe(IPAddress? address)
    {
        if (address is null) return "unknown";

        return address.IsIPv4MappedToIPv6
            ? address.MapToIPv4().ToString()
            : address.ToString();
    }

    internal static string Describe(EndPoint? endpoint) => endpoint switch
    {
        IPEndPoint ip => $"{Describe(ip.Address)}:{ip.Port}",
        null => "unknown",
        _ => endpoint.ToString() ?? "unknown",
    };

    // The address itself in its plain form, for anything that keeps one to show later — the
    // negotiation hands its client address to the tray and to the status page.
    internal static IPAddress Plain(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6
            ? address.MapToIPv4()
            : address;
}
