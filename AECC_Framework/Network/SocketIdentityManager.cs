using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using AECC.Core.Logging;
using AECC.Extensions;

namespace AECC.Network
{
    /// <summary>
    /// Socket lifecycle states during the identity handshake.
    /// </summary>
    public enum SocketState
    {
        /// <summary>Connected but not yet identified. Packets are queued.</summary>
        PendingIdentity,

        /// <summary>Server sent AssignId / client sent RestoreId, waiting for confirmation.</summary>
        AwaitingConfirmation,

        /// <summary>Identity confirmed. Packets flow normally.</summary>
        Ready
    }

    /// <summary>
    /// Reason the socket became ready — used to distinguish new vs restored connections
    /// for dispatching the correct lifecycle event.
    /// </summary>
    public enum SocketReadyReason
    {
        /// <summary>Brand new connection with a freshly assigned ID.</summary>
        NewConnection,

        /// <summary>Reconnected socket that restored a previously assigned ID.</summary>
        Restored
    }

    /// <summary>
    /// Tracks a single socket's identity state and queued messages.
    /// </summary>
    public class SocketIdentityEntry
    {
        public long AssignedId;
        public SocketState State = SocketState.PendingIdentity;
        public ISocketAdapter Socket;
        public ConcurrentQueue<byte[]> PendingMessages = new();
    }

    /// <summary>
    /// Manages the identity assignment and reconnection protocol for connection-oriented sockets.
    ///
    /// Server-side flow (new connection):
    ///   1. Client connects → state = PendingIdentity
    ///   2. Server generates long ID, sends AssignId(ID) → state = AwaitingConfirmation
    ///   3. Client responds with ConfirmId(ID) → state = Ready, socket exposed to business logic
    ///
    /// Client-side flow (new connection):
    ///   1. Client connects → state = PendingIdentity
    ///   2. Server sends AssignId(ID) → client stores ID, sends ConfirmId(ID) → state = Ready
    ///
    /// Reconnection flow:
    ///   1. Client reconnects → sends RestoreId(previousId) → state = AwaitingConfirmation
    ///   2. Server matches ID, reassigns socket ID → sends RestoreAccepted → state = Ready
    ///   3. Queued messages are flushed through the event pipeline
    /// </summary>
    public class SocketIdentityManager
    {
        /// <summary>
        /// Server-side confirmed sockets: AssignedId → SocketIdentityEntry.
        /// Only contains fully confirmed sockets.
        /// </summary>
        public ConcurrentDictionary<long, SocketIdentityEntry> ConfirmedSockets = new();

        /// <summary>
        /// Pending (not yet confirmed) sockets, keyed by the transport-level socket reference.
        /// </summary>
        private ConcurrentDictionary<ISocketAdapter, SocketIdentityEntry> _pendingSockets = new();

        /// <summary>Client-side: the ID assigned to us by the server.</summary>
        public long ClientAssignedId { get; private set; }

        /// <summary>Client-side: true if identity handshake is complete.</summary>
        public bool ClientIsReady { get; private set; }

        private readonly bool _isServer;
        private readonly Action<ISocketAdapter, SocketReadyReason> _onSocketReady;
        private readonly Action<ISocketAdapter> _onSocketDisconnected;

        /// <summary>
        /// Callback to re-inject queued raw messages into the processing pipeline.
        /// Signature: (socket, rawPayload) where rawPayload is the MessagePack event bytes.
        /// </summary>
        private readonly Action<ISocketAdapter, byte[]> _onFlushMessage;

        public SocketIdentityManager(bool isServer,
            Action<ISocketAdapter, SocketReadyReason> onSocketReady,
            Action<ISocketAdapter> onSocketDisconnected,
            Action<ISocketAdapter, byte[]> onFlushMessage = null)
        {
            _isServer = isServer;
            _onSocketReady = onSocketReady;
            _onSocketDisconnected = onSocketDisconnected;
            _onFlushMessage = onFlushMessage;
        }

        // =====================================================================
        //  Server-side operations
        // =====================================================================

        /// <summary>
        /// Called when a new client connects on the server side.
        /// Starts the identity handshake by assigning an ID.
        /// </summary>
        public void ServerOnClientConnected(ISocketAdapter socket)
        {
            var entry = new SocketIdentityEntry
            {
                AssignedId = Guid.NewGuid().GuidToLong(),
                State = SocketState.AwaitingConfirmation,
                Socket = socket
            };

            _pendingSockets[socket] = entry;

            // Send AssignId to client
            var idBytes = BitConverter.GetBytes(entry.AssignedId);
            SendSystemMessage(socket, MessageType.AssignId, idBytes);
        }

        /// <summary>
        /// Called when a client disconnects on the server side.
        /// </summary>
        public void ServerOnClientDisconnected(ISocketAdapter socket)
        {
            _pendingSockets.TryRemove(socket, out _);

            // Find and remove from confirmed (but keep the ID reserved for potential reconnect)
            // The entry stays in ConfirmedSockets for reconnection window.
            // A cleanup timer can remove stale entries.
            foreach (var kvp in ConfirmedSockets)
            {
                if (kvp.Value.Socket == socket)
                {
                    kvp.Value.State = SocketState.PendingIdentity;
                    kvp.Value.Socket = null;
                    break;
                }
            }

            _onSocketDisconnected?.Invoke(socket);
        }

