using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MessagePack;
using AECC.Core.Logging;
using AECC.Extensions;

namespace AECC.Network
{
    // =========================================================================
    //  Attributes
    // =========================================================================

    /// <summary>
    /// Assign a unique integer type ID to a concrete NetworkEvent subclass.
    /// This ID is used as the discriminator in the wire envelope.
    /// IDs must be globally unique across all event types.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public class NetworkEventIdAttribute : Attribute
    {
        public int TypeId { get; }
        public NetworkEventIdAttribute(int typeId) { TypeId = typeId; }
    }

    /// <summary>
    /// Assign a network abuse score to an event type.
    /// Used for malicious traffic detection.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public class NetworkScoreAttribute : Attribute
    {
        public int Score { get; }
        public NetworkScoreAttribute(int score) { Score = score; }
    }

    // =========================================================================
    //  NetworkEvent base class — NOT annotated with [MessagePackObject]
    // =========================================================================

    /// <summary>
    /// Base class for all events in the system.
    ///
    /// Concrete subclasses must:
    ///   1. Be annotated with [MessagePackObject]
    ///   2. Be annotated with [NetworkEventId(N)] where N is a unique integer
    ///   3. Use [Key(N)] on all serializable fields (start from 10+; 0-9 are reserved for base fields)
    ///
    /// The base fields (InstanceId, EntityOwnerId, WorldOwnerId) are serialized via
    /// the envelope — they are embedded in every concrete type at keys 0, 1, 2.
    /// Subclasses must include them as [Key(0)], [Key(1)], [Key(2)].
    /// To simplify this, inherit field declarations from this base and just add
    /// [Key(0)], [Key(1)], [Key(2)] in your subclass (MessagePack reads them
    /// from the concrete type, not the base).
    /// 
    /// [MessagePackObject]
    /// [NetworkEventId(300)]
    /// [NetworkScore(2)]
    /// public class ExampleEvent : NetworkEvent
    /// {
    ///     public override void Execute()
    ///     {
    ///         throw new NotImplementedException();
    ///     }
    /// }
    /// 
    /// Lifecycle rules:
    ///   - If Destination or Destinations is set → sent over the network, Execute() NOT called locally.
    ///   - If no network destination → Execute() called immediately, event discarded.
    ///   - If arrived from the network → Execute() called after deserialization.
    /// </summary>
    public abstract class NetworkEvent
    {
        /// <summary>Unique instance identifier.</summary>
        [Key(0)] public long InstanceId;

        /// <summary>Business-logic field: entity that owns this event.</summary>
        [Key(1)] public long EntityOwnerId;

        /// <summary>Business-logic field: world that owns this event.</summary>
        [Key(2)] public long WorldOwnerId;

        // ── Non-serialized runtime fields ──

        [IgnoreMemberAttribute] public long SocketSourceId => SocketSource?.Id ?? 0;

        [IgnoreMemberAttribute] public ISocketAdapter SocketSource;

        /// <summary>
        /// Single destination for point-to-point send.
        /// At least one of Destination / Destinations must be non-null for network dispatch.
        /// </summary>
        [IgnoreMemberAttribute] public NetworkDestination Destination;

        /// <summary>
        /// Multiple destinations for broadcast/multicast send.
        /// </summary>
        [IgnoreMemberAttribute] public List<NetworkDestination> Destinations;

        /// <summary>
        /// Controls outbound buffering behavior:
        ///
        ///   0 (Hot) — Sent as soon as the connection is available.
        ///   1 (Buffered) — May sit in the outbound buffer until capacity or age threshold.
        ///
        /// Default is 0 (hot). Override in subclasses or set per-instance.
        /// </summary>
        [IgnoreMemberAttribute] public virtual int BufferLevel { get; set; } = 0;

        /// <summary>
        /// Timestamp (UTC ticks) when this event was enqueued into the outbound buffer.
        /// Set automatically by the OutboundBuffer.
        /// </summary>
        [IgnoreMemberAttribute] public long EnqueuedAtTicks;

        /// <summary>
        /// Cached serialized form of this event (envelope bytes).
        /// Populated on first GetSerializedPacket() call.
        /// Business logic can broadcast the same packet to many sockets without re-serialization.
        /// </summary>
        [IgnoreMemberAttribute] public byte[] CachedSerializedData;

        // ── Overridable behavior ──

        /// <summary>Execute the event's business logic.</summary>
        public abstract void Execute();

        /// <summary>
        /// Validate the packet contents. Return false to reject (logged and dropped).
        /// </summary>
        public virtual bool CheckPacket() => true;

        /// <summary>
        /// Extra score added to the malicious-traffic counter for this event instance.
        /// </summary>
        public virtual int NetworkScoreBooster() => 0;

        /// <summary>
        /// Returns the serialized envelope form of this event, caching the result.
        /// </summary>
        public byte[] GetSerializedPacket()
        {
            if (CachedSerializedData == null)
                CachedSerializedData = NetworkSerialization.Serialize(this);
            return CachedSerializedData;
        }

        /// <summary>
        /// Returns true if this event should be dispatched over the network.
        /// </summary>
        [IgnoreMemberAttribute] public bool IsNetworkBound => Destination != null || (Destinations != null && Destinations.Count > 0);

        protected NetworkEvent()
        {
            InstanceId = Guid.NewGuid().GuidToLong();
        }
    }

    // =========================================================================
    //  Wire envelope — the only type that carries [MessagePackObject]
    // =========================================================================

