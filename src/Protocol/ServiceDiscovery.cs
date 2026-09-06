//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using RemoteGameHub.App;

namespace RemoteGameHub.Protocol;

// mDNS, carrying the one service name Moonlight looks for, _nvstream._tcp, so a client finds this
// machine without an address. The socket shares its port: Windows has a resolver on it already.
internal sealed class ServiceDiscovery : IDisposable
{
    private const int MulticastPort = 5353;
    private static readonly IPAddress MulticastGroup = IPAddress.Parse("224.0.0.251");

    // Two minutes, which is what most responders publish. Long enough that the network is quiet,
    // short enough that a host that has gone away stops being offered soon after.
    private const uint TtlSeconds = 120;

    // The service Moonlight browses for. Anything else and no client will look at it.
    private const string ServiceName = "_nvstream._tcp.local";

    // Record types, from the DNS specification.
    private const ushort TypeA = 1;
    private const ushort TypePtr = 12;
    private const ushort TypeTxt = 16;
    private const ushort TypeSrv = 33;
    private const ushort TypeAny = 255;

    private const ushort ClassIn = 1;

    // Set on a record whose owner alone may publish it, telling listeners to replace what they
    // have rather than add to it: without it a host that changes address is remembered at both.
    private const ushort CacheFlush = 0x8000;

    private readonly AppConfig _config;
    private readonly string _instanceName;
    private readonly string _hostRecordName;
    private readonly CancellationTokenSource _stopping = new();

    private UdpClient? _socket;
    private Task? _listening;

    internal ServiceDiscovery(AppConfig config, HostIdentity identity)
    {
        _config = config;

        // The instance name is what a user sees in the client's list before connecting. Dots would
        // be read as label separators and split the name into pieces, so they are replaced.
        _instanceName = $"{identity.HostName.Replace('.', '-')}.{ServiceName}";
        _hostRecordName = $"{Environment.MachineName.Replace('.', '-')}.local";
    }

    internal void Start()
    {
        try
        {
            _socket = OpenSocket();
        }
        catch (Exception error)
        {
            // Not fatal, and never fatal: a client can always be given the address by hand. This is
            // the convenience of not having to.
            Log.Warn(
                $"Automatic discovery is unavailable: {error.Message}\n" +
                "Clients will not find this machine by themselves. Add it in the client by its\n" +
                "address instead — everything else works the same.");
            return;
        }

        _listening = Task.Run(ListenAsync);

        Announce();
        Log.Info($"announcing \"{_instanceName}\" on port {_config.HttpPort} for automatic discovery");
    }

