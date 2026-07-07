using System.Collections.Concurrent;
using AECC.Extensions;
using AECC.Core.Logging;
using System.Collections.Concurrent;
using AECC.Extensions;
using AECC.Core.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.ComponentModel;
using AECC.Core.Serialization;
using AECC.Extensions.ThreadingSync;
using AECC.Collections;
using AECC.Locking;
using AECC.Core.BuiltInTypes.Components;
using System.Runtime.Serialization;

namespace AECC.Core
{
    [System.Serializable]
    public partial class EntityComponentStorage
    {
        private ECSEntity entity;
        public int ChangedComponent => changedComponents.Count;
        public bool isAsync => false; // PHASE 3a: async component storage retired (was allocating componentsAsync per entity)

        // PHASE 2: per-entity component storage on LockedDictionarySlim keyed by the stable type
        // UID (long, == component.GetId() == type.TypeId()). The heavyweight per-component
        // ReaderWriterLockSlim is gone (the cell carries an inline `long` lock). HoldKeys is ON:
        // absence-holds (HoldComponentAddition / ExecuteOnNotHasComponent) are part of the contract
        // machinery. Public API still takes Type; Kid() maps Type->id at the boundary (attribute-
        // based, registry-independent), so int->Type reverse lookups are never introduced.
        private LockedDictionarySlim<long, ECSComponent> componentsValue;
        private LockedDictionarySlim<long, ECSComponent> components
        {
            get
            {
                if (componentsValue == null)
                {
                    componentsValue = new LockedDictionarySlim<long, ECSComponent>(true);
                }
                return componentsValue;
            }
            set
            {
                componentsValue = value;
            }
        }

        // Type -> stable type UID. Reads [TypeUid] via the same mechanism as ECSComponent.GetId()
        // (cached reflection), so it does NOT depend on the serializer's TypeStorage being populated.
        private static long Kid(Type t) { return t.TypeId(); }

        private readonly IDictionary<Type, int> changedComponents = new DictionaryWrapper<Type, int>();
        public IDictionary<long, Type> IdToTypeComponentValue;
        public IDictionary<long, Type> IdToTypeComponent
        {
            get
            {
                if (IdToTypeComponentValue == null)
                {
                    IdToTypeComponentValue = new Dictionary<long, Type>();
                }
                return IdToTypeComponentValue;
            }
            set
            {
                IdToTypeComponentValue = value;
            }
        }
        private LockedDictionarySlim<long, object> SerializationContainerValue;
        public LockedDictionarySlim<long, object> SerializationContainer
        {
            get
            {
                if (SerializationContainerValue == null)
                {
                    SerializationContainerValue = new LockedDictionarySlim<long, object>();
                }
                return SerializationContainerValue;
            }
            set
            {
                SerializationContainerValue = value;
            }
        }

        public List<long> RemovedComponents = new List<long>();
        [System.NonSerialized]
        public RWLock StabilizationLockerValue;
        [IgnoreDataMember]
        public RWLock StabilizationLocker
        {
            get
            {
                if (StabilizationLockerValue == null)
                {
                    StabilizationLockerValue = new RWLock();
                }
                return StabilizationLockerValue;
            }
            set
            {
                StabilizationLockerValue = value;
            }
        }

        private bool IdToTypeMode = false; // only for debug reason

        public EntityComponentStorage(ECSEntity entity)
        {
            this.entity = entity;
        }

        #region serialization

