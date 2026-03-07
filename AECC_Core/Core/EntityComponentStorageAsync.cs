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
using System.Threading.Tasks;
using AECC.Collections;
using AECC.Core.BuiltInTypes.Components;

namespace AECC.Core
{
    public partial class EntityComponentStorage
    {
        
        // Используем асинхронный словарь для компонентов
        private readonly LockedDictionaryAsync<Type, ECSComponent> componentsAsync = new LockedDictionaryAsync<Type, ECSComponent>(true);
        
        // Контейнер сериализации также становится асинхронным
        public LockedDictionaryAsync<long, object> SerializationContainerAsync = new LockedDictionaryAsync<long, object>();

        #region serialization

        public async Task<Dictionary<long, byte[]>> SlicedSerializeStorageAsync(ISerializationAdapter serializationAdapter, bool serializeOnlyChanged, bool clearChanged)
        {
            if (serializeOnlyChanged)
            {
                // Для асинхронной стабилизации используем using с await
                // using (await this.StabilizationLocker.ReadLockAsync())
                {
                    Dictionary<long, byte[]> slicedComponents = new Dictionary<long, byte[]>();
                    var cachedChangedComponents = changedComponents.Keys.ToList();
                    
                    foreach (var changedComponent in cachedChangedComponents)
                    {
                        if (Defines.LogECSEntitySerializationComponents)
                        {
                            NLogger.Log($"Will serialized changed component {changedComponent} in {this.entity.AliasName}:{this.entity.instanceId}");
                        }
                        await componentsAsync.ExecuteReadLockedAsync(changedComponent, async (key, component) =>
                        {
                            using (MemoryStream writer = new MemoryStream())
                            {
                                var pairComponent = new KeyValuePair<long, ECSComponent>(component.GetId(), component);
                                byte[] serializedData = null;
                                lock (component.SerialLocker)
                                {
                                    component.EnterToSerialization();

                                    DBComponent dBComponent = null;
                                    SharedLock.LockToken dbLocktocken = null;

                                    if (component is DBComponent)
                                    {
                                        dBComponent = (component as DBComponent);
                                    }
                                    if (dBComponent != null)
                                    {
                                        // Предполагается, что SerializeDB синхронный, если есть асинхронный аналог - заменить на await
                                        dbLocktocken = dBComponent.SerializeDB(serializeOnlyChanged, clearChanged);
                                    }

                                    serializedData = serializationAdapter.SerializeECSComponent(component);

                                    if (dbLocktocken != null)
                                    {
                                        dbLocktocken.Dispose();
                                    }

                                    if (dBComponent != null)
                                    {
                                        dBComponent.AfterSerializationDB(clearChanged);
                                    }
                                    component.AfterSerialization();
                                }
                                slicedComponents[pairComponent.Key] = serializedData;
                                if (clearChanged)
                                    changedComponents.Remove(component.GetTypeFast(), out _);
                            }
                            await Task.CompletedTask;
                        });
                    }
                    return slicedComponents;
                }
            }
            else
            {
                // using (await this.StabilizationLocker.ReadLockAsync())
                {
                    Dictionary<long, byte[]> slicedComponents = new Dictionary<long, byte[]>();
                    var cacheSerializationContainerKeys = await SerializationContainerAsync.GetKeysAsync();
                    
                    foreach (var pairComponentKey in cacheSerializationContainerKeys)
                    {
                        await SerializationContainerAsync.ExecuteReadLockedAsync(pairComponentKey, async (key, pairComponent) => 
                        { 
                            using (MemoryStream writer = new MemoryStream())
                            {
                                var ecsComponent = pairComponent as ECSComponent;
                                if (!ecsComponent.Unregistered)
                                {
                                    DBComponent dbComp = null;

                                    if (Defines.LogECSEntitySerializationComponents)
                                    {
                                        NLogger.Log($"Will serialized component {pairComponent.GetType()} in {this.entity.AliasName}:{this.entity.instanceId}");
                                    }

                                    SharedLock.LockToken dbLocktocken = null;

                                    if (pairComponent is DBComponent)
                                    {
                                        dbComp = (pairComponent as DBComponent);
                                        dbLocktocken = dbComp.SerializeDB(serializeOnlyChanged, clearChanged);
                                    }

                                    var serializedData = serializationAdapter.SerializeECSComponent(ecsComponent);

                                    if (dbLocktocken != null)
                                    {
                                        dbLocktocken.Dispose();
                                    }

                                    slicedComponents[pairComponentKey] = serializedData;
                                    if (dbComp != null)
                                    {
                                        dbComp.AfterSerializationDB(clearChanged);
                                    }
                                    if (clearChanged)
                                        changedComponents.Remove(ecsComponent.GetTypeFast(), out _);
                                }
                            }
                            await Task.CompletedTask;
                        });
                    }
                    return slicedComponents;
                }
            }
        }