        /// <summary>
        /// Process a system message received on a server-side socket.
        /// Returns true if the message was handled as a system message.
        /// </summary>
        public bool ServerProcessSystemMessage(ISocketAdapter socket, byte msgType, byte[] payload)
        {
            switch (msgType)
            {
                case MessageType.ConfirmId:
                {
                    long confirmedId = BitConverter.ToInt64(payload, 0);

                    if (_pendingSockets.TryRemove(socket, out var entry) && entry.AssignedId == confirmedId)
                    {
                        entry.State = SocketState.Ready;
                        socket.Id = confirmedId;
                        ConfirmedSockets[confirmedId] = entry;

                        NLogger.LogNetwork($"Socket {confirmedId} identity confirmed ({socket.Address}:{socket.Port})");
                        _onSocketReady?.Invoke(socket, SocketReadyReason.NewConnection);

                        // Flush queued messages
                        FlushPendingMessages(entry);
                    }
                    else
                    {
                        NLogger.LogError($"ConfirmId mismatch or unknown socket from {socket.Address}:{socket.Port}");
                        socket.Disconnect();
                    }
                    return true;
                }

                case MessageType.RestoreId:
                {
                    long restoreId = BitConverter.ToInt64(payload, 0);

                    if (ConfirmedSockets.TryGetValue(restoreId, out var entry))
                    {
                        // Restore: reassign the old ID to the new socket
                        _pendingSockets.TryRemove(socket, out _);
                        entry.Socket = socket;
                        entry.State = SocketState.Ready;
                        socket.Id = restoreId;

                        SendSystemMessage(socket, MessageType.RestoreAccepted, BitConverter.GetBytes(restoreId));

                        NLogger.LogNetwork($"Socket {restoreId} restored from {socket.Address}:{socket.Port}");
                        _onSocketReady?.Invoke(socket, SocketReadyReason.Restored);
                        FlushPendingMessages(entry);
                    }
                    else
                    {
                        // Unknown ID — treat as new connection
                        NLogger.LogNetwork($"RestoreId {restoreId} not found, treating as new connection");
                        ServerOnClientConnected(socket);
                    }
                    return true;
                }

                default:
                    return false;
            }
        }

        /// <summary>
        /// Queue a data message for a pending socket. Returns true if the socket is pending
        /// (message was queued), false if the socket is ready (caller should process it).
        /// </summary>
        public bool ServerTryQueueMessage(ISocketAdapter socket, byte[] rawMessage)
        {
            if (_pendingSockets.TryGetValue(socket, out var entry))
            {
                entry.PendingMessages.Enqueue(rawMessage);
                return true;
            }

            // Check if socket is confirmed and ready
            long id = socket.Id;
            if (id != 0 && ConfirmedSockets.TryGetValue(id, out var confirmedEntry) && confirmedEntry.State == SocketState.Ready)
            {
                return false; // Ready, caller should process
            }

            // Unknown socket — queue just in case
            entry = new SocketIdentityEntry { Socket = socket, State = SocketState.PendingIdentity };
            _pendingSockets[socket] = entry;
            entry.PendingMessages.Enqueue(rawMessage);
            return true;
        }

        // =====================================================================
        //  Client-side operations
        // =====================================================================

        /// <summary>
        /// Called when the client connects to the server.
        /// If we have a previous ID, send RestoreId immediately.
        /// </summary>
        public void ClientOnConnected(ISocketAdapter socket)
        {
            ClientIsReady = false;
            if (ClientAssignedId != 0)
            {
                // Reconnecting — send our stored ID
                SendSystemMessage(socket, MessageType.RestoreId, BitConverter.GetBytes(ClientAssignedId));
            }
            // Otherwise, wait for server to send AssignId
        }

        /// <summary>
        /// Process a system message received on the client side.
        /// Returns true if handled.
        /// </summary>
        public bool ClientProcessSystemMessage(ISocketAdapter socket, byte msgType, byte[] payload)
        {
            switch (msgType)
            {
                case MessageType.AssignId:
                {
                    long id = BitConverter.ToInt64(payload, 0);
                    ClientAssignedId = id;
                    socket.Id = id;

                    // Send confirmation
                    SendSystemMessage(socket, MessageType.ConfirmId, BitConverter.GetBytes(id));
                    ClientIsReady = true;

                    NLogger.LogNetwork($"Client received ID {id}, confirmed.");
                    _onSocketReady?.Invoke(socket, SocketReadyReason.NewConnection);
                    return true;
                }

                case MessageType.RestoreAccepted:
                {
                    long id = BitConverter.ToInt64(payload, 0);
                    socket.Id = id;
                    ClientIsReady = true;

                    NLogger.LogNetwork($"Client ID {id} restored successfully.");
                    _onSocketReady?.Invoke(socket, SocketReadyReason.Restored);
                    return true;
                }

                default:
                    return false;
            }
        }

        // =====================================================================
        //  Shared helpers
        // =====================================================================

        private void FlushPendingMessages(SocketIdentityEntry entry)
        {
            while (entry.PendingMessages.TryDequeue(out var msg))
            {
                _onFlushMessage?.Invoke(entry.Socket, msg);
            }
        }

        private void SendSystemMessage(ISocketAdapter socket, byte msgType, byte[] payload)
        {
            byte[] frame;
            if (ProtocolTraits.UsesStreamFraming(socket.Protocol))
                frame = StreamFrameAccumulator.Pack(msgType, payload);
            else
                frame = DatagramFrame.Pack(msgType, payload);

            socket.SendAsync(frame);
        }

        /// <summary>
        /// Remove stale disconnected entries older than the given TimeSpan.
        /// Call periodically from a timer.
        /// </summary>
        public void CleanupStaleEntries(TimeSpan maxAge)
        {
            // For a full implementation, entries should track disconnect time.
            // Placeholder for business-logic extension.
        }

        /// <summary>
        /// Get a confirmed socket by its assigned ID. Returns null if not found or not ready.
        /// </summary>
        public ISocketAdapter GetSocketById(long id)
        {
            if (ConfirmedSockets.TryGetValue(id, out var entry) && entry.State == SocketState.Ready)
                return entry.Socket;
            return null;
        }
    }
}
