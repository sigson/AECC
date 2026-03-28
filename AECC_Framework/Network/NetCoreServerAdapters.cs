using System;
using System.Net;
using System.Net.Sockets;
using NetCoreServer;
using AECC.Core.Logging;

namespace AECC.Network.Adapters
{
    // =========================================================================
    //  TCP
    // =========================================================================

    #region TCP Session (server-side per-client connection)

    public class TcpSessionAdapter : TcpSession, ISocketAdapter
    {
        public long Id { get; set; }
        public new string Address => Socket?.RemoteEndPoint is IPEndPoint ep ? ep.Address.ToString() : "";
        int ISocketAdapter.Port => Socket?.RemoteEndPoint is IPEndPoint ep ? ep.Port : 0;
        public new bool IsConnected => base.IsConnected;
        public NetworkProtocol Protocol => NetworkProtocol.TCP;

        // Ping/latency
        public int LatencyMs { get; set; } = -1;
        public long LastPingTicks { get; set; }
        public long PingSentTicks { get; set; }
        public NetworkDestination CachedDestination { get; set; }

        public event Action<ISocketAdapter, byte[]> DataReceived;
        public event Action<ISocketAdapter> Connected;
        public event Action<ISocketAdapter> Disconnected;
        public event Action<ISocketAdapter, Exception> ErrorOccurred;

        private readonly TcpServerAdapter _serverRef;

        public TcpSessionAdapter(TcpServerAdapter server) : base(server)
        {
            _serverRef = server;
        }

        protected override void OnConnected()
        {
            Connected?.Invoke(this);
            _serverRef.RaiseClientConnected(this);
        }

        protected override void OnDisconnected()
        {
            Disconnected?.Invoke(this);
            _serverRef.RaiseClientDisconnected(this);
        }

        protected override void OnReceived(byte[] buffer, long offset, long size)
        {
            var data = new byte[size];
            System.Buffer.BlockCopy(buffer, (int)offset, data, 0, (int)size);
            DataReceived?.Invoke(this, data);
        }

        protected override void OnError(SocketError error)
        {
            ErrorOccurred?.Invoke(this, new SocketException((int)error));
        }

        void ISocketAdapter.Send(byte[] buf) => base.Send(buf);
        void ISocketAdapter.SendAsync(byte[] buf) => base.SendAsync(buf);
        void ISocketAdapter.Connect() { }
        void ISocketAdapter.Disconnect() => base.Disconnect();
        void ISocketAdapter.Reconnect() { }
    }

    #endregion

    #region TCP Server

    public class TcpServerAdapter : TcpServer, IServerAdapter
    {
        public new string Address { get; }
        public new int Port { get; }
        public int BufferSize { get; }
        public NetworkProtocol Protocol => NetworkProtocol.TCP;

        public event Action<ISocketAdapter> ClientConnected;
        public event Action<ISocketAdapter> ClientDisconnected;

        public TcpServerAdapter(string address, int port, int bufferSize)
            : base(IPAddress.Parse(address), port)
        {
            Address = address;
            Port = port;
            BufferSize = bufferSize;
            OptionReceiveBufferSize = bufferSize;
            OptionSendBufferSize = bufferSize;
            OptionNoDelay = true;
        }

        protected override TcpSession CreateSession() => new TcpSessionAdapter(this);

        internal void RaiseClientConnected(ISocketAdapter s) => ClientConnected?.Invoke(s);
        internal void RaiseClientDisconnected(ISocketAdapter s) => ClientDisconnected?.Invoke(s);

        void IServerAdapter.Start() => base.Start();
        void IServerAdapter.Stop() => base.Stop();
        void IServerAdapter.Broadcast(byte[] packet) => base.Multicast(packet);
    }

    #endregion

    #region TCP Client