        public async Task<Dictionary<long, byte[]>> SerializeStorageAsync(ISerializationAdapter serializationAdapter, bool serializeOnlyChanged, bool clearChanged) // OBSOLETE
        {
            Dictionary<long, byte[]> serializeContainer = new Dictionary<long, byte[]>();
            if (serializeOnlyChanged)
            {
                foreach (var changedComponent in changedComponents.ToList())
                {
                    var componentResult = await componentsAsync.TryGetValueAsync(changedComponent.Key);
                    if (componentResult.Success)
                    {
                        var component = componentResult.Value;
                        if (Defines.LogECSEntitySerializationComponents)
                        {
                            NLogger.Log($"Will serialized component {component.GetType()} in {this.entity.AliasName}:{this.entity.instanceId}");
                        }
                        serializeContainer[component.GetId()] = serializationAdapter.SerializeECSComponent(component);
                    }
                }
            }
            else
            {
                var keys = await SerializationContainerAsync.GetKeysAsync();
                foreach (var key in keys)
                {
                    var result = await SerializationContainerAsync.TryGetValueAsync(key);
                    if (result.Success)
                    {
                        serializeContainer[key] = serializationAdapter.SerializeECSComponent(result.Value as ECSComponent);
                    }
                }
            }
            if (clearChanged)
                changedComponents.Clear();
            return serializeContainer;
        }

        public async Task DeserializeStorageAsync(ISerializationAdapter serializationAdapter, Dictionary<long, byte[]> serializedComponents)
        {
            foreach (var serComponent in serializedComponents)
            {
                await this.SerializationContainerAsync.SetValueAsync(serComponent.Key, (ECSComponent)serializationAdapter.DeserializeECSComponent(serComponent.Value, serComponent.Key));
            }
        }

        public async Task RestoreComponentsAfterSerializationAsync(ECSEntity entity)
        {
            this.entity = entity;
            if (await componentsAsync.GetCountAsync() == 0)
            {
                List<ECSComponent> afterDeser = new List<ECSComponent>();
                var keys = await SerializationContainerAsync.GetKeysAsync();
                foreach (var objPairKey in keys)
                {
                    var result = await SerializationContainerAsync.TryGetValueAsync(objPairKey);
                    if (result.Success)
                    {
                        Type componentType;
                        if (EntitySerializer.TypeStorage.TryGetValue(objPairKey, out componentType))
                        {
                            var typedComponent = (ECSComponent)Convert.ChangeType(result.Value, componentType);
                            await AddComponentImmediatelyAsync(componentType, typedComponent, true, true);
                            afterDeser.Add(typedComponent);
                        }
                    }
                }
                
                afterDeser.ForEach(typedComponent =>
                {
                    if (typedComponent is DBComponent)
                    {
                        if (this.entity.ECSWorldOwner.WorldType == ECSWorld.WorldTypeEnum.Server || this.entity.ECSWorldOwner.WorldType == ECSWorld.WorldTypeEnum.Offline)
                        {
                            (typedComponent as DBComponent).UnserializeDB();
                        }
                        else
                        {
                            (typedComponent as DBComponent).UnserializeDB(true);
                        }
                    }
                    typedComponent.AfterDeserialization();
                });
            }
        }

        #endregion
        
        public async Task DirectiveChangeAsync(Type typeComponent)
        {
            await componentsAsync.ExecuteReadLockedAsync(typeComponent, async (key, component) =>
            {
                changedComponents[typeComponent] = 1;
                await Task.CompletedTask;
            });
        }

