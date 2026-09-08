//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.NetworkInformation;
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

    // What this machine is to whoever is on the other end of a connection: the address they
    // actually reached, so a machine on several networks names the one that answered them.
    internal static string ThisMachine(EndPoint? localEndPoint)
    {
        if (localEndPoint is IPEndPoint local)
        {
            var reached = Plain(local.Address);
            if (IsReachable(reached)) return reached.ToString();
        }

        // Loopback, or no connection to ask. Any address of this machine beats none: a client
        // told 0.0.0.0 builds its stream URL out of it and never arrives.
        return FirstLocalAddress()?.ToString() ?? IPAddress.Loopback.ToString();
    }

    // The first real IPv4 of an interface that is up, or null when the network is not. Not a route
    // probe against a fixed gateway: that names nothing at all once the default route is gone.
    internal static IPAddress? FirstLocalAddress()
    {
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var ip in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (IsReachable(ip.Address)) return ip.Address;
                }
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    // An IPv4 another machine could dial. APIPA (169.254.x) means the network is not up yet, and
    // naming it sends a client hunting an address nobody answers on.
    private static bool IsReachable(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        if (IPAddress.Any.Equals(address) || IPAddress.IsLoopback(address)) return false;

        var bytes = address.GetAddressBytes();
        return !(bytes[0] == 169 && bytes[1] == 254);
    }
}