    public class TcpClientAdapter : NetCoreServer.TcpClient, ISocketAdapter
    {
        public long Id { get; set; }
        public new string Address { get; }
        int ISocketAdapter.Port { get; }
        public new bool IsConnected => base.IsConnected;
        public NetworkProtocol Protocol => NetworkProtocol.TCP;

        // Ping/latency
        public int LatencyMs { get; set; } = -1;
        public long LastPingTicks { get; set; }
        public long PingSentTicks { get; set; }
        public NetworkDestination CachedDestination { get; set; }

        public event Action<ISocketAdapter, byte[]> DataReceived;
        public event Action<ISocketAdapter> Connected;
        public event Action<ISocketAdapter> Disconnected;
        public event Action<ISocketAdapter, Exception> ErrorOccurred;

        private readonly int _port;

        public TcpClientAdapter(string address, int port, int bufferSize)
            : base(address, port)
        {
            Address = address;
            _port = port;
            OptionReceiveBufferSize = bufferSize;
            OptionSendBufferSize = bufferSize;
            OptionNoDelay = true;
        }

        protected override void OnConnected()
        {
            Connected?.Invoke(this);
            ReceiveAsync();
        }

        protected override void OnDisconnected()
        {
            Disconnected?.Invoke(this);
        }

        protected override void OnReceived(byte[] buffer, long offset, long size)
        {
            var data = new byte[size];
            System.Buffer.BlockCopy(buffer, (int)offset, data, 0, (int)size);
            DataReceived?.Invoke(this, data);
            ReceiveAsync();
        }

        protected override void OnError(SocketError error)
        {
            ErrorOccurred?.Invoke(this, new SocketException((int)error));
        }

        void ISocketAdapter.Send(byte[] buf) => base.Send(buf);
        void ISocketAdapter.SendAsync(byte[] buf) => base.SendAsync(buf);
        void ISocketAdapter.Connect() => base.ConnectAsync();
        void ISocketAdapter.Disconnect() => base.DisconnectAsync();
        void ISocketAdapter.Reconnect() => base.ReconnectAsync();
    }

    #endregion

    // =========================================================================
    //  UDP
    // =========================================================================

    #region UDP Server

    public class UdpServerAdapter : UdpServer, IServerAdapter
    {
        public new string Address { get; }
        public new int Port { get; }
        public int BufferSize { get; }
        public NetworkProtocol Protocol => NetworkProtocol.UDP;

        public event Action<ISocketAdapter> ClientConnected;
        public event Action<ISocketAdapter> ClientDisconnected;

        /// <summary>Fired when a UDP datagram arrives. Contains sender endpoint + data.</summary>
        public event Action<EndPoint, byte[]> DatagramReceived;

        public UdpServerAdapter(string address, int port, int bufferSize)
            : base(IPAddress.Parse(address), port)
        {
            Address = address;
            Port = port;
            BufferSize = bufferSize;
        }

        protected override void OnStarted() => ReceiveAsync();

        protected override void OnReceived(EndPoint endpoint, byte[] buffer, long offset, long size)
        {
            var data = new byte[size];
            System.Buffer.BlockCopy(buffer, (int)offset, data, 0, (int)size);
            DatagramReceived?.Invoke(endpoint, data);
            ReceiveAsync();
        }

        public void SendTo(EndPoint endpoint, byte[] data) => base.Send(endpoint, data);

        void IServerAdapter.Start() => base.Start();
        void IServerAdapter.Stop() => base.Stop();
        void IServerAdapter.Broadcast(byte[] packet) { }
    }

    #endregion

    #region UDP Client

    public class UdpClientAdapter : NetCoreServer.UdpClient, ISocketAdapter
    {
        public long Id { get; set; }
        public new string Address { get; }
        int ISocketAdapter.Port => _port;
        public new bool IsConnected => base.IsConnected;
        public NetworkProtocol Protocol => NetworkProtocol.UDP;

        // Ping/latency
        public int LatencyMs { get; set; } = -1;
        public long LastPingTicks { get; set; }
        public long PingSentTicks { get; set; }
        public NetworkDestination CachedDestination { get; set; }