        #region Base functions

        public async Task<bool> AddOrChangeComponentImmediatelyAsync(Type comType, ECSComponent component, bool restoringMode = false, bool silent = false)
        {
            bool added = false;
            bool changed = false;
            
            await componentsAsync.ExecuteOnAddOrChangeLockedAsync(comType, component, 
                async (key, newcomponent) => 
                {
                    await AddComponentProcessAsync(comType, newcomponent, restoringMode);
                    added = true;
                }, 
                async (key, newcomponent, oldcomponent) => 
                {
                    changed = ChangeComponentProcess(newcomponent, oldcomponent, silent, restoringMode ? this.entity : null);
                    if (restoringMode)
                    {
                        if (newcomponent is DBComponent dBComponent)
                        {
                            this.componentsAsync.UnsafeChange(key, oldcomponent);
                            (oldcomponent as DBComponent).serializedDB = dBComponent.serializedDB;
                        }
                    }
                    await Task.CompletedTask;
                });

            if (added)
            {
                if (!silent)
                {
                    await componentsAsync.ExecuteReadLockedAsync(comType, async (key, addedcomponent) =>
                    {
                        if (component.ECSWorldOwner != null)
                        {
                            addedcomponent.Unregistered = false;
                            component.AddedReaction(this.entity);
                        }
                        await Task.CompletedTask;
                    });
                }
            }
            
            if (!silent && changed)
            {
                component.ChangeReaction(this.entity);
            }
            return added;
        }


        public async Task<bool> AddComponentImmediatelyAsync(Type comType, ECSComponent component, bool restoringMode = false, bool silent = false)
        {
            bool added = false;
            if (!await this.componentsAsync.ContainsKeyAsync(comType))
            {
                await componentsAsync.ExecuteOnAddLockedAsync(comType, component, async (key, newcomponent) =>
                {
                    await AddComponentProcessAsync(comType, newcomponent, restoringMode);
                    added = true;
                });
            }
            if (added)
            {
                if (!silent)
                {
                    await componentsAsync.ExecuteReadLockedAsync(comType, async (key, addedcomponent) =>
                    {
                        if (component.ECSWorldOwner != null)
                        {
                            addedcomponent.Unregistered = false;
                            component.AddedReaction(this.entity);
                        }
                        await Task.CompletedTask;
                    });
                }
            }
            else
                NLogger.Error($"try add presented component {comType.Name} into {this.entity.AliasName}:{this.entity.instanceId}");
            
            return added;
        }

        private async Task AddComponentProcessAsync(Type comType, ECSComponent component, bool restoringMode = false)
        {
            component.ownerEntity = this.entity;
            component.ECSWorldOwner = this.entity?.ECSWorldOwner;
            if (this.entity != null)
                this.entity.fastEntityComponentsId.AddI(component.instanceId, 0, this.entity.SerialLocker);
            else
                NLogger.LogError("null owner entity");
                
            if (restoringMode)
                await this.SerializationContainerAsync.AddAsync(component.GetId(), component);
            else
                await this.SerializationContainerAsync.SetValueAsync(component.GetId(), component);
                
            this.IdToTypeComponent.TryAdd(component.GetId(), component.GetTypeFast());
            component.ECSWorldOwner?.entityManager.OnAddComponent(this.entity, component);
        }

        public async Task<bool> ChangeComponentAsync(ECSComponent component, bool silent = false, ECSEntity restoringOwner = null)
        {
            bool changed = false;
            await componentsAsync.ExecuteOnChangeLockedAsync(component.GetTypeFast(), component, async (key, chcomponent, oldcomponent) =>
            {
                changed = ChangeComponentProcess(chcomponent, oldcomponent, silent, restoringOwner);
                await Task.CompletedTask;
            });
            
            if (!silent && changed)
            {
                component.ChangeReaction(this.entity);
            }
            return changed;
        }