        public Dictionary<long, byte[]> SlicedSerializeStorage(ISerializationAdapter serializationAdapter, bool serializeOnlyChanged, bool clearChanged)
        {
            if (serializeOnlyChanged)
            {
                //using (this.StabilizationLocker.ReadLock())//lock (this.serializationLocker)
                {
                    DictionaryWrapper<Type, ECSComponent> serializedContainer = new DictionaryWrapper<Type, ECSComponent>();
                    Dictionary<long, byte[]> slicedComponents = new Dictionary<long, byte[]>();
                    var cachedChangedComponents = changedComponents.Keys.ToList();
                    List<Type> errorList = new List<Type>();
                    foreach (var changedComponent in cachedChangedComponents)
                    {
                        if (Defines.LogECSEntitySerializationComponents)
                        {
                            NLogger.Log($"Will serialized changed component {changedComponent} in {this.entity.AliasName}:{this.entity.instanceId}");
                        }
                        components.ExecuteReadLocked(Kid(changedComponent), (key, component) =>
                        {
                            using (MemoryStream writer = new MemoryStream())
                            {
                                var pairComponent = new KeyValuePair<long, ECSComponent>(component.GetId(), component);
                                //var component = pairComponent.Value;
                                byte[] serializedData = null;
                                lock (component.SerialLocker)
                                {
                                    component.EnterToSerialization();

                                    DBComponent dBComponent = null;

                                    if (component is DBComponent)
                                    {
                                        dBComponent = (component as DBComponent);
                                    }
                                    if (dBComponent != null)
                                    {
                                        dBComponent.SerializeDB(serializeOnlyChanged, clearChanged);
                                    }

                                    //NetSerializer.Serializer.Default.Serialize(writer, component);
                                    serializedData = serializationAdapter.SerializeECSComponent(component);

                                    if (dBComponent != null)
                                    {
                                        dBComponent.AfterSerializationDB(clearChanged);
                                    }
                                    component.AfterSerialization();
                                }
                                slicedComponents[pairComponent.Key] = serializedData;//writer.ToArray();
                                if (clearChanged)
                                    changedComponents.Remove(component.GetTypeFast(), out _);
                            }
                        });
                    }
                    return slicedComponents;
                }
            }
            else
            {
                //using (this.StabilizationLocker.ReadLock())//lock (this.serializationLocker)
                {
                    Dictionary<long, byte[]> slicedComponents = new Dictionary<long, byte[]>();
                    var cacheSerializationContainerKeys = SerializationContainer.Keys.ToList();
                    foreach (var pairComponentKey in cacheSerializationContainerKeys)
                    {
                        SerializationContainer.ExecuteReadLocked(pairComponentKey, (key, pairComponent) => { 
                            using (MemoryStream writer = new MemoryStream())
                            {
                                if (!(pairComponent as ECSComponent).Unregistered)
                                {
                                    DBComponent dbComp = null;

                                    if (Defines.LogECSEntitySerializationComponents)
                                    {
                                        NLogger.Log($"Will serialized component {pairComponent.GetType()} in {this.entity.AliasName}:{this.entity.instanceId}");
                                    }

                                    if (pairComponent is DBComponent)
                                    {
                                        dbComp = (pairComponent as DBComponent);
                                        dbComp.SerializeDB(serializeOnlyChanged, clearChanged);
                                    }

                                    //NetSerializer.Serializer.Default.Serialize(writer, pairComponent);
                                    var serializedData = serializationAdapter.SerializeECSComponent((pairComponent as ECSComponent));

                                    slicedComponents[pairComponentKey] = serializedData;//writer.ToArray();
                                    if (dbComp != null)
                                    {
                                        dbComp.AfterSerializationDB(clearChanged);
                                    }
                                    if (clearChanged)
                                        changedComponents.Remove((pairComponent as ECSComponent).GetTypeFast(), out _);
                                }
                            }
                            });
                    }
                    return slicedComponents;
                }
                return null;
            }
        }