        public event Action<ISocketAdapter, byte[]> DataReceived;
        public event Action<ISocketAdapter> Connected;
        public event Action<ISocketAdapter> Disconnected;
        public event Action<ISocketAdapter, Exception> ErrorOccurred;

        private readonly int _port;

        public UdpClientAdapter(string address, int port, int bufferSize)
            : base(address, port)
        {
            Address = address;
            _port = port;
        }

        protected override void OnConnected()
        {
            Connected?.Invoke(this);
            ReceiveAsync();
        }

        protected override void OnDisconnected()
        {
            Disconnected?.Invoke(this);
        }

        protected override void OnReceived(EndPoint endpoint, byte[] buffer, long offset, long size)
        {
            var data = new byte[size];
            System.Buffer.BlockCopy(buffer, (int)offset, data, 0, (int)size);
            DataReceived?.Invoke(this, data);
            ReceiveAsync();
        }

        protected override void OnError(SocketError error)
        {
            ErrorOccurred?.Invoke(this, new SocketException((int)error));
        }

        void ISocketAdapter.Send(byte[] buf) => base.Send(buf);
        void ISocketAdapter.SendAsync(byte[] buf) => base.SendAsync(buf);
        void ISocketAdapter.Connect() => base.Connect();
        void ISocketAdapter.Disconnect() => base.Disconnect();
        void ISocketAdapter.Reconnect() { base.Disconnect(); base.Connect(); }
    }

    #endregion

    // =========================================================================
    //  WebSocket
    // =========================================================================

    #region WS Session (server-side)

    public class WsSessionAdapter : WsSession, ISocketAdapter
    {
        public long Id { get; set; }
        public new string Address => ((IPEndPoint)Socket?.RemoteEndPoint)?.Address.ToString() ?? "";
        int ISocketAdapter.Port => ((IPEndPoint)Socket?.RemoteEndPoint)?.Port ?? 0;
        public new bool IsConnected => base.IsConnected;
        public NetworkProtocol Protocol => NetworkProtocol.WebSocket;

        // Ping/latency
        public int LatencyMs { get; set; } = -1;
        public long LastPingTicks { get; set; }
        public long PingSentTicks { get; set; }
        public NetworkDestination CachedDestination { get; set; }

        public event Action<ISocketAdapter, byte[]> DataReceived;
        public event Action<ISocketAdapter> Connected;
        public event Action<ISocketAdapter> Disconnected;
        public event Action<ISocketAdapter, Exception> ErrorOccurred;

        private readonly WsServerAdapter _serverRef;

        public WsSessionAdapter(WsServerAdapter server) : base(server)
        {
            _serverRef = server;
        }

        public override void OnWsConnected(HttpRequest request)
        {
            Connected?.Invoke(this);
            _serverRef.RaiseClientConnected(this);
        }

        public override void OnWsDisconnected()
        {
            Disconnected?.Invoke(this);
            _serverRef.RaiseClientDisconnected(this);
        }

        public override void OnWsReceived(byte[] buffer, long offset, long size)
        {
            var data = new byte[size];
            System.Buffer.BlockCopy(buffer, (int)offset, data, 0, (int)size);
            DataReceived?.Invoke(this, data);
        }

        protected override void OnError(SocketError error)
        {
            ErrorOccurred?.Invoke(this, new SocketException((int)error));
        }

        void ISocketAdapter.Send(byte[] buf) => base.SendBinaryAsync(buf);
        void ISocketAdapter.SendAsync(byte[] buf) => base.SendBinaryAsync(buf);
        void ISocketAdapter.Connect() { }
        void ISocketAdapter.Disconnect() => base.Disconnect();
        void ISocketAdapter.Reconnect() { }
    }

    #endregion

    #region WS Server

