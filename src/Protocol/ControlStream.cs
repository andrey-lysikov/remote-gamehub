//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Security.Cryptography;
using RemoteGameHub.App;
using RemoteGameHub.Media;

namespace RemoteGameHub.Protocol;

// The GameStream messages over the ENet channel: input and key-frame requests in, rumble and
// termination out, AES-GCM under the rikey from /launch. Shapes from moonlight-common-c.
internal sealed class ControlStream : IDisposable
{
    // The Gen7Enc packet types, from ControlStream.c packetTypesGen7Enc.
    private const ushort TypeRequestIdrFrame = 0x0302;
    private const ushort TypeStartB = 0x0307;
    private const ushort TypeInvalidateRefFrames = 0x0301;
    private const ushort TypeLossStats = 0x0201;
    private const ushort TypeFrameStats = 0x0204;
    private const ushort TypeInputData = 0x0206;
    private const ushort TypeRumble = 0x010B;
    private const ushort TypeTermination = 0x0109;

    // Whether the picture being sent is high dynamic range, which the client cannot tell from the
    // stream alone: it configures its own screen from this, and shows PQ as washed-out without it.
    private const ushort TypeHdrMode = 0x010E;

    // The client's keep-alive: eight bytes, ten times a second, wanting no answer. Named here
    // because logged as an unhandled message it is ten lines a second and rotates the log away.
    private const ushort TypePeriodicPing = 0x0200;

    // The protocol's "ended normally" reason code, which clients show as a clean end rather
    // than an error.
    private const uint TerminationGraceful = 0x80030023;

    private const int TagLength = 16;

    private readonly EnetHost _host;
    private readonly AesGcm _cipher;
    private readonly object _sendGate = new();
    // The message types already reported as unhandled, so each is said once per stream.
    private readonly HashSet<ushort> _ignored = new();

    private uint _sendSequence;
    private bool _disposed;

    // The client asked for a key frame — by request or by invalidating references.
    internal event Action? IdrRequested;

    // One input event's payload, exactly as the client sent it. Step 7 gives it meaning.
    internal event Action<byte[]>? InputReceived;

    // The client ended the session, or the transport gave up on it.
    internal event Action<string>? Terminated;

    // What the client says it lost since it last said so: the count, the milliseconds it covers,
    // and the last frame it had whole. The only measurement of the network this end ever gets.
    internal event Action<int, int, int>? LossReported;

    internal ControlStream(int port, string bindAddress, byte[] riKey)
    {
        _cipher = new AesGcm(riKey, TagLength);
        _host = new EnetHost(port, bindAddress);

        _host.Received += (_, data) => HandleMessage(data);
        _host.Disconnected += reason => Terminated?.Invoke(reason);
    }

    internal void Start() => _host.Start();

    // ------------------------------------------------------------------ receiving

    // Unwraps one encrypted control message: envelope type 0x0001, length and sequence number,
    // the GCM tag, then ciphertext. The sequence number doubles as the IV, plus direction bytes.
    private void HandleMessage(byte[] data)
    {
        try
        {
            if (data.Length < 8 + TagLength + 4) return;
            if (BinaryPrimitives.ReadUInt16LittleEndian(data) != 0x0001) return;

            var length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
            if (length + 4 != data.Length) return;

            var sequence = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));

            Span<byte> nonce = stackalloc byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(nonce, sequence);
            nonce[10] = (byte)'C';   // client originated
            nonce[11] = (byte)'C';   // control stream

            var tag = data.AsSpan(8, TagLength);
            var ciphertext = data.AsSpan(8 + TagLength);
            var plaintext = new byte[ciphertext.Length];

            _cipher.Decrypt(nonce, ciphertext, tag, plaintext);

            if (plaintext.Length < 4) return;
            var type = BinaryPrimitives.ReadUInt16LittleEndian(plaintext);
            var payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(plaintext.AsSpan(2));
            if (payloadLength > plaintext.Length - 4) return;
            var payload = plaintext.AsSpan(4, payloadLength);

