using System;
using System.Collections.Generic;
using AECC.Locking;

namespace AECC.Core
{
    /// <summary>
    /// Слушатель репозитория сущностей (ТЗ 4.5.3). Та же дисциплина, что у
    /// IComponentStoreListener: НЕ .NET-event и не очередь — синхронный вызов интерфейса,
    /// зафиксированного при конструировании; параметры вместо args; асинхронность запрещена.
    ///
    /// Точки вызова дословно повторяют прежний ECSEntityManager.AddNewEntity/RemoveEntity:
    /// PrepareForThisWorld — перед TryAdd (бывш. `Entity.manager = this; Entity.ECSWorldOwner
    /// = world;`), EntityAdded — после успешного включения (бывш. AddNewEntityReaction),
    /// EntityRemoved — после успешного изъятия (бывш. хвост RemoveEntity: граф, OnDelete,
    /// контракты), AddFailed — ветка ошибки (репозиторий логгера не знает).
    ///
    /// Целевая картина (ТЗ 4.5.3): менеджер = шина из трёх подписок (Query-граф, Contracts,
    /// Serialization-pending); переходно слушатель — сам ECSEntityManager, который эти три
    /// реакции пока исполняет лично.
    /// </summary>
    public interface IEntityRepositoryListener
    {
        /// <summary>Привязка сущности к миру-владельцу репозитория — перед вставкой (и заново
        /// на каждом хопе rescue-делегирования, как в исходном redirect.AddNewEntity).</summary>
        void PrepareForThisWorld(ECSEntity entity);

        /// <summary>Сущность включена в хранилище (бывш. AddNewEntityReaction).</summary>
        void EntityAdded(ECSEntity entity, bool silent);

        /// <summary>Сущность изъята из хранилища (бывш. пост-remove хвост).</summary>
        void EntityRemoved(ECSEntity entity);

        /// <summary>TryAdd не удался и редиректа нет (бывш. NLogger.Error "error add entity ...").</summary>
        void AddFailed(ECSEntity entity);
    }

    /// <summary>
    /// Репозиторий сущностей мира (фаза 3, шаг 3; ТЗ 4.5.3): ТОЛЬКО хранение сущностей +
    /// событие прихода/ухода. Граф/метрики, триггеры контрактов и слив pending-десериализации —
    /// у слушателя (переходно менеджер; целево — три подписки).
    ///
    /// ГРАНИЦА РЕДИРЕКТА (ТЗ 4.5.4): «входной» редирект — снаружи, в
    /// <see cref="RedirectingEntityRepository"/>; rescue-проверки «проснулся посреди сквоша»
    /// вплетены МЕЖДУ шагами мутации (после TryAdd, после неудачного Remove) и ЖИВУТ ЗДЕСЬ,
    /// внутри операций, — их подъём в декоратор ломает семантику спасения (идея 1.9).
    /// Состояние цепочки редиректов (volatile, активация ДО освобождения локов сквоша) —
    /// тоже здесь: оно нужно rescue-точкам.
    /// </summary>
    public sealed class EntityRepository
    {
        // HoldKeys ВЫКЛЮЧЕН — хранилище сущностей не держит ключи по отсутствию (как и раньше).
        private readonly LockedDictionarySlim<long, ECSEntity> _storage = new LockedDictionarySlim<long, ECSEntity>();
        private readonly IEntityRepositoryListener _listener;

        /// <summary>Переходный тег хозяина (ECSEntityManager) для делегирования
        /// НЕ-репозиторных операций менеджера (OnAdd/RemoveComponent, SearchGraph) по цепочке
        /// редиректов. Уходит вместе с этими операциями в подписки фаз 5–6.</summary>
        internal readonly object HostTag;

        public EntityRepository(IEntityRepositoryListener listener, object hostTag)
        {
            if (listener == null) throw new ArgumentNullException("listener");
            _listener = listener;
            HostTag = hostTag;
        }

        // ───────── цепочка сквош-редиректов (механика 1.9, дословно) ─────────

        private volatile EntityRepository _squashRedirectTarget = null;

        /// <summary>Вызывается из SquashWorlds ПЕРЕД освобождением блокировок: volatile
        /// гарантирует видимость проснувшимся потокам.</summary>
        internal void ActivateSquashRedirect(EntityRepository target)
        {
            _squashRedirectTarget = target;
        }

        /// <summary>Конечный репозиторий с учётом цепочки (A→B→C → C); защита от
        /// зацикливания — максимум 100 шагов (дословно прежний ограничитель).</summary>
        internal EntityRepository ResolveRedirect()
        {
            var target = _squashRedirectTarget;
            if (target == null) return null;
            int safetyCounter = 0;
            while (target._squashRedirectTarget != null && safetyCounter < 100)
            {
                target = target._squashRedirectTarget;
                safetyCounter++;
            }
            return target;
        }

