using System;
using System.Net;
using System.Text;

namespace NetCoreServer
{
    /// <summary>
    /// WebSocket server
    /// </summary>
    /// <remarks> WebSocket server is used to communicate with clients using WebSocket protocol. Thread-safe.</remarks>
    public class WsServer : HttpServer, IWebSocket
    {
        internal readonly WebSocket WebSocket;

        /// <summary>
        /// Initialize WebSocket server with a given IP address and port number
        /// </summary>
        /// <param name="address">IP address</param>
        /// <param name="port">Port number</param>
        public WsServer(IPAddress address, int port) : base(address, port) { WebSocket = new WebSocket(this); }
        /// <summary>
        /// Initialize WebSocket server with a given IP address and port number
        /// </summary>
        /// <param name="address">IP address</param>
        /// <param name="port">Port number</param>
        public WsServer(string address, int port) : base(address, port) { WebSocket = new WebSocket(this); }
        /// <summary>
        /// Initialize WebSocket server with a given DNS endpoint
        /// </summary>
        /// <param name="endpoint">DNS endpoint</param>
        public WsServer(DnsEndPoint endpoint) : base(endpoint) { WebSocket = new WebSocket(this); }
        /// <summary>
        /// Initialize WebSocket server with a given IP endpoint
        /// </summary>
        /// <param name="endpoint">IP endpoint</param>
        public WsServer(IPEndPoint endpoint) : base(endpoint) { WebSocket = new WebSocket(this); }

        #region Session management

        public virtual bool CloseAll() => CloseAll(0, Span<byte>.Empty);
        public virtual bool CloseAll(int status) => CloseAll(status, Span<byte>.Empty);
        public virtual bool CloseAll(int status, string text) => CloseAll(status, Encoding.UTF8.GetBytes(text));
        public virtual bool CloseAll(int status, ReadOnlySpan<char> text) => CloseAll(status, Encoding.UTF8.GetBytes(text.ToArray()));
        public virtual bool CloseAll(int status, byte[] buffer) => CloseAll(status, buffer.AsSpan());
        public virtual bool CloseAll(int status, byte[] buffer, long offset, long size) => CloseAll(status, buffer.AsSpan((int)offset, (int)size));
        public virtual bool CloseAll(int status, ReadOnlySpan<byte> buffer)
        {
            lock (WebSocket.WsSendLock)
            {
                WebSocket.PrepareSendFrame(WebSocket.WS_FIN | WebSocket.WS_CLOSE, false, buffer, status);
                if (!Multicast(WebSocket.WsSendBuffer.AsSpan()))
                    return false;

                return base.DisconnectAll();
            }
        }

        #endregion

        #region Multicasting

        public override bool Multicast(ReadOnlySpan<byte> buffer)
        {
            if (!IsStarted)
                return false;

            if (buffer.IsEmpty)
                return true;

            // Multicast data to all WebSocket sessions
            foreach (var session in Sessions.Values)
            {
                if (session is WsSession wsSession)
                {
                    if (wsSession.WebSocket.WsHandshaked)
                        wsSession.SendAsync(buffer);
                }
            }

            return true;
        }

        #endregion

        #region WebSocket multicast text methods

        public bool MulticastText(string text) => MulticastText(Encoding.UTF8.GetBytes(text));
        public bool MulticastText(ReadOnlySpan<char> text) => MulticastText(Encoding.UTF8.GetBytes(text.ToArray()));
        public bool MulticastText(byte[] buffer) => MulticastText(buffer.AsSpan());
        public bool MulticastText(byte[] buffer, long offset, long size) => MulticastText(buffer.AsSpan((int)offset, (int)size));
        public bool MulticastText(ReadOnlySpan<byte> buffer)
        {
            lock (WebSocket.WsSendLock)
            {
                WebSocket.PrepareSendFrame(WebSocket.WS_FIN | WebSocket.WS_TEXT, false, buffer);
                return Multicast(WebSocket.WsSendBuffer.AsSpan());
            }
        }


        #endregion

        #region WebSocket multicast binary methods

        public bool MulticastBinary(string text) => MulticastBinary(Encoding.UTF8.GetBytes(text));
        public bool MulticastBinary(ReadOnlySpan<char> text) => MulticastBinary(Encoding.UTF8.GetBytes(text.ToArray()));
        public bool MulticastBinary(byte[] buffer) => MulticastBinary(buffer.AsSpan());
        public bool MulticastBinary(byte[] buffer, long offset, long size) => MulticastBinary(buffer.AsSpan((int)offset, (int)size));
        public bool MulticastBinary(ReadOnlySpan<byte> buffer)
        {
            lock (WebSocket.WsSendLock)
            {
                WebSocket.PrepareSendFrame(WebSocket.WS_FIN | WebSocket.WS_BINARY, false, buffer);
                return Multicast(WebSocket.WsSendBuffer.AsSpan());
            }
        }