        public Dictionary<long, byte[]> SerializeStorage(ISerializationAdapter serializationAdapter, bool serializeOnlyChanged, bool clearChanged) // OBSOLETE
        {
            Dictionary<long, byte[]> serializeContainer = new Dictionary<long, byte[]>();
            if (serializeOnlyChanged)
            {
                foreach (var changedComponent in changedComponents)
                {
                    var component = components[Kid(changedComponent.Key)];
                    if (Defines.LogECSEntitySerializationComponents)
                    {
                        NLogger.Log($"Will serialized component {component.GetType()} in {this.entity.AliasName}:{this.entity.instanceId}");
                    }
                    serializeContainer[component.GetId()] = serializationAdapter.SerializeECSComponent(component);
                }
            }
            else
            {
                foreach (var changedComponent in SerializationContainer)
                {
                    serializeContainer[changedComponent.Key] = serializationAdapter.SerializeECSComponent(changedComponent.Value as ECSComponent);
                }
            }
            if (clearChanged)
                changedComponents.Clear();
            return serializeContainer;
        }

        public void DeserializeStorage(ISerializationAdapter serializationAdapter, Dictionary<long, byte[]> serializedComponents)
        {
            foreach (var serComponent in serializedComponents)
            {
                this.SerializationContainer[serComponent.Key] = (ECSComponent)serializationAdapter.DeserializeECSComponent(serComponent.Value, serComponent.Key);
            }
        }

        public void RestoreComponentsAfterSerialization(ECSEntity entity)
        {
            this.entity = entity;
            if (components.Count == 0)
            {
                List<ECSComponent> afterDeser = new List<ECSComponent>();
                foreach (var objPair in SerializationContainer)
                {
                    ECSComponent objComponent = (ECSComponent)objPair.Value;
                    Type component;
                    if (EntitySerializer.TypeStorage.TryGetValue(objPair.Key, out component))
                    {
                        var typedComponent = (ECSComponent)Convert.ChangeType(objPair.Value, component);
                        
                        AddComponentImmediately(component, typedComponent, true, true);
                        afterDeser.Add(typedComponent);
                    }
                }
                afterDeser.ForEach(typedComponent =>
                {
                    if (typedComponent is DBComponent)
                    {
                        //TaskEx.RunAsync(() =>
                        //{
                        
                            if (this.entity.ECSWorldOwner.WorldType == ECSWorld.WorldTypeEnum.Server || this.entity.ECSWorldOwner.WorldType == ECSWorld.WorldTypeEnum.Offline)
                            {
                                (typedComponent as DBComponent).UnserializeDB();
                            }
                            else
                            {
                                (typedComponent as DBComponent).UnserializeDB(true);
                            }
                        //});
                    }
                    typedComponent.AfterDeserialization();
                });
            }
        }


        #endregion

        public bool CheckChanged(Type typeComponent) => changedComponents.Keys.Contains(typeComponent);
        public void DirectiveChange(Type typeComponent)
        {
            components.ExecuteReadLocked(Kid(typeComponent), (key, component) =>
            {
                changedComponents[typeComponent] = 1;
            });
        }

        #region Base functions

        public bool AddOrChangeComponentImmediately(Type comType, ECSComponent component, bool restoringMode = false, bool silent = false)
        {
            bool added = false;
            bool changed = false;
            components.ExecuteOnAddOrChangeLocked(Kid(comType), component, (key, newcomponent) => {
                AddComponentProcess(comType, newcomponent, restoringMode);
                    added = true;
            }, (key, newcomponent, oldcomponent) => {
                changed = ChangeComponentProcess(newcomponent, oldcomponent, silent, restoringMode ? this.entity : null);
                if (restoringMode)
				{
					if (newcomponent is DBComponent dBComponent)
                    {
                        this.components.UnsafeChange(key, oldcomponent);
                        (oldcomponent as DBComponent).serializedDB = (newcomponent as DBComponent).serializedDB;
                    }
				}
            });
            if (added)
            {
                if (!silent)
                {
                    components.ExecuteReadLocked(Kid(comType), (key, addedcomponent) =>
                    {
                        if (component.ECSWorldOwner != null)
                        {
                            addedcomponent.Unregistered = false;
                            component.AddedReaction(this.entity);
                        }
                    });
                }
            }
            // else
            //     NLogger.Error("try add presented component");
            
            if (!silent && changed)
            {
                component.ChangeReaction(this.entity);
            }
            return added;
        }


