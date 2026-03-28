using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using AECC.Core.Logging;
using AECC.ECS.Core;

namespace AECC.Network
{
    /// <summary>
    /// Simplified event manager. Events are one-shot:
    ///   - Network-bound events are serialized and routed to NetworkService for dispatch.
    ///   - Local events have Execute() called immediately.
    ///   - Events arriving from the network have Execute() called after deserialization.
    ///
    /// No event storage. Events are never retained after processing.
    /// </summary>
    public class EventManager
    {
        /// <summary>
        /// When true, keeps a rolling debug log of (SocketSourceId, EventInstanceId) pairs.
        /// </summary>
        public bool DebugTracing = false;

        /// <summary>Debug trace buffer. Only populated when DebugTracing == true.</summary>
        public ConcurrentQueue<(long SocketId, long EventId, string TypeName)> DebugTrace = new();

        private const int MaxDebugTraceSize = 10000;

        // ── Malicious scoring (carried over from original) ──
        public ConcurrentDictionary<long, ScoreObject> MaliciousScoringStorage = new();

        // ── Reference to network service for sending ──
        private NetworkService _networkService;

        public void Initialize(NetworkService networkService)
        {
            _networkService = networkService;
        }

        /// <summary>
        /// Main entry point: add an event for processing.
        /// </summary>
        public void Dispatch(NetworkEvent evt)
        {
            // ── Malicious scoring ──
            if (evt.SocketSourceId != 0)
            {
                if (MaliciousScoringStorage.TryGetValue(evt.SocketSourceId, out var scoreObj))
                {
                    try
                    {
                        var attr = evt.GetType().GetCustomAttribute<NetworkScore>();
                        int baseScore = attr?.Score ?? 0;
                        scoreObj.Score += baseScore + evt.NetworkScoreBooster();
                    }
                    catch (Exception ex)
                    {
                        NLogger.LogError(ex);
                    }
                }
            }

            // ── Packet validation ──
            if (!evt.CheckPacket())
            {
                NLogger.LogError($"Rejected invalid packet: {evt.GetType().Name}");
                return;
            }

            // ── Debug trace ──
            if (DebugTracing)
            {
                DebugTrace.Enqueue((evt.SocketSourceId, evt.InstanceId, evt.GetType().Name));
                while (DebugTrace.Count > MaxDebugTraceSize)
                    DebugTrace.TryDequeue(out _);
            }

            // ── Routing decision ──
            if (evt.IsNetworkBound)
            {
                // Network-bound: serialize and send. Do NOT call Execute().
                _networkService.SendEvent(evt);
            }
            else
            {
                // Local event or arrived-from-network: Execute immediately.
                try
                {
                    evt.Execute();
                }
                catch (Exception ex)
                {
                    NLogger.LogError($"Event {evt.GetType().Name} Execute() failed: {ex}");
#if DEBUG
                    throw;
#endif
                }
            }
        }

        /// <summary>
        /// Called by NetworkService when a network event arrives.
        /// SocketSource is already set. Destination fields are null (this is an incoming event).
        /// </summary>
        internal void DispatchFromNetwork(NetworkEvent evt, ISocketAdapter source)
        {
            evt.SocketSource = source;
            Dispatch(evt);
        }
    }

    public class ScoreObject
    {
        public long SocketId;
        public volatile int Score;
    }
}
