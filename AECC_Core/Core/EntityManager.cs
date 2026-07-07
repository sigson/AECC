using AECC.Core.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AECC.Extensions;
using AECC.Extensions.ThreadingSync;
using AECC.Collections;
using AECC.Locking;
using AECC.Core.Serialization; // Требуется для EntitySerializator

namespace AECC.Core
{
    public class ECSEntityManager : IEntityRepositoryListener
    {
        private ECSWorld world;

        // ФАЗА 3, шаг 3 (ТЗ 4.5.3–4.5.4): хранение сущностей — в EntityRepository (только
        // хранение + событие прихода/ухода; rescue-точки сквоша ВНУТРИ операций), входной
        // редирект — в sealed-декораторе RedirectingEntityRepository. Менеджер — слушатель
        // (переходно; целево — шина из трёх подписок: Query-граф, Contracts, Serialization).
        public readonly EntityRepository Repository;
        private readonly RedirectingEntityRepository _entities;

        [Obsolete("Фаза 3 (ТЗ 4.5.3): хранилище инкапсулировано в Repository; сырые операции — там же")]
        public LockedDictionarySlim<long, ECSEntity> EntityStorage { get { return Repository.RawStorage; } }

        public LockedDictionarySlim<string, ECSEntity> PreinitializedEntities = new LockedDictionarySlim<string, ECSEntity>();

        // Событийная замена ретрай-таймеров десериализации: объекты, ждущие прихода
        // ещё не пришедшей сущности, перепроверяются при AddNewEntityReaction вместо опроса.
        public readonly PendingDeserializationRegistry PendingDeserialization = new PendingDeserializationRegistry();

        // ФАЗА 5 (ТЗ 4.6): граф/поиск выселены в AECC.Query (EntityQueryIndex поверх
        // герметизированного DefaultEcs). Runtime публикует события в Core-интерфейс;
        // монтаж — QueryBootstrap.Attach(world). null = мир без поиска (breaking: Search
        // требует Attach).
        internal IWorldQueryIndex QueryIndex;

        // --- ИНФРАСТРУКТУРА СКВОШ-РЕДИРЕКТА ---
        // Состояние цепочки живёт в EntityRepository (оно нужно rescue-точкам внутри операций).
        // Здесь — переходные проекции для НЕ-репозиторных операций менеджера
        // (OnAdd/RemoveComponent, SearchGraph): уходят в подписки фаз 5–6.

        internal void ActivateSquashRedirect(ECSEntityManager target)
        {
            Repository.ActivateSquashRedirect(target.Repository);
        }

        internal ECSEntityManager ResolveRedirect()
        {
            var target = Repository.ResolveRedirect();
            return target == null ? null : (ECSEntityManager)target.HostTag;
        }

        // ФАЗА 5: инфраструктура графа (ROOT/аллокатор/маппинги/_nodeDescendants)
        // перенесена ДОСЛОВНО в AECC.Query.EntityQueryIndex.

        public ECSEntityManager(ECSWorld world)
        {
            this.world = world;
            Repository = new EntityRepository(this, this);
            _entities = new RedirectingEntityRepository(Repository);
            // ФАЗА 5: движок графа и корневой узел — в EntityQueryIndex (QueryBootstrap.Attach).
        }

        // --- БАЗОВЫЕ МЕТОДЫ ПОИСКА ---

        // Чтения — через декоратор входного редиректа (одна точка вопроса вместо четырёх повторов).
        public bool TryGetEntitySyncronized(long instanceEntityId, out ECSEntity rentity)
        {
            return _entities.TryGet(instanceEntityId, out rentity);
        }

        public bool ContainsEntitySyncronized(long instanceEntityId)
        {
            return _entities.Contains(instanceEntityId);
        }

        public ECSEntity TryGetEntity(long instanceEntityId)
        {
            ECSEntity rentity;
            return _entities.TryGet(instanceEntityId, out rentity) ? rentity : null;
        }