        public async Task<ECSComponent> GetComponentAsync(Type componentClass)
        {
            ECSComponent component = null;
            try
            {
                var result = await this.componentsAsync.TryGetValueAsync(componentClass);
                if (result.Success) component = result.Value;
            }
            catch (Exception ex)
            {
                if (ex is KeyNotFoundException && Defines.HiddenKeyNotFoundLog)
                    NLogger.Error(ex.Message + "  \n" + ex.StackTrace);
            }
            return component;
        }

        public async Task<ECSComponent> GetComponentAsync(long componentTypeId)
        {
            ECSComponent component = null;
            try
            {
                var result = await this.componentsAsync.TryGetValueAsync(this.IdToTypeComponent[componentTypeId]);
                if (result.Success) component = result.Value;
            }
            catch (Exception ex)
            {
                if (ex is KeyNotFoundException && Defines.HiddenKeyNotFoundLog)
                    NLogger.Error(ex.Message + "  \n" + ex.StackTrace);
            }
            return component;
        }

        public async Task<bool> MarkComponentChangedAsync(ECSComponent component, bool serializationSilent = false, bool eventSilent = false)
        {
            bool changed = false;
            await componentsAsync.ExecuteOnChangeLockedAsync(component.GetType(), component, async (key, chcomponent, oldcomponent) =>
            {
                Type componentClass = chcomponent.GetTypeFast();
                if (!serializationSilent)
                {
                    changedComponents[componentClass] = 1;
                    changed = true;
                }
                await Task.CompletedTask;
            });
            
            if (!eventSilent && changed)
            {
                component.ChangeReaction(this.entity);
            }
            return changed;
        }

        public async Task<ECSComponent> RemoveComponentImmediatelyAsync(Type componentClass)
        {
            var result = await componentsAsync.ExecuteOnRemoveLockedAsync(componentClass, async (key, component) =>
            {
                await RemoveComponentProcessAsync(componentClass, component);
            });
            
            if (result.Success)
            {
                result.Value.RemovingReaction(this.entity);
            }
            else
            {
                NLogger.LogError("try to remove non present component");
            }
            return result.Value;
        }

        private async Task RemoveComponentProcessAsync(Type componentClass, ECSComponent component)
        {
            this.changedComponents.Remove(componentClass, out _);
            await this.SerializationContainerAsync.RemoveAsync(component.GetId());
            this.IdToTypeComponent.Remove(component.GetId(), out _);
            this.entity.fastEntityComponentsId.RemoveI(component.instanceId, this.entity.SerialLocker);
            this.RemovedComponents.Add(component.GetId());
            component.ECSWorldOwner?.entityManager.OnRemoveComponent(this.entity, component);
        }

        public async Task RemoveComponentsWithGroupAsync(long componentGroup)
        {
            List<ECSComponent> toRemoveComponent = new List<ECSComponent>();
            List<ECSComponent> notRemovedComponent = new List<ECSComponent>();
            bool exception = false;
            
            var componentsList = await componentsAsync.GetValuesAsync();
            foreach (var component in componentsList)
            {
                if (component.ComponentGroups.TryGetValueI(componentGroup, out _, component.SerialLocker))
                {
                    toRemoveComponent.Add(component);
                }
            }

            foreach (var removedComponent in toRemoveComponent)
            {
                await this.ExecuteWriteLockedComponentAsync(removedComponent.GetTypeFast(), async (key, component) =>
                {
                    if (!await this.componentsAsync.ContainsKeyAsync(removedComponent.GetTypeFast()))
                    {
                        exception = true;
                        notRemovedComponent.Add(removedComponent);
                    }
                    else
                    {
                        this.changedComponents.Remove(removedComponent.GetTypeFast(), out _);
                        this.entity.fastEntityComponentsId.RemoveI(removedComponent.instanceId, this.entity.SerialLocker);
                        await this.componentsAsync.RemoveAsync(removedComponent.GetTypeFast());
                        await this.SerializationContainerAsync.RemoveAsync(removedComponent.GetId());
                        this.IdToTypeComponent.Remove(removedComponent.GetId(), out _);
                        this.RemovedComponents.Add(removedComponent.GetId());
                        removedComponent.ECSWorldOwner?.entityManager.OnRemoveComponent(this.entity, removedComponent);
                        removedComponent.RemovingReaction(this.entity);
                    }
                });
            }
            
            if (exception)
            {
                NLogger.Error("try to remove non present component in group removing");
            }
        }

