//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RemoteGameHub.App;

namespace RemoteGameHub.Protocol;

// Video over RTP on 47998/udp: fixed-size packets plus Reed-Solomon parity computed over them,
// headers included, before RTP and fecInfo are stamped. RTP is big-endian, the video header little.
internal sealed class VideoStream : IDisposable
{
    private const int RtpAndNvHeaderBytes = 32;   // RTP (12) + reserved (4) + NV_VIDEO_PACKET (16)
    private const int NvHeaderBytes = 16;
    private const int ShortFrameHeaderBytes = 8;
    private const int MaxFecBlocks = 4;           // two bits in multiFecBlocks, no more

    private const byte FlagContainsPicData = 0x1;
    private const byte FlagEof = 0x2;
    private const byte FlagSof = 0x4;

    // How many packets a millisecond may carry, and how many go out between two looks at the clock.
    // The stream's own bitrate with room to spare: a key frame poured out at once arrives as loss.
    private const int HeadroomOverBitrate = 4;
    private const int PacedBytesPerMsCeiling = 100_000;
    private const int PaceEvery = 16;
    private const double MaxPacePerFrameMs = 8.0;

    private readonly int _packetsPerMs;
    private readonly int _packetSize;
    private readonly int _blockSize;
    private readonly int _fecPercentage;
    private readonly int _minRequiredFecPackets;
    private readonly object _gate = new();

    private Socket? _socket;
    private Thread? _receiver;
    private volatile bool _running;
    private EndPoint? _peer;
    // The FEC block, kept for the life of the stream. See Shards.
    private byte[][] _shards = Array.Empty<byte[]>();

    // The codec for the last block shape, kept for the same reason. See Codec.
    private ReedSolomon? _codec;
    private int _codecData;
    private int _codecParity;

    // Every packet of the stream, counted. Not a ushort although the RTP sequence number is: the
    // same count goes out as a twenty-four-bit index the client reads as one that only ever rises.
    private uint _sequenceNumber;

    internal VideoStream(int port, string bindAddress, StreamNegotiation negotiation)
    {
        Port = port;
        BindAddress = bindAddress;
        _packetSize = negotiation.PacketSize;
        _blockSize = negotiation.PacketSize + 16;   // MAX_RTP_HEADER_SIZE, per the reference
        _fecPercentage = negotiation.FecPercentage;
        _minRequiredFecPackets = negotiation.MinRequiredFecPackets;
        // Kilobits a second to bytes a millisecond is a division by eight; the headroom is what
        // keeps a burst of key frames from being slowed to the average rate of the stream.
        var bytesPerMs = Math.Min(PacedBytesPerMsCeiling,
                                  negotiation.BitrateKbps / 8 * HeadroomOverBitrate);

        _packetsPerMs = Math.Max(1, bytesPerMs / _blockSize);
    }

    private int Port { get; }
    private string BindAddress { get; }

    // Whether the client has pinged and frames have somewhere to go.
    internal bool HasPeer => _peer is not null;

    // Windows' own default send buffer is too small for a key frame's worth of packets leaving in
    // one burst; too small makes SendTo block on the kernel draining it, seen as this server pausing.
    private const int SendBufferBytes = 1 << 20;

    internal void Start()
    {
        _socket = UdpBind(Port, BindAddress);
        _socket.SendBufferSize = SendBufferBytes;
        _running = true;
        // Same priority as the "stream" thread that calls SendFrame on this class.
        _receiver = new Thread(ReceiveLoop)
            { IsBackground = true, Name = "video-ping", Priority = ThreadPriority.Highest };
        _receiver.Start();

        Log.Info($"video stream ready on port {Port} " +
                 $"(packet size {_packetSize}, FEC {_fecPercentage}%, " +
                 $"paced at {_packetsPerMs} packet(s) per ms)");
    }

