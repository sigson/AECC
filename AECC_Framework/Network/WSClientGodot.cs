#if GODOT && !GODOT4_0_OR_GREATER
using System;
using System.Collections.Generic;
using Godot;
using AECC.Core.Logging;

namespace AECC.Network
{
    /// <summary>
    /// Godot 3 WebSocket client adapter for the AECC network event bus.
    ///
    /// Strictly single-threaded: uses Godot's _PhysicsProcess polling model,
    /// no System.Threading, no ConcurrentCollections under contention.
    /// All callbacks (Connected, Disconnected, DataReceived, ErrorOccurred)
    /// fire on the Godot main thread.
    ///
    /// Designed for web exports where multithreading is unavailable.
    /// Client-only — cannot be used as a server/listener.
    ///
    /// Wire protocol: the transport delivers complete WebSocket messages,
    /// so DatagramFrame (type-byte prefix) is used — no length-prefix stream framing.
    /// NetworkService.OnRawDataReceived handles the demux automatically.
    /// </summary>
    public class WSClientGodot : Node, ISocketAdapter
    {
        #region Fields

        // Godot WebSocket client
        private Godot.WebSocketClient _client;

        // Connection state
        private bool _isConnected = false;
        private bool _isConnecting = false;
        private bool _isDisposed = false;

        // Parsed connection data
        private string _address;
        private int _port;
        private long _id;
        private NetworkProtocol _protocol = NetworkProtocol.WebSocketGodot;

        // WebSocket sub-protocols
        private string[] _wsProtocols = new string[] { };

        // ── Optional packet queuing (LIFO, one per physics frame) ──

        /// <summary>
        /// When true, outbound packets are cached and sent one-per-physics-frame (LIFO).
        /// Useful for throttling high-frequency updates on constrained web builds.
        /// When false (default), packets are sent immediately.
        /// </summary>
        [Export]
        public bool EnablePacketQueuing { get; set; } = false;

        // Single-threaded: plain Stack is sufficient (no cross-thread access)
        private readonly Stack<byte[]> _packetCache = new Stack<byte[]>();
        private bool _packetSentThisFrame = false;

        #endregion

        #region ISocketAdapter — Events

        public event Action<ISocketAdapter, byte[]> DataReceived;
        public event Action<ISocketAdapter> Connected;
        public event Action<ISocketAdapter> Disconnected;
        public event Action<ISocketAdapter, Exception> ErrorOccurred;

        #endregion

        #region ISocketAdapter — Properties

        public long Id
        {
            get => _id;
            set => _id = value;
        }

        public string Address => _address;
        public int Port => _port;

        public bool IsConnected =>
            _isConnected && _client != null && _client.GetPeer(1).IsConnectedToHost();

        public NetworkProtocol Protocol => _protocol;

        // ── Latency (updated by PingService) ──
        public int LatencyMs { get; set; } = -1;
        public long LastPingTicks { get; set; }
        public long PingSentTicks { get; set; }

        #endregion

        #region Godot Lifecycle

        public override void _Ready()
        {
        }

        public override void _PhysicsProcess(float delta)
        {
            // Poll the Godot WebSocket client (drives connect/receive/close callbacks)
            if (_client != null && (_isConnected || _isConnecting))
            {
                _client.Poll();
            }

            // ── Packet queuing: send at most one cached packet per physics frame ──
            _packetSentThisFrame = false;

            if (!EnablePacketQueuing || !IsConnected || _packetCache.Count == 0)
                return;

            if (_packetCache.Count > 0)
            {
                var packetToSend = _packetCache.Pop();
                DirectSend(packetToSend);
                _packetSentThisFrame = true;
            }
        }

        public override void _ExitTree()
        {
            Dispose();
        }

        #endregion

        #region Initialization

