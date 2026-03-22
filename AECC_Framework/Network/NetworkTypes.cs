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
        HTTPS
    }

    /// <summary>
    /// Describes where a network event should be sent.
    /// If SocketId != 0, the event is routed by socket ID (server-side pattern).
    /// Otherwise Host/Port/Protocol are used to identify the target — and if no
    /// connection to that target exists yet, one is created on-the-fly.
    ///
    /// NetworkDestination is interchangeable with NetworkEndpointConfig:
    /// any destination can be promoted to a full config for auto-connect,
    /// and any config can be narrowed to a destination for sending.
    /// </summary>
    public class NetworkDestination
    {
        public string Host;
        public int Port;
        public NetworkProtocol Protocol;

        /// <summary>
        /// When non-zero, routes the packet by socket ID instead of Host/Port.
        /// This is primarily a server-side mechanism since servers track sockets by ID.
        /// </summary>
        public long SocketId;

        public bool IsSocketRouted => SocketId != 0;

        // ── Optional fields used when auto-creating a connection ──

        public int BufferSize = 65536;
        public string CertificatePath;
        public string CertificatePassword;

        /// <summary>
        /// The routing key used to look up / store client sockets.
        /// </summary>
        public (NetworkProtocol, string, int) RouteKey => (Protocol, Host, Port);

        /// <summary>
        /// Promote this destination to a full endpoint config for auto-connect.
        /// Always creates a client (IsListener = false).
        /// </summary>
        public NetworkEndpointConfig ToEndpointConfig()
        {
            return new NetworkEndpointConfig
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
        /// Create a destination from an endpoint config.
        /// </summary>
        public static NetworkDestination FromConfig(NetworkEndpointConfig config)
        {
            return new NetworkDestination
            {
                Host = config.Host,
                Port = config.Port,
                Protocol = config.Protocol,
                BufferSize = config.BufferSize,
                CertificatePath = config.CertificatePath,
                CertificatePassword = config.CertificatePassword
            };
        }
    }

    /// <summary>
    /// Configuration entry for initializing a network endpoint (listener or connector).
    /// NetworkService expects a List of these to set up all required protocols.
    /// Interchangeable with NetworkDestination for client connections.
    /// </summary>
    public class NetworkEndpointConfig
    {
        public string Host = "127.0.0.1";
        public int Port = 6667;
        public NetworkProtocol Protocol = NetworkProtocol.TCP;

        /// <summary>
        /// true = start a server/listener on this endpoint.
        /// false = connect as a client to this endpoint.
        /// </summary>
        public bool IsListener;

        public int BufferSize = 65536;

        // --- SSL/TLS (for WSS / HTTPS) ---
        public string CertificatePath;
        public string CertificatePassword;

        /// <summary>
        /// Narrow this config to a destination for sending events.
        /// </summary>
        public NetworkDestination ToDestination()
        {
            return NetworkDestination.FromConfig(this);
        }

        /// <summary>
        /// The routing key used to look up / store client sockets.
        /// </summary>
        public (NetworkProtocol, string, int) RouteKey => (Protocol, Host, Port);
    }
}
