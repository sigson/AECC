using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using AECC.Core.Logging;
using AECC.Extensions;
using AECC.Harness.Model;
using AECC.Harness.Services;
using AECC.Network.Adapters;
using NetCoreServer;

namespace AECC.Network
{
    public
#if GODOT4_0_OR_GREATER
    partial
#endif
    class NetworkService : IService
    {
        // =====================================================================
        //  Singleton
        // =====================================================================

        private static NetworkService _cacheInstance;
        public static NetworkService instance
        {
            get
            {
                if (_cacheInstance == null)
                    _cacheInstance = SGT.Get<NetworkService>();
                return _cacheInstance;
            }
        }

        // =====================================================================
        //  Configuration — populated before initialization step
        // =====================================================================

        /// <summary>
        /// List of endpoint destinations describing which protocols to start.
        /// Populate this before the service initialization step runs.
        /// Use IsListener = true for server endpoints, false for client connections.
        /// </summary>
        public List<NetworkDestination> EndpointConfigs = new();

        /// <summary>
        /// True if this process acts as a server for at least one endpoint.
        /// </summary>
        public bool IsServer => EndpointConfigs.Any(c => c.IsListener);

        // =====================================================================
        //  Runtime state
        // =====================================================================

        /// <summary>Event manager for dispatching events.</summary>
        public EventManager EventManager { get; } = new();

        /// <summary>All active server listeners, keyed by (Protocol, Port).</summary>
        public ConcurrentDictionary<(NetworkProtocol, int), IServerAdapter> Servers = new();

        /// <summary>All active client connections, keyed by (Protocol, Host, Port).</summary>
        public ConcurrentDictionary<(NetworkProtocol, string, int), ISocketAdapter> ClientSockets = new();

        /// <summary>All confirmed sockets by ID (server-side).</summary>
        public ConcurrentDictionary<long, ISocketAdapter> SocketsById = new();

        /// <summary>Per-socket stream frame accumulators (for stream-based protocols).</summary>
        private ConcurrentDictionary<ISocketAdapter, StreamFrameAccumulator> _accumulators = new();

        /// <summary>Per-socket RPC bridges.</summary>
        private ConcurrentDictionary<ISocketAdapter, RpcBridge> _rpcBridges = new();

        /// <summary>Identity manager for connection-oriented protocols.</summary>
        private SocketIdentityManager _identityManager;

        /// <summary>Reconnection timers for client sockets (TimerCompat-based).</summary>
        private ConcurrentDictionary<(NetworkProtocol, string, int), TimerCompat> _reconnectTimers = new();

        /// <summary>
        /// Outbound buffer hub. Buffers events while connections are being established
        /// and implements level-0 (hot) / level-1 (batched) send semantics.
        /// </summary>
        public OutboundBufferHub OutboundBuffer { get; private set; }

        /// <summary>
        /// Tracks which destinations have had connections auto-created.
        /// </summary>
        private ConcurrentDictionary<(NetworkProtocol, string, int), NetworkDestination> _autoCreatedConfigs = new();

        /// <summary>
        /// Ping service — periodic latency measurement for all confirmed sockets.
        /// Access LatencyMs on any ISocketAdapter to read the latest RTT.
        /// </summary>
        public PingService PingService { get; private set; }

        // ── Events for external consumers ──

        public event Action<ISocketAdapter> OnSocketReady;
        public event Action<ISocketAdapter> OnSocketDisconnected;

        /// <summary>
        /// Fired when a socket exceeds PingSettings.TimeoutMs without a Pong.
        /// Subscribe to take action (e.g., disconnect the socket).
        /// </summary>
        public event Action<ISocketAdapter> OnPingTimeout;

        // =====================================================================
        //  Initialization
        // =====================================================================