    /// <summary>
    /// On-the-wire envelope that wraps every NetworkEvent.
    ///
    /// Layout:
    ///   [Key(0)] int    TypeId       — discriminator from [NetworkEventId(N)]
    ///   [Key(1)] long   InstanceId
    ///   [Key(2)] long   EntityOwnerId
    ///   [Key(3)] long   WorldOwnerId
    ///   [Key(4)] byte[] Payload      — MessagePack-serialized concrete event
    ///
    /// The base fields are pulled out into the envelope so that infrastructure
    /// code can read them without deserializing the full payload.
    /// </summary>
    [MessagePackObject]
    public class NetworkEventEnvelope
    {
        [Key(0)] public int TypeId;
        [Key(1)] public long InstanceId;
        [Key(2)] public long EntityOwnerId;
        [Key(3)] public long WorldOwnerId;
        [Key(4)] public byte[] Payload;
    }

    // =========================================================================
    //  Type registry + Serialization
    // =========================================================================

    /// <summary>
    /// Registry that maps NetworkEventId ↔ concrete Type.
    /// Automatically scans all loaded assemblies on first use.
    ///
    /// Thread-safe; scan happens once via lazy initialization.
    /// </summary>
    public static class NetworkEventRegistry
    {
        private static readonly ConcurrentDictionary<int, Type> _idToType = new();
        private static readonly ConcurrentDictionary<Type, int> _typeToId = new();
        private static volatile bool _initialized;
        private static readonly object _lock = new();

        /// <summary>
        /// Ensure the registry is populated. Safe to call multiple times.
        /// Scans all loaded assemblies for [NetworkEventId] on NetworkEvent subclasses.
        /// </summary>
        public static void EnsureInitialized()
        {
            if (_initialized) return;
            lock (_lock)
            {
                if (_initialized) return;

                var baseType = typeof(NetworkEvent);
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch { continue; } // ReflectionTypeLoadException, etc.

                    foreach (var type in types)
                    {
                        if (type.IsAbstract || !baseType.IsAssignableFrom(type))
                            continue;

                        var attr = type.GetCustomAttribute<NetworkEventIdAttribute>();
                        if (attr == null)
                        {
                            NLogger.LogError($"NetworkEvent subclass {type.FullName} is missing [NetworkEventId] attribute — it will not be serializable.");
                            continue;
                        }

                        if (_idToType.TryGetValue(attr.TypeId, out var existing))
                        {
                            NLogger.LogError($"NetworkEventId collision: {type.FullName} and {existing.FullName} both use TypeId={attr.TypeId}");
                            continue;
                        }

                        _idToType[attr.TypeId] = type;
                        _typeToId[type] = attr.TypeId;
                    }
                }

                _initialized = true;
                NLogger.LogNetwork($"NetworkEventRegistry: {_idToType.Count} event types registered.");
            }
        }

        public static int GetTypeId(Type eventType)
        {
            EnsureInitialized();
            if (_typeToId.TryGetValue(eventType, out var id))
                return id;
            throw new InvalidOperationException(
                $"Type {eventType.FullName} is not registered. Add [NetworkEventId(N)] attribute.");
        }

        public static Type GetType(int typeId)
        {
            EnsureInitialized();
            if (_idToType.TryGetValue(typeId, out var type))
                return type;
            throw new InvalidOperationException(
                $"No NetworkEvent registered for TypeId={typeId}. Was the assembly loaded?");
        }

        /// <summary>
        /// Manually register a type. Useful for dynamically loaded assemblies.
        /// </summary>
        public static void Register<T>(int typeId) where T : NetworkEvent
        {
            _idToType[typeId] = typeof(T);
            _typeToId[typeof(T)] = typeId;
        }
    }

    /// <summary>
    /// Serialization helpers using the envelope pattern.
    ///
    /// Wire format: NetworkEventEnvelope (single [MessagePackObject]).
    /// The concrete event payload is serialized as its own MessagePack blob
    /// inside the envelope's Payload field.
    ///
    /// This avoids [Union] entirely — the TypeId in the envelope serves as
    /// the polymorphic discriminator.
    /// </summary>
    public static class NetworkSerialization
    {
        private static readonly MessagePackSerializerOptions Options =
            MessagePackSerializerOptions.Standard
                .WithSecurity(MessagePackSecurity.UntrustedData);

        /// <summary>
        /// Serialize a NetworkEvent into an envelope byte array.
        /// </summary>
        public static byte[] Serialize(NetworkEvent evt)
        {
            NetworkEventRegistry.EnsureInitialized();

            int typeId = NetworkEventRegistry.GetTypeId(evt.GetType());

            // Serialize the concrete type (which has [MessagePackObject] + [Key(N)])
            byte[] payload = MessagePackSerializer.Serialize(evt.GetType(), evt, Options);

            var envelope = new NetworkEventEnvelope
            {
                TypeId = typeId,
                InstanceId = evt.InstanceId,
                EntityOwnerId = evt.EntityOwnerId,
                WorldOwnerId = evt.WorldOwnerId,
                Payload = payload
            };

            return MessagePackSerializer.Serialize(envelope, Options);
        }

        /// <summary>
        /// Deserialize an envelope byte array back into a concrete NetworkEvent.
        /// </summary>
        public static NetworkEvent Deserialize(byte[] data)
        {
            NetworkEventRegistry.EnsureInitialized();

            var envelope = MessagePackSerializer.Deserialize<NetworkEventEnvelope>(data, Options);

            Type concreteType = NetworkEventRegistry.GetType(envelope.TypeId);

            var evt = (NetworkEvent)MessagePackSerializer.Deserialize(concreteType, envelope.Payload, Options);

            // Restore base fields from envelope (in case the concrete type
            // doesn't re-serialize them — belt and suspenders)
            evt.InstanceId = envelope.InstanceId;
            evt.EntityOwnerId = envelope.EntityOwnerId;
            evt.WorldOwnerId = envelope.WorldOwnerId;

            return evt;
        }
    }
}