        public bool AddComponentImmediately(Type comType, ECSComponent component, bool restoringMode = false, bool silent = false)
        {
            bool added = false;
            if (!this.components.ContainsKey(Kid(comType)))
            {
                components.ExecuteOnAddLocked(Kid(comType), component, (key, newcomponent) =>
                {
                    AddComponentProcess(comType, newcomponent, restoringMode);
                    added = true;
                });
            }
            if (added)
            {
                if (!silent)
                {
                    components.ExecuteReadLocked(Kid(comType), (key, addedcomponent) =>
                    {
                        if (component.ECSWorldOwner != null)
                        {
                            addedcomponent.Unregistered = false;
                            component.AddedReaction(this.entity);
                        }
                    });
                }
            }
            else
                NLogger.Error($"try add presented component {comType.Name} into {this.entity.AliasName}:{this.entity.instanceId}");
            return added;
        }

        private void AddComponentProcess(Type comType, ECSComponent component, bool restoringMode = false)
        {
            component.ownerEntity = this.entity;
            component.ECSWorldOwner = this.entity?.ECSWorldOwner;
            if (this.entity != null)
            {
                if((!Defines.CutClientServerCollections) || (this.entity.ECSWorldOwner == null &&  !Defines.CutClientServerCollections) || (this.entity.ECSWorldOwner != null && this.entity.ECSWorldOwner.WorldType != ECSWorld.WorldTypeEnum.Offline && !Defines.CutClientServerCollections))
                {
                    this.entity.fastEntityComponentsId.AddI(component.instanceId, 0, this.entity.SerialLocker);
                }
            }
            else
            {
                NLogger.LogError("null owner entity");
            }

            if((!Defines.CutClientServerCollections) || (this.entity.ECSWorldOwner == null &&  !Defines.CutClientServerCollections) || (this.entity.ECSWorldOwner != null && this.entity.ECSWorldOwner.WorldType != ECSWorld.WorldTypeEnum.Offline && !Defines.CutClientServerCollections))
            {
                if (restoringMode)
                    this.SerializationContainer.TryAdd(component.GetId(), component);
                else
                    this.SerializationContainer[component.GetId()] = component;
            }
            if(IdToTypeMode)
                this.IdToTypeComponent.TryAdd(component.GetId(), component.GetTypeFast());
            component.ECSWorldOwner?.entityManager.OnAddComponent(this.entity, component);
        }

        public bool ChangeComponent(ECSComponent component, bool silent = false, ECSEntity restoringOwner = null)
        {
            bool changed = false;
            components.ExecuteOnChangeLocked(component.GetId(), component, (key, chcomponent, oldcomponent) =>
                {
                    changed = ChangeComponentProcess(chcomponent,oldcomponent, silent, restoringOwner);
                }
            );
            if (!silent && changed)
            {
                component.ChangeReaction(this.entity);
            }
            return changed;
        }

        private bool ChangeComponentProcess(ECSComponent component, ECSComponent oldcomponent, bool silent = false, ECSEntity restoringOwner = null)
        {
            bool changed = false;
            if (restoringOwner != null)
                component.ownerEntity = restoringOwner;
            if(component.ECSWorldOwner != null)
                component.Unregistered = false;
            //component.StateReactionQueue = oldcomponent.StateReactionQueue;
            if (!silent)
            {
                Type componentClass = component.GetTypeFast();
                changedComponents[componentClass] = 1;
                changed = true;
            }
            return changed;
        }

        public ECSComponent GetComponent(Type componentClass)
        {
            ECSComponent component = null;
            try
            {
                component = this.components[Kid(componentClass)];
            }
            catch (Exception ex)
            {
                if (ex is KeyNotFoundException && Defines.HiddenKeyNotFoundLog)
                    NLogger.Error(ex.Message + "  \n" + ex.StackTrace);
            }
            return component;
        }

