using AECC.Core.Logging;
using AECC.Extensions;
using System.Collections.Concurrent;
using AECC.Extensions;
using AECC.Core.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using AECC.Extensions.ThreadingSync;
using System.Runtime.Serialization;
using AECC.Collections;
using AECC.Core.BuiltInTypes.Components;

namespace AECC.Core
{
    [System.Serializable]
    [TypeUid(3)]
    /// <summary>
    /// ATTENTION! Use lock(this.SerialLocker) when you edit component fields if you want to edit fields value for prevent serialization error!
    /// </summary>
    /// <param name="entity"></param>
    public class ECSComponent : IECSObject, ICloneable
    {
        static new public long Id { get; set; } = 3;

        [System.NonSerialized]
        [IgnoreDataMember]
        public ECSEntity ownerEntity;
        [System.NonSerialized]
        [IgnoreDataMember]
        public ComponentsDBComponent ownerDB;
        [System.NonSerialized]
        [IgnoreDataMember]
        private ReaderWriterLockSlim lockerValue = null;
        [IgnoreDataMember]
        public ReaderWriterLockSlim locker
        {
            get
            {
                if (lockerValue == null)
                    lockerValue = new ReaderWriterLockSlim();
                return lockerValue;
            }
            set
            {
                lockerValue = value;
            }
        }
        [System.NonSerialized]
        [IgnoreDataMember]
        private SharedLock monoLockerValue = null;
        [IgnoreDataMember]
        public SharedLock monoLocker
        {
            get
            {
                if (monoLockerValue == null)
                    monoLockerValue = new SharedLock();
                return monoLockerValue;
            }
            set
            {
                monoLockerValue = value;
            }
        }

        public Dictionary<long, ECSComponentGroup> ComponentGroups = new Dictionary<long, ECSComponentGroup>();//todo: concurrent replace to normal
        [System.NonSerialized]
        [IgnoreDataMember]
        public List<Action<ECSEntity, ECSComponent>> OnChangeHandlers = new List<Action<ECSEntity, ECSComponent>>();
        [System.NonSerialized]
        [IgnoreDataMember]
        public bool Unregistered = true;
        [System.NonSerialized]
        [IgnoreDataMember]
        public bool AlreadyRemovedReaction = false;

        public enum StateReactionType
        {
            Added,
            Changed,
            Removed
        }

        [System.NonSerialized]
        private ComponentLifecycleState _lifecycleState = null;

        [IgnoreDataMember]
        private ComponentLifecycleState LifecycleState
        {
            get
            {
                if (this.ECSWorldOwner != null && this.ECSWorldOwner.WorldType == ECSWorld.WorldTypeEnum.Client)
                {
                    // Кешируем в ECSSharedField один легкий объект состояния
                    return ECSSharedField<ComponentLifecycleState>.GetOrAdd(
                        this.instanceId, 
                        "LifecycleState", 
                        () => new ComponentLifecycleState()
                    );
                }
                else
                {
                    if (_lifecycleState == null)
                    {
                        _lifecycleState = new ComponentLifecycleState();
                    }
                    return _lifecycleState;
                }
            }
        }


        public ECSComponent()
        {
            //componentManagers.ownerComponent = this;
            //StateReactionQueue = new PriorityEventQueue<StateReactionType, Action>(new List<StateReactionType>() { StateReactionType.Added, StateReactionType.Changed, StateReactionType.Removed }, 1, x => x + 2);
        }

        public List<Action<ECSEntity, ECSComponent>> GetOnChangeComponentCallback()
        {
            if (ObjectType == null)
            {
                ObjectType = GetType();
            }
            try
            {
                if(OnChangeHandlers == null)
                {
                    OnChangeHandlers = ECSComponentManager.OnChangeCallbacksDB[this.GetId()];
                }
                
                return OnChangeHandlers;
            }
            catch
            {
                NLogger.Log(ObjectType);
                NLogger.Log("Type not has callbacks");
                return null;
            }
            
        }

        public void DirectiveSetChanged()
        {
            if (ownerEntity != null && ownerDB == null)
            {
                ownerEntity.entityComponents.DirectiveChange(this.GetType());
            }
            if(ownerDB != null && (this.ECSWorldOwner.WorldType == ECSWorld.WorldTypeEnum.Server || this.ECSWorldOwner.WorldType == ECSWorld.WorldTypeEnum.Offline))
            {
                ownerDB.ChangeComponent(this);
                ownerDB.DirectiveSetChanged();
            }
        }

        public void MarkAsChanged(bool serializationSilent = false, bool eventSilent = false)
        {
            if (ownerEntity != null && ownerDB == null)
            {
                ownerEntity.entityComponents.MarkComponentChanged(this, serializationSilent, eventSilent);
            }
            if(ownerDB != null && (this.ECSWorldOwner.WorldType == ECSWorld.WorldTypeEnum.Server || this.ECSWorldOwner.WorldType == ECSWorld.WorldTypeEnum.Offline))
            {
                ownerDB.ChangeComponent(this);
                ownerDB.DirectiveSetChanged();
            }
        }

        public ECSComponent SetGlobalComponentGroup()
        {
            this.ComponentGroups.SetI(ECSComponentManager.GlobalProgramComponentGroup.GetId(), ECSComponentManager.GlobalProgramComponentGroup, this.SerialLocker);

            return this; 
        }

        public ECSComponent AddComponentGroup(ECSComponentGroup componentGroup)
        {
            this.ComponentGroups.SetI(componentGroup.GetId(), componentGroup, this.SerialLocker);
            return this;
        }