            switch (type)
            {
                case TypeRequestIdrFrame:
                case TypeInvalidateRefFrames:
                    // Reference invalidation was never offered, but a client that sends it anyway
                    // needs a picture it can decode. Which of the two arrived is said once each.
                    if (_ignored.Add(type))
                        Log.Info($"the client asks for key frames with message 0x{type:X4}");

                    IdrRequested?.Invoke();
                    break;

                case TypeInputData:
                    InputReceived?.Invoke(payload.ToArray());
                    break;

                case TypeTermination:
                    Terminated?.Invoke("the client ended the session");
                    break;

                case TypeLossStats:
                    // Four-byte counts in moonlight-common-c's order: packets lost, milliseconds
                    // covered, frames in them, and the last frame that arrived whole.
                    if (payload.Length >= 16)
                    {
                        // The first of them, said once: a report of no loss at all is itself the
                        // answer to "is the network dropping this", and silence would not be.
                        if (_ignored.Add(type))
                        {
                            Log.Info($"the client reports on losses ({payload.Length} bytes): " +
                                     $"{BinaryPrimitives.ReadInt32LittleEndian(payload)} lost, " +
                                     $"{BinaryPrimitives.ReadInt32LittleEndian(payload[4..])} ms, " +
                                     $"last whole frame " +
                                     $"{BinaryPrimitives.ReadInt32LittleEndian(payload[12..])}");
                        }

                        LossReported?.Invoke(
                            BinaryPrimitives.ReadInt32LittleEndian(payload),
                            BinaryPrimitives.ReadInt32LittleEndian(payload[4..]),
                            BinaryPrimitives.ReadInt32LittleEndian(payload[12..]));
                    }
                    else if (_ignored.Add(type))
                    {
                        // The client writes thirty-two bytes; anything else means this end reads
                        // the message wrongly, and only the bytes themselves can show how.
                        Log.Info($"the client's loss report is {payload.Length} byte(s), not the " +
                                 $"32 expected; the message decrypts to " +
                                 $"{Convert.ToHexString(plaintext.AsSpan(0, Math.Min(40, plaintext.Length)))}");
                    }

                    break;

                case TypeStartB:
                case TypeFrameStats:
                case TypePeriodicPing:
                    // Handshake chatter, the keep-alive and periodic reports. Nothing acts on
                    // them yet; the loss numbers may one day steer the bitrate.
                    break;

                default:
                    // Once per kind. A client sends types this switch does not know for as long
                    // as it streams, and each line here is a file opened under the log's lock.
                    if (_ignored.Add(type))
                        Log.Input($"control message 0x{type:X4} ({payloadLength} byte(s)) ignored");
                    break;
            }
        }
        catch (CryptographicException)
        {
            // A wrong key or a corrupted message. One is not worth killing the session over;
            // a stream of them will show as an unresponsive client and time out on its own.
            Log.Warn("a control message failed authentication and was dropped");
        }
    }

    // ------------------------------------------------------------------ sending

    // Rumble, to the client's controller. Fields little-endian, as Sunshine sends them.
    internal void SendRumble(ushort controllerId, ushort lowFrequency, ushort highFrequency)
    {
        Span<byte> payload = stackalloc byte[10];
        // The first four bytes exist in the packet and are read by no client; zero is the only
        // honest value for a field with no meaning.
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[4..], controllerId);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[6..], lowFrequency);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[8..], highFrequency);

        Send(TypeRumble, payload);
    }

    // Says whether the stream is in high dynamic range, and what the screen it comes from can
    // show. The chromaticities go out normalised to fifty thousand, as the protocol has them.
    internal void SendHdrMode(bool enabled, HdrDisplay display)
    {
        var payload = new byte[27];
        payload[0] = (byte)(enabled ? 1 : 0);

        // Red, green, blue, the white point, then the luminances. The two zeros are the content's
        // own light levels, which nothing here measures: a desktop is not graded material, and a
        // number invented at this end is worse than the client's own default.
        var values = new[]
        {
            display.RedX, display.RedY, display.GreenX, display.GreenY,
            display.BlueX, display.BlueY, display.WhiteX, display.WhiteY,
            display.MaxLuminance, display.MinLuminance,
            0, 0,
            display.MaxFullFrameLuminance,
        };

        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(1 + i * 2),
                (ushort)Math.Clamp(values[i], 0, ushort.MaxValue));
        }

        Send(TypeHdrMode, payload);
    }

    // Tells the client the session is over, with the protocol's clean-end reason.
    internal void SendTermination()
    {
        Span<byte> payload = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, TerminationGraceful);

        Send(TypeTermination, payload);
    }

    private void Send(ushort type, ReadOnlySpan<byte> payload)
    {
        if (_disposed) return;

        var plaintext = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(plaintext, type);
        BinaryPrimitives.WriteUInt16LittleEndian(plaintext.AsSpan(2), (ushort)payload.Length);
        payload.CopyTo(plaintext.AsSpan(4));

        var message = new byte[8 + TagLength + plaintext.Length];

        lock (_sendGate)
        {
            var sequence = _sendSequence++;

            BinaryPrimitives.WriteUInt16LittleEndian(message, 0x0001);
            BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(2),
                (ushort)(4 + TagLength + plaintext.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(4), sequence);

            Span<byte> nonce = stackalloc byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(nonce, sequence);
            nonce[10] = (byte)'H';   // host originated
            nonce[11] = (byte)'C';   // control stream

            _cipher.Encrypt(nonce, plaintext, message.AsSpan(8 + TagLength), message.AsSpan(8, TagLength));
        }

        _host.SendReliable(0, message);
    }

    public void Dispose()
    {
        if (_disposed) return;

        try
        {
            SendTermination();
        }
        catch (Exception)
        {
            // The client's own timeout covers a goodbye that did not arrive.
        }

        _disposed = true;
        _host.Dispose();
        _cipher.Dispose();
    }
}
