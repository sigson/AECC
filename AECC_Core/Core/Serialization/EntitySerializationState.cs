using System;
using System.Collections.Generic;
using AECC.Collections;
using AECC.Locking;

namespace AECC.Core.Serialization
{
    /// <summary>
    /// Пер-сущностное сериализационное состояние: dirty-set, removed-список,
    /// binSerializedEntity, emptySerialized. ВЛАДЕНИЕ — у Serialization: модель и
    /// хранилище эти данные не объявляют.
    ///
    /// Полносрезная сериализация читает живой ComponentStore напрямую (без отдельного
    /// зеркала компонентов); транзитный буфер десериализации — локальный словарь
    /// пайплайна (DeserializeStorage возвращает, Restore/Update принимают параметром).
    ///
    /// Хранение — OPAQUE-СЛОТ на сущности (ECSEntity.serializationState типа object:
    /// модель хранит, не интерпретирует) — внешняя таблица instanceId→state дала бы
    /// лукап и контенцию на каждом чейндже. Горячие пути (dirty-запись на каждый change)
    /// идут через кэш-ссылку EntityComponentStorage._serState.
    ///
    /// Пайплайн-методы (Sliced/Serialize/Deserialize/Restore) живут на
    /// EntityComponentStorage.
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
        /// Горячие потребители кэшируют результат в поле (ссылка вместо повторных резолвов).
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