        public ECSComponent GetComponentBroadcastType(Type componentClass)
        {
            ECSComponent component = null;
            try
            {
                component = this.components.FirstOrDefault(x => componentClass.IsInstanceOfType(x.Value)).Value;
            }
            catch (Exception ex)
            {
                if (ex is KeyNotFoundException && Defines.HiddenKeyNotFoundLog)
                    NLogger.Error(ex.Message + "  \n" + ex.StackTrace);
            }
            return component;
        }

        public IEnumerable<ECSComponent> GetComponentsBroadcastType(Type componentClass)
        {
            IEnumerable<ECSComponent> component = null;
            try
            {
                component = this.components.Where(x => componentClass.IsInstanceOfType(x.Value)).Select(x => x.Value);
            }
            catch (Exception ex)
            {
                if (ex is KeyNotFoundException && Defines.HiddenKeyNotFoundLog)
                    NLogger.Error(ex.Message + "  \n" + ex.StackTrace);
            }
            return component;
        }

        public ECSComponent GetComponent(long componentTypeId)
        {
            ECSComponent component = null;
            try
            {
                // typeId IS the dictionary key now (== component.GetId()); no registry reverse needed.
                component = this.components[componentTypeId];
            }
            catch (Exception ex)
            {
                if (ex is KeyNotFoundException && Defines.HiddenKeyNotFoundLog)
                    NLogger.Error(ex.Message + "  \n" + ex.StackTrace);
            }
            return component;
        }

        public bool MarkComponentChanged(ECSComponent component, bool serializationSilent = false, bool eventSilent = false)
        {
            bool changed = false;
            components.ExecuteOnChangeLocked(component.GetId(), component, (key, chcomponent, oldcomponent) =>
                {
                    Type componentClass = chcomponent.GetTypeFast();
                    if (!serializationSilent)
                    {
                        changedComponents[componentClass] = 1;
                        changed = true;
                    }
                }
            );
            if (!eventSilent && changed)
            {
                component.ChangeReaction(this.entity);
            }
            return changed;
        }

        public ECSComponent RemoveComponentImmediately(Type componentClass)
        {
            ECSComponent component2 = null;
            bool removed = false;

            components.ExecuteOnRemoveLocked(Kid(componentClass), out component2, (key, component) =>
            {
                RemoveComponentProcess(component.GetTypeFast(), component);
                removed = true;
            });
            if (removed)
            {
                component2.RemovingReaction(this.entity);
            }
            else
            {
                NLogger.LogError("try to remove non present component");
            }
            return component2;
        }

        // Id-based removal: typeId IS the slot key; the Type for RemoveComponentProcess is taken
        // from the live component instance (registry-independent reverse).
        private ECSComponent RemoveComponentByIdImmediately(long typeId)
        {
            ECSComponent component2 = null;
            bool removed = false;
            components.ExecuteOnRemoveLocked(typeId, out component2, (key, component) =>
            {
                RemoveComponentProcess(component.GetTypeFast(), component);
                removed = true;
            });
            if (removed)
            {
                component2.RemovingReaction(this.entity);
            }
            else
            {
                NLogger.LogError("try to remove non present component");
            }
            return component2;
        }

        private void RemoveComponentProcess(Type componentClass, ECSComponent component)
        {
            this.changedComponents.Remove(componentClass, out _);
            if(IdToTypeMode)
                this.IdToTypeComponent.Remove(component.GetId(), out _);
            if((!Defines.CutClientServerCollections) || (this.entity.ECSWorldOwner == null &&  !Defines.CutClientServerCollections) || (this.entity.ECSWorldOwner != null && this.entity.ECSWorldOwner.WorldType != ECSWorld.WorldTypeEnum.Offline && !Defines.CutClientServerCollections))
            {
                this.SerializationContainer.Remove(component.GetId(), out _);
                this.entity.fastEntityComponentsId.RemoveI(component.instanceId, this.entity.SerialLocker);
                this.RemovedComponents.Add(component.GetId());
            }
            
            component.ECSWorldOwner?.entityManager.OnRemoveComponent(this.entity, component);
        }