    public class WsServerAdapter : WsServer, IServerAdapter
    {
        public new string Address { get; }
        public new int Port { get; }
        public int BufferSize { get; }
        public NetworkProtocol Protocol => NetworkProtocol.WebSocket;

        public event Action<ISocketAdapter> ClientConnected;
        public event Action<ISocketAdapter> ClientDisconnected;

        public WsServerAdapter(string address, int port, int bufferSize)
            : base(IPAddress.Parse(address), port)
        {
            Address = address;
            Port = port;
            BufferSize = bufferSize;
        }

        protected override TcpSession CreateSession() => new WsSessionAdapter(this);

        internal void RaiseClientConnected(ISocketAdapter s) => ClientConnected?.Invoke(s);
        internal void RaiseClientDisconnected(ISocketAdapter s) => ClientDisconnected?.Invoke(s);

        void IServerAdapter.Start() => base.Start();
        void IServerAdapter.Stop() => base.Stop();
        void IServerAdapter.Broadcast(byte[] packet) => base.MulticastBinary(packet);
    }

    #endregion

    #region WS Client

    public class WsClientAdapter : WsClient, ISocketAdapter
    {
        public long Id { get; set; }
        public new string Address { get; }
        int ISocketAdapter.Port => _port;
        public new bool IsConnected => base.IsConnected;
        public NetworkProtocol Protocol => NetworkProtocol.WebSocket;

        // Ping/latency
        public int LatencyMs { get; set; } = -1;
        public long LastPingTicks { get; set; }
        public long PingSentTicks { get; set; }
        public NetworkDestination CachedDestination { get; set; }

        public event Action<ISocketAdapter, byte[]> DataReceived;
        public event Action<ISocketAdapter> Connected;
        public event Action<ISocketAdapter> Disconnected;
        public event Action<ISocketAdapter, Exception> ErrorOccurred;

        private readonly int _port;

        public WsClientAdapter(string address, int port, int bufferSize)
            : base(address, port)
        {
            Address = address;
            _port = port;
        }

        public override void OnWsConnected(HttpResponse response)
        {
            Connected?.Invoke(this);
        }

        public override void OnWsDisconnected()
        {
            Disconnected?.Invoke(this);
        }

        public override void OnWsReceived(byte[] buffer, long offset, long size)
        {
            var data = new byte[size];
            System.Buffer.BlockCopy(buffer, (int)offset, data, 0, (int)size);
            DataReceived?.Invoke(this, data);
        }

        protected override void OnError(SocketError error)
        {
            ErrorOccurred?.Invoke(this, new SocketException((int)error));
        }

        void ISocketAdapter.Send(byte[] buf) => base.SendBinaryAsync(buf);
        void ISocketAdapter.SendAsync(byte[] buf) => base.SendBinaryAsync(buf);
        void ISocketAdapter.Connect() => base.ConnectAsync();
        void ISocketAdapter.Disconnect() => base.DisconnectAsync();
        void ISocketAdapter.Reconnect() => base.ReconnectAsync();
    }

    #endregion

    // =========================================================================
    //  WebSocket Secure (WSS)
    // =========================================================================

    #region WSS Session (server-side)

    public class WssSessionAdapter : WssSession, ISocketAdapter
    {
        public long Id { get; set; }
        public new string Address => ((IPEndPoint)Socket?.RemoteEndPoint)?.Address.ToString() ?? "";
        int ISocketAdapter.Port => ((IPEndPoint)Socket?.RemoteEndPoint)?.Port ?? 0;
        public new bool IsConnected => base.IsConnected;
        public NetworkProtocol Protocol => NetworkProtocol.WebSocketSecure;

        // Ping/latency
        public int LatencyMs { get; set; } = -1;
        public long LastPingTicks { get; set; }
        public long PingSentTicks { get; set; }
        public NetworkDestination CachedDestination { get; set; }

        public event Action<ISocketAdapter, byte[]> DataReceived;
        public event Action<ISocketAdapter> Connected;
        public event Action<ISocketAdapter> Disconnected;
        public event Action<ISocketAdapter, Exception> ErrorOccurred;

