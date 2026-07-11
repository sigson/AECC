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

        // Lazily allocated: starts null and is created only on first real use. A null field
        // round-trips as null through serialization, so all access sites are null-guarded.
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

        // Residence of the lifecycle state depends on the world profile. On the server
        // (instances are stable) it lives in an instance field; on the client the source of
        // truth is identity-keyed (so it survives instance replacement on UpdateDeserialize),
        // but is resolved once: a new instance looks up the shared dispatcher from the
        // identity table a single time and caches the reference in a field, avoiding a
        // dictionary lookup on every access.
        [IgnoreDataMember]
        private ComponentLifecycleDispatcher Lifecycle
        {
            get
            {
                var d = _lifecycle;
                if (d != null) return d;

                var world = this.ECSWorldOwner;
                ComponentLifecycleDispatcher created;
                if (world != null && world.Profile.IdentityKeyedLifecycleState)
                {
                    created = (ComponentLifecycleDispatcher)SharedFieldTable
                        .GetRow(world.instanceId, this.instanceId)
                        .GetOrAddSystem(SystemFieldId.LifecycleState, () => new ComponentLifecycleDispatcher());
                }
                else
                {
                    created = new ComponentLifecycleDispatcher();
                }
                // CAS ensures that under concurrent first access only one dispatcher instance wins
                // and is retained, even if multiple threads race to create one.
                d = System.Threading.Interlocked.CompareExchange(ref _lifecycle, created, null) ?? created;
                return d;
            }
        }

        /// <summary>Планировщик дрейна: мира-владельца, если он есть; иначе процессный дефолт
        /// (DefaultScheduler).</summary>
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

        // Cached delegate: created once per instance and reused, instead of allocating a new
        // Action<Exception> for every lifecycle reaction.
        [System.NonSerialized]
        [IgnoreDataMember]
        private Action<Exception> _onLifecycleError;
        private Action<Exception> LifecycleErrorHandler
        {
            get { return _onLifecycleError ?? (_onLifecycleError = OnLifecycleError); }
        }

        // Компонент без переопределённых OnAdded/OnChanged/OnRemoved не порождает
        // пользовательских реакций. Для таких (подавляющее большинство data-компонентов)
        // диспетчер (ComponentLifecycleDispatcher), замыкания SetPendingAdd/EnqueueChange/
        // SetPendingRemove, Queue<Action> и планирование через RunAsync не нужны:
        //   • Added  — база OnAdded это no-op, если мир не помечает Changed при добавлении;
        //   • Changed— база OnChanged пустая;
        //   • Removed— пользовательской логики нет, штатная очистка исполняется ИНЛАЙН.
        // Условие едино для всех трёх реакций (сохраняет инвариант «Add→Change→Remove»:
        // если хоть один хук переопределён — все три идут асинхронным путём, чтобы
        // не смешивать инлайн и очередь и не нарушить порядок). Проверка переопределений —
        // рефлексия один раз на тип, результат кэшируется.
        private static readonly ConcurrentDictionary<Type, bool> _typeHasUserLifecycleHooks
            = new ConcurrentDictionary<Type, bool>();

        private static bool TypeHasUserLifecycleHooks(Type t)
        {
            return _typeHasUserLifecycleHooks.GetOrAdd(t, type =>
                IsLifecycleHookOverridden(type, "OnAdded")
                || IsLifecycleHookOverridden(type, "OnChanged")
                || IsLifecycleHookOverridden(type, "OnRemoved"));
        }

        private static bool IsLifecycleHookOverridden(Type type, string name)
        {
            var m = type.GetMethod(name,
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public,
                null, new[] { typeof(ECSEntity) }, null);
            return m != null && m.DeclaringType != typeof(ECSComponent);
        }

        /// <summary>true, если у компонента нет пользовательских lifecycle-хуков и текущий мир
        /// не требует серверной пометки Changed при добавлении (тогда база OnAdded — no-op).
        /// В этом случае реакции исполняются без диспетчера и без RunAsync.</summary>
        private bool CanRunLifecycleInline
        {
            get
            {
                if (TypeHasUserLifecycleHooks(this.GetTypeFast())) return false;
                var w = this.ECSWorldOwner;
                if (w != null && w.Profile.ServerMarksChangedOnAdd) return false; // база OnAdded не no-op
                return true;
            }
        }


        public ECSComponent()
        {
        }

        public void DirectiveSetChanged()
        {
            if (ownerEntity != null && ownerDB == null)
            {
                ownerEntity.entityComponents.DirectiveChange(this.GetType());
            }
            // P7: мир мог не доехать до вложенного компонента (восстановление из сети,
            // ручная сборка DB) — резолвим через владельцев и не падаем на null.
            // Резолв — строго под ownerDB != null: MarkAsChanged/DirectiveSetChanged —
            // горячий путь ВСЕХ компонентов, обычным (не-DB) мир здесь не нужен.
            if (ownerDB != null)
            {
                var dbWorld = ResolveDbMarkingWorld();
                if (dbWorld != null && dbWorld.Profile.DbAuthoritativeChangeMarking)
                {
                    ownerDB.ChangeComponent(this);
                    ownerDB.DirectiveSetChanged();
                }
            }
        }

        public void MarkAsChanged(bool serializationSilent = false, bool eventSilent = false)
        {
            if (ownerEntity != null && ownerDB == null)
            {
                ownerEntity.entityComponents.MarkComponentChanged(this, serializationSilent, eventSilent);
            }
            // P7: см. DirectiveSetChanged.
            if (ownerDB != null)
            {
                var dbWorld = ResolveDbMarkingWorld();
                if (dbWorld != null && dbWorld.Profile.DbAuthoritativeChangeMarking)
                {
                    ownerDB.ChangeComponent(this);
                    ownerDB.DirectiveSetChanged();
                }
            }
        }

        /// <summary>P7: мир для авторитарной DB-пометки вложенного компонента.
        /// ECSWorldOwnerId == 0 — легальное состояние (сборка DB до прикрепления к миру),
        /// поэтому геттер ECSWorldOwner на нулевом id не дёргаем: он репортит ошибку при
        /// включённой диагностике, а GetWorld на промахе способен создать fallback-мир.
        /// Мир, найденный через владельцев, записываем обратно — самолечение: дальше резолв
        /// идёт напрямую, и P7-проброс в DBComponent.AddComponent увидит у детей этого
        /// компонента уже ненулевой мир.</summary>
        private ECSWorld ResolveDbMarkingWorld()
        {
            var world = this.ECSWorldOwnerId != 0 ? this.ECSWorldOwner : null;
            if (world == null && ownerEntity != null && ownerEntity.ECSWorldOwnerId != 0)
                world = ownerEntity.ECSWorldOwner;
            if (world == null && ownerDB != null && ownerDB.ECSWorldOwnerId != 0)
                world = ownerDB.ECSWorldOwner;
            if (world != null && this.ECSWorldOwnerId == 0)
                this.ECSWorldOwner = world;
            return world;
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

        /// <summary>
        /// ATTENTION! Use lock(this.SerialLocker) if you want to edit fields value for prevent serialization error!
        /// </summary>
        /// <param name="entity"></param>
        public void AddedReaction(ECSEntity entity)
        {
            // Fast-path: листовой компонент, база OnAdded — no-op для этого мира.
            // Ни диспетчера, ни замыкания, ни RunAsync.
            if (CanRunLifecycleInline) return;

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
            d.Drain(LifecycleScheduler, LifecycleErrorHandler);
        }

        protected virtual void OnAdded(ECSEntity entity)
        {
            if (this.ECSWorldOwner != null && this.ECSWorldOwner.Profile.ServerMarksChangedOnAdd)
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
            // Fast-path: листовой компонент, база OnChanged пустая. Нечего исполнять.
            if (CanRunLifecycleInline) return;

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
            d.Drain(LifecycleScheduler, LifecycleErrorHandler);
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

            // Fast-path: листовой компонент. Пользовательской логики OnRemoved нет, но
            // штатная очистка (сброс shared-полей + IECSDispose) обязана выполниться —
            // делаем это ИНЛАЙН, без диспетчера/планировщика. Безопасно: RemovingReaction
            // вызывается уже ПОСЛЕ Store.Remove (лок ячейки хранилища не удерживается), а
            // IECSDispose у бездетного объекта не реентрит RemoveComponent (ChainedIECSDispose
            // вызывается только на детях, которых нет).
            if (CanRunLifecycleInline)
            {
                try
                {
                    this.OnRemoved(entity);
                    ECSSharedField<object>.RemoveAllCachedValuesForId(this.instanceId);
                    this.IECSDispose();
                }
                catch (Exception ex)
                {
                    OnLifecycleError(ex);
                }
                return;
            }

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
            d.Drain(LifecycleScheduler, LifecycleErrorHandler);
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

            // Сброс состояния для переиспользования компонента.
            // Не материализуем диспетчер ради Reset, если его никогда не создавали (fast-path).
            _lifecycle?.Reset();
            AlreadyRemovedReaction = false;
        }
        public object Clone() => MemberwiseClone();

    }
}