        /// <summary>
        /// Принадлежит ли сущность группе (флаг-членство): есть ли хотя бы один компонент,
        /// помеченный группой с данным Id. Группа — независимая IDObject-единица, не компонент.
        /// </summary>
        public bool HasComponentInGroup(long componentGroup)
        {
            foreach (var component in components)
            {
                if (component.Value.ComponentGroups != null
                    && component.Value.ComponentGroups.TryGetValueI(componentGroup, out _, component.Value.SerialLocker))
                {
                    return true;
                }
            }
            return false;
        }

        public void RemoveComponentsWithGroup(long componentGroup)
        {
            List<ECSComponent> toRemoveComponent = new List<ECSComponent>();
            List<ECSComponent> notRemovedComponent = new List<ECSComponent>();
            bool exception = false;
            foreach (var component in components)
            {
                if (component.Value.ComponentGroups != null && component.Value.ComponentGroups.TryGetValueI(componentGroup, out _, component.Value.SerialLocker))
                {
                    toRemoveComponent.Add(component.Value);
                }
            }
            toRemoveComponent.ForEach((removedComponent) =>
                {
                    this.ExecuteWriteLockedComponent(removedComponent.GetTypeFast(), (key, component) =>
                    {
                        if (!this.components.ContainsKey(removedComponent.GetId()))
                        {
                            exception = true;
                            notRemovedComponent.Add(removedComponent);
                        }
                        else
                        {
                            this.changedComponents.Remove(removedComponent.GetTypeFast(), out _);
                            this.components.Remove(removedComponent.GetId());
                            if((!Defines.CutClientServerCollections) || (this.entity.ECSWorldOwner == null &&  !Defines.CutClientServerCollections) || (this.entity.ECSWorldOwner != null && this.entity.ECSWorldOwner.WorldType != ECSWorld.WorldTypeEnum.Offline && !Defines.CutClientServerCollections))
                            {
                                this.SerializationContainer.Remove(removedComponent.GetId(), out _);
                                this.entity.fastEntityComponentsId.RemoveI(removedComponent.instanceId, this.entity.SerialLocker);
                                this.RemovedComponents.Add(removedComponent.GetId());
                            }
                            if(IdToTypeMode)
                                this.IdToTypeComponent.Remove(removedComponent.GetId(), out _);
                            
                            removedComponent.ECSWorldOwner?.entityManager.OnRemoveComponent(this.entity, removedComponent);
                            removedComponent.RemovingReaction(this.entity);
                        }
                    });
                });
            if (exception)
            {
                NLogger.Error("try to remove non present component in group removing");
            }
        }

        public void FilterRemovedComponents(List<long> filterList, List<long> filteringOnlyGroups)
        {
            var bufFilterList = new List<long>(filterList);
            foreach (var component in this.components)
            {
                if (filteringOnlyGroups.Count == 0)
                {
                    var id = component.Value.instanceId;
                    bool finded = false;
                    int i;
                    for (i = 0; i < bufFilterList.Count; i++)
                    {
                        if (id == bufFilterList[i])
                        {
                            finded = true;
                        }
                    }
                    if (!finded)
                    {
                        this.RemoveComponentImmediately(component.Key);
                    }
                }
                else
                {
                    foreach (var group in filteringOnlyGroups)
                    {
                        if (component.Value.ComponentGroups == null) continue;
                        foreach (var componentGroup in component.Value.ComponentGroups.SnapshotI(component.Value.SerialLocker))
                        {
                            if (componentGroup.Key == group)
                            {
                                var id = component.Value.instanceId;
                                bool finded = false;
                                int i;
                                for (i = 0; i < bufFilterList.Count; i++)
                                {
                                    if (id == bufFilterList[i])
                                    {
                                        finded = true;
                                    }
                                }
                                if (!finded)
                                {
                                    this.RemoveComponentImmediately(component.Key);
                                }
                            }
                        }
                    }
                }
            }
        }