        private readonly WssServerAdapter _serverRef;

        public WssSessionAdapter(WssServerAdapter server) : base(server)
        {
            _serverRef = server;
        }

        public override void OnWsConnected(HttpRequest request)
        {
            Connected?.Invoke(this);
            _serverRef.RaiseClientConnected(this);
        }

        public override void OnWsDisconnected()
        {
            Disconnected?.Invoke(this);
            _serverRef.RaiseClientDisconnected(this);
        }

        public override void OnWsReceived(byte[] buffer, long offset, long size)
        {
            var data = new byte[size];
            System.Buffer.BlockCopy(buffer, (int)offset, data, 0, (int)size);
            DataReceived?.Invoke(this, data);
        }

        protected override void OnError(SocketError error)
        {
            ErrorOccurred?.Invoke(this, new SocketException((int)error));
        }

        void ISocketAdapter.Send(byte[] buf) => base.SendBinaryAsync(buf);
        void ISocketAdapter.SendAsync(byte[] buf) => base.SendBinaryAsync(buf);
        void ISocketAdapter.Connect() { }
        void ISocketAdapter.Disconnect() => base.Disconnect();
        void ISocketAdapter.Reconnect() { }
    }

    #endregion

    #region WSS Server

    public class WssServerAdapter : WssServer, IServerAdapter
    {
        public new string Address { get; }
        public new int Port { get; }
        public int BufferSize { get; }
        public NetworkProtocol Protocol => NetworkProtocol.WebSocketSecure;

        public event Action<ISocketAdapter> ClientConnected;
        public event Action<ISocketAdapter> ClientDisconnected;

        public WssServerAdapter(string address, int port, int bufferSize, SslContext context)
            : base(context, IPAddress.Parse(address), port)
        {
            Address = address;
            Port = port;
            BufferSize = bufferSize;
        }

        protected override SslSession CreateSession() => new WssSessionAdapter(this);

        internal void RaiseClientConnected(ISocketAdapter s) => ClientConnected?.Invoke(s);
        internal void RaiseClientDisconnected(ISocketAdapter s) => ClientDisconnected?.Invoke(s);

        void IServerAdapter.Start() => base.Start();
        void IServerAdapter.Stop() => base.Stop();
        void IServerAdapter.Broadcast(byte[] packet) => base.MulticastBinary(packet);
    }

    #endregion

    #region WSS Client

    public class WssClientAdapter : WssClient, ISocketAdapter
    {
        public long Id { get; set; }
        public new string Address { get; }
        int ISocketAdapter.Port => _port;
        public new bool IsConnected => base.IsConnected;
        public NetworkProtocol Protocol => NetworkProtocol.WebSocketSecure;

        // Ping/latency
        public int LatencyMs { get; set; } = -1;
        public long LastPingTicks { get; set; }
        public long PingSentTicks { get; set; }
        public NetworkDestination CachedDestination { get; set; }

        public event Action<ISocketAdapter, byte[]> DataReceived;
        public event Action<ISocketAdapter> Connected;
        public event Action<ISocketAdapter> Disconnected;
        public event Action<ISocketAdapter, Exception> ErrorOccurred;

        private readonly int _port;

        public WssClientAdapter(SslContext context, string address, int port, int bufferSize)
            : base(context, address, port)
        {
            Address = address;
            _port = port;
        }

        public override void OnWsConnected(HttpResponse response)
        {
            Connected?.Invoke(this);
        }

        public override void OnWsDisconnected()
        {
            Disconnected?.Invoke(this);
        }

        public override void OnWsReceived(byte[] buffer, long offset, long size)
        {
            var data = new byte[size];
            System.Buffer.BlockCopy(buffer, (int)offset, data, 0, (int)size);
            DataReceived?.Invoke(this, data);
        }

