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

        // PHASE 3c: lazy per-component collections. These were eagerly allocated for EVERY
        // component (~12M Dictionaries + ~12M Lists at 1M x 12). The fields stay fields (the
        // serialization member set is UNCHANGED — a null field round-trips as null), they just
        // start null and are allocated only on first real use. All access sites are null-guarded.
        public Dictionary<long, ECSComponentGroup> ComponentGroups = null;//todo: concurrent replace to normal
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
        private ComponentLifecycleDispatcher _lifecycle = null;

        // Фаза 3, шаги 1+8 (ТЗ 4.5.2, 4.5.8; чинит дефект 6.2): резиденция состояния — по
        // профилю мира. Сервер (инстансы стабильны) — поле инстанса; клиент — источник истины
        // identity-keyed (идея 1.11: переживание подмены инстанса при UpdateDeserialize), но
        // с ОДНОКРАТНЫМ резолвом: новый инстанс резолвит разделяемый диспетчер из
        // identity-таблицы один раз и держит ссылку в поле (прежний геттер ходил в
        // GetOrAdd(instanceId, "LifecycleState") на КАЖДОЕ обращение — двойной словарный
        // лукап со string-хешем на каждом MarkAs*/шаге обработчика). Системное поле —
        // числовой слот (4.5.8г), скоуп — мир-владелец (4.5.8б).
        [IgnoreDataMember]
        private ComponentLifecycleDispatcher Lifecycle
        {
            get
            {
                var d = _lifecycle;
                if (d != null) return d;

                var world = this.ECSWorldOwner;
                ComponentLifecycleDispatcher created;
                if (world != null && world.Profile.IdentityKeyedLifecycleState) // профиль (ТЗ 4.5.2/4.5.6)
                {
                    created = (ComponentLifecycleDispatcher)SharedFieldTable
                        .GetRow(world.instanceId, this.instanceId)
                        .GetOrAddSystem(SystemFieldId.LifecycleState, () => new ComponentLifecycleDispatcher());
                }
                else
                {
                    created = new ComponentLifecycleDispatcher();
                }
                // ФИКС ДЕФЕКТА №17 (флейк сетки «MaxActive == 2»): ленивая инициализация была
                // неатомарной — два потока видели null и получали КАЖДЫЙ СВОЙ диспетчер
                // (две очереди, два дрейнера). Гонка унаследована: оригинальный геттер серверной
                // ветки делал то же `if (_x == null) _x = new ...`. CAS гарантирует единственный.
                d = System.Threading.Interlocked.CompareExchange(ref _lifecycle, created, null) ?? created;
                return d;
            }
        }

        /// <summary>Планировщик дрейна: мира-владельца, если он есть; иначе процессный дефолт
        /// (DefaultScheduler == семантика прежнего прямого TaskEx.RunAsync).</summary>
        private AECC.Abstractions.IScheduler LifecycleScheduler
        {
            get
            {
                var world = this.ECSWorldOwner;
                return world != null ? world.Scheduler : DefaultScheduler.Instance;
            }
        }

        private void OnLifecycleError(Exception ex)
        {
            NLogger.Log($"Error in component lifecycle execution: {ex.Message}\nType: {this.GetTypeFast()}");
            NLogger.LogError(ex);
        }


        public ECSComponent()
        {
            //componentManagers.ownerComponent = this;
            //StateReactionQueue = new PriorityEventQueue<StateReactionType, Action>(new List<StateReactionType>() { StateReactionType.Added, StateReactionType.Changed, StateReactionType.Removed }, 1, x => x + 2);
        }

        public void DirectiveSetChanged()
        {
            if (ownerEntity != null && ownerDB == null)
            {
                ownerEntity.entityComponents.DirectiveChange(this.GetType());
            }
            if(ownerDB != null && this.ECSWorldOwner.Profile.DbAuthoritativeChangeMarking) // профиль (идея 1.12/1.15)
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
            if(ownerDB != null && this.ECSWorldOwner.Profile.DbAuthoritativeChangeMarking) // профиль (идея 1.12/1.15)
            {
                ownerDB.ChangeComponent(this);
                ownerDB.DirectiveSetChanged();
            }
        }

        public ECSComponent SetGlobalComponentGroup()
        {
            if (this.ComponentGroups == null) this.ComponentGroups = new Dictionary<long, ECSComponentGroup>();
            this.ComponentGroups.SetI(ECSComponentManager.GlobalProgramComponentGroup.GetId(), ECSComponentManager.GlobalProgramComponentGroup, this.SerialLocker);

            return this; 
        }

        public ECSComponent AddComponentGroup(ECSComponentGroup componentGroup)
        {
            if (this.ComponentGroups == null) this.ComponentGroups = new Dictionary<long, ECSComponentGroup>();
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
            var d = Lifecycle;
            lock (d.SyncRoot)
            {
                if (AlreadyRemovedReaction) return;

                d.SetPendingAdd(() =>
                {
                    lock (d.SyncRoot)
                    {
                        this.OnAdded(entity);
                    }
                });
            }
            d.Drain(LifecycleScheduler, OnLifecycleError);
        }

        protected virtual void OnAdded(ECSEntity entity)
        {
            if (this.ECSWorldOwner != null && this.ECSWorldOwner.Profile.ServerMarksChangedOnAdd) // профиль (идея 1.15)
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
            var d = Lifecycle;
            lock (d.SyncRoot)
            {
                if (AlreadyRemovedReaction) return;

                d.EnqueueChange(() =>
                {
                    lock (d.SyncRoot)
                    {
                        this.OnChanged(entity);
                    }
                });
            }
            d.Drain(LifecycleScheduler, OnLifecycleError);
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

            var d = Lifecycle;
            lock (d.SyncRoot)
            {
                d.SetPendingRemove(() =>
                {
                    lock (d.SyncRoot)
                    {
                        this.OnRemoved(entity);
                        ECSSharedField<object>.RemoveAllCachedValuesForId(this.instanceId);
                        this.IECSDispose();
                    }
                });
            }
            d.Drain(LifecycleScheduler, OnLifecycleError);
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
            if (ComponentGroups != null) ComponentGroups.ClearI(this.SerialLocker);
            ECSSharedField<object>.RemoveAllCachedValuesForId(this.instanceId);

            // Сброс состояния для переиспользования компонента
            Lifecycle.Reset();
            AlreadyRemovedReaction = false;
        }
        public object Clone() => MemberwiseClone();

    }
}