        #endregion

        #region Unsafe component functions

        public bool AddComponentUnsafe(Type componentType, ECSComponent component, bool restoringMode = false, bool silent = false)
        {
            if (this.components.UnsafeAdd(Kid(componentType), component))
            {
                AddComponentProcess(componentType, component, restoringMode);
                if (!silent)
                {
                    component.AddedReaction(this.entity);
                }
                return true;
            }
            return false;
        }

        public bool ChangeComponentUnsafe(ECSComponent component, bool silent = false, ECSEntity restoringOwner = null)
        {
            var oldcomponent = GetComponentUnsafe(component.GetTypeFast());
            if (this.components.UnsafeChange(component.GetId(), component))
            {
                ChangeComponentProcess(component, oldcomponent, silent, restoringOwner);
                if (!silent)
                {
                    component.ChangeReaction(this.entity);
                }
                return true;
            }
            return false;
        }

        public bool RemoveComponentUnsafeSilent(Type componentType)
        {
            if (this.components.UnsafeRemove(Kid(componentType), out var component))
            {
                RemoveComponentProcess(componentType, component);
                return true;
            }
            return false;
        }

        public ECSComponent GetComponentUnsafe(Type componentType)
        {
            ECSComponent component;
            return (!this.components.TryGetValue(Kid(componentType), out component) ? null : component);
        }

        public ECSComponent GetComponentUnsafe(long componentTypeId)
        {
            ECSComponent component;
            // typeId IS the slot key (== component.GetId()); same in both modes.
            return !this.components.TryGetValue(componentTypeId, out component) ? null : component;
        }
        #endregion

        #region Async component functions
        public void AddComponentAsync(Type componentType, ECSComponent component)
        {
            TaskEx.RunAsync(() => AddComponentImmediately(componentType, component));
        }

        public void AddComponentsAsync(IList<ECSComponent> addedComponents)
        {
            TaskEx.RunAsync(() => AddComponentsImmediately(addedComponents));
        }

        public void RemoveComponentAsync(Type componentType)
        {
            TaskEx.RunAsync(() => RemoveComponentImmediately(componentType));
        }
        #endregion

        #region Additional Base functions

        public ECSComponent RemoveComponentImmediately(long componentTypeId)
        {
            return RemoveComponentByIdImmediately(componentTypeId);
        }

        public void AddComponentsImmediately(IList<ECSComponent> addedComponents)
        {
            addedComponents.ForEach<ECSComponent>(component => this.AddComponentImmediately(component.GetTypeFast(), component));
        }

        public void RemoveComponentsImmediately(IList<ECSComponent> removedComponents)
        {
            removedComponents.ForEach(component => this.RemoveComponentImmediately(component.GetTypeFast()));
        }

        public void RegisterAllComponents(bool previous_changed = false)
        {
            if (previous_changed)//bullshit from oldest version, need to check, but better been deleted
            {
                List<ECSComponent> changed_components = new List<ECSComponent>();
                foreach (var component in components)
                {
                    if (component.Value.Unregistered)
                    {
                        if (MarkComponentChanged(component.Value, false, true))
                        {
                            changed_components.Add(component.Value);
                        }
                    }
                }
                foreach (var component in changed_components)
                {
                    component.ECSWorldOwner = this.entity?.ECSWorldOwner;
                    if (component.ECSWorldOwner != null)
                    {
                        component.Unregistered = false;
                        component.AddedReaction(entity);
                        component.ECSWorldOwner?.entityManager.OnAddComponent(this.entity, component);
                    }
                }
            }
            else
            {
                foreach (var component1 in components)
                {
                    components.ExecuteReadLocked(component1.Key, (key, component) =>
                    {
                        if (component.Unregistered)
                        {
                            component.ECSWorldOwner = this.entity?.ECSWorldOwner;
                            if (component.ECSWorldOwner != null)
                            {
                                component.Unregistered = false;
                                component.AddedReaction(entity);
                                component.ECSWorldOwner?.entityManager.OnAddComponent(this.entity, component);
                            }
                            
                        }
                    });
                }
            }
            this.entity.Alive = true;
        }