        public override void InitializeProcess()
        {
            // Ensure the event type registry is populated before any traffic
            NetworkEventRegistry.EnsureInitialized();

            EventManager.Initialize(this);

            _identityManager = new SocketIdentityManager(
                isServer: IsServer,
                onSocketReady: OnSocketIdentityReady,
                onSocketDisconnected: OnSocketIdentityLost,
                onFlushMessage: (socket, payload) => ProcessIncomingEvent(socket, payload)
            );

            OutboundBuffer = new OutboundBufferHub(
                connectFactory: AutoCreateConnection,
                socketResolver: ResolveSocket
            );

            // ── Ping service ──
            // Server pings all confirmed sockets (SocketsById).
            // Client also pings its server connections (wrapped into SocketsById after identity).
            PingService = new PingService(
                socketsProvider: () => SocketsById,
                sendSystemMessage: SendSystemMessage
            );
            PingService.OnPingTimeout += (socket) => OnPingTimeout?.Invoke(socket);

            foreach (var config in EndpointConfigs)
            {
                try
                {
                    if (config.IsListener)
                        StartListener(config);
                    else
                        StartClient(config);
                }
                catch (Exception ex)
                {
                    NLogger.LogError($"Failed to initialize {config.Protocol} endpoint {config.Host}:{config.Port} — {ex.Message}");
                }
            }

            // Start ping timer after all endpoints are up
            PingService.Start();
        }

        // =====================================================================
        //  System message helper (used by identity, ping, etc.)
        // =====================================================================

        /// <summary>
        /// Send a system-level message (identity handshake, ping/pong, etc.)
        /// with proper framing for the socket's protocol.
        /// </summary>
        internal void SendSystemMessage(ISocketAdapter socket, byte msgType, byte[] payload)
        {
            byte[] frame;
            if (ProtocolTraits.UsesStreamFraming(socket.Protocol))
                frame = StreamFrameAccumulator.Pack(msgType, payload);
            else
                frame = DatagramFrame.Pack(msgType, payload);

            socket.SendAsync(frame);
        }

        // =====================================================================
        //  Listener (Server) startup
        // =====================================================================

        private void StartListener(NetworkDestination config)
        {
            // ── Godot protocols are client-only ──
            if (ProtocolTraits.IsGodotProtocol(config.Protocol))
                throw new NotSupportedException(
                    $"Protocol {config.Protocol} is client-only and cannot be used as a server/listener. " +
                    "Use WebSocket or WebSocketSecure (NetCoreServer-based) for server endpoints.");

            IServerAdapter server;

            switch (config.Protocol)
            {
                case NetworkProtocol.TCP:
                    server = new TcpServerAdapter(config.Host, config.Port, config.BufferSize);
                    break;

                case NetworkProtocol.UDP:
                {
                    var udp = new UdpServerAdapter(config.Host, config.Port, config.BufferSize);
                    udp.DatagramReceived += (endpoint, data) => HandleUdpDatagram(udp, endpoint, data);
                    server = udp;
                    break;
                }

                case NetworkProtocol.WebSocket:
                    server = new WsServerAdapter(config.Host, config.Port, config.BufferSize);
                    break;

                case NetworkProtocol.WebSocketSecure:
                {
                    var sslCtx = CreateSslContext(config);
                    server = new WssServerAdapter(config.Host, config.Port, config.BufferSize, sslCtx);
                    break;
                }

                case NetworkProtocol.HTTP:
                    server = new HttpServerAdapter(config.Host, config.Port, config.BufferSize);
                    break;

                case NetworkProtocol.HTTPS:
                {
                    var sslCtx = CreateSslContext(config);
                    server = new HttpsServerAdapter(config.Host, config.Port, config.BufferSize, sslCtx);
                    break;
                }

                default:
                    throw new NotSupportedException($"Protocol {config.Protocol} not supported for listener");
            }

            server.ClientConnected += OnTransportClientConnected;
            server.ClientDisconnected += OnTransportClientDisconnected;
            server.Start();

            Servers[(config.Protocol, config.Port)] = server;
            NLogger.LogNetwork($"Server listening: {config.Protocol} on {config.Host}:{config.Port}");
        }

