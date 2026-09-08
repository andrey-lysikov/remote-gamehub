//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using RemoteGameHub.App;

namespace RemoteGameHub.Protocol;

// ENet, the reliable-datagram protocol Moonlight's control channel speaks, implemented here for
// one peer. Wire details from github.com/cgutman/enet; every multi-byte field is big-endian.
internal sealed class EnetHost : IDisposable
{
    // protocol.h, verified with gcc: command sizes indexed by command number 0..12.
    private static readonly int[] CommandSizes = { 0, 8, 48, 44, 8, 4, 6, 8, 24, 8, 12, 16, 24 };

    // Where connectID sits in both CONNECT and VERIFY_CONNECT: its last field, and CONNECT's
    // second-to-last — data follows at 44, and reading 44 makes the client ignore the answer.
    private const int ConnectIdOffset = 40;

    private const byte CommandAcknowledge = 1;
    private const byte CommandConnect = 2;
    private const byte CommandVerifyConnect = 3;
    private const byte CommandDisconnect = 4;
    private const byte CommandPing = 5;
    private const byte CommandSendReliable = 6;
    private const byte CommandSendUnreliable = 7;
    private const byte CommandSendFragment = 8;
    private const byte CommandSendUnsequenced = 9;

    private const byte FlagAcknowledge = 1 << 7;
    private const byte CommandMask = 0x0F;

    private const ushort HeaderFlagSentTime = 1 << 15;
    private const ushort HeaderFlagCompressed = 1 << 14;
    private const int HeaderSessionShift = 12;
    private const ushort MaximumPeerId = 0xFFF;

    private const int MinimumMtu = 576;
    private const int MaximumMtu = 4096;
    private const uint MinimumWindowSize = 4096;
    private const uint MaximumWindowSize = 65536;

    private const int ReliableWindowSize = 0x1000;
    private const int ReliableWindows = 16;
    private const int FreeReliableWindows = 8;

    private const int PingIntervalMs = 500;
    private const int DefaultRoundTripTimeMs = 500;

    // How long a reliable command may go unacknowledged, retransmissions included, before the peer
    // is declared gone. ENet's own ceiling is 30 s.
    private const int PeerTimeoutMs = 10_000;

    private readonly int _port;
    private readonly string _bindAddress;
    private readonly object _gate = new();

    private Socket? _socket;
    private Thread? _thread;
    private volatile bool _running;

    // ------------------------------------------------------------------ the one peer

    private bool _hasPeer;
    private bool _connected;
    private EndPoint _peerEndPoint = new IPEndPoint(IPAddress.Any, 0);
    private ushort _clientPeerId;
    private byte _sendSessionId;
    private byte _receiveSessionId;
    private readonly byte[] _connectId = new byte[4];
    private int _mtu = MinimumMtu;
    private uint _throttleInterval, _throttleAcceleration, _throttleDeceleration;

    private Channel[] _channels = Array.Empty<Channel>();

    private ushort _hostSequence;     // channel 0xFF: VERIFY_CONNECT and pings

    private readonly List<OutgoingCommand> _outgoing = new();
    private readonly List<OutgoingCommand> _sent = new();
    // One outgoing datagram, reused. See Flush.
    private byte[] _datagram = Array.Empty<byte>();

    private readonly List<Acknowledgement> _acks = new();

    // Whether anything at all has arrived on this port. See HandleDatagram.
    private bool _sawAnything;

    private int _lastReceiveTime;
    private uint _roundTripTime = DefaultRoundTripTimeMs;
    private uint _roundTripVariance;


    // A complete message, reliable or not, in order: channel and payload.
    internal event Action<byte, byte[]>? Received;

    internal event Action<string>? Disconnected;

    private sealed class Channel
    {
        internal ushort OutgoingReliable;
        internal ushort IncomingReliable;
        internal ushort IncomingUnreliable;
        internal readonly Dictionary<ushort, PendingIncoming> Pending = new();
    }