        public bool ContainsEntity(long instanceEntityId)
        {
            return _entities.Contains(instanceEntityId);
        }

        // --- ТОПОЛОГИЯ ГРАФА ---

        // ФАЗА 5: _graphEngineLock (№18) СНЯТ — Query получил честную границу:
        // мутации/чтения DefaultEcs — под собственным локом IGraphNodeStore-реализации,
        // метрики — MVCC (MetricIndex). Обходы предков (Sync/Add/RemoveNodeToAncestors)
        // перенесены ДОСЛОВНО в EntityQueryIndex (одно место — мандат стратегии 3.6).

        // --- ЛОГИКА ДОБАВЛЕНИЯ СУЩНОСТЕЙ ---

        public bool AddNewEntity(ECSEntity Entity, bool silent = false)
        {
            // Входной редирект — декоратор; prepare/rescue/reaction — внутри репозитория
            // (граница ТЗ 4.5.4). Семантика дословно прежняя.
            return _entities.Add(Entity, silent);
        }

        // ───── IEntityRepositoryListener (переходно: менеджер лично исполняет три будущие подписки) ─────

        /// <summary>Бывш. `Entity.manager = this; Entity.ECSWorldOwner = world;` перед TryAdd.</summary>
        public void PrepareForThisWorld(ECSEntity entity)
        {
            entity.manager = this;
            entity.ECSWorldOwner = world;
        }

        /// <summary>Бывш. NLogger.Error в ветке неудачного TryAdd (репозиторий логгера не знает).</summary>
        public void AddFailed(ECSEntity entity)
        {
            NLogger.Error($"error add entity {entity.instanceId} to storage");
        }

        public void EntityAdded(ECSEntity entity, bool silent)
        {
            AddNewEntityReaction(entity, silent);
        }

        public void AddNewEntityReaction(ECSEntity Entity, bool silent = false)
        {
            // ФАЗА 5: аллокация узла + предки + Comp-метрики состава — в индексе (было дословно здесь).
            QueryIndex?.OnEntityAdded(Entity);

            if (!silent)
            {
                TaskEx.RunAsync(() =>
                {
                    Entity.entityComponents.RegisterAllComponents();
                    this.world.contractsManager.OnEntityCreated(Entity);
                });
            }

            // Приход сущности — событие для слива отложенной десериализации
            // (событийная замена ретрай-таймеров). Асинхронно, чтобы не исполнять
            // повторные попытки (берущие SerialLocker) внутри стека прихода.
            TaskEx.RunAsync(() => this.PendingDeserialization.Drain());
        }

        // --- ЛОГИКА УДАЛЕНИЯ И ПЕРЕПОДЧИНЕНИЯ ---

        public void RemoveEntity(ECSEntity Entity)
        {
            // Входной редирект — декоратор; rescue после неудачного remove — внутри
            // репозитория (граница ТЗ 4.5.4); пост-remove хвост — событие EntityRemoved.
            _entities.Remove(Entity);
        }

        public void EntityRemoved(ECSEntity entity)
        {
            InternalGraphRemoval(entity);

            entity.OnDelete();
            TaskEx.RunAsync(() => { this.world.contractsManager.OnEntityDestroyed(entity); });
        }

        private void InternalGraphRemoval(ECSEntity Entity)
        {
            // ФАЗА 5: шаги 1–2 (снятие у предков по СТАРОЙ цепочке владельцев + чистка
            // метрик) и шаг 4 (освобождение узла) — в индексе; шаг 3 (переподчинение,
            // «логическое, графу больше ничего не надо») — здесь. Дословный порядок 1→2→3→4
            // и гейт «нет узла — нет всей последовательности» сохранены (анти-бомба 7.9).
            // Мир без индекса: переподчинение выполняется безусловно (детей нельзя осиротить).
            bool hadNode = QueryIndex?.OnEntityRemoving(Entity) ?? true;
            if (hadNode)
            {
                ReparentChildrenUpwards(Entity);
                QueryIndex?.OnEntityRemoved(Entity);
            }
        }

