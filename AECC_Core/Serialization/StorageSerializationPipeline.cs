using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AECC.Collections;
using AECC.Core.Logging;
using AECC.Extensions;

using AECC.Core.Serialization;

using AECC.Core;

namespace AECC.Serialization
{
    /// <summary>
    /// Extension methods providing storage-level (de)serialization for EntityComponentStorage.
    /// Access to the storage's internal members (Store, changedComponents, entity, Kid) is
    /// granted via InternalsVisibleTo("AECC.Serialization").
    /// </summary>
    public static class EntityComponentStorageSerialization
    {
        public static Dictionary<long, byte[]> SlicedSerializeStorage(this EntityComponentStorage s, ISerializationAdapter serializationAdapter, bool serializeOnlyChanged, bool clearChanged)
        {
            if (serializeOnlyChanged)
            {
                {
                    DictionaryWrapper<Type, ECSComponent> serializedContainer = new DictionaryWrapper<Type, ECSComponent>();
                    Dictionary<long, byte[]> slicedComponents = new Dictionary<long, byte[]>();
                    var cachedChangedComponents = s.changedComponents.Keys.ToList();
                    List<Type> errorList = new List<Type>();
                    foreach (var changedComponent in cachedChangedComponents)
                    {
                        if (Defines.LogECSEntitySerializationComponents)
                        {
                            NLogger.Log($"Will serialized changed component {changedComponent} in {s.entity.AliasName}:{s.entity.instanceId}");
                        }
                        s.Store.ExecuteReadLocked(EntityComponentStorage.Kid(changedComponent), (key, component) =>
                        {
                            using (MemoryStream writer = new MemoryStream())
                            {
                                var pairComponent = new KeyValuePair<long, ECSComponent>(component.GetId(), component);
                                byte[] serializedData = null;
                                lock (component.SerialLocker)
                                {
                                    component.EnterToSerialization();

                                    var participant = component as AECC.Abstractions.ISerializationParticipant;
                                    if (participant != null)
                                    {
                                        participant.BeforeSnapshot(serializeOnlyChanged, clearChanged);
                                    }

                                    serializedData = serializationAdapter.SerializeECSComponent(component);

                                    if (participant != null)
                                    {
                                        participant.AfterSnapshot(clearChanged);
                                    }
                                    component.AfterSerialization();
                                }
                                slicedComponents[pairComponent.Key] = serializedData;
                                if (clearChanged)
                                    s.changedComponents.Remove(component.GetTypeFast(), out _);
                            }
                        });
                    }
                    return slicedComponents;
                }
            }
            else
            {
                {
                    Dictionary<long, byte[]> slicedComponents = new Dictionary<long, byte[]>();
                    // Full slice is read directly from the live Store (key == component.GetId()),
                    // following the same discipline as the changed-only branch above: snapshot
                    // the keys, then ExecuteReadLocked per key so the read excludes concurrent
                    // mutation of that component.
                    var cacheStoreKeys = s.Store.Keys.ToList();
                    foreach (var pairComponentKey in cacheStoreKeys)
                    {
                        s.Store.ExecuteReadLocked(pairComponentKey, (key, pairComponent) => { 
                            using (MemoryStream writer = new MemoryStream())
                            {
                                if (!(pairComponent as ECSComponent).Unregistered)
                                {
                                    if (Defines.LogECSEntitySerializationComponents)
                                    {
                                        NLogger.Log($"Will serialized component {pairComponent.GetType()} in {s.entity.AliasName}:{s.entity.instanceId}");
                                    }

                                    var participant = pairComponent as AECC.Abstractions.ISerializationParticipant;
                                    if (participant != null)
                                    {
                                        participant.BeforeSnapshot(serializeOnlyChanged, clearChanged);
                                    }

                                    var serializedData = serializationAdapter.SerializeECSComponent((pairComponent as ECSComponent));

                                    slicedComponents[pairComponentKey] = serializedData;
                                    if (participant != null)
                                    {
                                        participant.AfterSnapshot(clearChanged);
                                    }
                                    if (clearChanged)
                                        s.changedComponents.Remove((pairComponent as ECSComponent).GetTypeFast(), out _);
                                }
                            }
                            });
                    }
                    return slicedComponents;
                }
                return null;
            }
        }

        public static Dictionary<long, byte[]> SerializeStorage(this EntityComponentStorage s, ISerializationAdapter serializationAdapter, bool serializeOnlyChanged, bool clearChanged)
        {
            Dictionary<long, byte[]> serializeContainer = new Dictionary<long, byte[]>();
            if (serializeOnlyChanged)
            {
                foreach (var changedComponent in s.changedComponents)
                {
                    var component = s.Store.GetOrThrow(EntityComponentStorage.Kid(changedComponent.Key));
                    if (Defines.LogECSEntitySerializationComponents)
                    {
                        NLogger.Log($"Will serialized component {component.GetType()} in {s.entity.AliasName}:{s.entity.instanceId}");
                    }
                    serializeContainer[component.GetId()] = serializationAdapter.SerializeECSComponent(component);
                }
            }
            else
            {
                // Full pass iterates the live Store directly (key == component.GetId()).
                foreach (var changedComponent in s.Store)
                {
                    serializeContainer[changedComponent.Key] = serializationAdapter.SerializeECSComponent(changedComponent.Value);
                }
            }
            if (clearChanged)
                s.changedComponents.Clear();
            return serializeContainer;
        }

        /// <summary>
        /// Unpacks a packet's components. Unpacked components are transient — they live
        /// only for the duration of this call (unpack -> transfer into the live Store) — so
        /// the result is a local dictionary rather than state stored on the entity.
        /// </summary>
        public static Dictionary<long, ECSComponent> DeserializeStorage(this EntityComponentStorage s, ISerializationAdapter serializationAdapter, Dictionary<long, byte[]> serializedComponents)
        {
            var landed = new Dictionary<long, ECSComponent>(serializedComponents.Count);
            foreach (var serComponent in serializedComponents)
            {
                landed[serComponent.Key] = (ECSComponent)serializationAdapter.DeserializeECSComponent(serComponent.Value, serComponent.Key);
            }
            return landed;
        }

    }
}