    private sealed class PendingIncoming
    {
        internal byte[] Data = Array.Empty<byte>();
        internal uint FragmentCount;         // 0 for a plain reliable message
        internal uint FragmentsRemaining;
        internal HashSet<uint>? SeenFragments;
    }

    private sealed class OutgoingCommand
    {
        internal byte[] Bytes = Array.Empty<byte>();   // fully serialised command, payload included
        internal byte ChannelId;
        internal ushort ReliableSequence;
        internal byte CommandNumber;
        internal int SentTime;
        internal uint Timeout;               // current RTO; doubled per retransmission
        internal int FirstSentTime;
        internal int SendAttempts;
    }

    private readonly record struct Acknowledgement(byte ChannelId, ushort Sequence, ushort SentTime);

    internal EnetHost(int port, string bindAddress)
    {
        _port = port;
        _bindAddress = bindAddress;
    }

    internal void Start()
    {
        var socket = string.Equals(_bindAddress, "any", StringComparison.OrdinalIgnoreCase)
            ? CreateDualModeSocket()
            : CreateBoundSocket(IPAddress.Parse(_bindAddress));

        // A short receive timeout doubles as the service tick: retransmissions, pings and the
        // peer timeout are all checked between receives.
        socket.ReceiveTimeout = 20;

        _socket = socket;
        _running = true;
        // Same priority as the "stream" thread: input and rumble ride this channel, and a
        // Normal-priority control thread losing its core to a Highest one reads as input lag.
        _thread = new Thread(Loop)
            { IsBackground = true, Name = "enet", Priority = ThreadPriority.Highest };
        _thread.Start();

        Log.Info($"listening on port {_port} (enet control)");
    }