        private void ReparentChildrenUpwards(ECSEntity deletedEntity)
        {
            var newParent = deletedEntity.ownerECSObject;
            var children = new List<ECSEntity>();

            if (deletedEntity.childECSObjectsId != null) // ленивое зеркало: null == детей нет
            {
                foreach (var kvp in deletedEntity.childECSObjectsId)
                {
                    if (deletedEntity.TryGetChildObject(kvp.Key, out var childObj) && childObj is ECSEntity childEnt)
                    {
                        children.Add(childEnt);
                    }
                }
            }

            // Т.к. дети уже числятся в _nodeDescendants у бабушек/дедушек, 
            // топологию перестраивать не нужно. Нужно только поменять ownerECSObject.
            foreach (var child in children)
            {
                deletedEntity.RemoveChildObject(child.instanceId, false);
                if (newParent != null)
                {
                    newParent.AddChildObject(child, true);
                }
                else
                {
                    child.ownerECSObject = null;
                }
            }
        }

        // --- МЕТРИКИ (ДОБАВЛЕНИЕ/УДАЛЕНИЕ КОМПОНЕНТОВ) ---

        public void OnAddComponent(ECSEntity Entity, ECSComponent Component)
        {
            // Редирект: компонент добавлен к сущности, которая уже в целевом мире
            var redirect = ResolveRedirect();
            if (redirect != null)
            {
                redirect.OnAddComponent(Entity, Component);
                return;
            }

            // ФАЗА 5: метрика — событие индексу (числовой ключ вместо $"Comp:{id}" — дефект 6.6).
            if (Entity != null) QueryIndex?.OnComponentAdded(Entity, Component);
            TaskEx.RunAsync(() => { this.world.contractsManager.OnEntityComponentAddedReaction(Entity, Component); });

            // Приход компонента — тоже событие для слива отложенной десериализации
            // (на случай, когда владелец-ссылка — компонент, добавленный к уже существующей сущности).
            TaskEx.RunAsync(() => this.PendingDeserialization.Drain());
        }

        public void OnRemoveComponent(ECSEntity Entity, ECSComponent Component)
        {
            var redirect = ResolveRedirect();
            if (redirect != null)
            {
                redirect.OnRemoveComponent(Entity, Component);
                return;
            }

            if (Entity != null) QueryIndex?.OnComponentRemoved(Entity, Component);
            TaskEx.RunAsync(() => { this.world.contractsManager.OnEntityComponentRemovedReaction(Entity, Component); });
        }

        // --- ЭЛЕГАНТНЫЙ ПОИСК ПО ГРАФУ ---

        /// <summary>
        /// Выполняет SIMD поиск по графу.
        /// </summary>
        /// <param name="parentScope">Контекст поиска. Если указан, область аппаратно сужается до потомков этой ноды.</param>
        /// <param name="withComponentTypes">Требуемые типы компонентов.</param>
        /// <param name="withoutComponentTypes">Исключаемые типы компонентов.</param>
        [Obsolete("Фаза 5 (ТЗ 4.6, breaking): используйте world.Query.Search(scope, with, without)")]
        public IEnumerable<ECSEntity> SearchGraph(
            ECSEntity parentScope = null, 
            Type[] withComponentTypes = null, 
            Type[] withoutComponentTypes = null)
        {
            // Тело (метрики через ITypeRegistry, scope-резолв, материализация) — ДОСЛОВНО
            // в EntityQueryIndex.Search; редирект-голова — тоже там (мир мог быть сквошнут).
            if (QueryIndex == null)
                return System.Linq.Enumerable.Empty<ECSEntity>(); // мир без Attach (breaking, см. журнал)
            return QueryIndex.Search(parentScope, withComponentTypes, withoutComponentTypes);
        }
    }
}
