using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AECC.Core.Logging;
using AECC.Extensions;

namespace AECC.Network
{
    /// <summary>
    /// Global outbound buffer settings.
    /// </summary>
    public static class OutboundBufferSettings
    {
        /// <summary>
        /// Maximum number of level-1 events per destination before the buffer is force-flushed.
        /// </summary>
        public static int MaxBufferedEvents = 64;

        /// <summary>
        /// Maximum age (in milliseconds) of the oldest level-1 event in a destination buffer
        /// before the entire buffer is flushed. The freshness of the buffer is determined by
        /// the timestamp of the very first event that entered it.
        /// </summary>
        public static int MaxBufferAgeMs = 100;

        /// <summary>
        /// How often the background sweep checks all destination buffers for age expiry (ms).
        /// </summary>
        public static int SweepIntervalMs = 25;
    }

    /// <summary>
    /// Holds the outbound state for a single destination route.
    ///
    /// Each destination identified by (Protocol, Host, Port) or SocketId gets one of these.
    /// Events are pre-serialized before entering the buffer — the buffer stores framed byte[]
    /// payloads ready for socket.SendAsync().
    ///
    /// Two queues:
    ///   HotQueue   — level-0 events. Drained immediately when the socket is ready.
    ///   BatchQueue — level-1 events. Drained when capacity or age threshold is exceeded.
    ///
    /// While the socket is not yet ready (connecting / reconnecting / identity handshake),
    /// both queues accumulate. As soon as the socket becomes ready:
    ///   1. All HotQueue items are sent first (FIFO).
    ///   2. If BatchQueue meets flush criteria, it is sent next.
    /// </summary>
    public class DestinationBuffer
    {
        public (NetworkProtocol Protocol, string Host, int Port) RouteKey;

        /// <summary>If non-zero, this buffer targets a specific socket by ID.</summary>
        public long TargetSocketId;

        /// <summary>Level-0 events — must go out ASAP.</summary>
        public ConcurrentQueue<byte[]> HotQueue = new();

        /// <summary>Level-1 events — can be batched.</summary>
        public ConcurrentQueue<byte[]> BatchQueue = new();

        /// <summary>
        /// UTC ticks of the oldest event currently sitting in BatchQueue.
        /// Reset to 0 after every flush. Used to determine buffer staleness.
        /// </summary>
        public long OldestBatchEntryTicks;

        /// <summary>
        /// True when a connection to this destination is in progress (not yet Ready).
        /// While true, both hot and batch events accumulate. When it flips to false,
        /// the OutboundBufferHub drains everything.
        /// </summary>
        public volatile bool IsConnecting;

        /// <summary>
        /// True if a connection attempt has already been initiated for this destination.
        /// Prevents duplicate connect calls.
        /// </summary>
        public volatile bool ConnectInitiated;

        /// <summary>
        /// The resolved socket for this destination (set once ready).
        /// </summary>
        public ISocketAdapter Socket;

        /// <summary>Number of events currently in the batch queue.</summary>
        public int BatchCount => BatchQueue.Count;
    }

    /// <summary>
    /// Central outbound buffer hub. Manages per-destination buffers, triggers on-demand
    /// connection creation, and runs a background sweep for age-based flushing.
    ///
    /// Thread-safe: all public methods can be called from any thread.
    /// </summary>
    public class OutboundBufferHub : IDisposable
    {
        private readonly ConcurrentDictionary<(NetworkProtocol, string, int), DestinationBuffer> _routeBuffers = new();
        private readonly ConcurrentDictionary<long, DestinationBuffer> _socketIdBuffers = new();

        private TimerCompat _sweepTimer;
        private readonly Action<NetworkDestination> _connectFactory;
        private readonly Func<NetworkDestination, ISocketAdapter> _socketResolver;

