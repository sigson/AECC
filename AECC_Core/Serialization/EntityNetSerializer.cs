 using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using AECC.Core.Logging;
using AECC.Extensions;
using AECC.Extensions.ThreadingSync;
using AECC.Core.BuiltInTypes.ComponentsGroup;
using AECC.Core.BuiltInTypes.Components;

using AECC.Core.Serialization; // резиденты Core: State/Shadow/participant (мигрируют после слайса 2 DBComponent)

using AECC.Core; // видимость Core больше не наследуется от родительского неймспейса

namespace AECC.Serialization
{
    /// <summary>
    /// Конкретная реализация сериализации сущностей.
    /// </summary>
    public class EntityNetSerializer : EntitySerializer
    {
        protected override byte[] FullSerialize(ECSEntity entity, bool serializeOnlyChanged = false)
        {
            var resultObject = new SerializedEntity();
            entity.EnterToSerialization();
            resultObject.Entity = serializationAdapter.SerializeECSEntity(entity);
            resultObject.Components = entity.entityComponents.SerializeStorage(serializationAdapter, serializeOnlyChanged, true);

            return serializationAdapter.SerializeAdapterEntity(resultObject);
        }

        protected override Dictionary<long, byte[]> SlicedSerialize(ECSEntity entity, bool serializeOnlyChanged = false, bool clearChanged = false)
        {
            var resultObject = new SerializedEntity();

            using (entity.entityComponents.StabilizationGate.ReadLock())
            {
                entity.EnterToSerialization();
                resultObject.Entity = serializationAdapter.SerializeECSEntity(entity);
                resultObject.Components = entity.entityComponents.SlicedSerializeStorage(serializationAdapter, serializeOnlyChanged, clearChanged);

                resultObject.Components[ECSEntity.Id] = resultObject.Entity;
            }
            return resultObject.Components;
        }

        public override void SerializeEntity(ECSEntity entity, bool serializeOnlyChanged = false)
        {
            var serializedData = SlicedSerialize(entity, serializeOnlyChanged, true);
            bool emptyData = true;
            foreach (var GDAP in entity.dataAccessPolicies)
            {
                if (Defines.LogECSEntitySerializationComponents)
                {
                    var preparedData = "";
                    GDAP.BinAvailableComponents.ForEach(x => preparedData += TypeRegistry.Global.GetTypeOrThrow(x.Key).ToString() + "\n");
                    if (preparedData != "")
                    {
                        NLogger.Log($"Will removed last serialization data in {entity.AliasName}:{entity.instanceId} as\n {preparedData}");
                    }
                }
                GDAP.BinAvailableComponents.Clear();
                GDAP.BinRestrictedComponents.Clear();
                GDAP.IncludeRemovedAvailable = false;
                GDAP.IncludeRemovedRestricted = false;
                
                foreach (var availableComp in GDAP.AvailableComponents)
                {
                    byte[] serialData = null;
                    if (entity.entityComponents.RemovedComponents.Contains(availableComp))
                    {
                        GDAP.IncludeRemovedAvailable = true;
                        emptyData = false;
                    }
                    if (!serializedData.TryGetValue(availableComp, out serialData))
                        continue;
                    GDAP.BinAvailableComponents[availableComp] = serialData;
                    emptyData = false;
                }
                foreach (var availableComp in GDAP.RestrictedComponents)
                {
                    byte[] serialData = null;
                    if (entity.entityComponents.RemovedComponents.Contains(availableComp))
                    {
                        GDAP.IncludeRemovedRestricted = true;
                        emptyData = false;
                    }
                    if (!serializedData.TryGetValue(availableComp, out serialData))
                        continue;
                    GDAP.BinRestrictedComponents[availableComp] = serialData;
                    emptyData = false;
                }
            }
            entity.entityComponents.RemovedComponents.Clear();
            entity.binSerializedEntity = serializedData[ECSEntity.Id];
            entity.emptySerialized = emptyData;
        }

