using System;
using System.Collections.Generic;

namespace AECC.Network
{
    public enum NetworkProtocol
    {
        TCP,
        UDP,
        WebSocket,
        WebSocketSecure,
        HTTP,
        HTTPS,

        /// <summary>
        /// Godot 3 WebSocket client (single-threaded, poll-based).
        /// Client-only — cannot be used as a server/listener.
        /// Designed for web exports where multithreading is unavailable.
        /// </summary>
        WebSocketGodot,

        /// <summary>
        /// Godot 3 WebSocket Secure client (single-threaded, poll-based).
        /// Client-only — cannot be used as a server/listener.
        /// Designed for web exports where multithreading is unavailable.
        /// </summary>
        WebSocketSecureGodot
    }

    /// <summary>
    /// Protocol classification helpers.
    /// Centralizes knowledge about which protocols use stream framing,
    /// which are connection-oriented, and which are Godot single-threaded variants.
    /// </summary>
    public static class ProtocolTraits
    {
        /// <summary>
        /// Returns true if the protocol requires length-prefixed stream framing
        /// (StreamFrameAccumulator). Currently only TCP.
        /// All other protocols (WebSocket, UDP, Godot WS) use message-based
        /// datagram framing (DatagramFrame).
        /// </summary>
        public static bool UsesStreamFraming(NetworkProtocol protocol)
        {
            return protocol == NetworkProtocol.TCP;
        }

        /// <summary>
        /// Returns true if the protocol is connection-oriented and requires
        /// the identity handshake (AssignId → ConfirmId / RestoreId → RestoreAccepted).
        /// </summary>
        public static bool IsConnectionOriented(NetworkProtocol protocol)
        {
            switch (protocol)
            {
                case NetworkProtocol.TCP:
                case NetworkProtocol.WebSocket:
                case NetworkProtocol.WebSocketSecure:
                case NetworkProtocol.WebSocketGodot:
                case NetworkProtocol.WebSocketSecureGodot:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Returns true if the protocol is a Godot single-threaded variant.
        /// These protocols must never be used as server/listener endpoints
        /// and must not be combined with any multithreaded API calls.
        /// </summary>
        public static bool IsGodotProtocol(NetworkProtocol protocol)
        {
            return protocol == NetworkProtocol.WebSocketGodot
                || protocol == NetworkProtocol.WebSocketSecureGodot;
        }
    }

    /// <summary>
    /// Unified network endpoint descriptor. Used both as a send destination
    /// (routing events to a specific socket or host:port) and as an endpoint
    /// configuration (defining listeners and client connections at startup).
    ///
    /// Routing modes:
    ///   - SocketId != 0 → routed by socket ID (server-side pattern).
    ///   - Otherwise Host/Port/Protocol identify the target; if no connection
    ///     exists yet, one is created on-the-fly.
    ///
    /// Instances are cached on ISocketAdapter.CachedDestination to avoid
    /// repeated allocation. When you receive a NetworkEvent from a socket,
    /// use socket.CachedDestination to build a reply destination with zero allocs.
    /// </summary>
    public class NetworkDestination
    {
        public string Host = "127.0.0.1";
        public int Port = 6667;
        public NetworkProtocol Protocol = NetworkProtocol.TCP;

        /// <summary>
        /// When non-zero, routes the packet by socket ID instead of Host/Port.
        /// This is primarily a server-side mechanism since servers track sockets by ID.
        /// </summary>
        public long SocketId;

        /// <summary>
        /// true = start a server/listener on this endpoint.
        /// false = connect as a client / use as a send destination.
        /// Only meaningful during initialization; ignored at send time.
        /// </summary>
        public bool IsListener;

        public int BufferSize = 65536;

        // --- SSL/TLS (for WSS / HTTPS) ---
        public string CertificatePath;
        public string CertificatePassword;

        public bool IsSocketRouted => SocketId != 0;

        /// <summary>
        /// The routing key used to look up / store client sockets.
        /// </summary>
        public (NetworkProtocol, string, int) RouteKey => (Protocol, Host, Port);

        /// <summary>
        /// Create a client (non-listener) destination by copying connection fields.
        /// Useful when you need a clean send-only copy from a listener config.
        /// </summary>
        public NetworkDestination ToClientDestination()
        {
            return new NetworkDestination
            {
                Host = Host,
                Port = Port,
                Protocol = Protocol,
                IsListener = false,
                BufferSize = BufferSize,
                CertificatePath = CertificatePath,
                CertificatePassword = CertificatePassword
            };
        }

        /// <summary>
        /// Create a socket-routed destination targeting a specific socket ID.
        /// </summary>
        public static NetworkDestination ForSocket(long socketId)
        {
            return new NetworkDestination { SocketId = socketId };
        }

        /// <summary>
        /// Create a host-routed destination.
        /// </summary>
        public static NetworkDestination ForHost(string host, int port, NetworkProtocol protocol)
        {
            return new NetworkDestination
            {
                Host = host,
                Port = port,
                Protocol = protocol,
                IsListener = false
            };
        }
    }
}
