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

using AECC.Core.Serialization;

using AECC.Core;

namespace AECC.Serialization
{
    /// <summary>
    /// Конкретная реализация сериализации сущностей.
    /// </summary>
    public class EntityNetSerializer : EntitySerializer
    {
        /// <summary>
        /// Blob сущности: NetSerializer энумерирует её сериализуемые коллекции
        /// (fastEntityComponentsId — обычный Dictionary), а ВСЕ мутаторы словаря
        /// (AddI/RemoveI/ClearI при добавлении/снятии компонентов из контрактов и
        /// таймер-потоков) идут под entity.SerialLocker БЕЗ StabilizationGate —
        /// без того же лока энумерация рвётся («Collection was modified» в тике
        /// роллинга). Порядок локов: gate(read) → SerialLocker; мутаторы берут
        /// SerialLocker коротко и гейт внутри не берут — дедлока нет.
        /// </summary>
        private byte[] SerializeEntityBlobLocked(ECSEntity entity)
        {
            lock (entity.SerialLocker)
            {
                return serializationAdapter.SerializeECSEntity(entity);
            }
        }

        protected override byte[] FullSerialize(ECSEntity entity, bool serializeOnlyChanged = false)
        {
            var resultObject = new SerializedEntity();
            entity.EnterToSerialization();
            resultObject.Entity = SerializeEntityBlobLocked(entity);
            resultObject.Components = entity.entityComponents.SerializeStorage(serializationAdapter, serializeOnlyChanged, true);

            return serializationAdapter.SerializeAdapterEntity(resultObject);
        }

        protected override Dictionary<long, byte[]> SlicedSerialize(ECSEntity entity, bool serializeOnlyChanged = false, bool clearChanged = false)
        {
            var resultObject = new SerializedEntity();

            using (entity.entityComponents.StabilizationGate.ReadLock())
            {
                entity.EnterToSerialization();
                resultObject.Entity = SerializeEntityBlobLocked(entity);
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
            var landedComponents = storage.DeserializeStorage(serializationAdapter, bufEntity.Components);
            storage.RestoreComponentsAfterSerialization(bufEntity.desEntity, landedComponents);
            bufEntity.desEntity.fastEntityComponentsId = new Dictionary<long, int>(bufEntity.desEntity.entityComponents.Components.ToDictionary(k => k.instanceId, t => 0));
            bufEntity.desEntity.AfterDeserialization();
            return bufEntity.desEntity;
        }

        public override void UpdateDeserialize(byte[] serializedData)
        {
            // Concurrency is per-entity: each entity's StabilizationGate orders its own
            // updates (write side) against its own slice serialization (read side), so
            // updates to different entities proceed in parallel while updates to the same
            // entity are serialized by that entity's gate. Lock order is gate ->
            // lock(serializedDB) -> SerialLocker, matching the mutation APIs; the write
            // gate is reentrant for the same thread, so nested WriteLocks taken under it
            // are safe.
            ECSEntity entity;
            SerializedEntity bufEntity;
            EntityComponentStorage storage;

            bufEntity = serializationAdapter.DeserializeAdapterEntity(serializedData);
            bufEntity.DeserializeEntity();
            var ecsWorld = this.worldOwner;

            if (!ecsWorld.entityManager.TryGetEntitySyncronized(bufEntity.desEntity.instanceId, out entity))
            {
                var candidate = bufEntity.desEntity;
                storage = candidate.entityComponents;
                // Take the candidate's gate before publishing it: from AddNewEntity onward,
                // concurrent updates to the same entity queue on this same gate, so
                // publish + restore are atomic per-entity.
                using (candidate.entityComponents.StabilizationGate.WriteLock())
                {
                    var landedCandidate = storage.DeserializeStorage(serializationAdapter, bufEntity.Components);
                    if (ecsWorld.entityManager.AddNewEntity(candidate, true))
                    {
                        entity = candidate;
                        storage.RestoreComponentsAfterSerialization(entity, landedCandidate);
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
                // Lost the add race (id already published by a concurrent packet): fall
                // through and treat this packet as an update to the now-live entity.
                if (!ecsWorld.entityManager.TryGetEntitySyncronized(candidate.instanceId, out entity))
                {
                    NLogger.Error($"UpdateDeserialize: entity {candidate.instanceId} lost add-race but is not resolvable — packet dropped");
                    return;
                }
            }

            using (entity.entityComponents.StabilizationGate.WriteLock())
            {
                var landedUpdate = bufEntity.desEntity.entityComponents.DeserializeStorage(serializationAdapter, bufEntity.Components);

                // Client filters the Server group and vice versa; which group counts as
                // "foreign" is supplied by the world profile (RestoreFilterForeignGroupId)
                // rather than hardcoded here.
                entity.entityComponents.FilterRemovedComponents(bufEntity.desEntity.fastEntityComponentsId.Keys.ToList(), new List<long>() { this.worldOwner.Profile.RestoreFilterForeignGroupId });
                entity.entityComponents.RegisterAllComponents();

                List<ECSComponent> afterDeser = new List<ECSComponent>();

                foreach (var component in landedUpdate)
                {
                    var tComponent = component.Value;
                    if (Defines.LogECSEntitySerializationComponents)
                    {
                        NLogger.Log($"In entity {bufEntity.desEntity.AliasName}:{entity.instanceId} will updated {tComponent.GetType().Name}");
                    }
                    entity.AddOrChangeComponentWithOwnerRestoring(tComponent);
                    afterDeser.Add(tComponent);
                }
                
                afterDeser.ForEach(tComponent => {
                    // Restore hooks fire on the live instance: restoring mode keeps the old
                    // aggregator instance and has it take over the incoming payload, so the
                    // component must be re-fetched by id rather than used directly.
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