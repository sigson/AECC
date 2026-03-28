using System;

namespace AECC.Network
{
    /// <summary>
    /// Abstraction over a single network connection (client or server-side session).
    /// Implementations wrap NetCoreServer's TcpClient, TcpSession, WsClient, WsSession,
    /// UdpClient, UdpServer, etc.
    /// </summary>
    public interface ISocketAdapter
    {
        /// <summary>
        /// Unique socket identifier. Assigned by server during the identity handshake.
        /// For client sockets, this is received from the server.
        /// </summary>
        long Id { get; set; }

        string Address { get; }
        int Port { get; }
        bool IsConnected { get; }
        NetworkProtocol Protocol { get; }

        // ── Latency ──

        /// <summary>
        /// Current measured round-trip latency in milliseconds.
        /// Updated periodically by the PingService. -1 if not yet measured.
        /// </summary>
        int LatencyMs { get; set; }

        /// <summary>
        /// UTC ticks of the last successful ping/pong round-trip completion.
        /// 0 if no ping has completed yet.
        /// </summary>
        long LastPingTicks { get; set; }

        /// <summary>
        /// UTC ticks when the most recent outbound Ping was sent.
        /// Used by PingService to compute RTT when the Pong arrives.
        /// 0 if no ping is in-flight.
        /// </summary>
        long PingSentTicks { get; set; }

        // ── Cached destination ──

        /// <summary>
        /// Cached NetworkDestination for this socket, populated once the socket
        /// is fully identified/ready. Allows zero-allocation reply routing:
        /// when receiving a NetworkEvent, use SocketSource.CachedDestination
        /// as the Destination for the reply event.
        ///
        /// For server-side sessions: SocketId-routed destination.
        /// For client sockets: Host/Port/Protocol-routed destination.
        /// Null until the identity handshake completes.
        /// </summary>
        NetworkDestination CachedDestination { get; set; }

        // ── Transport ──

        /// <summary>
        /// Send raw bytes over this socket.
        /// For connection-oriented protocols, this sends to the remote peer.
        /// For UDP, use SendTo overload with endpoint info.
        /// </summary>
        void Send(byte[] buffer);
        void SendAsync(byte[] buffer);

        void Connect();
        void Disconnect();
        void Reconnect();

        // ── Events ──

        /// <summary>Fired when raw data is received on this socket.</summary>
        event Action<ISocketAdapter, byte[]> DataReceived;

        /// <summary>Fired when the connection is established.</summary>
        event Action<ISocketAdapter> Connected;

        /// <summary>Fired when the connection is lost.</summary>
        event Action<ISocketAdapter> Disconnected;

        /// <summary>Fired on transport errors.</summary>
        event Action<ISocketAdapter, Exception> ErrorOccurred;
    }

    /// <summary>
    /// Abstraction over a server that listens for incoming connections.
    /// Implementations wrap NetCoreServer's TcpServer, WsServer, WssServer, UdpServer, etc.
    /// </summary>
    public interface IServerAdapter
    {
        string Address { get; }
        int Port { get; }
        int BufferSize { get; }
        NetworkProtocol Protocol { get; }

        void Start();
        void Stop();

        /// <summary>Broadcast raw bytes to all connected sessions.</summary>
        void Broadcast(byte[] packet);

        /// <summary>Fired when a new client session connects.</summary>
        event Action<ISocketAdapter> ClientConnected;

        /// <summary>Fired when a client session disconnects.</summary>
        event Action<ISocketAdapter> ClientDisconnected;
    }
}
