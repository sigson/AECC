using System;

using AECC.Core;

namespace AECC.Serialization
{
    /// <summary>
    /// Mounts serialization onto a world. The world stores the serializer/adapter in
    /// untyped (object) slots and does not interpret them, since Core has no reference to
    /// Serialization; typed access is provided only from this side.
    /// </summary>
    public static class SerializationBootstrap
    {
        /// <summary>Default adapter factory.</summary>
        public static Func<ISerializationAdapter> GetSerializationAdapter = () => new DummySerializationAdapter();

        /// <summary>Mounts serialization on the world. Call after Configure
        /// (or pass an adapter into Configure — Attach respects an already-filled slot).</summary>
        public static EntityNetSerializer Attach(ECSWorld world, ISerializationAdapter adapter = null)
        {
            var effective = adapter
                ?? world.serializationAdapter as ISerializationAdapter
                ?? GetSerializationAdapter();
            world.serializationAdapter = effective;
            var serializer = new EntityNetSerializer();
            serializer.InitSerialize(world, effective);
            world.EntityWorldSerializer = serializer;
            return serializer;
        }

        /// <summary>Typed access to the world's serializer (backed by an object slot).</summary>
        public static EntityNetSerializer SerializerOf(ECSWorld world)
        {
            return world.EntityWorldSerializer as EntityNetSerializer;
        }
    }
}
