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

namespace AECC.Core.Serialization
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

            using (entity.entityComponents.StabilizationLocker.ReadLock())
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
                    GDAP.BinAvailableComponents.ForEach(x => preparedData += EntitySerializer.TypeStorage[x.Key].ToString() + "\n");
                    if (preparedData != "")
                    {
                        NLogger.Log($"Will removed last serialization data in {entity.AliasName}:{entity.instanceId} as\n {preparedData}");
                    }
                }
                GDAP.JsonAvailableComponents = "";
                GDAP.BinAvailableComponents.Clear();
                GDAP.JsonRestrictedComponents = "";
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
            var data = GroupDataAccessPolicy.ComponentsFilter(toEntity, fromEntity);
            if (Defines.LogECSEntitySerializationComponents)
            {
                var preparedData = "";
                data.Item2.ForEach(x => preparedData += EntitySerializer.TypeStorage[x.Key].ToString() + "\n");
                if (preparedData != "")
                {
                    NLogger.Log($"GDAP filtering result from base {toEntity.AliasName}:{toEntity.instanceId} to {fromEntity.AliasName}:{fromEntity.instanceId} as\n {preparedData}");
                }
            }
            var resultObject = new SerializedEntity();
            if (data.Item1 == "" && data.Item2.Count() == 0 && !ignoreNullData)
            {
                return new byte[0];
            }
            resultObject.Entity = fromEntity.binSerializedEntity;
            if (!(data.Item1 == "#INCLUDEREMOVED#" || ignoreNullData))
            {
                data.Item1 = "";
                resultObject.Components = data.Item2;
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
            ECSEntity entity;
            SerializedEntity bufEntity;
            EntityComponentStorage storage;

            lock (this)
            {
                bufEntity = serializationAdapter.DeserializeAdapterEntity(serializedData);
                bufEntity.DeserializeEntity();
                var ecsWorld = this.worldOwner;
                
                if (!ecsWorld.entityManager.TryGetEntitySyncronized(bufEntity.desEntity.instanceId, out entity))
                {
                    entity = bufEntity.desEntity;
                    storage = bufEntity.desEntity.entityComponents;
                    storage.DeserializeStorage(serializationAdapter, bufEntity.Components);
                    ecsWorld.entityManager.AddNewEntity(entity, true);
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
                
                bufEntity.desEntity.entityComponents.DeserializeStorage(serializationAdapter, bufEntity.Components);

                if (this.worldOwner.WorldType == ECSWorld.WorldTypeEnum.Client)
                {
                    entity.entityComponents.FilterRemovedComponents(bufEntity.desEntity.fastEntityComponentsId.Keys.ToList(), new List<long>() { ServerComponentGroup.Id });
                }
                else if (this.worldOwner.WorldType == ECSWorld.WorldTypeEnum.Server || this.worldOwner.WorldType == ECSWorld.WorldTypeEnum.Offline)
                {
                    entity.entityComponents.FilterRemovedComponents(bufEntity.desEntity.fastEntityComponentsId.Keys.ToList(), new List<long>() { ClientComponentGroup.Id });
                }
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
                    if (tComponent is DBComponent)
                    {
                        if (this.worldOwner.WorldType == ECSWorld.WorldTypeEnum.Server || this.worldOwner.WorldType == ECSWorld.WorldTypeEnum.Offline)
                        {
                            entity.GetComponent<DBComponent>(tComponent.GetId()).UnserializeDB();
                        }
                        else
                        {
                            entity.GetComponent<DBComponent>(tComponent.GetId()).UnserializeDB(true);
                        }
                        entity.GetComponent<DBComponent>(tComponent.GetId()).AfterDeserialization();
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