        // =====================================================================
        //  Client startup (explicit + on-demand)
        // =====================================================================

        private void StartClient(NetworkDestination config)
        {
            var key = config.RouteKey;

            if (ClientSockets.ContainsKey(key))
            {
                NLogger.LogNetwork($"Client connection already exists for {config.Protocol} {config.Host}:{config.Port}, skipping");
                return;
            }

            // ── Godot protocols cannot be auto-created here ──
            // They must be registered externally via RegisterExternalClient().
            if (ProtocolTraits.IsGodotProtocol(config.Protocol))
            {
                NLogger.LogError(
                    $"Cannot auto-create {config.Protocol} client from StartClient(). " +
                    "Godot WebSocket clients are Godot Nodes — create the WSClientGodot in the scene tree, " +
                    "call InitializeClient(), then register via NetworkService.RegisterExternalClient().");
                return;
            }

            ISocketAdapter client;

            switch (config.Protocol)
            {
                case NetworkProtocol.TCP:
                    client = new TcpClientAdapter(config.Host, config.Port, config.BufferSize);
                    break;

                case NetworkProtocol.UDP:
                    client = new UdpClientAdapter(config.Host, config.Port, config.BufferSize);
                    break;

                case NetworkProtocol.WebSocket:
                    client = new WsClientAdapter(config.Host, config.Port, config.BufferSize);
                    break;

                case NetworkProtocol.WebSocketSecure:
                {
                    var sslCtx = CreateSslContext(config);
                    client = new WssClientAdapter(sslCtx, config.Host, config.Port, config.BufferSize);
                    break;
                }

                default:
                    throw new NotSupportedException($"Protocol {config.Protocol} not supported for client");
            }

            WireClientEvents(client, config, key);
            client.Connect();

            NLogger.LogNetwork($"Client connecting: {config.Protocol} to {config.Host}:{config.Port}");
        }

        /// <summary>
        /// Register an externally-created ISocketAdapter (e.g. WSClientGodot) with the network service.
        ///
        /// The caller is responsible for:
        ///   1. Creating and initializing the socket (e.g. WSClientGodot.InitializeClient())
        ///   2. Adding it to the Godot scene tree (if applicable)
        ///   3. Calling Connect() after registration, OR setting autoConnect = true
        ///
        /// This hooks the socket into the full event bus pipeline:
        /// identity handshake, outbound buffering, ping service, event dispatch.
        ///
        /// Usage (Godot):
        ///   var ws = new WSClientGodot();
        ///   AddChild(ws);
        ///   ws.InitializeClient("example.com", 443, protocol: NetworkProtocol.WebSocketSecureGodot);
        ///   var dest = NetworkDestination.ForHost("example.com", 443, NetworkProtocol.WebSocketSecureGodot);
        ///   NetworkService.instance.RegisterExternalClient(ws, dest);
        ///   ws.Connect();
        /// </summary>
        /// <param name="client">Pre-created socket adapter.</param>
        /// <param name="config">Destination describing this connection (IsListener must be false).</param>
        /// <param name="autoConnect">If true, calls client.Connect() after registration.</param>
        public void RegisterExternalClient(ISocketAdapter client, NetworkDestination config, bool autoConnect = false)
        {
            if (config.IsListener)
                throw new ArgumentException("Cannot register a client socket with IsListener = true");

            var key = config.RouteKey;

            if (ClientSockets.ContainsKey(key))
            {
                NLogger.LogNetwork(
                    $"Client connection already exists for {config.Protocol} {config.Host}:{config.Port}, skipping registration");
                return;
            }

            WireClientEvents(client, config, key);

            NLogger.LogNetwork($"External client registered: {config.Protocol} to {config.Host}:{config.Port}");

            if (autoConnect)
                client.Connect();
        }