        public override byte[] BuildSerializedEntityWithGDAP(ECSEntity toEntity, ECSEntity fromEntity, bool ignoreNullData = false)
        {
            bool includeRemoved;
            var data = GroupDataAccessPolicy.ComponentsFilter(toEntity, fromEntity, out includeRemoved);
            if (Defines.LogECSEntitySerializationComponents)
            {
                var preparedData = "";
                data.ForEach(x => preparedData += TypeRegistry.Global.GetTypeOrThrow(x.Key).ToString() + "\n");
                if (preparedData != "")
                {
                    NLogger.Log($"GDAP filtering result from base {toEntity.AliasName}:{toEntity.instanceId} to {fromEntity.AliasName}:{fromEntity.instanceId} as\n {preparedData}");
                }
            }
            var resultObject = new SerializedEntity();
            if (!includeRemoved && data.Count == 0 && !ignoreNullData)
            {
                return new byte[0];
            }
            resultObject.Entity = fromEntity.binSerializedEntity;
            if (!(includeRemoved || ignoreNullData))
            {
                resultObject.Components = data;
            }

            return serializationAdapter.SerializeAdapterEntity(resultObject);
        }

        public override byte[] BuildFullSerializedEntityWithGDAP(ECSEntity toEntity, ECSEntity fromEntity)
        {
            var componentData = GroupDataAccessPolicy.RawComponentsFilter(toEntity, fromEntity);
            var resultObject = new SerializedEntity();
            if (componentData.Count == 0)
            {
                return new byte[0];
            }
            var serializedData = SlicedSerialize(fromEntity);
            foreach (var comp in componentData)
            {
                byte[] serialData = null;
                if (!serializedData.TryGetValue(comp, out serialData))
                    continue;
                resultObject.Components[comp] = serialData;
            }
            resultObject.Entity = serializedData[ECSEntity.Id];

            return serializationAdapter.SerializeAdapterEntity(resultObject);
        }

        protected override byte[] BuildFullSerializedEntity(ECSEntity Entity)
        {
            var serializedData = FullSerialize(Entity, false);
            return serializedData;
        }

        public override ECSEntity Deserialize(byte[] serializedData)
        {
            SerializedEntity bufEntity;
            EntityComponentStorage storage;

            bufEntity = serializationAdapter.DeserializeAdapterEntity(serializedData);
            bufEntity.DeserializeEntity();

            storage = bufEntity.desEntity.entityComponents;
            storage.DeserializeStorage(serializationAdapter, bufEntity.Components);
            storage.RestoreComponentsAfterSerialization(bufEntity.desEntity);
            bufEntity.desEntity.fastEntityComponentsId = new Dictionary<long, int>(bufEntity.desEntity.entityComponents.Components.ToDictionary(k => k.instanceId, t => 0));
            bufEntity.desEntity.AfterDeserialization();
            return bufEntity.desEntity;
        }

