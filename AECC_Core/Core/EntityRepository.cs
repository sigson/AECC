using System;
using System.Collections.Generic;
using AECC.Locking;

namespace AECC.Core
{
    /// <summary>
    /// Слушатель репозитория сущностей. Та же дисциплина, что у IComponentStoreListener:
    /// НЕ .NET-event и не очередь — синхронный вызов интерфейса, зафиксированного при
    /// конструировании; параметры вместо args; асинхронность запрещена.
    ///
    /// Слушатель — ECSEntityManager, который исполняет реакции на приход/уход сущности
    /// (граф Query, Contracts, слив pending-десериализации) лично.
    /// </summary>
    public interface IEntityRepositoryListener
    {
        /// <summary>Привязка сущности к миру-владельцу репозитория — перед вставкой (и заново
        /// на каждом хопе rescue-делегирования).</summary>
        void PrepareForThisWorld(ECSEntity entity);

        /// <summary>Сущность включена в хранилище.</summary>
        void EntityAdded(ECSEntity entity, bool silent);

        /// <summary>Сущность изъята из хранилища.</summary>
        void EntityRemoved(ECSEntity entity);

        /// <summary>TryAdd не удался и редиректа нет.</summary>
        void AddFailed(ECSEntity entity);
    }

    /// <summary>
    /// Репозиторий сущностей мира: ТОЛЬКО хранение сущностей + событие прихода/ухода.
    /// Граф/метрики, триггеры контрактов и слив pending-десериализации — у слушателя.
    ///
    /// ГРАНИЦА РЕДИРЕКТА: «входной» редирект — снаружи, в
    /// <see cref="RedirectingEntityRepository"/>; rescue-проверки «проснулся посреди сквоша»
    /// вплетены МЕЖДУ шагами мутации (после TryAdd, после неудачного Remove) и живут здесь,
    /// внутри операций — поднять их в декоратор сломало бы семантику спасения.
    /// Состояние цепочки редиректов (volatile, активация до освобождения локов сквоша) —
    /// тоже здесь: оно нужно rescue-точкам.
    /// </summary>
    public sealed class EntityRepository
    {
        // HoldKeys выключен — хранилище сущностей не держит ключи по отсутствию.
        private readonly LockedDictionarySlim<long, ECSEntity> _storage = new LockedDictionarySlim<long, ECSEntity>();
        private readonly IEntityRepositoryListener _listener;

        /// <summary>Тег хозяина (ECSEntityManager) для делегирования НЕ-репозиторных операций
        /// менеджера (OnAdd/RemoveComponent, SearchGraph) по цепочке редиректов.</summary>
        internal readonly object HostTag;

        public EntityRepository(IEntityRepositoryListener listener, object hostTag)
        {
            if (listener == null) throw new ArgumentNullException("listener");
            _listener = listener;
            HostTag = hostTag;
        }

        // ───────── цепочка сквош-редиректов ─────────

        private volatile EntityRepository _squashRedirectTarget = null;

        /// <summary>Вызывается из SquashWorlds ПЕРЕД освобождением блокировок: volatile
        /// гарантирует видимость проснувшимся потокам.</summary>
        internal void ActivateSquashRedirect(EntityRepository target)
        {
            _squashRedirectTarget = target;
        }

        /// <summary>Конечный репозиторий с учётом цепочки (A→B→C → C); защита от
        /// зацикливания — максимум 100 шагов.</summary>
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

        // ───────── операции с rescue внутри ─────────

        /// <summary>
        /// Включение сущности. Без входного редиректа (он в декораторе); rescue-проверки —
        /// две точки внутри операции.
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
            // к своему миру через PrepareForThisWorld).
            var redirect2 = ResolveRedirect();
            if (redirect2 != null)
            {
                _storage.UnsafeRemove(entity.instanceId, out _);
                return redirect2.Add(entity, silent);
            }

            _listener.EntityAdded(entity, silent);
            return true;
        }

        /// <summary>Изъятие сущности. Без входного редиректа; rescue после неудачного remove
        /// предотвращает NPE/потерю операции.</summary>
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
    /// Sealed-декоратор входного редиректа: «в какой мир пришла операция».
    /// Ровно один вопрос на входе каждой операции — конечный репозиторий цепочки; вся
    /// остальная семантика (включая rescue) — внутри EntityRepository.
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