        public async Task FilterRemovedComponentsAsync(List<long> filterList, List<long> filteringOnlyGroups)
        {
            var bufFilterList = new List<long>(filterList);
            var componentValues = await this.componentsAsync.GetValuesAsync();
            
            foreach (var component in componentValues)
            {
                if (filteringOnlyGroups.Count == 0)
                {
                    var id = component.instanceId;
                    bool finded = false;
                    for (int i = 0; i < bufFilterList.Count; i++)
                    {
                        if (id == bufFilterList[i])
                        {
                            finded = true;
                        }
                    }
                    if (!finded)
                    {
                        await this.RemoveComponentImmediatelyAsync(component.GetTypeFast());
                    }
                }
                else
                {
                    foreach (var group in filteringOnlyGroups)
                    {
                        foreach (var componentGroup in component.ComponentGroups.SnapshotI(component.SerialLocker))
                        {
                            if (componentGroup.Key == group)
                            {
                                var id = component.instanceId;
                                bool finded = false;
                                for (int i = 0; i < bufFilterList.Count; i++)
                                {
                                    if (id == bufFilterList[i])
                                    {
                                        finded = true;
                                    }
                                }
                                if (!finded)
                                {
                                    await this.RemoveComponentImmediatelyAsync(component.GetTypeFast());
                                }
                            }
                        }
                    }
                }
            }
        }

        #endregion

        #region Unsafe component functions

        public async Task<bool> AddComponentUnsafeAsync(Type componentType, ECSComponent component, bool restoringMode = false, bool silent = false)
        {
            if (this.componentsAsync.UnsafeAdd(componentType, component))
            {
                await AddComponentProcessAsync(componentType, component, restoringMode);
                if (!silent)
                {
                    component.AddedReaction(this.entity);
                }
                return true;
            }
            return false;
        }