        protected override void OnError(SocketError error)
        {
            ErrorOccurred?.Invoke(this, new SocketException((int)error));
        }

        void ISocketAdapter.Send(byte[] buf) => base.SendBinaryAsync(buf);
        void ISocketAdapter.SendAsync(byte[] buf) => base.SendBinaryAsync(buf);
        void ISocketAdapter.Connect() => base.ConnectAsync();
        void ISocketAdapter.Disconnect() => base.DisconnectAsync();
        void ISocketAdapter.Reconnect() => base.ReconnectAsync();
    }

    #endregion

    // =========================================================================
    //  HTTP / HTTPS (stateless request-response transport)
    // =========================================================================

    #region HTTP Session

    public class HttpSessionAdapter : HttpSession, ISocketAdapter
    {
        public long Id { get; set; }
        public new string Address => ((IPEndPoint)Socket?.RemoteEndPoint)?.Address.ToString() ?? "";
        int ISocketAdapter.Port => ((IPEndPoint)Socket?.RemoteEndPoint)?.Port ?? 0;
        public new bool IsConnected => base.IsConnected;
        public NetworkProtocol Protocol => NetworkProtocol.HTTP;

        // Ping/latency (mostly N/A for HTTP but kept for interface compliance)
        public int LatencyMs { get; set; } = -1;
        public long LastPingTicks { get; set; }
        public long PingSentTicks { get; set; }
        public NetworkDestination CachedDestination { get; set; }

        public event Action<ISocketAdapter, byte[]> DataReceived;
        public event Action<ISocketAdapter> Connected;
        public event Action<ISocketAdapter> Disconnected;
        public event Action<ISocketAdapter, Exception> ErrorOccurred;

        private readonly HttpServerAdapter _serverRef;

        public HttpSessionAdapter(HttpServerAdapter server) : base(server)
        {
            _serverRef = server;
        }

        protected override void OnReceivedRequest(HttpRequest request)
        {
            if (request.Method == "POST" && request.Url == "/event")
            {
                var body = new byte[request.BodyLength];
                System.Buffer.BlockCopy(request.BodyBytes, 0, body, 0, (int)request.BodyLength);
                DataReceived?.Invoke(this, body);
                SendResponseAsync(Response.MakeOkResponse());
            }
            else
            {
                SendResponseAsync(Response.MakeErrorResponse(404));
            }
        }

        protected override void OnConnected()
        {
            Connected?.Invoke(this);
            _serverRef.RaiseClientConnected(this);
        }

        protected override void OnDisconnected()
        {
            Disconnected?.Invoke(this);
            _serverRef.RaiseClientDisconnected(this);
        }

        void ISocketAdapter.Send(byte[] buf)
        {
            var response = new HttpResponse();
            response.SetBegin(200);
            response.SetContentType("application/octet-stream");
            response.SetBody(buf);
            SendResponseAsync(response);
        }

        void ISocketAdapter.SendAsync(byte[] buf) => ((ISocketAdapter)this).Send(buf);
        void ISocketAdapter.Connect() { }
        void ISocketAdapter.Disconnect() => base.Disconnect();
        void ISocketAdapter.Reconnect() { }
    }

    #endregion

    #region HTTP Server

    public class HttpServerAdapter : HttpServer, IServerAdapter
    {
        public new string Address { get; }
        public new int Port { get; }
        public int BufferSize { get; }
        public NetworkProtocol Protocol => NetworkProtocol.HTTP;

        public event Action<ISocketAdapter> ClientConnected;
        public event Action<ISocketAdapter> ClientDisconnected;

        public HttpServerAdapter(string address, int port, int bufferSize)
            : base(IPAddress.Parse(address), port)
        {
            Address = address;
            Port = port;
            BufferSize = bufferSize;
        }

        protected override TcpSession CreateSession() => new HttpSessionAdapter(this);

