using AECC.Core;
using AECC.Serialization;

namespace AECC.Runtime
{
    /// <summary>
    /// ФАЗА 7 (ТЗ 4.9, фиксация публичного API): ЕДИНАЯ точка монтажа мира для прикладного
    /// кода. Прежде приклад после ECSWorld.Configure звал два бутстрапа из двух сборок
    /// (SerializationBootstrap.Attach + QueryBootstrap.Attach) в правильном порядке —
    /// теперь один вызов. Живёт в AECC.Query (верхняя сборка, видит и Serialization, и
    /// Query); Core о ней не знает (гейты фаз 4–5 держатся).
    ///
    /// Порядок фиксирован: сериализация → поиск (индекс запросов не зависит от
    /// сериализатора, но монтаж сериализации первым воспроизводит исторический порядок
    /// Configure). Идемпотентность — на совести нижних Attach.
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