        public async Task<bool> ChangeComponentUnsafeAsync(ECSComponent component, bool silent = false, ECSEntity restoringOwner = null)
        {
            var oldcomponent = await GetComponentUnsafeAsync(component.GetTypeFast());
            if (this.componentsAsync.UnsafeChange(component.GetTypeFast(), component))
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

        public async Task<bool> RemoveComponentUnsafeSilentAsync(Type componentType)
        {
            if (this.componentsAsync.UnsafeRemove(componentType, out var component))
            {
                await RemoveComponentProcessAsync(componentType, component);
                return true;
            }
            return false;
        }

        public async Task<ECSComponent> GetComponentUnsafeAsync(Type componentType)
        {
            var result = await this.componentsAsync.TryGetValueAsync(componentType);
            return result.Success ? result.Value : null;
        }

        public async Task<ECSComponent> GetComponentUnsafeAsync(long componentTypeId)
        {
            var result = await this.componentsAsync.TryGetValueAsync(this.IdToTypeComponent[componentTypeId]);
            return result.Success ? result.Value : null;
        }
        #endregion

        #region Async component functions (Wrappers for TaskEx.RunAsync compatibility)
        public void AddComponentFireAndForget(Type componentType, ECSComponent component)
        {
            TaskEx.RunAsync(async () => await AddComponentImmediatelyAsync(componentType, component), false, true);
        }

        public void AddComponentsFireAndForget(IList<ECSComponent> addedComponents)
        {
            TaskEx.RunAsync(async () => await AddComponentsImmediatelyAsync(addedComponents), false, true);
        }

        public void ChangeComponentFireAndForget(ECSComponent component)
        {
            TaskEx.RunAsync(async () => await ChangeComponentAsync(component), false, true);
        }

        public void RemoveComponentFireAndForget(Type componentType)
        {
            TaskEx.RunAsync(async () => await RemoveComponentImmediatelyAsync(componentType), false, true);
        }
        #endregion

        #region Additional Base functions

        public Task<ECSComponent> RemoveComponentImmediatelyAsync(long componentTypeId)
        {
            return RemoveComponentImmediatelyAsync(componentTypeId.IdToECSType());
        }

        public async Task AddComponentsImmediatelyAsync(IList<ECSComponent> addedComponents)
        {
            foreach (var component in addedComponents)
            {
                await this.AddComponentImmediatelyAsync(component.GetTypeFast(), component);
            }
        }

        public async Task RemoveComponentsImmediatelyAsync(IList<ECSComponent> removedComponents)
        {
            foreach(var component in removedComponents)
            {
                await this.RemoveComponentImmediatelyAsync(component.GetTypeFast());
            }
        }

        public async Task RegisterAllComponentsAsync(bool previous_changed = false)
        {
            if (previous_changed)
            {
                List<ECSComponent> changed_components = new List<ECSComponent>();
                var componentsList = await componentsAsync.GetValuesAsync();
                foreach (var component in componentsList)
                {
                    if (component.Unregistered)
                    {
                        if (await MarkComponentChangedAsync(component, false, true))
                        {
                            changed_components.Add(component);
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
                var keys = await componentsAsync.GetKeysAsync();
                foreach (var componentKey in keys)
                {
                    await componentsAsync.ExecuteReadLockedAsync(componentKey, async (key, component) =>
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
                        await Task.CompletedTask;
                    });
                }
            }
            this.entity.Alive = true;
        }

        public Task<bool> HasComponentAsync(Type componentClass) =>
            this.componentsAsync.ContainsKeyAsync(componentClass);
        public Task<bool> HasComponentAsync(long componentClassId)
        {
            return Task.Run(() => this.IdToTypeComponent.ContainsKey(componentClassId));
        }

        public async Task OnEntityDeleteAsync()
        {
            var removedComponents = await this.componentsAsync.ClearSnapshotAsync();
            await this.SerializationContainerAsync.ClearAsync();
            this.IdToTypeComponent.Clear();
            this.changedComponents.Clear();
            this.RemovedComponents.Clear();
            
            foreach (var component in removedComponents)
            {
                component.Value.OnRemove();
            }
        }

        public async Task<bool> ExecuteOnNotHasComponentAsync(Type componentType, Func<Task> asyncAction)
        {
            return await this.componentsAsync.ExecuteOnKeyHoldedAsync(componentType, asyncAction);
        }

        public Task<(bool Success, IDisposable LockToken)> HoldComponentAdditionAsync(Type componentType, bool holdMode = true)
        {
            return this.componentsAsync.HoldKeyAsync(componentType, holdMode);
        }

        public Task ExecuteReadLockedComponentAsync(Type componentType, Func<Type, ECSComponent, Task> asyncAction)
        {
            return componentsAsync.ExecuteReadLockedAsync(componentType, asyncAction);
        }

        public Task ExecuteWriteLockedComponentAsync(Type componentType, Func<Type, ECSComponent, Task> asyncAction)
        {
            return componentsAsync.ExecuteWriteLockedAsync(componentType, asyncAction);
        }

        public async Task<(bool Success, T Component, IDisposable Token)> GetReadLockedComponentAsync<T>() where T : ECSComponent
        {
            var result = await this.GetReadLockedComponentAsync(typeof(T));
            var castedComponent = result.Component as T;
            return (result.Success && castedComponent != null, castedComponent, result.Token);
        }

        public async Task<(bool Success, ECSComponent Component, IDisposable Token)> GetReadLockedComponentAsync(Type componentType)
        {
            var result = await componentsAsync.TryGetLockedElementAsync(componentType, false);
            return (result.Success, result.Value, result.LockToken);
        }

        public async Task<(bool Success, ECSComponent Component, IDisposable Token)> GetWriteLockedComponentAsync(Type componentType)
        {
            var result = await componentsAsync.TryGetLockedElementAsync(componentType, true);
            return (result.Success, result.Value, result.LockToken);
        }

        public ValueTask<IDisposable> GetWriteLockedComponentStorageAsync()
        {
            return componentsAsync.LockStorageAsync();
        }

        #endregion

        public Task<ICollection<Type>> GetComponentClassesAsync() =>
            this.componentsAsync.GetKeysAsync();

        public Task<ICollection<ECSComponent>> GetComponentsAsync() =>
            this.componentsAsync.GetValuesAsync();
    }
}