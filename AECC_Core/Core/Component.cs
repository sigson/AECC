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
        public ReaderWriterLockSlim locker = new ReaderWriterLockSlim();
        [System.NonSerialized]
        [IgnoreDataMember]
        public SharedLock monoLocker = new SharedLock();

        [System.NonSerialized]
        [IgnoreDataMember]
        public List<string> ConfigPath = new List<string>();

        public Dictionary<long, ECSComponentGroup> ComponentGroups = new Dictionary<long, ECSComponentGroup>();//todo: concurrent replace to normal
        [System.NonSerialized]
        [IgnoreDataMember]
        static public List<Action> StaticOnChangeHandlers = new List<Action>();
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
        private PriorityEventQueue<StateReactionType, Action> _stateReactionQueue = null;
        [IgnoreDataMember]
        public PriorityEventQueue<StateReactionType, Action> StateReactionQueue
        {
            get
            {
                if (this.ECSWorldOwner.WorldType == ECSWorld.WorldTypeEnum.Client)
                    return ECSSharedField<PriorityEventQueue<StateReactionType, Action>>.GetOrAdd(instanceId, "StateReactionQueue", new PriorityEventQueue<StateReactionType, Action>(new List<StateReactionType>() { StateReactionType.Added, StateReactionType.Changed, StateReactionType.Removed }, 1, x => x + 2, this.GetTypeFast()));
                else
                {
                    if (_stateReactionQueue == null)
                    {
                        _stateReactionQueue = new PriorityEventQueue<StateReactionType, Action>(new List<StateReactionType>() { StateReactionType.Added, StateReactionType.Changed, StateReactionType.Removed }, 1, x => x + 2, this.GetTypeFast());
                    }
                    return _stateReactionQueue;
                }

            }
            set
            {
                if (this.ECSWorldOwner.WorldType == ECSWorld.WorldTypeEnum.Client)
                    ECSSharedField<PriorityEventQueue<StateReactionType, Action>>.SetCachedValue(instanceId, "StateReactionQueue", value);
                else
                    _stateReactionQueue = value;
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

        // overridable functional for damage transformer, after adding component of damage effect - in this method we send transformer action to damage transformers agregator
        /// <summary>
        /// ATTENTION! Use lock(this.SerialLocker) if you want to edit fields value for prevent serialization error!
        /// </summary>
        /// <param name="entity"></param>
        public void AddedReaction(ECSEntity entity)
        {
            StateReactionQueue.AddEvent(StateReactionType.Added, () =>
            {
                lock (this.StateReactionQueue)
                {
                    this.OnAdded(entity);
                }
            });
        }

        protected virtual void OnAdded(ECSEntity entity)
        {
            this.MarkAsChanged();
        }

        /// <summary>
        /// ATTENTION! Use lock(this.SerialLocker) if you want to edit fields value for prevent serialization error!
        /// </summary>
        /// <param name="entity"></param>
        public void ChangeReaction(ECSEntity entity)
        {
            StateReactionQueue.AddEvent(StateReactionType.Changed, () =>
            {
                lock (this.StateReactionQueue)
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

        protected virtual void OnChanged(ECSEntity entity)
        {
            
        }
        /// <summary>
        /// ATTENTION! Use lock(this.SerialLocker) if you want to edit fields value for prevent serialization error!
        /// </summary>
        /// <param name="entity"></param>
        public void RemovingReaction(ECSEntity entity)
        {
            if (AlreadyRemovedReaction)//unsafe, but i don't care
            {
                return;
            }
            AlreadyRemovedReaction = true;
            StateReactionQueue.AddEvent(StateReactionType.Removed, () =>
            {
                lock (this.StateReactionQueue)
                {
                    if (this.ECSWorldOwner.WorldType == ECSWorld.WorldTypeEnum.Client || this.ECSWorldOwner.WorldType == ECSWorld.WorldTypeEnum.Offline)
                    {
                        
                    }
                    
                    this.OnRemoved(entity);
                    ECSSharedField<object>.RemoveAllCachedValuesForId(this.instanceId);
                    this.IECSDispose();
                }
            });
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
            ConfigPath.Clear();
            ComponentGroups.ClearI(this.SerialLocker);
            OnChangeHandlers.Clear();
            ECSSharedField<object>.RemoveAllCachedValuesForId(this.instanceId);
        }
        public void RunOnChangeCallbacks(ECSEntity parentEntity)
        {
            
        }

        public object Clone() => MemberwiseClone();
    }
}