        /// <summary>
        /// Create the hub.
        /// </summary>
        /// <param name="connectFactory">
        /// Called when a send targets a destination with no existing connection.
        /// Should initiate an async connect; the hub will buffer events until OnSocketReady is called.
        /// </param>
        /// <param name="socketResolver">
        /// Resolves a destination to an existing ready socket, or returns null.
        /// </param>
        public OutboundBufferHub(
            Action<NetworkDestination> connectFactory,
            Func<NetworkDestination, ISocketAdapter> socketResolver)
        {
            _connectFactory = connectFactory;
            _socketResolver = socketResolver;

            _sweepTimer = new TimerCompat(
                OutboundBufferSettings.SweepIntervalMs,
                (sender, e) => SweepCallback(),
                loop: true,
                asyncRun: true);
            _sweepTimer.Start();
        }

        // =====================================================================
        //  Enqueue
        // =====================================================================

        /// <summary>
        /// Enqueue a pre-serialized event frame for a destination.
        /// If no socket exists, triggers on-demand connection creation.
        /// If the socket is ready and the event is hot (level 0), sends immediately.
        /// </summary>
        /// <param name="dest">Target destination.</param>
        /// <param name="framedPayload">Fully framed bytes ready for socket.SendAsync().</param>
        /// <param name="bufferLevel">0 = hot, 1 = buffered.</param>
        public void Enqueue(NetworkDestination dest, byte[] framedPayload, int bufferLevel)
        {
            var buffer = GetOrCreateBuffer(dest);

            // ── If socket is ready, try to send directly ──
            if (!buffer.IsConnecting && buffer.Socket != null && buffer.Socket.IsConnected)
            {
                if (bufferLevel == 0)
                {
                    // Hot: drain any pending hot items first (FIFO ordering guarantee), then send
                    DrainHotQueue(buffer);
                    buffer.Socket.SendAsync(framedPayload);
                    return;
                }
                else
                {
                    // Buffered (level 1): add to batch, check flush criteria
                    AddToBatch(buffer, framedPayload);
                    TryFlushBatch(buffer);
                    return;
                }
            }

            // ── Socket not ready: buffer the event ──
            if (bufferLevel == 0)
                buffer.HotQueue.Enqueue(framedPayload);
            else
                AddToBatch(buffer, framedPayload);

            // ── Trigger connection if not already started ──
            if (!buffer.ConnectInitiated && !dest.IsSocketRouted)
            {
                buffer.ConnectInitiated = true;
                buffer.IsConnecting = true;
                try
                {
                    _connectFactory(dest);
                }
                catch (Exception ex)
                {
                    NLogger.LogError($"OutboundBuffer: auto-connect failed for {dest.Host}:{dest.Port} — {ex.Message}");
                    buffer.ConnectInitiated = false;
                    buffer.IsConnecting = false;
                }
            }
        }

        // =====================================================================
        //  Socket lifecycle callbacks
        // =====================================================================

        /// <summary>
        /// Called when a socket for a destination becomes ready (identity confirmed, etc.).
        /// Flushes all pending events.
        /// </summary>
        public void OnSocketReady(ISocketAdapter socket, NetworkDestination dest)
        {
            DestinationBuffer buffer = null;

            if (dest != null && !dest.IsSocketRouted)
                _routeBuffers.TryGetValue(dest.RouteKey, out buffer);

            if (buffer == null && socket.Id != 0)
                _socketIdBuffers.TryGetValue(socket.Id, out buffer);

            if (buffer == null) return;

            buffer.Socket = socket;
            buffer.IsConnecting = false;

            DrainAll(buffer);
        }

        /// <summary>
        /// Called when a socket disconnects. Marks the buffer as connecting
        /// so new events accumulate until reconnection completes.
        /// </summary>
        public void OnSocketDisconnected(ISocketAdapter socket, NetworkDestination dest)
        {
            DestinationBuffer buffer = null;

            if (dest != null && !dest.IsSocketRouted)
                _routeBuffers.TryGetValue(dest.RouteKey, out buffer);

            if (buffer == null && socket.Id != 0)
                _socketIdBuffers.TryGetValue(socket.Id, out buffer);

            if (buffer == null) return;

            buffer.IsConnecting = true;
            // Socket reference is kept — reconnect will reuse or replace it.
        }

        // =====================================================================
        //  Background sweep (age-based flush)
        // =====================================================================

