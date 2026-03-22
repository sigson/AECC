using System;
using MessagePack;
using AECC.Core;

namespace AECC.Network
{
    // =========================================================================
    //  Socket lifecycle system events
    //
    //  Dispatched through the EventManager when a socket completes identity
    //  handshake (new or restored) or loses its connection.
    //
    //  These are LOCAL-only events: they have no Destination set, so
    //  EventManager calls Execute() immediately. Business logic subscribes
    //  by overriding Execute() in a subclass or by listening to the
    //  static event hooks below.
    //
    //  TypeId range 9001-9003 is reserved for socket lifecycle events.
    // =========================================================================

    /// <summary>
    /// Fired when a socket has completed a NEW identity handshake
    /// (server assigned a fresh ID, client confirmed it).
    /// </summary>
    [MessagePackObject]
    [TypeUid(9001)]
    public class SocketConnectedEvent : NetworkEvent
    {
        /// <summary>The confirmed socket ID.</summary>
        [Key(10)] public long SocketId;

        /// <summary>Remote address of the socket.</summary>
        [Key(11)] public string Address;

        /// <summary>Remote port of the socket.</summary>
        [Key(12)] public int Port;

        /// <summary>Protocol used by this socket.</summary>
        [Key(13)] public int ProtocolId;

        /// <summary>Static hook — subscribe to receive all SocketConnected events.</summary>
        public static event Action<SocketConnectedEvent> OnSocketConnected;

        public override void Execute()
        {
            OnSocketConnected?.Invoke(this);
        }
    }

    /// <summary>
    /// Fired when a socket has successfully restored a previous identity
    /// after a reconnection (RestoreId → RestoreAccepted handshake).
    /// </summary>
    [MessagePackObject]
    [TypeUid(9002)]
    public class SocketReconnectedEvent : NetworkEvent
    {
        /// <summary>The restored socket ID (same as before disconnect).</summary>
        [Key(10)] public long SocketId;

        /// <summary>Remote address of the socket.</summary>
        [Key(11)] public string Address;

        /// <summary>Remote port of the socket.</summary>
        [Key(12)] public int Port;

        /// <summary>Protocol used by this socket.</summary>
        [Key(13)] public int ProtocolId;

        /// <summary>Static hook — subscribe to receive all SocketReconnected events.</summary>
        public static event Action<SocketReconnectedEvent> OnSocketReconnected;

        public override void Execute()
        {
            OnSocketReconnected?.Invoke(this);
        }
    }

    /// <summary>
    /// Fired when a confirmed socket loses its connection.
    /// </summary>
    [MessagePackObject]
    [TypeUid(9003)]
    public class SocketDisconnectedEvent : NetworkEvent
    {
        /// <summary>The socket ID that disconnected.</summary>
        [Key(10)] public long SocketId;

        /// <summary>Remote address of the socket (at the time of disconnect).</summary>
        [Key(11)] public string Address;

        /// <summary>Remote port of the socket (at the time of disconnect).</summary>
        [Key(12)] public int Port;

        /// <summary>Protocol used by this socket.</summary>
        [Key(13)] public int ProtocolId;

        /// <summary>Static hook — subscribe to receive all SocketDisconnected events.</summary>
        public static event Action<SocketDisconnectedEvent> OnSocketDisconnected;

        public override void Execute()
        {
            OnSocketDisconnected?.Invoke(this);
        }
    }
}