        /// <summary>
        /// Wire up a client socket's events and register it in ClientSockets.
        /// Shared by StartClient and RegisterExternalClient.
        /// Also caches the NetworkDestination on the socket for zero-alloc reply routing.
        /// </summary>
        private void WireClientEvents(ISocketAdapter client, NetworkDestination config,
            (NetworkProtocol, string, int) key)
        {
            // Cache the destination on the socket for reply routing
            client.CachedDestination = config.IsListener ? config.ToClientDestination() : config;

            client.Connected += OnTransportClientSelfConnected;
            client.Disconnected += (s) => OnTransportClientSelfDisconnected(s, config);
            client.DataReceived += OnRawDataReceived;

            ClientSockets[key] = client;
        }

        /// <summary>
        /// Auto-create a client connection for a destination that has no existing socket.
        /// Called by OutboundBufferHub when it encounters an unknown destination.
        /// </summary>
        private void AutoCreateConnection(NetworkDestination dest)
        {
            if (dest.IsSocketRouted)
            {
                NLogger.LogError($"Cannot auto-create connection for socket-routed destination ID={dest.SocketId}");
                return;
            }

            // Godot protocols can't be auto-created
            if (ProtocolTraits.IsGodotProtocol(dest.Protocol))
            {
                NLogger.LogError(
                    $"Cannot auto-create {dest.Protocol} connection to {dest.Host}:{dest.Port}. " +
                    "Register Godot WebSocket clients manually via RegisterExternalClient().");
                return;
            }

            var key = dest.RouteKey;

            // Ensure we use a non-listener config for the client
            var clientDest = dest.IsListener ? dest.ToClientDestination() : dest;
            _autoCreatedConfigs[key] = clientDest;

            NLogger.LogNetwork($"Auto-creating connection: {clientDest.Protocol} to {clientDest.Host}:{clientDest.Port}");
            StartClient(clientDest);
        }

        // =====================================================================
        //  Transport event handlers
        // =====================================================================

        private void OnTransportClientConnected(ISocketAdapter socket)
        {
            socket.DataReceived += OnRawDataReceived;

            if (ProtocolTraits.IsConnectionOriented(socket.Protocol))
            {
                if (ProtocolTraits.UsesStreamFraming(socket.Protocol))
                    _accumulators[socket] = new StreamFrameAccumulator();

                _identityManager.ServerOnClientConnected(socket);
            }
            else
            {
                OnSocketIdentityReady(socket, SocketReadyReason.NewConnection);
            }
        }

        private void OnTransportClientDisconnected(ISocketAdapter socket)
        {
            _accumulators.TryRemove(socket, out _);

            if (_rpcBridges.TryRemove(socket, out var bridge))
                bridge.Dispose();

            if (ProtocolTraits.IsConnectionOriented(socket.Protocol))
                _identityManager.ServerOnClientDisconnected(socket);

            if (socket.Id != 0)
                SocketsById.TryRemove(socket.Id, out _);
        }

        private void OnTransportClientSelfConnected(ISocketAdapter socket)
        {
            if (ProtocolTraits.UsesStreamFraming(socket.Protocol))
                _accumulators[socket] = new StreamFrameAccumulator();

            if (ProtocolTraits.IsConnectionOriented(socket.Protocol))
                _identityManager.ClientOnConnected(socket);

            var key = (socket.Protocol, socket.Address, socket.Port);
            if (_reconnectTimers.TryRemove(key, out var timer))
            {
                timer.Stop();
                timer.Dispose();
            }
        }

        private void OnTransportClientSelfDisconnected(ISocketAdapter socket, NetworkDestination config)
        {
            _accumulators.TryRemove(socket, out _);

            if (_rpcBridges.TryRemove(socket, out var bridge))
                bridge.Dispose();

            OutboundBuffer.OnSocketDisconnected(socket, config);

            // ── Dispatch SocketDisconnectedEvent into the event bus ──
            DispatchSocketDisconnectedEvent(socket);

            OnSocketDisconnected?.Invoke(socket);

            var key = config.RouteKey;
            if (!_reconnectTimers.ContainsKey(key))
            {
                var timer = new TimerCompat(2000, (sender, e) =>
                {
                    try
                    {
                        NLogger.LogNetwork($"Reconnecting: {config.Protocol} to {config.Host}:{config.Port}");
                        socket.Reconnect();
                    }
                    catch (Exception ex)
                    {
                        NLogger.LogError($"Reconnect failed: {ex.Message}");
                    }
                }, loop: true, asyncRun: true);

                _reconnectTimers[key] = timer;
                timer.Start();
            }
        }