        public Type GetTypeFast()
        {
            if (ObjectType == null)
            {
                ObjectType = GetType();
            }
            return ObjectType;
        }

        private void ProcessLifecycleQueue()
        {
            var state = LifecycleState;

            // Защита от параллельного запуска нескольких обработчиков одной и той же очереди
            if (Interlocked.CompareExchange(ref state.Processing, 1, 0) != 0)
            {
                return;
            }

            TaskEx.RunAsync(() =>
            {
                while (true)
                {
                    Action actionToRun = null;

                    lock (state.Lock)
                    {
                        // СТРОГИЙ ПРИОРИТЕТ ВЫПОЛНЕНИЯ: Add -> Change -> Remove
                        if (state.PendingAdd != null)
                        {
                            actionToRun = state.PendingAdd;
                            state.PendingAdd = null;
                        }
                        else if (state.PendingChanges != null && state.PendingChanges.Count > 0)
                        {
                            actionToRun = state.PendingChanges.Dequeue();
                        }
                        else if (state.PendingRemove != null)
                        {
                            actionToRun = state.PendingRemove;
                            state.PendingRemove = null;
                        }
                        else
                        {
                            // Очередь пуста, снимаем флаг и выходим
                            state.Processing = 0;
                            return; 
                        }
                    }

                    // Выполняем задачу. (Блокировки внутри делегатов сохранят вашу логику из оригинального кода)
                    try
                    {
                        actionToRun?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        NLogger.Log($"Error in component lifecycle execution: {ex.Message}\nType: {this.GetTypeFast()}");
                    }
                }
            });
        }

        // overridable functional for damage transformer, after adding component of damage effect - in this method we send transformer action to damage transformers agregator
        /// <summary>
        /// ATTENTION! Use lock(this.SerialLocker) if you want to edit fields value for prevent serialization error!
        /// </summary>
        /// <param name="entity"></param>
        public void AddedReaction(ECSEntity entity)
        {
            var state = LifecycleState;
            lock (state.Lock)
            {
                if (AlreadyRemovedReaction) return;
                
                state.PendingAdd = () =>
                {
                    lock (state.Lock)
                    {
                        this.OnAdded(entity);
                    }
                };
            }
            ProcessLifecycleQueue();
        }

        protected virtual void OnAdded(ECSEntity entity)
        {
            if (this.ECSWorldOwner != null && this.ECSWorldOwner.WorldType == ECSWorld.WorldTypeEnum.Server)
            {
                this.MarkAsChanged();
            }
        }

        /// <summary>
        /// ATTENTION! Use lock(this.SerialLocker) if you want to edit fields value for prevent serialization error!
        /// </summary>
        /// <param name="entity"></param>
        public void ChangeReaction(ECSEntity entity)
        {
            var state = LifecycleState;
            lock (state.Lock)
            {
                if (AlreadyRemovedReaction) return;

                if (state.PendingChanges == null)
                    state.PendingChanges = new Queue<Action>();

                state.PendingChanges.Enqueue(() =>
                {
                    lock (state.Lock)
                    {
                        List<Action<ECSEntity, ECSComponent>> callbackActions;
                        ECSComponentManager.OnChangeCallbacksDB.TryGetValue(this.GetId(), out callbackActions);
                        this.OnChanged(entity);
                        if (callbackActions != null)
                        {
                            foreach (var act in callbackActions)
                            {
                                act(entity, this);
                            }
                        }
                    }
                });
            }
            ProcessLifecycleQueue();
        }

        protected virtual void OnChanged(ECSEntity entity)
        {
            
        }
        /// <summary>
        /// ATTENTION! Use lock(this.SerialLocker) if you want to edit fields value for prevent serialization error!
        /// </summary>
        /// <param name="entity"></param>
        public void RemovingReaction(ECSEntity entity)
        {
            if (AlreadyRemovedReaction) return;
            AlreadyRemovedReaction = true;

            var state = LifecycleState;
            lock (state.Lock)
            {
                state.PendingRemove = () =>
                {
                    lock (state.Lock)
                    {
                        this.OnRemoved(entity);
                        ECSSharedField<object>.RemoveAllCachedValuesForId(this.instanceId);
                        this.IECSDispose();
                    }
                };
            }
            ProcessLifecycleQueue();
        }

        protected virtual void OnRemoved(ECSEntity entity)
        {
            
        }

        public override void ChainedIECSDispose()
        {
            base.ChainedIECSDispose();
            if(this.ownerEntity != null)
            {
                if(this.ownerDB != null)
                {
                    this.ownerDB.RemoveComponent(this.instanceId);
                }
                else
                {
                    this.ownerEntity.RemoveComponent(this.GetTypeFast());
                }
            }
        }

        public void OnRemove()
        {
            ComponentGroups.ClearI(this.SerialLocker);
            OnChangeHandlers.Clear();
            ECSSharedField<object>.RemoveAllCachedValuesForId(this.instanceId);

            // Сброс состояния для переиспользования компонента
            lock (LifecycleState.Lock)
            {
                LifecycleState.PendingAdd = null;
                LifecycleState.PendingChanges?.Clear();
                LifecycleState.PendingRemove = null;
                LifecycleState.Processing = 0;
                AlreadyRemovedReaction = false;
            }
        }
        public void RunOnChangeCallbacks(ECSEntity parentEntity)
        {
            
        }

        public object Clone() => MemberwiseClone();

        public class ComponentLifecycleState
        {
            public readonly object Lock = new object();
            public int Processing = 0;
            public Action PendingAdd = null;
            public Queue<Action> PendingChanges = null;
            public Action PendingRemove = null;
    }
    }
}