        /// <summary>
        /// Initialize the underlying Godot WebSocketClient for the given endpoint.
        /// Must be called before Connect().
        /// 
        /// The protocol (WebSocketGodot vs WebSocketSecureGodot) is auto-detected
        /// from the host scheme, or can be set explicitly via <paramref name="protocol"/>.
        /// </summary>
        public void InitializeClient(string host, int port, int bufferSize = 1024,
            NetworkProtocol protocol = NetworkProtocol.WebSocketGodot)
        {
            _protocol = protocol;

            string url;
            if (!host.Contains("s://"))
            {
                // Plain host — build ws:// URL
                url = $"ws://{host}:{port}";
            }
            else
            {
                // Full scheme already present (ws://, wss://, etc.)
                url = port == -1 ? host : $"{host}:{port}";

                // Auto-detect secure variant from scheme
                if (host.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
                    _protocol = NetworkProtocol.WebSocketSecureGodot;
            }

            ParseUrl(url);

            if (_client != null)
                CleanupClient();

            _client = new Godot.WebSocketClient();

            // Connect Godot signals
            _client.Connect("connection_closed", this, nameof(OnConnectionClosed));
            _client.Connect("connection_error", this, nameof(OnConnectionError));
            _client.Connect("connection_established", this, nameof(OnConnectionEstablished));
            _client.Connect("data_received", this, nameof(OnGodotDataReceived));
            _client.Connect("server_close_request", this, nameof(OnServerCloseRequest));
        }

        private void ParseUrl(string url)
        {
            try
            {
                var uri = new Uri(url);
                _address = uri.Host;
                _port = uri.Port;

                if (_port == -1)
                    _port = uri.Scheme == "wss" ? 443 : 80;
            }
            catch (Exception ex)
            {
                NLogger.LogError($"WSClientGodot: failed to parse URL '{url}': {ex.Message}");
                _address = "localhost";
                _port = 8080;
            }
        }

        private void CleanupClient()
        {
            if (_client == null) return;

            try
            {
                TryDisconnectSignal("connection_closed", nameof(OnConnectionClosed));
                TryDisconnectSignal("connection_error", nameof(OnConnectionError));
                TryDisconnectSignal("connection_established", nameof(OnConnectionEstablished));
                TryDisconnectSignal("data_received", nameof(OnGodotDataReceived));
                TryDisconnectSignal("server_close_request", nameof(OnServerCloseRequest));

                if (_isConnected)
                    _client.DisconnectFromHost();

                _client = null;
            }
            catch (Exception ex)
            {
                NLogger.LogError($"WSClientGodot: cleanup error: {ex.Message}");
            }
        }

        private void TryDisconnectSignal(string signal, string method)
        {
            if (_client.IsConnected(signal, this, method))
                _client.Disconnect(signal, this, method);
        }

        #endregion

        #region Godot Signal Handlers

        private void OnConnectionEstablished(string protocol = "")
        {
            _isConnected = true;
            _isConnecting = false;

            NLogger.LogNetwork($"WSClientGodot: connected (sub-protocol: '{protocol}')");
            Connected?.Invoke(this);
        }

        private void OnConnectionClosed(bool wasClean = false)
        {
            _isConnected = false;
            _isConnecting = false;

            NLogger.LogNetwork($"WSClientGodot: connection closed (clean: {wasClean})");
            Disconnected?.Invoke(this);

            if (!wasClean)
                ErrorOccurred?.Invoke(this, new Exception("WebSocket connection closed unexpectedly"));
        }

        private void OnConnectionError()
        {
            _isConnected = false;
            _isConnecting = false;

            NLogger.LogError("WSClientGodot: connection error");
            ErrorOccurred?.Invoke(this, new Exception("WebSocket connection error"));
        }

        /// <summary>
        /// Godot signal: raw data arrived from the server.
        /// Feeds complete WebSocket messages to the DataReceived event
        /// for NetworkService to demux via DatagramFrame.Unpack.
        /// </summary>
        private void OnGodotDataReceived()
        {
            try
            {
                if (!_isConnected)
                    OnConnectionEstablished();
            }
            catch (Exception ex)
            {
                NLogger.LogError($"WSClientGodot: state recovery error: {ex.Message}");
            }

            try
            {
                var peer = _client.GetPeer(1);

                while (peer.GetAvailablePacketCount() > 0)
                {
                    var packet = peer.GetPacket();
                    if (packet != null && packet.Length > 0)
                    {
                        // Feed raw bytes directly — NetworkService handles framing
                        DataReceived?.Invoke(this, packet);
                    }
                }
            }
            catch (Exception ex)
            {
                NLogger.LogError($"WSClientGodot: data receive error: {ex.Message}");
                ErrorOccurred?.Invoke(this, ex);
            }
        }

        private void OnServerCloseRequest(int code, string reason)
        {
            NLogger.LogNetwork($"WSClientGodot: server close request (code={code}, reason='{reason}')");
            _client.DisconnectFromHost(code, reason);
        }

        #endregion

        #region ISocketAdapter — Connection

        public void Connect()
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(WSClientGodot));
            if (_isConnected || _isConnecting) return;