        // =====================================================================
        //  Identity callbacks
        // =====================================================================

        private void OnSocketIdentityReady(ISocketAdapter socket, SocketReadyReason reason)
        {
            if (socket.Id != 0)
                SocketsById[socket.Id] = socket;

            // ── Cache destination on the socket for zero-alloc reply routing ──
            if (socket.CachedDestination == null)
            {
                // Server-side sessions: route by socket ID
                if (socket.Id != 0)
                {
                    socket.CachedDestination = NetworkDestination.ForSocket(socket.Id);
                }
                else
                {
                    // Fallback: look up from ClientSockets
                    foreach (var kvp in ClientSockets)
                    {
                        if (kvp.Value == socket)
                        {
                            socket.CachedDestination = NetworkDestination.ForHost(
                                kvp.Key.Item2, kvp.Key.Item3, kvp.Key.Item1);
                            break;
                        }
                    }
                }
            }
            else if (IsServer && socket.Id != 0 && !socket.CachedDestination.IsSocketRouted)
            {
                // Server-side: upgrade to socket-routed destination now that we have the ID
                socket.CachedDestination = NetworkDestination.ForSocket(socket.Id);
            }

            // RPC bridge: skip for Godot protocols (StreamJsonRpc uses System.IO.Pipelines
            // which requires async threading — incompatible with single-threaded Godot web)
            if (!ProtocolTraits.IsGodotProtocol(socket.Protocol))
            {
                var rpcBridge = new RpcBridge(socket);
                _rpcBridges[socket] = rpcBridge;
                rpcBridge.Start();
            }

            if (IsServer)
                EventManager.MaliciousScoringStorage[socket.Id] = new ScoreObject { SocketId = socket.Id };

            OutboundBuffer.OnSocketReady(socket, socket.CachedDestination);

            // ── Dispatch lifecycle event into the event bus ──
            switch (reason)
            {
                case SocketReadyReason.NewConnection:
                    DispatchSocketConnectedEvent(socket);
                    break;

                case SocketReadyReason.Restored:
                    DispatchSocketReconnectedEvent(socket);
                    break;
            }

            OnSocketReady?.Invoke(socket);
        }

        private void OnSocketIdentityLost(ISocketAdapter socket)
        {
            if (IsServer)
                EventManager.MaliciousScoringStorage.TryRemove(socket.Id, out _);

            // ── Dispatch SocketDisconnectedEvent into the event bus ──
            DispatchSocketDisconnectedEvent(socket);

            OnSocketDisconnected?.Invoke(socket);
        }

        // =====================================================================
        //  Socket lifecycle event dispatchers
        // =====================================================================

        private void DispatchSocketConnectedEvent(ISocketAdapter socket)
        {
            var evt = new SocketConnectedEvent
            {
                SocketId = socket.Id,
                Address = socket.Address,
                Port = socket.Port,
                ProtocolId = (int)socket.Protocol
            };

            try
            {
                EventManager.Dispatch(evt);
            }
            catch (Exception ex)
            {
                NLogger.LogError($"Failed to dispatch SocketConnectedEvent for socket {socket.Id}: {ex.Message}");
            }
        }

        private void DispatchSocketReconnectedEvent(ISocketAdapter socket)
        {
            var evt = new SocketReconnectedEvent
            {
                SocketId = socket.Id,
                Address = socket.Address,
                Port = socket.Port,
                ProtocolId = (int)socket.Protocol
            };

            try
            {
                EventManager.Dispatch(evt);
            }
            catch (Exception ex)
            {
                NLogger.LogError($"Failed to dispatch SocketReconnectedEvent for socket {socket.Id}: {ex.Message}");
            }
        }