        // ───────── операции с rescue внутри (ТЗ 4.5.4) ─────────

        /// <summary>
        /// Включение сущности. БЕЗ входного редиректа (декоратор); rescue-проверки — дословно
        /// прежние две точки AddNewEntity.
        /// </summary>
        public bool Add(ECSEntity entity, bool silent)
        {
            _listener.PrepareForThisWorld(entity);
            if (!_storage.TryAdd(entity.instanceId, entity))
            {
                // Rescue 1b: пока ждали TryAdd, сквош мог перенести сущность с этим id в цель.
                var redirect = ResolveRedirect();
                if (redirect != null)
                    return redirect.Add(entity, silent);

                _listener.AddFailed(entity);
                return false;
            }

            // Rescue 2: поток проснулся ПОСЛЕ сквоша и добавил в мёртвое хранилище —
            // забираем и делегируем в цель (каждый хоп заново привязывает сущность
            // к своему миру через PrepareForThisWorld, как прежний redirect.AddNewEntity).
            var redirect2 = ResolveRedirect();
            if (redirect2 != null)
            {
                _storage.UnsafeRemove(entity.instanceId, out _);
                return redirect2.Add(entity, silent);
            }

            _listener.EntityAdded(entity, silent);
            return true;
        }

        /// <summary>Изъятие сущности. БЕЗ входного редиректа; rescue после неудачного remove —
        /// дословно прежняя точка RemoveEntity (без неё — NPE/потеря операции).</summary>
        public void Remove(ECSEntity entity)
        {
            var entityRef = entity; // out ниже перезапишет при неудаче
            _storage.ExecuteOnRemoveLocked(entity.instanceId, out entity, (longv, entt) => { });

            var redirect = ResolveRedirect();
            if (redirect != null)
            {
                redirect.Remove(entityRef);
                return;
            }

            if (entity == null)
            {
                return;
            }

            _listener.EntityRemoved(entity);
        }

        // ───────── чтения (входной редирект — у декоратора) ─────────

        public bool TryGet(long instanceEntityId, out ECSEntity entity) { return _storage.TryGetValue(instanceEntityId, out entity); }
        public bool Contains(long instanceEntityId) { return _storage.ContainsKey(instanceEntityId); }

        // ───────── raw-проекции (сквош-машинерия и legacy-потребители) ─────────
        // Намеренно НЕ редиректятся: сквош оперирует именно мёртвым/целевым хранилищем.

        public bool ContainsKey(long id) { return _storage.ContainsKey(id); }
        public bool TryGetValue(long id, out ECSEntity entity) { return _storage.TryGetValue(id, out entity); }
        public ICollection<long> Keys { get { return _storage.Keys; } }
        public int Count { get { return _storage.Count; } }
        public RWToken LockStorage() { return _storage.LockStorage(); }
        public bool UnsafeAdd(long id, ECSEntity entity) { return _storage.UnsafeAdd(id, entity); }
        public bool UnsafeRemove(long id, out ECSEntity entity) { return _storage.UnsafeRemove(id, out entity); }
        public void ExecuteReadLockedContinuously(long id, Action<long, ECSEntity> action, out RWToken token) { _storage.ExecuteReadLockedContinuously(id, action, out token); }

        /// <summary>Тот же инстанс словаря — для [Obsolete]-фасада ECSEntityManager.EntityStorage.</summary>
        internal LockedDictionarySlim<long, ECSEntity> RawStorage { get { return _storage; } }
    }

    /// <summary>
    /// Sealed-декоратор входного редиректа (ТЗ 4.5.4): «в какой мир пришла операция».
    /// Ровно один вопрос на входе каждой операции — конечный репозиторий цепочки; вся
    /// остальная семантика (включая rescue) — внутри EntityRepository.
    /// Заменяет ~10 повторов `var redirect = ResolveRedirect(); if (redirect != null) ...`
    /// в начале методов менеджера.
    /// </summary>
    public sealed class RedirectingEntityRepository
    {
        private readonly EntityRepository _inner;

        public RedirectingEntityRepository(EntityRepository inner)
        {
            if (inner == null) throw new ArgumentNullException("inner");
            _inner = inner;
        }

        private EntityRepository Target { get { return _inner.ResolveRedirect() ?? _inner; } }

        public bool Add(ECSEntity entity, bool silent) { return Target.Add(entity, silent); }
        public void Remove(ECSEntity entity) { Target.Remove(entity); }
        public bool TryGet(long instanceEntityId, out ECSEntity entity) { return Target.TryGet(instanceEntityId, out entity); }
        public bool Contains(long instanceEntityId) { return Target.Contains(instanceEntityId); }
    }
}