        public override void UpdateDeserialize(byte[] serializedData)
        {
            // ФАЗА 4 (ТЗ 4.7, дефект 6.5): был lock(this) — ОДИН монитор на весь входящий
            // поток мира. Теперь порядок ПЕР-СУЩНОСТНЫЙ: write-сторона существующего
            // StabilizationGate сущности (его документированное назначение с фазы 3:
            // write — мутации, read — сериализация среза; потребители — ровно
            // EntityNetSerializer и DBComponent). Следствия:
            //  * апдейты РАЗНЫХ сущностей идут параллельно;
            //  * апдейты ОДНОЙ сущности упорядочены её гейтом;
            //  * SlicedSerialize (read-гейт) больше не может снять срез ПОСРЕДИ
            //    полуприменённого апдейта той же сущности — прежние lock(this)/ReadLock
            //    не исключали друг друга; это целевой эффект переезда на гейт.
            // Дисциплина порядка локов: гейт → lock(serializedDB) → SerialLocker —
            // совпадает с мутационными API DBComponent; write-гейт реентерабелен
            // (повторный захват тем же потоком → mock-токен), поэтому внутренние
            // WriteLock'и DB-агрегатора под нашим гейтом безопасны.
            ECSEntity entity;
            SerializedEntity bufEntity;
            EntityComponentStorage storage;

            // Декодирование входного пакета — работа над thread-local носителем, вне локов.
            bufEntity = serializationAdapter.DeserializeAdapterEntity(serializedData);
            bufEntity.DeserializeEntity();
            var ecsWorld = this.worldOwner;

            if (!ecsWorld.entityManager.TryGetEntitySyncronized(bufEntity.desEntity.instanceId, out entity))
            {
                var candidate = bufEntity.desEntity;
                storage = candidate.entityComponents;
                // Гейт кандидата берётся ДО публикации: с момента AddNewEntity
                // конкурирующие апдейты этой же сущности встают на этот же гейт —
                // «публикация + restore» атомарны пер-сущностно, как были атомарны
                // под прежним монитором.
                using (candidate.entityComponents.StabilizationGate.WriteLock())
                {
                    storage.DeserializeStorage(serializationAdapter, bufEntity.Components);
                    if (ecsWorld.entityManager.AddNewEntity(candidate, true))
                    {
                        entity = candidate;
                        storage.RestoreComponentsAfterSerialization(entity);
                        entity.fastEntityComponentsId = new Dictionary<long, int>(entity.entityComponents.Components.ToDictionary(k => k.instanceId, t => 0));
                        entity.AfterDeserialization();

                        if (Defines.LogECSEntitySerializationComponents)
                        {
                            NLogger.Log($"In {bufEntity.desEntity.AliasName} Entity added " + bufEntity.desEntity.instanceId.ToString() + $" with {entity.entityComponents.ComponentClasses.Select(x => x.Name).ToStringListing()} components");
                        }
                        ecsWorld.entityManager.AddNewEntityReaction(entity);
                        return;
                    }
                }
                // Проигранная гонка добавления (id уже опубликован конкурентом): прежний
                // монитор исполнил бы этот пакет ВТОРЫМ — как апдейт живой сущности;
                // воспроизводим ровно эту очерёдность. (AddFailed-лог репозитория уже
                // отработал внутри AddNewEntity.)
                if (!ecsWorld.entityManager.TryGetEntitySyncronized(candidate.instanceId, out entity))
                {
                    NLogger.Error($"UpdateDeserialize: entity {candidate.instanceId} lost add-race but is not resolvable — packet dropped");
                    return;
                }
            }

            using (entity.entityComponents.StabilizationGate.WriteLock())
            {
                bufEntity.desEntity.entityComponents.DeserializeStorage(serializationAdapter, bufEntity.Components);

                // ТЗ 4.7: «клиент фильтрует Server-группу и наоборот» — маппинг «какая
                // группа чужая» уехал в профиль (RestoreFilterForeignGroupId, идея 1.15);
                // сериализатор больше не знает BuiltIn-групп (готовность к выносу сборки).
                entity.entityComponents.FilterRemovedComponents(bufEntity.desEntity.fastEntityComponentsId.Keys.ToList(), new List<long>() { this.worldOwner.Profile.RestoreFilterForeignGroupId });
                entity.entityComponents.RegisterAllComponents();

                List<ECSComponent> afterDeser = new List<ECSComponent>();

                foreach (var component in bufEntity.desEntity.entityComponents.SerializationContainer)
                {
                    var tComponent = (ECSComponent)component.Value;
                    if (Defines.LogECSEntitySerializationComponents)
                    {
                        NLogger.Log($"In entity {bufEntity.desEntity.AliasName}:{entity.instanceId} will updated {tComponent.GetType().Name}");
                    }
                    entity.AddOrChangeComponentWithOwnerRestoring(tComponent);
                    afterDeser.Add(tComponent);
                }
                
                afterDeser.ForEach(tComponent => {
                    // Фаза 4, шаг 2 (ТЗ 4.7): участник вместо is DBComponent. Хуки зовутся на
                    // ЖИВОМ инстансе (restoring-режим сохраняет старый инстанс агрегатора,
                    // перенимая payload) — прежний re-fetch по id сохранён дословно.
                    if (tComponent is AECC.Abstractions.ISerializationParticipant)
                    {
                        var live = entity.GetComponent(tComponent.GetId());
                        ((AECC.Abstractions.ISerializationParticipant)live)
                            .AfterRestore(this.worldOwner.Profile.ClientRetryOnMissingRefs);
                        live.AfterDeserialization();
                    }
                    else
                    {
                        tComponent.AfterDeserialization();
                    }
                });
                
                entity.AfterDeserialization();
                entity.entityComponents.RegisterAllComponents();
            }
        }
    }
}