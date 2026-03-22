using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace AECC.Network
{
    /// <summary>
    /// Wire protocol framing for stream-based transports (TCP).
    /// 
    /// Each message on the wire:
    ///   [4 bytes: payload length, little-endian int32]
    ///   [1 byte:  message type]
    ///   [N bytes: payload]
    ///
    /// Message types:
    ///   0x01 = NetworkEvent envelope (MessagePack-serialized)
    ///   0x10 = System: AssignId        (server → client, payload: 8 bytes long)
    ///   0x11 = System: ConfirmId       (client → server, payload: 8 bytes long)
    ///   0x12 = System: RestoreId       (client → server on reconnect, payload: 8 bytes long)
    ///   0x13 = System: RestoreAccepted (server → client, payload: 8 bytes long)
    ///   0x14 = System: Ping            (either direction, payload: 8 bytes UTC ticks)
    ///   0x15 = System: Pong            (echo reply, payload: same 8 bytes echoed back)
    ///   0x20 = RPC    (StreamJsonRpc traffic)
    ///
    /// For WebSocket / UDP, framing is handled by the transport itself,
    /// so only the type byte + payload are used (no length prefix).
    /// </summary>
    public static class MessageType
    {
        public const byte Event = 0x01;

        // Identity handshake
        public const byte AssignId = 0x10;
        public const byte ConfirmId = 0x11;
        public const byte RestoreId = 0x12;
        public const byte RestoreAccepted = 0x13;

        // Ping / Pong
        public const byte Ping = 0x14;
        public const byte Pong = 0x15;

        /// <summary>Min/max for the identity handshake range check in HandleFramedMessage.</summary>
        public const byte SystemMin = AssignId;
        public const byte SystemMax = Pong;
    }

    /// <summary>
    /// Accumulates raw bytes from a stream transport and emits complete framed messages.
    /// One instance per TCP connection.
    /// </summary>
    public class StreamFrameAccumulator
    {
        private readonly List<byte> _buffer = new();
        private const int HeaderSize = 5; // 4 (length) + 1 (type)

        /// <summary>
        /// Feed raw bytes from the transport. Returns zero or more complete messages.
        /// </summary>
        public List<(byte Type, byte[] Payload)> Feed(byte[] data)
        {
            var results = new List<(byte, byte[])>();
            _buffer.AddRange(data);

            while (_buffer.Count >= HeaderSize)
            {
                int payloadLength = BitConverter.ToInt32(_buffer.GetRange(0, 4).ToArray(), 0);
                int totalLength = HeaderSize + payloadLength;

                if (_buffer.Count < totalLength)
                    break; // incomplete message, wait for more data

                byte msgType = _buffer[4];
                byte[] payload = _buffer.GetRange(5, payloadLength).ToArray();
                _buffer.RemoveRange(0, totalLength);
                results.Add((msgType, payload));
            }

            return results;
        }

        /// <summary>
        /// Pack a message into a framed byte array for sending over a stream transport.
        /// </summary>
        public static byte[] Pack(byte messageType, byte[] payload)
        {
            var result = new byte[HeaderSize + payload.Length];
            BitConverter.GetBytes(payload.Length).CopyTo(result, 0);
            result[4] = messageType;
            Buffer.BlockCopy(payload, 0, result, HeaderSize, payload.Length);
            return result;
        }
    }

    /// <summary>
    /// For WebSocket/UDP: messages don't need length framing,
    /// only the type byte prefix.
    /// </summary>
    public static class DatagramFrame
    {
        public static byte[] Pack(byte messageType, byte[] payload)
        {
            var result = new byte[1 + payload.Length];
            result[0] = messageType;
            Buffer.BlockCopy(payload, 0, result, 1, payload.Length);
            return result;
        }

        public static (byte Type, byte[] Payload) Unpack(byte[] data)
        {
            byte type = data[0];
            var payload = new byte[data.Length - 1];
            Buffer.BlockCopy(data, 1, payload, 0, payload.Length);
            return (type, payload);
        }
    }
}