    private Socket CreateDualModeSocket()
    {
        try
        {
            var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp)
            {
                DualMode = true,
            };
            socket.Bind(new IPEndPoint(IPAddress.IPv6Any, _port));
            return socket;
        }
        catch (Exception)
        {
            return CreateBoundSocket(IPAddress.Any);
        }
    }

    private Socket CreateBoundSocket(IPAddress address)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(address, _port));
        return socket;
    }

    private void Loop()
    {
        try
        {
            Receive();
        }
        finally
        {
            // Input is typed from this thread, which attached it to the input desktop; the handle
            // that took is not closed by Windows when the thread goes.
            Session.InputDesktop.Detach();
        }
    }

    private void Receive()
    {
        var buffer = new byte[MaximumMtu];
        var from = (EndPoint)new IPEndPoint(
            _socket!.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);

        while (_running)
        {
            int length = 0;
            EndPoint sender = from;
            try
            {
                length = _socket.ReceiveFrom(buffer, ref sender);
            }
            catch (SocketException error) when (error.SocketErrorCode is SocketError.TimedOut
                                                or SocketError.WouldBlock)
            {
                // The service tick.
            }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionReset)
            {
                // A previous send bounced off a closed port (ICMP). Ordinary on Windows; the
                // datagram socket survives it and the peer timeout decides the rest.
            }
            catch (Exception)
            {
                if (!_running) return;
                continue;
            }

            lock (_gate)
            {
                try
                {
                    if (length > 0) HandleDatagram(buffer.AsSpan(0, length), sender);
                    Service();
                }
                catch (Exception error)
                {
                    // One malformed datagram must not take the whole channel down.
                    Log.Warn($"the control channel hiccupped: {error.GetType().Name}: {error.Message}");
                }
            }
        }
    }

    // ------------------------------------------------------------------ receiving

    private void HandleDatagram(ReadOnlySpan<byte> datagram, EndPoint sender)
    {
        // The first thing to arrive on this port, whatever it is: without it, a client that never
        // reached the port and one dropped here for a reason of our own look alike. Said once.
        if (!_sawAnything)
        {
            _sawAnything = true;
            Log.Info($"control: first datagram from {Peer.Describe(sender)}, {datagram.Length} bytes" +
                     (datagram.Length >= 4
                         ? $", header {Convert.ToHexString(datagram[..4])}"
                         : string.Empty));
        }

        if (datagram.Length < 2) return;

        var peerWord = BinaryPrimitives.ReadUInt16BigEndian(datagram);
        var sessionId = (byte)((peerWord >> HeaderSessionShift) & 3);
        var flags = (ushort)(peerWord & (HeaderFlagSentTime | HeaderFlagCompressed));
        var peerId = (ushort)(peerWord & 0x0FFF);

        if ((flags & HeaderFlagCompressed) != 0) return;   // never negotiated; not spoken here

        var headerSize = (flags & HeaderFlagSentTime) != 0 ? 4 : 2;
        if (datagram.Length < headerSize) return;
        var sentTime = (flags & HeaderFlagSentTime) != 0
            ? BinaryPrimitives.ReadUInt16BigEndian(datagram[2..])
            : (ushort)0;

        var connecting = peerId == MaximumPeerId;
        if (!connecting)
        {
            // We are peer 0 and there is no other. A header for anyone else is not for us, and a
            // wrong session id is a packet from a session that no longer exists.
            if (peerId != 0 || !_hasPeer) return;
            if (_connected && sessionId != _receiveSessionId) return;

            // The peer's address follows its packets, as in the reference implementation: a
            // client whose NAT rebound mid-stream is still the client.
            _peerEndPoint = sender;
        }

        _lastReceiveTime = Environment.TickCount;

        var offset = headerSize;
        while (offset + 4 <= datagram.Length)
        {
            var commandByte = datagram[offset];
            var commandNumber = (byte)(commandByte & CommandMask);
            if (commandNumber == 0 || commandNumber >= CommandSizes.Length) break;

            var commandSize = CommandSizes[commandNumber];
            if (offset + commandSize > datagram.Length) break;

            // A packet sent before the sender has a peer id may carry nothing but the CONNECT
            // itself — the reference drops everything else, and so does this.
            if (connecting && commandNumber != CommandConnect) break;

            var command = datagram.Slice(offset, commandSize);
            var channelId = command[1];
            var sequence = BinaryPrimitives.ReadUInt16BigEndian(command[2..]);
            var payloadLength = 0;

            switch (commandNumber)
            {
                case CommandAcknowledge:
                    HandleAcknowledge(command);
                    break;

                case CommandConnect:
                    if (_hasPeer && _connected) break;
                    HandleConnect(command, sender);
                    break;

                case CommandPing:
                    break;

                case CommandSendReliable:
                {
                    payloadLength = BinaryPrimitives.ReadUInt16BigEndian(command[4..]);
                    if (offset + commandSize + payloadLength > datagram.Length) return;
                    var payload = datagram.Slice(offset + commandSize, payloadLength).ToArray();
                    QueueReliable(channelId, sequence, payload);
                    break;
                }

                case CommandSendFragment:
                {
                    payloadLength = BinaryPrimitives.ReadUInt16BigEndian(command[6..]);
                    if (offset + commandSize + payloadLength > datagram.Length) return;

                    var startSequence = BinaryPrimitives.ReadUInt16BigEndian(command[4..]);
                    var fragmentCount = BinaryPrimitives.ReadUInt32BigEndian(command[8..]);
                    var fragmentNumber = BinaryPrimitives.ReadUInt32BigEndian(command[12..]);
                    var totalLength = BinaryPrimitives.ReadUInt32BigEndian(command[16..]);
                    var fragmentOffset = BinaryPrimitives.ReadUInt32BigEndian(command[20..]);

                    HandleFragment(channelId, startSequence, fragmentCount, fragmentNumber,
                        totalLength, fragmentOffset,
                        datagram.Slice(offset + commandSize, payloadLength));
                    break;
                }

                case CommandSendUnreliable:
                {
                    payloadLength = BinaryPrimitives.ReadUInt16BigEndian(command[6..]);
                    if (offset + commandSize + payloadLength > datagram.Length) return;

                    var unreliableSequence = BinaryPrimitives.ReadUInt16BigEndian(command[4..]);
                    HandleUnreliable(channelId, sequence, unreliableSequence,
                        datagram.Slice(offset + commandSize, payloadLength));
                    break;
                }

                case CommandSendUnsequenced:
                {
                    payloadLength = BinaryPrimitives.ReadUInt16BigEndian(command[6..]);
                    if (offset + commandSize + payloadLength > datagram.Length) return;
                    if (_connected && channelId < _channels.Length)
                        Received?.Invoke(channelId, datagram.Slice(offset + commandSize, payloadLength).ToArray());
                    break;
                }

                case CommandDisconnect:
                {
                    if ((commandByte & FlagAcknowledge) != 0 && (flags & HeaderFlagSentTime) != 0)
                        _acks.Add(new Acknowledgement(channelId, sequence, sentTime));

                    var wasPeer = _hasPeer;
                    ResetPeer();
                    if (wasPeer) Disconnected?.Invoke("the client disconnected");
                    return;
                }

                default:
                    // BANDWIDTH_LIMIT, THROTTLE_CONFIGURE, VERIFY_CONNECT: parsed for size,
                    // nothing this single-purpose host acts on.
                    break;
            }

            // Every command marked for acknowledgement gets one, duplicates included, because the
            // ack is how the sender stops resending. VERIFY_CONNECT answers CONNECT instead.
            if ((commandByte & FlagAcknowledge) != 0 &&
                commandNumber != CommandConnect &&
                _connected &&
                (flags & HeaderFlagSentTime) != 0)
            {
                _acks.Add(new Acknowledgement(channelId, sequence, sentTime));
            }

            offset += commandSize + payloadLength;
        }
    }

    // The handshake, mirroring enet_protocol_handle_connect: adopt the client's numbers, derive
    // the session ids, and answer with VERIFY_CONNECT as reliable command number one.
    private void HandleConnect(ReadOnlySpan<byte> command, EndPoint sender)
    {
        var channelCount = BinaryPrimitives.ReadUInt32BigEndian(command[16..]);
        if (channelCount is < 1 or > 255) return;

        if (_hasPeer)
        {
            // A retransmitted CONNECT for the connection being set up: answer it again by
            // letting the queued VERIFY_CONNECT be retransmitted, not by building a second peer.
            if (command.Slice(ConnectIdOffset, 4).SequenceEqual(_connectId)) return;

            // A different connection attempt while one exists: the old client is gone.
            ResetPeer();
            Disconnected?.Invoke("a new client replaced the old connection");
        }

        _hasPeer = true;
        _peerEndPoint = sender;
        _clientPeerId = BinaryPrimitives.ReadUInt16BigEndian(command[4..]);
        command.Slice(ConnectIdOffset, 4).CopyTo(_connectId);

        var mtu = BinaryPrimitives.ReadUInt32BigEndian(command[8..]);
        _mtu = (int)Math.Clamp(mtu, MinimumMtu, MaximumMtu);

        _throttleInterval = BinaryPrimitives.ReadUInt32BigEndian(command[28..]);
        _throttleAcceleration = BinaryPrimitives.ReadUInt32BigEndian(command[32..]);
        _throttleDeceleration = BinaryPrimitives.ReadUInt32BigEndian(command[36..]);

        // Session ids, from protocol.c lines 330-340: start from what the client sent (0xFF on a
        // first connection), add one within the two-bit space, and step over a collision.
        var incomingSession = DeriveSessionId(command[6], _sendSessionId);
        _sendSessionId = incomingSession;
        var outgoingSession = DeriveSessionId(command[7], _receiveSessionId);
        _receiveSessionId = outgoingSession;

        _channels = new Channel[channelCount];
        for (var i = 0; i < _channels.Length; i++) _channels[i] = new Channel();

        // Our incoming bandwidth is unlimited, so the window is the client's ask clamped to the
        // protocol's bounds (protocol.c lines 388-401 with incomingBandwidth == 0).
        var windowSize = Math.Clamp(BinaryPrimitives.ReadUInt32BigEndian(command[12..]),
            MinimumWindowSize, MaximumWindowSize);

        _hostSequence = 0;
        _outgoing.Clear();
        _sent.Clear();
        _acks.Clear();
        _roundTripTime = DefaultRoundTripTimeMs;
        _roundTripVariance = 0;

        var verify = new byte[44];
        verify[0] = CommandVerifyConnect | FlagAcknowledge;
        verify[1] = 0xFF;
        BinaryPrimitives.WriteUInt16BigEndian(verify.AsSpan(2), ++_hostSequence);
        BinaryPrimitives.WriteUInt16BigEndian(verify.AsSpan(4), 0);   // the client's id for us: peer 0
        verify[6] = incomingSession;
        verify[7] = outgoingSession;
        BinaryPrimitives.WriteUInt32BigEndian(verify.AsSpan(8), (uint)_mtu);
        BinaryPrimitives.WriteUInt32BigEndian(verify.AsSpan(12), windowSize);
        BinaryPrimitives.WriteUInt32BigEndian(verify.AsSpan(16), channelCount);
        BinaryPrimitives.WriteUInt32BigEndian(verify.AsSpan(20), 0);  // incoming bandwidth: unlimited
        BinaryPrimitives.WriteUInt32BigEndian(verify.AsSpan(24), 0);  // outgoing bandwidth: unlimited
        BinaryPrimitives.WriteUInt32BigEndian(verify.AsSpan(28), _throttleInterval);
        BinaryPrimitives.WriteUInt32BigEndian(verify.AsSpan(32), _throttleAcceleration);
        BinaryPrimitives.WriteUInt32BigEndian(verify.AsSpan(36), _throttleDeceleration);
        _connectId.CopyTo(verify.AsSpan(40));   // echoed raw, never byte-swapped — as the original

        _outgoing.Add(new OutgoingCommand
        {
            Bytes = verify,
            ChannelId = 0xFF,
            ReliableSequence = _hostSequence,
            CommandNumber = CommandVerifyConnect,
        });
    }

    private static byte DeriveSessionId(byte requested, byte previous)
    {
        var id = requested == 0xFF ? previous : requested;
        id = (byte)((id + 1) & 3);
        if (id == previous) id = (byte)((id + 1) & 3);
        return id;
    }

    private void HandleAcknowledge(ReadOnlySpan<byte> command)
    {
        if (!_hasPeer) return;

        var channelId = command[1];
        var acked = BinaryPrimitives.ReadUInt16BigEndian(command[4..]);
        var echoedSentTime = BinaryPrimitives.ReadUInt16BigEndian(command[6..]);

        // Reconstruct the full send time from its low 16 bits, exactly as the reference does,
        // then fold the sample into the smoothed round-trip estimate.
        var now = Environment.TickCount;
        var sentTime = (now & ~0xFFFF) | echoedSentTime;
        if ((sentTime & 0x8000) > (now & 0x8000)) sentTime -= 0x10000;
        var sample = (uint)Math.Max(0, now - sentTime);

        _roundTripVariance -= _roundTripVariance / 4;
        if (sample >= _roundTripTime)
        {
            _roundTripTime += (sample - _roundTripTime) / 8;
            _roundTripVariance += (sample - _roundTripTime) / 4;
        }
        else
        {
            _roundTripTime -= (_roundTripTime - sample) / 8;
            _roundTripVariance += (_roundTripTime - sample) / 4;
        }

        for (var i = 0; i < _sent.Count; i++)
        {
            if (_sent[i].ReliableSequence != acked || _sent[i].ChannelId != channelId) continue;

            var commandNumber = _sent[i].CommandNumber;
            _sent.RemoveAt(i);

            if (commandNumber == CommandVerifyConnect && !_connected)
            {
                _connected = true;
                Log.Info("control channel connected " +
                         $"({_channels.Length} channel(s), mtu {_mtu})");
            }
            return;
        }

        // Not sent yet but queued (a retransmission raced the ack): drop it from the queue too.
        for (var i = 0; i < _outgoing.Count; i++)
        {
            if (_outgoing[i].ReliableSequence == acked && _outgoing[i].ChannelId == channelId &&
                _outgoing[i].SendAttempts > 0)
            {
                _outgoing.RemoveAt(i);
                return;
            }
        }
    }

    // The reliable-window test from enet_peer_queue_incoming_command: sequence numbers live in
    // sixteen windows of 4096, seven ahead of the current one open. A stale one falls out here.
    private static bool InsideReliableWindow(ushort sequence, ushort current)
    {
        var window = sequence / ReliableWindowSize;
        var currentWindow = current / ReliableWindowSize;
        if (sequence < current) window += ReliableWindows;
        return window >= currentWindow && window < currentWindow + FreeReliableWindows - 1;
    }

    private void QueueReliable(byte channelId, ushort sequence, byte[] payload)
    {
        if (!_connected || channelId >= _channels.Length) return;
        var channel = _channels[channelId];

        if (!InsideReliableWindow(sequence, channel.IncomingReliable)) return;
        if (sequence == channel.IncomingReliable) return;   // a duplicate of the last delivered
        if (channel.Pending.ContainsKey(sequence)) return;  // a duplicate of one still waiting

        channel.Pending[sequence] = new PendingIncoming { Data = payload };
        Deliver(channelId, channel);
    }

    private void HandleFragment(byte channelId, ushort startSequence, uint fragmentCount,
                                uint fragmentNumber, uint totalLength, uint fragmentOffset,
                                ReadOnlySpan<byte> payload)
    {
        if (!_connected || channelId >= _channels.Length) return;
        var channel = _channels[channelId];

        if (!InsideReliableWindow(startSequence, channel.IncomingReliable)) return;
        if (fragmentCount > 1024 * 1024 || fragmentNumber >= fragmentCount ||
            totalLength > 32 * 1024 * 1024 || fragmentOffset >= totalLength ||
            payload.Length > totalLength - fragmentOffset)
            return;

        if (!channel.Pending.TryGetValue(startSequence, out var pending))
        {
            pending = new PendingIncoming
            {
                Data = new byte[totalLength],
                FragmentCount = fragmentCount,
                FragmentsRemaining = fragmentCount,
                SeenFragments = new HashSet<uint>(),
            };
            channel.Pending[startSequence] = pending;
        }
        else if (pending.FragmentCount != fragmentCount || (uint)pending.Data.Length != totalLength)
        {
            return;
        }

        if (!pending.SeenFragments!.Add(fragmentNumber)) return;

        payload.CopyTo(pending.Data.AsSpan((int)fragmentOffset));
        pending.FragmentsRemaining--;

        if (pending.FragmentsRemaining == 0) Deliver(channelId, channel);
    }

    // In-order delivery from the next expected sequence number. A fragmented message occupies as
    // many sequence numbers as it has fragments, as the reference's dispatch advances past them.
    private void Deliver(byte channelId, Channel channel)
    {
        while (true)
        {
            var next = (ushort)(channel.IncomingReliable + 1);
            if (!channel.Pending.TryGetValue(next, out var pending)) return;
            if (pending.FragmentCount > 0 && pending.FragmentsRemaining > 0) return;

            channel.Pending.Remove(next);
            channel.IncomingReliable = pending.FragmentCount > 0
                ? (ushort)(next + pending.FragmentCount - 1)
                : next;
            channel.IncomingUnreliable = 0;

            Received?.Invoke(channelId, pending.Data);
        }
    }

    private void HandleUnreliable(byte channelId, ushort reliableSequence, ushort unreliableSequence,
                                  ReadOnlySpan<byte> payload)
    {
        if (!_connected || channelId >= _channels.Length) return;
        var channel = _channels[channelId];

        // Unreliables are ordered under the reliable sequence they were sent after. The newest is
        // delivered and late arrivals dropped: an old periodic report is worthless.
        if (reliableSequence == channel.IncomingReliable &&
            unreliableSequence <= channel.IncomingUnreliable)
            return;

        if (reliableSequence == channel.IncomingReliable)
            channel.IncomingUnreliable = unreliableSequence;

        Received?.Invoke(channelId, payload.ToArray());
    }

    // ------------------------------------------------------------------ sending

    // Queues one reliable message; large ones are fragmented at the peer's MTU exactly as
    // enet_peer_send does. Actual transmission happens on the service tick.
    internal void SendReliable(byte channelId, ReadOnlySpan<byte> payload)
    {
        lock (_gate)
        {
            if (!_connected || channelId >= _channels.Length) return;
            var channel = _channels[channelId];

            // The same threshold as enet_peer_send: what does not fit one fragment's worth of
            // space is fragmented.
            var fragmentLength = _mtu - 4 - CommandSizes[CommandSendFragment];

            if (payload.Length <= fragmentLength)
            {
                var bytes = new byte[CommandSizes[CommandSendReliable] + payload.Length];
                bytes[0] = CommandSendReliable | FlagAcknowledge;
                bytes[1] = channelId;
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), ++channel.OutgoingReliable);
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), (ushort)payload.Length);
                payload.CopyTo(bytes.AsSpan(6));

                _outgoing.Add(new OutgoingCommand
                {
                    Bytes = bytes,
                    ChannelId = channelId,
                    ReliableSequence = channel.OutgoingReliable,
                    CommandNumber = CommandSendReliable,
                });
            }
            else
            {
                var fragmentCount = (uint)((payload.Length + fragmentLength - 1) / fragmentLength);
                var startSequence = (ushort)(channel.OutgoingReliable + 1);

                for (uint number = 0; number < fragmentCount; number++)
                {
                    var offset = (int)(number * fragmentLength);
                    var length = Math.Min(fragmentLength, payload.Length - offset);

                    var bytes = new byte[CommandSizes[CommandSendFragment] + length];
                    bytes[0] = CommandSendFragment | FlagAcknowledge;
                    bytes[1] = channelId;
                    BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), ++channel.OutgoingReliable);
                    BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), startSequence);
                    BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6), (ushort)length);
                    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), fragmentCount);
                    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), number);
                    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), (uint)payload.Length);
                    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), (uint)offset);
                    payload.Slice(offset, length).CopyTo(bytes.AsSpan(24));

                    _outgoing.Add(new OutgoingCommand
                    {
                        Bytes = bytes,
                        ChannelId = channelId,
                        ReliableSequence = channel.OutgoingReliable,
                        CommandNumber = CommandSendFragment,
                    });
                }
            }

            Flush();
        }
    }

    // Retransmissions, the keep-alive ping, the peer timeout, and the actual sending.
    private void Service()
    {
        if (!_hasPeer) return;

        var now = Environment.TickCount;

        for (var i = _sent.Count - 1; i >= 0; i--)
        {
            var command = _sent[i];
            if ((uint)(now - command.SentTime) < command.Timeout) continue;

            if ((uint)(now - command.FirstSentTime) >= PeerTimeoutMs)
            {
                ResetPeer();
                Disconnected?.Invoke("the client stopped acknowledging; the connection timed out");
                return;
            }

            command.Timeout *= 2;
            _sent.RemoveAt(i);
            _outgoing.Insert(0, command);
        }

        if (_connected && _outgoing.Count == 0 && _sent.Count == 0 &&
            (uint)(now - _lastReceiveTime) >= PingIntervalMs)
        {
            var ping = new byte[CommandSizes[CommandPing]];
            ping[0] = CommandPing | FlagAcknowledge;
            ping[1] = 0xFF;
            BinaryPrimitives.WriteUInt16BigEndian(ping.AsSpan(2), ++_hostSequence);

            _outgoing.Add(new OutgoingCommand
            {
                Bytes = ping,
                ChannelId = 0xFF,
                ReliableSequence = _hostSequence,
                CommandNumber = CommandPing,
            });
        }

        Flush();
    }

    // Builds and sends datagrams: acknowledgements first, then queued commands, as many as fit
    // each MTU, until both queues are drained.
    private void Flush()
    {
        if (_socket is null || !_hasPeer) return;

        var now = Environment.TickCount;

        while (_acks.Count > 0 || _outgoing.Count > 0)
        {
            // The buffer is held rather than made: Flush runs after every received datagram, so an
            // input packet's acknowledgement alone was allocating the client's whole MTU.
            if (_datagram.Length < _mtu) _datagram = new byte[_mtu];
            var datagram = _datagram;

            var offset = 4;   // room for the largest header; trimmed below if sentTime is absent
            ushort flags = 0;

            while (_acks.Count > 0 && offset + CommandSizes[CommandAcknowledge] <= _mtu)
            {
                var ack = _acks[0];
                _acks.RemoveAt(0);

                datagram[offset] = CommandAcknowledge;   // acks themselves are never acked
                datagram[offset + 1] = ack.ChannelId;
                BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(offset + 2), ack.Sequence);
                BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(offset + 4), ack.Sequence);
                BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(offset + 6), ack.SentTime);
                offset += CommandSizes[CommandAcknowledge];
            }

            while (_outgoing.Count > 0 && offset + _outgoing[0].Bytes.Length <= _mtu)
            {
                var command = _outgoing[0];
                _outgoing.RemoveAt(0);

                command.SentTime = now;
                if (command.SendAttempts == 0)
                {
                    command.FirstSentTime = now;
                    command.Timeout = Math.Max(_roundTripTime + 4 * _roundTripVariance, 100);
                }
                command.SendAttempts++;

                command.Bytes.CopyTo(datagram.AsSpan(offset));
                offset += command.Bytes.Length;
                _sent.Add(command);

                flags |= HeaderFlagSentTime;
            }

            if (offset == 4) return;   // nothing fit or nothing left

            int start;
            var peerWord = (ushort)(_clientPeerId | flags | (ushort)(_sendSessionId << HeaderSessionShift));
            if ((flags & HeaderFlagSentTime) != 0)
            {
                start = 0;
                BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(0), peerWord);
                BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(2), (ushort)(now & 0xFFFF));
            }
            else
            {
                start = 2;
                BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(2), peerWord);
            }

            try
            {
                _socket.SendTo(datagram.AsSpan(start, offset - start), _peerEndPoint);
            }
            catch (Exception)
            {
                // The service tick's timeout logic decides whether the peer is gone.
                return;
            }
        }
    }

    private void ResetPeer()
    {
        _hasPeer = false;
        _connected = false;
        _channels = Array.Empty<Channel>();
        _outgoing.Clear();
        _sent.Clear();
        _acks.Clear();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_connected && _socket is not null)
            {
                // A courtesy, sent once and not retransmitted: the process is leaving, and the
                // client's own timeout covers the case where this datagram is lost.
                var disconnect = new byte[CommandSizes[CommandDisconnect]];
                disconnect[0] = CommandDisconnect;
                disconnect[1] = 0xFF;
                BinaryPrimitives.WriteUInt16BigEndian(disconnect.AsSpan(2), ++_hostSequence);

                _outgoing.Add(new OutgoingCommand
                {
                    Bytes = disconnect,
                    ChannelId = 0xFF,
                    ReliableSequence = _hostSequence,
                    CommandNumber = CommandDisconnect,
                });
                Flush();
            }

            _running = false;
            _socket?.Close();
            _socket = null;
        }

        // Never from the service thread itself: a disconnect noticed there ends the session, and
        // the teardown that follows must not wait for the thread it is running on.
        if (_thread is not null && _thread != Thread.CurrentThread) _thread.Join(500);
    }
}
