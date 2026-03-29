using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using AECC.Core.Logging;
using AECC.Harness.Model;
using AECC.Harness.Services;

namespace AECC.Network
{
    /// <summary>
    /// IService wrapper around NetworkingInstance. Provides backward-compatible
    /// singleton access via NetworkService.instance while delegating all networking
    /// logic to the underlying NetworkingInstance.
    ///
    /// For multi-instance scenarios, create NetworkingInstance objects directly —
    /// they are fully self-contained and independent.
    ///
    /// Migration guide:
    ///   Old:  NetworkService.instance.EventManager.Dispatch(evt);
    ///   New:  NetworkService.instance.Instance.EventManager.Dispatch(evt);
    ///   — or hold a direct reference to the NetworkingInstance.
    ///
    /// Convenience proxy properties/methods are provided so that existing code
    /// using NetworkService.instance.EventManager, .SocketsById, etc. continues
    /// to compile without changes.
    /// </summary>
    public
#if GODOT4_0_OR_GREATER
    partial
#endif
    class NetworkService : IService
    {
        // =====================================================================
        //  Singleton (backward compat)
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
        //  The underlying networking instance
        // =====================================================================

        /// <summary>
        /// The primary NetworkingInstance managed by this service.
        /// Created during initialization. For additional instances, construct
        /// NetworkingInstance directly.
        /// </summary>
        public NetworkingInstance Instance { get; private set; }

        /// <summary>
        /// All NetworkingInstance objects managed by this service.
        /// The primary instance is always at key "default".
        /// Additional instances can be created via CreateInstance().
        /// </summary>
        private readonly ConcurrentDictionary<string, NetworkingInstance> _instances = new();

        // =====================================================================
        //  Configuration — populate before initialization step
        // =====================================================================

        /// <summary>
        /// Endpoint configs for the primary ("default") instance.
        /// Populate this before the service initialization step runs.
        /// </summary>
        public List<NetworkDestination> EndpointConfigs = new();

        // =====================================================================
        //  Convenience proxies (backward compatibility)
        //
        //  These forward to the primary Instance so that existing code like
        //  NetworkService.instance.EventManager continues to work.
        // =====================================================================

        /// <summary>True if the primary instance acts as a server.</summary>
        public bool IsServer => Instance?.IsServer ?? EndpointConfigs.Any(c => c.IsListener);

        /// <summary>Event manager of the primary instance.</summary>
        public EventManager EventManager => Instance?.EventManager;

        /// <summary>All active server listeners of the primary instance.</summary>
        public ConcurrentDictionary<(NetworkProtocol, int), IServerAdapter> Servers =>
            Instance?.Servers;

        /// <summary>All active client connections of the primary instance.</summary>
        public ConcurrentDictionary<(NetworkProtocol, string, int), ISocketAdapter> ClientSockets =>
            Instance?.ClientSockets;

        /// <summary>All confirmed sockets by ID of the primary instance.</summary>
        public ConcurrentDictionary<long, ISocketAdapter> SocketsById =>
            Instance?.SocketsById;

        /// <summary>Outbound buffer hub of the primary instance.</summary>
        public OutboundBufferHub OutboundBuffer => Instance?.OutboundBuffer;

        /// <summary>Ping service of the primary instance.</summary>
        public PingService PingService => Instance?.PingService;

        // ── Proxy events ──

        /// <summary>Fired when a socket on the primary instance becomes ready.</summary>
        public event Action<ISocketAdapter> OnSocketReady
        {
            add => _onSocketReady += value;
            remove => _onSocketReady -= value;
        }
        private Action<ISocketAdapter> _onSocketReady;

        /// <summary>Fired when a socket on the primary instance disconnects.</summary>
        public event Action<ISocketAdapter> OnSocketDisconnected
        {
            add => _onSocketDisconnected += value;
            remove => _onSocketDisconnected -= value;
        }
        private Action<ISocketAdapter> _onSocketDisconnected;

        /// <summary>Fired when a socket on the primary instance times out.</summary>
        public event Action<ISocketAdapter> OnPingTimeout
        {
            add => _onPingTimeout += value;
            remove => _onPingTimeout -= value;
        }
        private Action<ISocketAdapter> _onPingTimeout;