    private UdpClient OpenSocket()
    {
        var socket = new UdpClient();

        // Both are needed, and in this order: the address has to be shareable before it is bound,
        // because Windows already has a resolver on this port and binding exclusively would fail.
        socket.ExclusiveAddressUse = false;
        socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Client.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));

        // Joined on every interface that is up, rather than the default one: a client is rarely on
        // the interface the routing table prefers.
        var joined = 0;
        foreach (var index in InterfaceIndexes())
        {
            try
            {
                socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                    new MulticastOption(MulticastGroup, index));
                joined++;
            }
            catch (SocketException)
            {
                // An interface that will not join is one this machine cannot be found on. The
                // others still work, so this is not worth failing over.
            }
        }

        if (joined == 0)
            throw new InvalidOperationException("no network interface would join the multicast group.");

        socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
        return socket;
    }

    private static IEnumerable<int> InterfaceIndexes()
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up) continue;
            if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (!adapter.SupportsMulticast) continue;

            // An adapter with no IPv4 configured does not return null here but throws, with a
            // message about a protocol not being configured. One of them switched discovery off.
            int index;
            try
            {
                index = adapter.GetIPProperties().GetIPv4Properties().Index;
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            yield return index;
        }
    }

    private async Task ListenAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await _socket!.ReceiveAsync(_stopping.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception error)
            {
                if (_stopping.IsCancellationRequested) return;
                Log.Info($"the discovery socket stopped receiving: {error.Message}");
                return;
            }

            try
            {
                if (!Asks(received.Buffer)) continue;

                // Logged for one reason: when a client does not find this machine, the question is
                // whether its query ever arrived — multicast is the first thing a network segments.
                Log.Info($"discovery: {Peer.Describe(received.RemoteEndPoint)} asked for this service");

                // Answered twice, deliberately: the shared record belongs on the group, but a
                // query that crossed a segment not forwarding multicast back needs the unicast.
                Respond(received.RemoteEndPoint);
                Respond(new IPEndPoint(MulticastGroup, MulticastPort));
            }
            catch (Exception error)
            {
                // The multicast group carries every device on the network talking about itself.
                // A message this server cannot read is somebody else's, not a fault.
                Log.Info($"a discovery message could not be read: {error.Message}");
            }
        }
    }

    // Whether the message is a query for the service this server offers. Everything else on the
    // group — and there is a great deal of it — is ignored without being parsed further.
    private bool Asks(byte[] message)
    {
        if (message.Length < 12) return false;

        // A response, not a query: the QR bit of the flags.
        if ((message[2] & 0x80) != 0) return false;

        var questions = (message[4] << 8) | message[5];
        var position = 12;

        for (var i = 0; i < questions; i++)
        {
            var name = ReadName(message, ref position);
            if (position + 4 > message.Length) return false;

            var type = (ushort)((message[position] << 8) | message[position + 1]);
            position += 4;   // type and class

            var wanted =
                name.Equals(ServiceName, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(_instanceName, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(_hostRecordName, StringComparison.OrdinalIgnoreCase);

            if (wanted && type is TypePtr or TypeSrv or TypeTxt or TypeA or TypeAny)
                return true;
        }

        return false;
    }

    // Reads a name, following the pointers a sender may use to avoid repeating a suffix. The jump
    // count is capped: a message pointing back at itself would otherwise be read forever.
    private static string ReadName(byte[] message, ref int position)
    {
        var labels = new List<string>();
        var jumps = 0;
        var current = position;
        var advanced = false;

        while (current < message.Length)
        {
            var length = message[current];

            if (length == 0)
            {
                current++;
                break;
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (current + 1 >= message.Length || ++jumps > 16) break;

                if (!advanced)
                {
                    position = current + 2;
                    advanced = true;
                }

                current = ((length & 0x3F) << 8) | message[current + 1];
                continue;
            }

            if (current + 1 + length > message.Length) break;

            labels.Add(Encoding.UTF8.GetString(message, current + 1, length));
            current += 1 + length;
        }

        if (!advanced) position = current;
        return string.Join('.', labels);
    }

    // Sent unprompted at startup, three times a second apart, so a client already looking finds it
    // at once. Three because a single multicast datagram is the easiest thing to lose.
    private void Announce()
    {
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (_stopping.IsCancellationRequested) return;

                Respond(new IPEndPoint(MulticastGroup, MulticastPort));

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), _stopping.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });
    }

    private void Respond(IPEndPoint destination)
    {
        var address = LocalAddressFor(destination.Address);
        var message = BuildAnswer(address);

        try
        {
            _socket!.Send(message, message.Length, destination);
        }
        catch (Exception error)
        {
            Log.Info($"a discovery answer could not be sent to {destination}: {error.Message}");
        }
    }

    // The address to advertise, chosen as the one this machine would answer that client from: one
    // picked at startup puts a virtual-switch or VPN address in front of clients that cannot use it.
    private static IPAddress LocalAddressFor(IPAddress client)
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(client.AddressFamily == AddressFamily.InterNetwork ? client : IPAddress.Parse("192.168.1.1"), 9);
            return (probe.LocalEndPoint as IPEndPoint)?.Address ?? IPAddress.Loopback;
        }
        catch (Exception)
        {
            return IPAddress.Loopback;
        }
    }

    private byte[] BuildAnswer(IPAddress address)
    {
        var message = new List<byte>(512);

        // Header. A response carries no questions: the answers stand on their own, and repeating
        // the question back is a convention of ordinary DNS, not of this one.
        message.AddRange(new byte[] { 0, 0, 0x84, 0 });      // id 0, flags: response, authoritative
        AddUInt16(message, 0);                               // questions
        AddUInt16(message, 1);                               // answers
        AddUInt16(message, 0);                               // authorities
        AddUInt16(message, 3);                               // additional records

        // The answer: this service exists, and this is its instance.
        AddRecord(message, ServiceName, TypePtr, cacheFlush: false, EncodeName(_instanceName));

        // The three records a client needs to act on it: where to connect, what to know, and what
        // the name resolves to. Sent together, to save three more round trips.
        var srv = new List<byte>();
        AddUInt16(srv, 0);                                   // priority
        AddUInt16(srv, 0);                                   // weight
        AddUInt16(srv, (ushort)_config.HttpPort);
        srv.AddRange(EncodeName(_hostRecordName));
        AddRecord(message, _instanceName, TypeSrv, cacheFlush: true, srv.ToArray());

        // Empty, but present: some resolvers treat a service with no TXT record as incomplete and
        // will not report it at all.
        AddRecord(message, _instanceName, TypeTxt, cacheFlush: true, new byte[] { 0 });

        AddRecord(message, _hostRecordName, TypeA, cacheFlush: true, address.GetAddressBytes());

        return message.ToArray();
    }

    private static void AddRecord(List<byte> message, string name, ushort type, bool cacheFlush,
                                  byte[] data)
    {
        message.AddRange(EncodeName(name));
        AddUInt16(message, type);
        AddUInt16(message, (ushort)(ClassIn | (cacheFlush ? CacheFlush : 0)));
        AddUInt32(message, TtlSeconds);
        AddUInt16(message, (ushort)data.Length);
        message.AddRange(data);
    }

    private static byte[] EncodeName(string name)
    {
        var bytes = new List<byte>(name.Length + 2);

        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var text = Encoding.UTF8.GetBytes(label);
            if (text.Length > 63) throw new FormatException($"the label \"{label}\" is too long for a name.");

            bytes.Add((byte)text.Length);
            bytes.AddRange(text);
        }

        bytes.Add(0);
        return bytes.ToArray();
    }

    private static void AddUInt16(List<byte> message, ushort value)
    {
        message.Add((byte)(value >> 8));
        message.Add((byte)value);
    }

    private static void AddUInt32(List<byte> message, uint value)
    {
        message.Add((byte)(value >> 24));
        message.Add((byte)(value >> 16));
        message.Add((byte)(value >> 8));
        message.Add((byte)value);
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _socket?.Dispose();

        try
        {
            _listening?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (Exception)
        {
            // Shutting down is not a place to wait on a socket to notice it has been closed.
        }

        _stopping.Dispose();
    }
}