        #endregion

        #region WebSocket multicast ping methods

        public bool MulticastPing(string text) => MulticastPing(Encoding.UTF8.GetBytes(text));
        public bool MulticastPing(ReadOnlySpan<char> text) => MulticastPing(Encoding.UTF8.GetBytes(text.ToArray()));
        public bool MulticastPing(byte[] buffer) => MulticastPing(buffer.AsSpan());
        public bool MulticastPing(byte[] buffer, long offset, long size) => MulticastPing(buffer.AsSpan((int)offset, (int)size));
        public bool MulticastPing(ReadOnlySpan<byte> buffer)
        {
            lock (WebSocket.WsSendLock)
            {
                WebSocket.PrepareSendFrame(WebSocket.WS_FIN | WebSocket.WS_PING, false, buffer);
                return Multicast(WebSocket.WsSendBuffer.AsSpan());
            }
        }

        #endregion

        protected override TcpSession CreateSession() { return new WsSession(this); }

        #region TrashImplementation
        /// <summary>
        /// Handle WebSocket client connecting notification
        /// </summary>
        /// <remarks>Notification is called when WebSocket client is connecting to the server. You can handle the connection and change WebSocket upgrade HTTP request by providing your own headers.</remarks>
        /// <param name="request">WebSocket upgrade HTTP request</param>
        public void OnWsConnecting(HttpRequest request) {}
        /// <summary>
        /// Handle WebSocket client connected notification
        /// </summary>
        /// <param name="response">WebSocket upgrade HTTP response</param>
        public void OnWsConnected(HttpResponse response) {}

        /// <summary>
        /// Handle WebSocket server session validating notification
        /// </summary>
        /// <remarks>Notification is called when WebSocket client is connecting to the server. You can handle the connection and validate WebSocket upgrade HTTP request.</remarks>
        /// <param name="request">WebSocket upgrade HTTP request</param>
        /// <param name="response">WebSocket upgrade HTTP response</param>
        /// <returns>return 'true' if the WebSocket update request is valid, 'false' if the WebSocket update request is not valid</returns>
        public bool OnWsConnecting(HttpRequest request, HttpResponse response) { return true; }
        /// <summary>
        /// Handle WebSocket server session connected notification
        /// </summary>
        /// <param name="request">WebSocket upgrade HTTP request</param>
        public void OnWsConnected(HttpRequest request) {}

        /// <summary>
        /// Handle WebSocket client disconnecting notification
        /// </summary>
        public void OnWsDisconnecting() {}
        /// <summary>
        /// Handle WebSocket client disconnected notification
        /// </summary>
        public void OnWsDisconnected() {}

        /// <summary>
        /// Handle WebSocket received notification
        /// </summary>
        /// <param name="buffer">Received buffer</param>
        /// <param name="offset">Received buffer offset</param>
        /// <param name="size">Received buffer size</param>
        public void OnWsReceived(byte[] buffer, long offset, long size) {}

        /// <summary>
        /// Handle WebSocket client close notification
        /// </summary>
        /// <param name="buffer">Received buffer</param>
        /// <param name="offset">Received buffer offset</param>
        /// <param name="size">Received buffer size</param>
        /// <param name="status">WebSocket close status (default is 1000)</param>
        public void OnWsClose(byte[] buffer, long offset, long size, int status = 1000) {}

        /// <summary>
        /// Handle WebSocket ping notification
        /// </summary>
        /// <param name="buffer">Received buffer</param>
        /// <param name="offset">Received buffer offset</param>
        /// <param name="size">Received buffer size</param>
        public void OnWsPing(byte[] buffer, long offset, long size) {}

        /// <summary>
        /// Handle WebSocket pong notification
        /// </summary>
        /// <param name="buffer">Received buffer</param>
        /// <param name="offset">Received buffer offset</param>
        /// <param name="size">Received buffer size</param>
        public void OnWsPong(byte[] buffer, long offset, long size) {}

        /// <summary>
        /// Handle WebSocket error notification
        /// </summary>
        /// <param name="error">Error message</param>
        public void OnWsError(string error) {}

        /// <summary>
        /// Handle socket error notification
        /// </summary>
        /// <param name="error">Socket error</param>
        public void OnWsError(System.Net.Sockets.SocketError error) {}

        /// <summary>
        /// Send WebSocket server upgrade response
        /// </summary>
        /// <param name="response">WebSocket upgrade HTTP response</param>
        public void SendUpgrade(HttpResponse response) {}
        #endregion
    }
}