    internal static Socket UdpBind(int port, string bindAddress)
    {
        if (!string.Equals(bindAddress, "any", StringComparison.OrdinalIgnoreCase))
        {
            var address = IPAddress.Parse(bindAddress);
            var bound = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            bound.Bind(new IPEndPoint(address, port));
            return bound;
        }

        try
        {
            var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp)
            {
                DualMode = true,
            };
            socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            return socket;
        }
        catch (Exception)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            return socket;
        }
    }

    // The client sends small PING datagrams from the socket it will receive on: that is how the
    // stream learns which address and which source port survived the client's NAT.
    private void ReceiveLoop()
    {
        var buffer = new byte[64];
        var any = new IPEndPoint(
            _socket!.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);

        while (_running)
        {
            try
            {
                EndPoint sender = any;
                var length = _socket.ReceiveFrom(buffer, ref sender);
                if (length <= 0) continue;

                if (_peer is null) Log.Info($"video: the client pings from {Peer.Describe(sender)}");
                _peer = sender;
            }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionReset)
            {
                // ICMP from one of our own sends; ignored, the pings keep the truth current.
            }
            catch (Exception)
            {
                if (!_running) return;
            }
        }
    }

    // Packetises and sends one encoded frame. frameIndex starts at 1; timestamp90KHz is the
    // capture time on RTP's 90 kHz clock. Returns quietly when the client has not pinged yet.
    internal void SendFrame(ReadOnlySpan<byte> frame, bool keyFrame, uint frameIndex, uint timestamp90KHz)
    {
        var peer = _peer;
        if (peer is null || _socket is null || frame.Length == 0) return;

        lock (_gate)
        {
            var payloadPerPacket = _blockSize - RtpAndNvHeaderBytes;
            var payloadBytes = frame.Length + ShortFrameHeaderBytes;
            var totalPackets = (payloadBytes + payloadPerPacket - 1) / payloadPerPacket;
            var assembledBytes = (totalPackets - 1) * _blockSize +
                                 RtpAndNvHeaderBytes + payloadBytes - (totalPackets - 1) * payloadPerPacket;

            // The FEC percentage may drop to zero for a frame too large for four blocks; the
            // block count and the percentage both go into every packet, so they are per-frame.
            var fecPercentage = _fecPercentage;
            var maxDataShardsPerBlock = 255 * 100 / (100 + fecPercentage);
            var fecBlocksNeeded = (assembledBytes + maxDataShardsPerBlock * _blockSize - 1) /
                                  (maxDataShardsPerBlock * _blockSize);
            if (fecBlocksNeeded > MaxFecBlocks)
            {
                Log.WarnOccasionally("large frames",
                    $"frame {frameIndex} is abnormally large ({frame.Length} bytes), so it is " +
                    "sent without parity. A frame this size usually means the bitrate is far " +
                    "above what the picture needs.");
                fecPercentage = 0;
                fecBlocksNeeded = MaxFecBlocks;
            }

            var alignedBytes = (assembledBytes / fecBlocksNeeded + _blockSize - 1) / _blockSize * _blockSize;
            var packetsPerAlignedBlock = alignedBytes / _blockSize;

            // The 8-byte short frame header rides at the front of the first packet's payload.
            Span<byte> shortHeader = stackalloc byte[ShortFrameHeaderBytes];
            shortHeader[0] = 0x01;
            // Bytes 1-2: frame processing latency in 0.1 ms units; zero until measured.
            shortHeader[3] = keyFrame ? (byte)2 : (byte)1;
            var lastPayloadLen = payloadBytes % (_packetSize - NvHeaderBytes);
            if (lastPayloadLen == 0) lastPayloadLen = _packetSize - NvHeaderBytes;
            BinaryPrimitives.WriteUInt16LittleEndian(shortHeader[4..], (ushort)lastPayloadLen);

            // The pacing below is against this, not against the clock at each packet: what matters
            // is how long the whole frame took to leave, blocks and parity together.
            var frameStarted = Stopwatch.GetTimestamp();
            var packetsOut = 0;

            var packetInFrame = 0;
            for (var blockIndex = 0; blockIndex < fecBlocksNeeded && packetInFrame < totalPackets; blockIndex++)
            {
                var packetsInBlock = Math.Min(packetsPerAlignedBlock, totalPackets - packetInFrame);
                if (blockIndex == fecBlocksNeeded - 1) packetsInBlock = totalPackets - packetInFrame;

                var dataShards = packetsInBlock;
                var parityShards = fecPercentage == 0 ? 0 : (dataShards * fecPercentage + 99) / 100;
                var blockPercentage = fecPercentage;
                if (parityShards < _minRequiredFecPackets && fecPercentage != 0)
                {
                    // Below the client's floor the share is raised, and the announced percentage
                    // must follow, because the client derives the shard counts back from it.
                    parityShards = _minRequiredFecPackets;
                    blockPercentage = 100 * parityShards / dataShards;
                }

                // Two limits the arithmetic can cross by one: a block holds at most 255 shards, so
                // parity gives way to data; and the percentage travels in eight bits and can wrap.
                if (dataShards >= 255) parityShards = 0;
                else if (dataShards + parityShards > 255) parityShards = 255 - dataShards;

                if (parityShards == 0) blockPercentage = 0;
                blockPercentage = Math.Min(blockPercentage, 255);

                var shards = Shards(dataShards + Math.Max(parityShards, 0));

                // Fill the data packets: header space first, payload after, and the video header
                // fields the parity must protect. Anything stamped after the FEC pass is not here.
                for (var x = 0; x < dataShards; x++, packetInFrame++)
                {
                    var shard = shards[x];
                    var nv = shard.AsSpan(16);

                    BinaryPrimitives.WriteUInt32LittleEndian(nv, (uint)(_sequenceNumber + x) << 8);
                    BinaryPrimitives.WriteUInt32LittleEndian(nv[4..], frameIndex);
                    nv[8] = (byte)(FlagContainsPicData |
                                   (x == 0 ? FlagSof : 0) |
                                   (x == dataShards - 1 ? FlagEof : 0));
                    nv[10] = 0x10;   // multiFecFlags, matched with the client
                    nv[11] = (byte)((blockIndex << 4) | ((fecBlocksNeeded - 1) << 6));

                    CopyPayloadChunk(frame, shortHeader, packetInFrame, payloadPerPacket,
                        shard.AsSpan(RtpAndNvHeaderBytes));
                }

                if (parityShards > 0) Codec(dataShards, parityShards).Encode(shards, _blockSize);

                // Now the fields the client patches back out before reconstructing: over every
                // shard, parity included.
                for (var x = 0; x < shards.Length; x++)
                {
                    var shard = shards[x];

                    shard[0] = 0x90;   // version 2 + the extension flag GameStream sets
                    BinaryPrimitives.WriteUInt16BigEndian(shard.AsSpan(2), (ushort)(_sequenceNumber + x));
                    BinaryPrimitives.WriteUInt32BigEndian(shard.AsSpan(4), timestamp90KHz);

                    var nv = shard.AsSpan(16);
                    BinaryPrimitives.WriteUInt32LittleEndian(nv[4..], frameIndex);
                    nv[11] = (byte)((blockIndex << 4) | ((fecBlocksNeeded - 1) << 6));
                    BinaryPrimitives.WriteUInt32LittleEndian(nv[12..],
                        (uint)(x << 12 | dataShards << 22 | blockPercentage << 4));

                    try
                    {
                        _socket.SendTo(shard, peer);
                    }
                    catch (Exception error)
                    {
                        Log.WarnOccasionally("video send", $"video send failed: {error.Message}");
                        return;
                    }

                    if (++packetsOut % PaceEvery == 0) Pace(frameStarted, packetsOut);
                }

                _sequenceNumber += (uint)shards.Length;
            }
        }
    }

    // Waits until this many packets are due. Spun rather than slept: the waits are fractions of a
    // millisecond and Windows' timer would round every one of them up to fifteen.
    private void Pace(long frameStarted, int packetsOut)
    {
        // Never more than this for one frame: the loop that sends is the loop that captures, and a
        // key frame must not hold up the frames behind it. Past the cap the rest goes out at once.
        var dueMs = Math.Min(MaxPacePerFrameMs, (double)packetsOut / _packetsPerMs);
        var goneMs = (Stopwatch.GetTimestamp() - frameStarted) * 1000.0 / Stopwatch.Frequency;
        if (goneMs >= dueMs) return;

        var until = frameStarted + (long)(dueMs * Stopwatch.Frequency / 1000.0);
        while (Stopwatch.GetTimestamp() < until) Thread.SpinWait(64);
    }

    // The block's shards, kept between frames. Allocated fresh they were 54 KB a frame at the
    // shipped defaults and 193 KB at 4K120, all of it garbage a moment later.
    private byte[][] Shards(int count)
    {
        if (_shards.Length < count)
        {
            var grown = new byte[count][];
            Array.Copy(_shards, grown, _shards.Length);
            for (var x = _shards.Length; x < count; x++) grown[x] = new byte[_blockSize];
            _shards = grown;
        }

        // Cleared, not just reused: a short last payload would otherwise send the tail of an
        // older frame, with the parity protecting it.
        for (var x = 0; x < count; x++) Array.Clear(_shards[x], 0, _blockSize);

        return _shards.Length == count ? _shards : _shards[..count];
    }

    // The codec for one block shape. Its constructor builds the Cauchy matrix, and consecutive
    // frames ask for the same shape essentially always.
    private ReedSolomon Codec(int dataShards, int parityShards)
    {
        if (_codec is null || dataShards != _codecData || parityShards != _codecParity)
        {
            _codec = new ReedSolomon(dataShards, parityShards);
            _codecData = dataShards;
            _codecParity = parityShards;
        }

        return _codec;
    }

    private static void CopyPayloadChunk(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> shortHeader,
                                         int packetIndex, int payloadPerPacket, Span<byte> destination)
    {
        // The frame's payload stream is the short header followed by the encoded bytes; packet n
        // carries bytes [n·payloadPerPacket, (n+1)·payloadPerPacket) of it.
        var start = packetIndex * payloadPerPacket;
        var end = Math.Min(start + payloadPerPacket, shortHeader.Length + frame.Length);

        var written = 0;
        if (start < shortHeader.Length)
        {
            var take = Math.Min(shortHeader.Length - start, end - start);
            shortHeader.Slice(start, take).CopyTo(destination);
            written = take;
        }

        if (start + written < end)
        {
            var frameStart = start + written - shortHeader.Length;
            frame.Slice(frameStart, end - start - written).CopyTo(destination[written..]);
        }
    }

    public void Dispose()
    {
        _running = false;
        _socket?.Close();
        _socket = null;
        _receiver?.Join(500);
    }
}