        private void DispatchSocketDisconnectedEvent(ISocketAdapter socket)
        {
            var evt = new SocketDisconnectedEvent
            {
                SocketId = socket.Id,
                Address = socket.Address,
                Port = socket.Port,
                ProtocolId = (int)socket.Protocol
            };

            try
            {
                EventManager.Dispatch(evt);
            }
            catch (Exception ex)
            {
                NLogger.LogError($"Failed to dispatch SocketDisconnectedEvent for socket {socket.Id}: {ex.Message}");
            }
        }

        // =====================================================================
        //  Raw data reception & demultiplexing
        // =====================================================================

        private void OnRawDataReceived(ISocketAdapter socket, byte[] rawData)
        {
            if (ProtocolTraits.UsesStreamFraming(socket.Protocol))
            {
                if (!_accumulators.TryGetValue(socket, out var acc))
                {
                    acc = new StreamFrameAccumulator();
                    _accumulators[socket] = acc;
                }

                var messages = acc.Feed(rawData);
                foreach (var (type, payload) in messages)
                {
                    HandleFramedMessage(socket, type, payload);
                }
            }
            else
            {
                var (type, payload) = DatagramFrame.Unpack(rawData);
                HandleFramedMessage(socket, type, payload);
            }
        }

        private void HandleFramedMessage(ISocketAdapter socket, byte msgType, byte[] payload)
        {
            // ── System messages (identity handshake + ping/pong) ──
            if (msgType >= MessageType.SystemMin && msgType <= MessageType.SystemMax)
            {
                // Identity handshake messages
                if (msgType >= MessageType.AssignId && msgType <= MessageType.RestoreAccepted)
                {
                    bool handled;
                    if (IsServer)
                        handled = _identityManager.ServerProcessSystemMessage(socket, msgType, payload);
                    else
                        handled = _identityManager.ClientProcessSystemMessage(socket, msgType, payload);

                    if (handled) return;
                }

                // Ping / Pong
                if (msgType == MessageType.Ping)
                {
                    PingService.HandlePing(socket, payload);
                    return;
                }

                if (msgType == MessageType.Pong)
                {
                    PingService.HandlePong(socket, payload);
                    return;
                }
            }

            // ── RPC messages → StreamJsonRpc bridge ──
            if (msgType == RpcBridge.RpcMessageType)
            {
                if (_rpcBridges.TryGetValue(socket, out var rpcBridge))
                    rpcBridge.FeedIncoming(payload);
                return;
            }

            // ── Event messages ──
            if (msgType == MessageType.Event)
            {
                if (IsServer && _identityManager.ServerTryQueueMessage(socket, payload))
                    return;

                ProcessIncomingEvent(socket, payload);
            }
        }

        private void HandleUdpDatagram(UdpServerAdapter server, System.Net.EndPoint endpoint, byte[] data)
        {
            var (type, payload) = DatagramFrame.Unpack(data);

            if (type == MessageType.Event)
            {
                try
                {
                    var evt = NetworkSerialization.Deserialize(payload);
                    EventManager.DispatchFromNetwork(evt, null);
                }
                catch (Exception ex)
                {
                    NLogger.LogError($"UDP event deserialization failed: {ex.Message}");
                }
            }
        }

        private void ProcessIncomingEvent(ISocketAdapter socket, byte[] payload)
        {
            try
            {
                var evt = NetworkSerialization.Deserialize(payload);
                EventManager.DispatchFromNetwork(evt, socket);
            }
            catch (Exception ex)
            {
                NLogger.LogError($"Event deserialization failed from socket {socket.Id}: {ex.Message}");
            }
        }

        // =====================================================================
        //  Sending events
        // =====================================================================

