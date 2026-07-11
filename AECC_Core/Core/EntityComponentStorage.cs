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
    public partial class EntityComponentStorage : IComponentStoreListener
    {
        // internal: extension-пайплайн сериализации (IVT в AECC.Serialization).
        internal ECSEntity entity;
        public int ChangedComponent => changedComponents.Count;
        public bool isAsync => false;

        // Словарь компонентов инкапсулирован в ComponentStore — только хранение +
        // транзакционная матрица + absence-holds; ключ — стабильный type-uid (long,
        // == component.GetId() == type.TypeId()). Side-effects (зеркала сериализации,
        // fastEntityComponentsId, manager-нотификации) исполняет слушатель
        // IComponentStoreListener — этот же класс. Public API принимает Type;
        // Kid() маппит Type->id на границе, обратных int->Type лукапов нет.
        // ConcurrencyMode фиксируется лениво, на момент первого обращения к Store.
        private ComponentStore storeValue;
        internal ComponentStore Store
        {
            get
            {
                if (storeValue == null)
                {
                    storeValue = new ComponentStore(AECC.Locking.KernelRuntime.DefaultMode, this);
                }
                return storeValue;
            }
        }

        // Type -> stable type UID. Reads [TypeUid] via the same mechanism as ECSComponent.GetId()
        // (cached reflection), so it does NOT depend on the serializer's TypeStorage being populated.
        internal static long Kid(Type t) { return t.TypeId(); }

        // dirty-set / зеркало / removed / bin / empty принадлежат Serialization
        // (EntitySerializationState в opaque-слоте сущности). Горячий путь (dirty-запись
        // на каждый change) идёт через эту кэш-ссылку. Пересадка на новую сущность — в
        // RestoreComponentsAfterSerialization.
        [System.NonSerialized]
        private AECC.Core.Serialization.EntitySerializationState _serState;
        private AECC.Core.Serialization.EntitySerializationState SerState
        {
            get
            {
                if (_serState == null)
                    _serState = AECC.Core.Serialization.EntitySerializationState.Of(this.entity);
                return _serState;
            }
        }

        internal IDictionary<Type, int> changedComponents { get { return SerState.ChangedComponents; } }

        // Полносрезная сериализация читает Store напрямую (StorageSerializationPipeline);
        // отдельного живого зеркала нет. Транзитный буфер десериализации — локальный
        // словарь, передаваемый параметром (см. DeserializeStorage /
        // RestoreComponentsAfterSerialization / UpdateDeserialize).

        [IgnoreDataMember]
        public List<long> RemovedComponents
        {
            get { return SerState.RemovedComponents; }
            set { SerState.RemovedComponents = value; }
        }
        [System.NonSerialized]
        private RWLock stabilizationGateValue;

        /// <summary>
        /// Entity-wide стабилизационный гейт: RW-барьер уровня сущности, под write-стороной
        /// которого выполняются мутации DB-агрегатора, а под read-стороной — сериализация
        /// среза сущности. Потребители — сериализация (EntityNetSerializer) и DBComponent
        /// (единый авторитет синхронизации DB). Это НЕ лок словаря компонентов — у слотов
        /// свои инлайновые ячейки.
        /// </summary>
        [IgnoreDataMember]
        public RWLock StabilizationGate
        {
            get
            {
                if (stabilizationGateValue == null)
                {
                    stabilizationGateValue = new RWLock();
                }
                return stabilizationGateValue;
            }
            set
            {
                stabilizationGateValue = value;
            }
        }

        [Obsolete("Переименовано в StabilizationGate — entity-wide стабилизационный гейт")]
        [IgnoreDataMember]
        public RWLock StabilizationLocker
        {
            get { return StabilizationGate; }
            set { StabilizationGate = value; }
        }

        public EntityComponentStorage(ECSEntity entity)
        {
            this.entity = entity;
        }

        #region serialization

        // SlicedSerializeStorage / SerializeStorage / DeserializeStorage живут в
        // extension-классе EntityComponentStorageSerialization (сборка AECC.Serialization;
        // вызовы сохраняют синтаксис через using). Здесь — адаптер-независимые части
        // пайплайна: RestoreComponentsAfterSerialization и FilterRemovedComponents.

        /// <summary>
        /// Перенос распакованных компонентов (результат DeserializeStorage) в живой Store
        /// пустой сущности + пересадка сериализационного состояния носителя. Буфер приходит
        /// параметром — отдельного пер-сущностного контейнера для него нет.
        /// </summary>
        public void RestoreComponentsAfterSerialization(ECSEntity entity, Dictionary<long, ECSComponent> deserializedComponents)
        {
            // Пересадка владельца: state НОСИТЕЛЯ кладётся в слот НОВОЙ сущности
            // (данные принадлежат сущности; десериализованный носитель отдаёт их ей).
            // ВАЖЕН ПОРЯДОК: state резолвится ДО переключения this.entity — иначе ленивый
            // SerState-геттер (при холодном кэше) создал бы СВЕЖИЙ state уже на целевой
            // сущности, молча теряя dirty/removed носителя.
            var st = SerState;   // state носителя (this.entity ещё старый)
            this.entity = entity;
            entity.serializationState = st;
            _serState = st;
            if (Store.Count == 0 && deserializedComponents != null)
            {
                List<ECSComponent> afterDeser = new List<ECSComponent>();
                foreach (var objPair in deserializedComponents)
                {
                    ECSComponent objComponent = (ECSComponent)objPair.Value;
                    Type component;
                    if (TypeRegistry.Global.TryGetType(objPair.Key, out component))
                    {
                        var typedComponent = (ECSComponent)Convert.ChangeType(objPair.Value, component);
                        
                        AddComponentImmediately(component, typedComponent, true, true);
                        afterDeser.Add(typedComponent);
                    }
                }
                afterDeser.ForEach(typedComponent =>
                {
                    // AfterRestore участника; clientRetry берётся из профиля мира
                    // (событийный ретрай на клиентской ветке).
                    var participant = typedComponent as AECC.Abstractions.ISerializationParticipant;
                    if (participant != null)
                    {
                        participant.AfterRestore(this.entity.ECSWorldOwner.Profile.ClientRetryOnMissingRefs);
                    }
                    typedComponent.AfterDeserialization();
                });
            }
        }


        #endregion

        public bool CheckChanged(Type typeComponent) => changedComponents.Keys.Contains(typeComponent);
        public void DirectiveChange(Type typeComponent)
        {
            Store.ExecuteReadLocked(Kid(typeComponent), (key, component) =>
            {
                changedComponents[typeComponent] = 1;
            });
        }

        #region Base functions

        public bool AddOrChangeComponentImmediately(Type comType, ECSComponent component, bool restoringMode = false, bool silent = false)
        {
            bool added;
            bool changedBranch;
            Store.AddOrChange(Kid(comType), component, restoringMode, silent, restoringMode ? this.entity : null, out added, out changedBranch);
            // change-ветка помечает dirty только при !silent (см. ComponentChanged)
            bool changed = changedBranch && !silent;
            if (added)
            {
                if (!silent)
                {
                    Store.ExecuteReadLocked(Kid(comType), (key, addedcomponent) =>
                    {
                        if (component.ECSWorldOwner != null)
                        {
                            addedcomponent.Unregistered = false;
                            component.AddedReaction(this.entity);
                        }
                    });
                }
            }

            if (!silent && changed)
            {
                component.ChangeReaction(this.entity);
            }
            return added;
        }


        public bool AddComponentImmediately(Type comType, ECSComponent component, bool restoringMode = false, bool silent = false)
        {
            bool added = Store.Add(Kid(comType), component, restoringMode);
            if (added)
            {
                if (!silent)
                {
                    Store.ExecuteReadLocked(Kid(comType), (key, addedcomponent) =>
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

        /// <summary>IComponentStoreListener: вызывается под write-локом ячейки, после мутации словаря.</summary>
        public void ComponentAdded(long typeUid, ECSComponent component, bool restoringMode)
        {
            component.ownerEntity = this.entity;
            component.ECSWorldOwner = this.entity?.ECSWorldOwner;
            if (this.entity != null)
            {
                if(WorldProfile.SerializationCollections(this.entity.ECSWorldOwner))
                {
                    this.entity.fastEntityComponentsId.AddI(component.instanceId, 0, this.entity.SerialLocker);
                }
            }
            else
            {
                NLogger.LogError("null owner entity");
            }

            // Компонент уже лежит в живом Store под тем же ключом typeUid; полносрезная
            // сериализация читает Store напрямую (только DeserializeStorage наполняет
            // отдельный посадочный буфер десериализации).
            component.ECSWorldOwner?.entityManager.OnAddComponent(this.entity, component);
        }

        public bool ChangeComponent(ECSComponent component, bool silent = false, ECSEntity restoringOwner = null)
        {
            bool changed = Store.Change(component.GetId(), component, silent, restoringOwner) && !silent;
            if (!silent && changed)
            {
                component.ChangeReaction(this.entity);
            }
            return changed;
        }

        /// <summary>IComponentStoreListener: вызывается под write-локом ячейки.</summary>
        public void ComponentChanged(long typeUid, ECSComponent component, ECSComponent oldcomponent, bool silent, ECSEntity restoringOwner, bool restoringMode)
        {
            if (restoringOwner != null)
                component.ownerEntity = restoringOwner;
            if(component.ECSWorldOwner != null)
                component.Unregistered = false;
            if (!silent)
            {
                Type componentClass = component.GetTypeFast();
                changedComponents[componentClass] = 1;
            }
            if (restoringMode)
            {
                // В restoring-режиме DB-агрегатор сохраняет старый инстанс, перенимая
                // только serializedDB.
                if (component is DBComponent dBComponent)
                {
                    Store.UnsafeChange(typeUid, oldcomponent); // под удержанным ячеечным локом
                    (oldcomponent as DBComponent).serializedDB = (component as DBComponent).serializedDB;
                }
            }
        }

        /// <summary>IComponentStoreListener: пометка изменённым без замены значения.</summary>
        public void ComponentMarkedChanged(long typeUid, ECSComponent component, bool serializationSilent)
        {
            Type componentClass = component.GetTypeFast();
            if (!serializationSilent)
            {
                changedComponents[componentClass] = 1;
            }
        }

        public ECSComponent GetComponent(Type componentClass)
        {
            ECSComponent component = null;
            try
            {
                component = this.Store.GetOrThrow(Kid(componentClass));
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
                component = this.Store.FirstOrDefault(x => componentClass.IsInstanceOfType(x.Value)).Value;
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
                component = this.Store.Where(x => componentClass.IsInstanceOfType(x.Value)).Select(x => x.Value);
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
                // typeId is the dictionary key (== component.GetId()); no registry reverse lookup needed.
                component = this.Store.GetOrThrow(componentTypeId);
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
            bool changed = Store.MarkChanged(component.GetId(), component, serializationSilent) && !serializationSilent;
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

            removed = Store.Remove(Kid(componentClass), out component2);
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

        // Id-based removal: typeId is the slot key; the Type is taken from the live
        // component instance (no registry reverse-lookup needed).
        private ECSComponent RemoveComponentByIdImmediately(long typeId)
        {
            ECSComponent component2 = null;
            bool removed = false;
            removed = Store.Remove(typeId, out component2);
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

        /// <summary>IComponentStoreListener: вызывается под write-локом ячейки, после изъятия из словаря.</summary>
        public void ComponentRemoved(long typeUid, ECSComponent component)
        {
            Type componentClass = component.GetTypeFast();
            this.changedComponents.Remove(componentClass, out _);
            if(WorldProfile.SerializationCollections(this.entity.ECSWorldOwner))
            {
                this.entity.fastEntityComponentsId.RemoveI(component.instanceId, this.entity.SerialLocker);
                if (Defines.TrackRemovedComponents)
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
            foreach (var component in Store)
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
            foreach (var component in Store)
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
                        if (!this.Store.ContainsKey(removedComponent.GetId()))
                        {
                            exception = true;
                            notRemovedComponent.Add(removedComponent);
                        }
                        else
                        {
                            this.changedComponents.Remove(removedComponent.GetTypeFast(), out _);
                            this.Store.RemoveRaw(removedComponent.GetId());
                            if(WorldProfile.SerializationCollections(this.entity.ECSWorldOwner))
                            {
                                this.entity.fastEntityComponentsId.RemoveI(removedComponent.instanceId, this.entity.SerialLocker);
                                if (Defines.TrackRemovedComponents)
                                    this.RemovedComponents.Add(removedComponent.GetId());
                            }
                            
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
            foreach (var component in this.Store)
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
            if (this.Store.UnsafeAdd(Kid(componentType), component))
            {
                ComponentAdded(Kid(componentType), component, restoringMode);
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
            if (this.Store.UnsafeChange(component.GetId(), component))
            {
                ComponentChanged(component.GetId(), component, oldcomponent, silent, restoringOwner, false);
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
            if (this.Store.UnsafeRemove(Kid(componentType), out var component))
            {
                ComponentRemoved(Kid(componentType), component);
                return true;
            }
            return false;
        }

        public ECSComponent GetComponentUnsafe(Type componentType)
        {
            ECSComponent component;
            return (!this.Store.TryGetValue(Kid(componentType), out component) ? null : component);
        }

        public ECSComponent GetComponentUnsafe(long componentTypeId)
        {
            ECSComponent component;
            // typeId is the slot key (== component.GetId()).
            return !this.Store.TryGetValue(componentTypeId, out component) ? null : component;
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
                foreach (var component in Store)
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
                foreach (var component1 in Store)
                {
                    Store.ExecuteReadLocked(component1.Key, (key, component) =>
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
            this.Store.ContainsKey(Kid(componentClass));

        public bool HasComponent(long componentClassId)
        {
            return this.Store.ContainsKey(componentClassId);
        }
            

        public void OnEntityDelete()
        {
            this.Store.EnterLockdown();
            List<long> snapshot;
            try
            {
                snapshot = this.Store.Keys.ToList();
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
            if (WorldProfile.SerializationCollections(this.entity.ECSWorldOwner))
            {
                this.RemovedComponents.Clear();
            }


            this.changedComponents.Clear();
        }

        public bool ExecuteOnNotHasComponent(Type componentType, Action action)
        {
            ECSComponent component;
            if (this.Store.ExecuteOnAbsent(Kid(componentType), action))
            {
                return true;
            }
            return false;
        }

        public bool HoldComponentAddition(Type componentType, out RWToken token, bool holdMode = true)
        {
            return this.Store.HoldAbsence(Kid(componentType), out token, holdMode);
        }

        public void ExecuteReadLockedComponent(Type componentType, Action<Type, ECSComponent> action)
        {
            // Public API keeps the Type-typed action; adapt to the long-keyed slot, supplying the
            // original Type from the closure so callers never see the internal id.
            Store.ExecuteReadLocked(Kid(componentType), (key, component) => action(componentType, component));
        }

        public void ExecuteWriteLockedComponent(Type componentType, Action<Type, ECSComponent> action)
        {
            Store.ExecuteWriteLocked(Kid(componentType), (key, component) => action(componentType, component));
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
            return Store.TryGetLockedElement(Kid(componentType), out component, out token, false);
        }

        public bool GetWriteLockedComponent(Type componentType, out ECSComponent component, out RWToken token)
        {
            return Store.TryGetLockedElement(Kid(componentType), out component, out token, true);
        }

        public RWToken GetWriteLockedComponentStorage()
        {
            return Store.LockStorage();
        }

        #endregion

        public ICollection<Type> ComponentClasses =>
            this.Store.Values.Select(c => c.GetTypeFast()).ToList();

        public ICollection<ECSComponent> Components =>
            this.Store.Values;
    }
}