            if (_client == null)
            {
                NLogger.LogError("WSClientGodot: client not initialized, call InitializeClient first");
                return;
            }

            _isConnecting = true;

            string scheme = _protocol == NetworkProtocol.WebSocketSecureGodot ? "wss" : "ws";
            string url = _port == -1 || (_port == 80 && scheme == "ws") || (_port == 443 && scheme == "wss")
                ? $"{scheme}://{_address}"
                : $"{scheme}://{_address}:{_port}";

            var err = _client.ConnectToUrl(url, _wsProtocols);

            if (err != Error.Ok)
            {
                _isConnecting = false;
                var exception = new Exception($"WSClientGodot: ConnectToUrl failed — {err}");
                ErrorOccurred?.Invoke(this, exception);
                throw exception;
            }
        }

        public void Disconnect()
        {
            if (_client == null || !_isConnected) return;

            _client.DisconnectFromHost(1000, "Client disconnect");
            _isConnected = false;
            _isConnecting = false;
        }

        public void Reconnect()
        {
            Disconnect();
            // Defer to next frame to let Godot finalize the close
            CallDeferred(nameof(Connect));
        }

        #endregion

        #region ISocketAdapter — Send

        /// <summary>
        /// Synchronous send. If packet queuing is enabled, the packet is cached
        /// and sent one-per-physics-frame (LIFO). Otherwise sends immediately.
        /// </summary>
        public void Send(byte[] buffer)
        {
            if (EnablePacketQueuing)
            {
                _packetCache.Push(buffer);
            }
            else
            {
                DirectSend(buffer);
            }
        }

        /// <summary>
        /// Asynchronous send — defers to the Godot main thread via CallDeferred.
        /// Safe to call from timer callbacks or any context.
        /// </summary>
        public void SendAsync(byte[] buffer)
        {
            if (!IsConnected)
            {
                ErrorOccurred?.Invoke(this, new InvalidOperationException("WSClientGodot: not connected"));
                return;
            }

            // CallDeferred guarantees execution on the Godot main thread
            CallDeferred(nameof(Send), buffer);
        }

        private void DirectSend(byte[] buffer)
        {
            if (!IsConnected)
            {
                var error = new InvalidOperationException("WSClientGodot: not connected");
                ErrorOccurred?.Invoke(this, error);
                throw error;
            }

            try
            {
                var peer = _client.GetPeer(1);
                var err = peer.PutPacket(buffer);

                if (err != Error.Ok)
                {
                    var exception = new Exception($"WSClientGodot: PutPacket failed — {err}");
                    ErrorOccurred?.Invoke(this, exception);
                    throw exception;
                }
            }
            catch (InvalidOperationException)
            {
                throw; // re-throw our own
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, ex);
                throw;
            }
        }

        #endregion

        #region Public Helpers

        public void SetProtocols(string[] protocols)
        {
            _wsProtocols = protocols ?? new string[] { };
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            Disconnect();
            CleanupClient();

            DataReceived = null;
            ErrorOccurred = null;
            Connected = null;
            Disconnected = null;
        }

        #endregion
    }
}
#endif
