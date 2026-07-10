using System;
using System.Collections.Generic;
using AECC.Collections;
using AECC.Locking;

namespace AECC.Core.Serialization
{
    /// <summary>
    /// Пер-сущностное сериализационное состояние (фаза 4, шаг 1; ТЗ 4.7): dirty-set,
    /// removed-список, binSerializedEntity, emptySerialized. ВЛАДЕНИЕ — у Serialization:
    /// модель и хранилище эти данные больше не объявляют.
    ///
    /// ОПТИМИЗАЦИЯ ПАМЯТИ: бывшее «зеркало компонентов» (SerializationContainer)
    /// УДАЛЕНО ПОЛНОСТЬЮ. Живая копия дублировала ComponentStore по тому же ключу
    /// typeUid (в снапшоте — ConcurrentDictionary Tables/Node + Cell на каждую сущность,
    /// ~220 МБ на 100k сущностей); полносрезная сериализация читает живой Store напрямую,
    /// а транзитный буфер десериализации стал локальным словарём пайплайна
    /// (DeserializeStorage возвращает, Restore/Update принимают параметром).
    ///
    /// Хранение — OPAQUE-СЛОТ на сущности (ECSEntity.serializationState типа object:
    /// модель хранит, не интерпретирует) — ДЕФОЛТ по ТЗ: внешняя таблица instanceId→state
    /// дала бы лукап+контенцию на каждом чейндже. Горячие пути (dirty-запись на каждый
    /// change) идут через кэш-ссылку EntityComponentStorage._serState — по стоимости это
    /// прежнее чтение поля хранилища.
    ///
    /// Пайплайн-методы (Sliced/Serialize/Deserialize/Restore) переходно остаются на
    /// EntityComponentStorage; при выносе сборки AECC.Serialization они становятся её
    /// внутренними методами (санкционированный breaking ТЗ 4.7).
    /// </summary>
    public sealed class EntitySerializationState
    {
        // ───── dirty-set (пер-сущностный, как и был) ─────
        public readonly IDictionary<Type, int> ChangedComponents = new DictionaryWrapper<Type, int>();

        // ───── доставка удалений (идея 1.6/1.7: IncludeRemoved) ─────
        public List<long> RemovedComponents = new List<long>();

        // ───── пер-сущностный снапшот (бывшие поля ECSEntity) ─────
        public byte[] BinSerializedEntity;
        public bool EmptySerialized = true;

        /// <summary>
        /// Состояние из opaque-слота сущности (создаёт при первом обращении).
        /// Горячие потребители кэшируют результат в поле (мандат ТЗ 4.7 — слот вместо таблицы,
        /// ссылка вместо повторных резолвов).
        /// </summary>
        public static EntitySerializationState Of(ECSEntity entity)
        {
            var state = entity.serializationState as EntitySerializationState;
            if (state == null)
            {
                state = new EntitySerializationState();
                entity.serializationState = state;
            }
            return state;
        }
    }
}