        // ── Proxy methods ──

        /// <summary>Register an external client on the primary instance.</summary>
        public void RegisterExternalClient(ISocketAdapter client, NetworkDestination config, bool autoConnect = false)
            => Instance.RegisterExternalClient(client, config, autoConnect);

        /// <summary>Resolve a socket on the primary instance.</summary>
        public ISocketAdapter ResolveSocket(NetworkDestination dest)
            => Instance.ResolveSocket(dest);

        /// <summary>Broadcast raw bytes on the primary instance.</summary>
        public void BroadcastRaw(byte[] serializedPayload)
            => Instance.BroadcastRaw(serializedPayload);

        /// <summary>Get RPC bridge by socket on the primary instance.</summary>
        public RpcBridge GetRpcBridge(ISocketAdapter socket)
            => Instance.GetRpcBridge(socket);

        /// <summary>Get RPC bridge by socket ID on the primary instance.</summary>
        public RpcBridge GetRpcBridge(long socketId)
            => Instance.GetRpcBridge(socketId);

        /// <summary>Send a system message on the primary instance.</summary>
        internal void SendSystemMessage(ISocketAdapter socket, byte msgType, byte[] payload)
            => Instance.SendSystemMessage(socket, msgType, payload);

        /// <summary>Send event on the primary instance.</summary>
        internal void SendEvent(NetworkEvent evt)
            => Instance.SendEvent(evt);

        // =====================================================================
        //  Multi-instance management
        // =====================================================================

        /// <summary>
        /// Create an additional named NetworkingInstance managed by this service.
        /// The instance is not started — call Configure + Start on the returned object.
        /// Disposed automatically when the service is destroyed.
        ///
        /// Usage:
        ///   var lobby = NetworkService.instance.CreateInstance("lobby");
        ///   lobby.EndpointConfigs.Add(...);
        ///   lobby.Start();
        ///
        ///   var game = NetworkService.instance.CreateInstance("game");
        ///   game.EndpointConfigs.Add(...);
        ///   game.Start();
        /// </summary>
        /// <param name="name">Unique name for this instance. "default" is reserved.</param>
        /// <returns>The new NetworkingInstance (not yet started).</returns>
        public NetworkingInstance CreateInstance(string name)
        {
            if (string.Equals(name, "default", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Name 'default' is reserved for the primary instance.");

            var inst = new NetworkingInstance();
            if (!_instances.TryAdd(name, inst))
                throw new InvalidOperationException($"NetworkingInstance '{name}' already exists.");

            return inst;
        }

        /// <summary>
        /// Get a named instance, or null if not found.
        /// </summary>
        public NetworkingInstance GetInstance(string name)
        {
            _instances.TryGetValue(name, out var inst);
            return inst;
        }

        /// <summary>
        /// Stop and remove a named instance.
        /// </summary>
        public bool DestroyInstance(string name)
        {
            if (string.Equals(name, "default", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Cannot destroy the primary ('default') instance via this method. Use the service lifecycle.");

            if (_instances.TryRemove(name, out var inst))
            {
                inst.Dispose();
                return true;
            }
            return false;
        }

        // =====================================================================
        //  IService lifecycle
        // =====================================================================

        public override void InitializeProcess()
        {
            Instance = new NetworkingInstance();
            Instance.EndpointConfigs = EndpointConfigs;

            // Wire proxy events
            Instance.OnSocketReady += (s) => _onSocketReady?.Invoke(s);
            Instance.OnSocketDisconnected += (s) => _onSocketDisconnected?.Invoke(s);
            Instance.OnPingTimeout += (s) => _onPingTimeout?.Invoke(s);

            _instances["default"] = Instance;

            Instance.Start();
        }

        public override void OnDestroyReaction()
        {
            foreach (var kvp in _instances)
            {
                try
                {
                    kvp.Value.Dispose();
                }
                catch (Exception ex)
                {
                    NLogger.LogError($"Error disposing NetworkingInstance '{kvp.Key}': {ex.Message}");
                }
            }
            _instances.Clear();
            Instance = null;
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