        private void SweepCallback()
        {
            long nowTicks = DateTime.UtcNow.Ticks;
            long maxAgeTicks = TimeSpan.FromMilliseconds(OutboundBufferSettings.MaxBufferAgeMs).Ticks;

            foreach (var buffer in _routeBuffers.Values)
            {
                if (buffer.IsConnecting || buffer.Socket == null || !buffer.Socket.IsConnected)
                    continue;

                if (buffer.OldestBatchEntryTicks > 0 && (nowTicks - buffer.OldestBatchEntryTicks) >= maxAgeTicks)
                {
                    FlushBatch(buffer);
                }
            }

            foreach (var buffer in _socketIdBuffers.Values)
            {
                if (buffer.IsConnecting || buffer.Socket == null || !buffer.Socket.IsConnected)
                    continue;

                if (buffer.OldestBatchEntryTicks > 0 && (nowTicks - buffer.OldestBatchEntryTicks) >= maxAgeTicks)
                {
                    FlushBatch(buffer);
                }
            }
        }

        // =====================================================================
        //  Internals
        // =====================================================================

        private DestinationBuffer GetOrCreateBuffer(NetworkDestination dest)
        {
            if (dest.IsSocketRouted)
            {
                return _socketIdBuffers.GetOrAdd(dest.SocketId, _ => new DestinationBuffer
                {
                    TargetSocketId = dest.SocketId,
                    Socket = _socketResolver(dest)
                });
            }

            return _routeBuffers.GetOrAdd(dest.RouteKey, _ =>
            {
                var existingSocket = _socketResolver(dest);
                return new DestinationBuffer
                {
                    RouteKey = dest.RouteKey,
                    Socket = existingSocket,
                    IsConnecting = existingSocket == null || !existingSocket.IsConnected
                };
            });
        }

        private void AddToBatch(DestinationBuffer buffer, byte[] framedPayload)
        {
            buffer.BatchQueue.Enqueue(framedPayload);

            // Track oldest entry — only set once (the first event determines buffer freshness)
            if (buffer.OldestBatchEntryTicks == 0)
                Interlocked.CompareExchange(ref buffer.OldestBatchEntryTicks, DateTime.UtcNow.Ticks, 0);
        }

        private void TryFlushBatch(DestinationBuffer buffer)
        {
            bool capacityReached = buffer.BatchCount >= OutboundBufferSettings.MaxBufferedEvents;

            bool ageReached = false;
            if (buffer.OldestBatchEntryTicks > 0)
            {
                long ageTicks = DateTime.UtcNow.Ticks - buffer.OldestBatchEntryTicks;
                ageReached = ageTicks >= TimeSpan.FromMilliseconds(OutboundBufferSettings.MaxBufferAgeMs).Ticks;
            }

            if (capacityReached || ageReached)
                FlushBatch(buffer);
        }

        private void FlushBatch(DestinationBuffer buffer)
        {
            if (buffer.Socket == null || !buffer.Socket.IsConnected) return;

            while (buffer.BatchQueue.TryDequeue(out var frame))
            {
                buffer.Socket.SendAsync(frame);
            }

            Interlocked.Exchange(ref buffer.OldestBatchEntryTicks, 0);
        }

        private void DrainHotQueue(DestinationBuffer buffer)
        {
            while (buffer.HotQueue.TryDequeue(out var frame))
            {
                buffer.Socket.SendAsync(frame);
            }
        }

        /// <summary>
        /// Drain everything: hot first, then batch (regardless of batch criteria).
        /// Called on socket-ready to flush all accumulated events.
        /// </summary>
        private void DrainAll(DestinationBuffer buffer)
        {
            if (buffer.Socket == null || !buffer.Socket.IsConnected) return;

            DrainHotQueue(buffer);

            // Force-flush batch on reconnect/connect
            while (buffer.BatchQueue.TryDequeue(out var frame))
            {
                buffer.Socket.SendAsync(frame);
            }

            Interlocked.Exchange(ref buffer.OldestBatchEntryTicks, 0);
        }

        public void Dispose()
        {
            _sweepTimer?.Stop();
            _sweepTimer?.Dispose();
            _sweepTimer = null;
        }
    }
}
