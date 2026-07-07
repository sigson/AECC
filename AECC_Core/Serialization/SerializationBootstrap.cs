using System;

using AECC.Core; // видимость Core больше не наследуется от родительского неймспейса

namespace AECC.Serialization
{
    /// <summary>
    /// ФАЗА 4, вынос сборок (ТЗ 4.7): монтаж сериализации на мир. Бывшие строки
    /// ECSWorld.Configure (`serializationAdapter = adapter ?? GetSerializationAdapter();
    /// EntityWorldSerializer.InitSerialize(this, serializationAdapter);`) — ДОСЛОВНО здесь:
    /// мир хранит сериализатор/адаптер object-слотами и не интерпретирует их (гейт
    /// «Core без ссылки на Serialization»); типизированный доступ — только с этой стороны.
    /// </summary>
    public static class SerializationBootstrap
    {
        /// <summary>Бывший ECSWorld.GetSerializationAdapter (дефолт-фабрика адаптера).</summary>
        public static Func<ISerializationAdapter> GetSerializationAdapter = () => new DummySerializationAdapter();

        /// <summary>Смонтировать сериализацию на мир. Вызывать после Configure
        /// (или передать адаптер в Configure — Attach уважает уже заполненный слот).</summary>
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

        /// <summary>Типизированный доступ к сериализатору мира (object-слот).</summary>
        public static EntityNetSerializer SerializerOf(ECSWorld world)
        {
            return world.EntityWorldSerializer as EntityNetSerializer;
        }
    }
}