        public bool HasComponent(Type componentClass) =>
            this.components.ContainsKey(Kid(componentClass));

        public bool HasComponent(long componentClassId)
        {
            if(IdToTypeMode)
            {
                return this.IdToTypeComponent.ContainsKey(componentClassId);
            }
            else
            {
                return this.components.ContainsKey(componentClassId);
            }
        }
            

        public void OnEntityDelete()
        {
            this.components.EnterLockdown();
            List<long> snapshot;
            try
            {
                snapshot = this.components.Keys.ToList();
            }
            catch (Exception ex)
            {
                NLogger.LogError(ex);
                snapshot = new List<long>();
            }

            foreach (var componentType in snapshot)
            {
                try
                {
                    this.RemoveComponentImmediately(componentType);
                }
                catch (Exception ex)
                {
                    NLogger.Log($"OnEntityDelete: failed to remove component {componentType}");
                    NLogger.LogError(ex);
                }
            }

            // Дочищаем технологические словари. К этому моменту RemoveComponentProcess
            // уже должен был вынести из них почти всё — это страховка от рассинхрона
            // и от компонентов, которых не оказалось в основном словаре.
            if ((!Defines.CutClientServerCollections)
                || (this.entity.ECSWorldOwner == null && !Defines.CutClientServerCollections)
                || (this.entity.ECSWorldOwner != null
                    && this.entity.ECSWorldOwner.WorldType != ECSWorld.WorldTypeEnum.Offline
                    && !Defines.CutClientServerCollections))
            {
                this.SerializationContainer.Clear();
                this.RemovedComponents.Clear();
            }

            if (IdToTypeMode)
                this.IdToTypeComponent.Clear();

            this.changedComponents.Clear();
            this.IdToTypeComponent.Clear();
        }

        public bool ExecuteOnNotHasComponent(Type componentType, Action action)
        {
            ECSComponent component;
            if (this.components.ExecuteOnKeyHolded(Kid(componentType), action))
            {
                return true;
            }
            return false;
        }

        public bool HoldComponentAddition(Type componentType, out RWToken token, bool holdMode = true)
        {
            return this.components.HoldKey(Kid(componentType), out token, holdMode);
        }

        public void ExecuteReadLockedComponent(Type componentType, Action<Type, ECSComponent> action)
        {
            // Public API keeps the Type-typed action; adapt to the long-keyed slot, supplying the
            // original Type from the closure so callers never see the internal id.
            components.ExecuteReadLocked(Kid(componentType), (key, component) => action(componentType, component));
        }

        public void ExecuteWriteLockedComponent(Type componentType, Action<Type, ECSComponent> action)
        {
            components.ExecuteWriteLocked(Kid(componentType), (key, component) => action(componentType, component));
        }

        public bool GetReadLockedComponent<T>(out T component, out RWToken token) where T : ECSComponent
        {
            ECSComponent tempComponent;

            bool result = this.GetReadLockedComponent(typeof(T), out tempComponent, out token);

            component = tempComponent as T; 

            return result && component != null;
        }

        public bool GetReadLockedComponent(Type componentType, out ECSComponent component, out RWToken token)
        {
            return components.TryGetLockedElement(Kid(componentType), out component, out token, false);
        }

        public bool GetWriteLockedComponent(Type componentType, out ECSComponent component, out RWToken token)
        {
            return components.TryGetLockedElement(Kid(componentType), out component, out token, true);
        }

        public RWToken GetWriteLockedComponentStorage()
        {
            return components.LockStorage();
        }

        #endregion

        public ICollection<Type> ComponentClasses =>
            this.components.Values.Select(c => c.GetTypeFast()).ToList();

        public ICollection<ECSComponent> Components =>
            this.components.Values;
    }
}