        internal void SendEvent(NetworkEvent evt)
        {
            byte[] serialized = evt.GetSerializedPacket();

            if (evt.Destination != null)
                SendToDestination(evt.Destination, serialized, evt.BufferLevel);

            if (evt.Destinations != null)
            {
                foreach (var dest in evt.Destinations)
                    SendToDestination(dest, serialized, evt.BufferLevel);
            }
        }

        private void SendToDestination(NetworkDestination dest, byte[] serializedPayload, int bufferLevel)
        {
            NetworkProtocol protocol = dest.Protocol;
            if (dest.IsSocketRouted)
            {
                if (SocketsById.TryGetValue(dest.SocketId, out var sock))
                    protocol = sock.Protocol;
                else if (_identityManager.GetSocketById(dest.SocketId) is ISocketAdapter s)
                    protocol = s.Protocol;
            }

            byte[] frame;
            if (ProtocolTraits.UsesStreamFraming(protocol))
                frame = StreamFrameAccumulator.Pack(MessageType.Event, serializedPayload);
            else
                frame = DatagramFrame.Pack(MessageType.Event, serializedPayload);

            OutboundBuffer.Enqueue(dest, frame, bufferLevel);
        }

        public ISocketAdapter ResolveSocket(NetworkDestination dest)
        {
            if (dest.IsSocketRouted)
            {
                if (SocketsById.TryGetValue(dest.SocketId, out var s))
                    return s;

                return _identityManager.GetSocketById(dest.SocketId);
            }

            var key = dest.RouteKey;
            if (ClientSockets.TryGetValue(key, out var client))
                return client;

            return null;
        }

        public void BroadcastRaw(byte[] serializedPayload)
        {
            foreach (var kvp in SocketsById)
            {
                var socket = kvp.Value;
                byte[] frame;
                if (ProtocolTraits.UsesStreamFraming(socket.Protocol))
                    frame = StreamFrameAccumulator.Pack(MessageType.Event, serializedPayload);
                else
                    frame = DatagramFrame.Pack(MessageType.Event, serializedPayload);

                socket.SendAsync(frame);
            }
        }

        public RpcBridge GetRpcBridge(ISocketAdapter socket)
        {
            _rpcBridges.TryGetValue(socket, out var bridge);
            return bridge;
        }

        public RpcBridge GetRpcBridge(long socketId)
        {
            if (SocketsById.TryGetValue(socketId, out var socket))
                return GetRpcBridge(socket);
            return null;
        }

        // =====================================================================
        //  Helpers
        // =====================================================================

        private static SslContext CreateSslContext(NetworkDestination config)
        {
            if (string.IsNullOrEmpty(config.CertificatePath))
                throw new InvalidOperationException($"SSL certificate required for {config.Protocol}");

            var context = new SslContext(SslProtocols.Tls12,
                new X509Certificate2(config.CertificatePath, config.CertificatePassword));
            return context;
        }

        // =====================================================================
        //  IService lifecycle
        // =====================================================================

        public override void OnDestroyReaction()
        {
            PingService?.Dispose();
            OutboundBuffer?.Dispose();

            foreach (var timer in _reconnectTimers.Values)
            {
                timer.Stop();
                timer.Dispose();
            }
            _reconnectTimers.Clear();

            foreach (var bridge in _rpcBridges.Values)
                bridge.Dispose();
            _rpcBridges.Clear();

            foreach (var server in Servers.Values)
                server.Stop();
            Servers.Clear();

            foreach (var client in ClientSockets.Values)
            {
                try { client.Disconnect(); } catch { }
            }
            ClientSockets.Clear();

            SocketsById.Clear();
            _autoCreatedConfigs.Clear();
        }

        public override void PostInitializeProcess()
        {
        }

        protected override Action<int>[] GetInitializationSteps()
        {
            return new Action<int>[]
            {
                (step) => { /* EndpointConfigs must be populated by this point */ },
                (step) => { InitializeProcess(); },
            };
        }

        protected override void SetupCallbacks(List<IService> allServices)
        {
        }
    }
}