        internal void RaiseClientConnected(ISocketAdapter s) => ClientConnected?.Invoke(s);
        internal void RaiseClientDisconnected(ISocketAdapter s) => ClientDisconnected?.Invoke(s);

        void IServerAdapter.Start() => base.Start();
        void IServerAdapter.Stop() => base.Stop();
        void IServerAdapter.Broadcast(byte[] packet) { }
    }

    #endregion

    #region HTTPS Session

    public class HttpsSessionAdapter : HttpsSession, ISocketAdapter
    {
        public long Id { get; set; }
        public new string Address => ((IPEndPoint)Socket?.RemoteEndPoint)?.Address.ToString() ?? "";
        int ISocketAdapter.Port => ((IPEndPoint)Socket?.RemoteEndPoint)?.Port ?? 0;
        public new bool IsConnected => base.IsConnected;
        public NetworkProtocol Protocol => NetworkProtocol.HTTPS;

        // Ping/latency
        public int LatencyMs { get; set; } = -1;
        public long LastPingTicks { get; set; }
        public long PingSentTicks { get; set; }
        public NetworkDestination CachedDestination { get; set; }

        public event Action<ISocketAdapter, byte[]> DataReceived;
        public event Action<ISocketAdapter> Connected;
        public event Action<ISocketAdapter> Disconnected;
        public event Action<ISocketAdapter, Exception> ErrorOccurred;

        private readonly HttpsServerAdapter _serverRef;

        public HttpsSessionAdapter(HttpsServerAdapter server) : base(server)
        {
            _serverRef = server;
        }

        protected override void OnReceivedRequest(HttpRequest request)
        {
            if (request.Method == "POST" && request.Url == "/event")
            {
                var body = new byte[request.BodyLength];
                System.Buffer.BlockCopy(request.BodyBytes, 0, body, 0, (int)request.BodyLength);
                DataReceived?.Invoke(this, body);
                SendResponseAsync(Response.MakeOkResponse());
            }
            else
            {
                SendResponseAsync(Response.MakeErrorResponse(404));
            }
        }

        protected override void OnConnected()
        {
            Connected?.Invoke(this);
            _serverRef.RaiseClientConnected(this);
        }

        protected override void OnDisconnected()
        {
            Disconnected?.Invoke(this);
            _serverRef.RaiseClientDisconnected(this);
        }

        void ISocketAdapter.Send(byte[] buf)
        {
            var response = new HttpResponse();
            response.SetBegin(200);
            response.SetContentType("application/octet-stream");
            response.SetBody(buf);
            SendResponseAsync(response);
        }

        void ISocketAdapter.SendAsync(byte[] buf) => ((ISocketAdapter)this).Send(buf);
        void ISocketAdapter.Connect() { }
        void ISocketAdapter.Disconnect() => base.Disconnect();
        void ISocketAdapter.Reconnect() { }
    }

    #endregion

    #region HTTPS Server

    public class HttpsServerAdapter : HttpsServer, IServerAdapter
    {
        public new string Address { get; }
        public new int Port { get; }
        public int BufferSize { get; }
        public NetworkProtocol Protocol => NetworkProtocol.HTTPS;

        public event Action<ISocketAdapter> ClientConnected;
        public event Action<ISocketAdapter> ClientDisconnected;

        public HttpsServerAdapter(string address, int port, int bufferSize, SslContext context)
            : base(context, IPAddress.Parse(address), port)
        {
            Address = address;
            Port = port;
            BufferSize = bufferSize;
        }

        protected override SslSession CreateSession() => new HttpsSessionAdapter(this);

        internal void RaiseClientConnected(ISocketAdapter s) => ClientConnected?.Invoke(s);
        internal void RaiseClientDisconnected(ISocketAdapter s) => ClientDisconnected?.Invoke(s);

        void IServerAdapter.Start() => base.Start();
        void IServerAdapter.Stop() => base.Stop();
        void IServerAdapter.Broadcast(byte[] packet) { }
    }

    #endregion
}
