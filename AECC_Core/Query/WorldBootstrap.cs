using AECC.Core;
using AECC.Serialization;

namespace AECC.Runtime
{
    /// <summary>
    /// Единая точка монтажа рантайма на мир для прикладного кода: один вызов вместо
    /// раздельных SerializationBootstrap.Attach и QueryBootstrap.Attach. Живёт в AECC.Query
    /// (верхняя сборка, видит и Serialization, и Query); Core о ней не знает.
    ///
    /// Порядок фиксирован: сначала сериализация, затем поиск (индекс запросов от
    /// сериализатора не зависит, но порядок сохраняется намеренно). Идемпотентность
    /// обеспечивается нижележащими Attach.
    /// </summary>
    public static class Bootstrap
    {
        /// <summary>Смонтировать полный рантайм на мир после ECSWorld.Configure:
        /// сериализацию (adapter — опционально) и индекс запросов (world.Query).</summary>
        public static void AttachRuntime(ECSWorld world, ISerializationAdapter adapter = null)
        {
            SerializationBootstrap.Attach(world, adapter);
            AECC.Query.QueryBootstrap.Attach(world);
        }
    }
}
