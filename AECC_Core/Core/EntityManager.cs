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
using AECC.Core.Serialization;

namespace AECC.Core
{
    public class ECSEntityManager : IEntityRepositoryListener
    {
        private ECSWorld world;

        // Хранение сущностей — в EntityRepository (только хранение + событие прихода/ухода;
        // rescue-точки сквоша ВНУТРИ операций), входной редирект — в sealed-декораторе
        // RedirectingEntityRepository. Менеджер выступает слушателем репозитория.
        public readonly EntityRepository Repository;
        private readonly RedirectingEntityRepository _entities;

        [Obsolete("Хранилище инкапсулировано в Repository; сырые операции — там же")]
        public LockedDictionarySlim<long, ECSEntity> EntityStorage { get { return Repository.RawStorage; } }

        public LockedDictionarySlim<string, ECSEntity> PreinitializedEntities = new LockedDictionarySlim<string, ECSEntity>();

        // Событийная замена ретрай-таймеров десериализации: объекты, ждущие прихода
        // ещё не пришедшей сущности, перепроверяются при AddNewEntityReaction вместо опроса.
        public readonly PendingDeserializationRegistry PendingDeserialization = new PendingDeserializationRegistry();

        // Граф/поиск живут в AECC.Query (EntityQueryIndex поверх герметизированного DefaultEcs).
        // Runtime публикует события в Core-интерфейс; монтаж — QueryBootstrap.Attach(world).
        // null = мир без поиска (Search требует Attach).
        internal IWorldQueryIndex QueryIndex;

        // --- ИНФРАСТРУКТУРА СКВОШ-РЕДИРЕКТА ---
        // Состояние цепочки живёт в EntityRepository (оно нужно rescue-точкам внутри операций).
        // Здесь — проекции для НЕ-репозиторных операций менеджера (OnAdd/RemoveComponent, SearchGraph).

        internal void ActivateSquashRedirect(ECSEntityManager target)
        {
            Repository.ActivateSquashRedirect(target.Repository);
        }

        internal ECSEntityManager ResolveRedirect()
        {
            var target = Repository.ResolveRedirect();
            return target == null ? null : (ECSEntityManager)target.HostTag;
        }

        public ECSEntityManager(ECSWorld world)
        {
            this.world = world;
            Repository = new EntityRepository(this, this);
            _entities = new RedirectingEntityRepository(Repository);
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

        // Мутации/чтения DefaultEcs идут под собственным локом IGraphNodeStore-реализации,
        // метрики — MVCC (MetricIndex). Обходы предков (Sync/Add/RemoveNodeToAncestors)
        // живут в EntityQueryIndex.

        // --- ЛОГИКА ДОБАВЛЕНИЯ СУЩНОСТЕЙ ---

        public bool AddNewEntity(ECSEntity Entity, bool silent = false)
        {
            // Входной редирект — декоратор; prepare/rescue/reaction — внутри репозитория.
            return _entities.Add(Entity, silent);
        }

        // ───── IEntityRepositoryListener ─────

        /// <summary>Готовит сущность к владению этим миром перед TryAdd.</summary>
        public void PrepareForThisWorld(ECSEntity entity)
        {
            entity.manager = this;
            entity.ECSWorldOwner = world;
        }

        /// <summary>Логирует неудачный TryAdd (репозиторий логгера не знает).</summary>
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
            // Аллокация узла + предки + Comp-метрики состава — в индексе.
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
            // (событийная замена ретрай-таймеров). RequestDrain: при пустом реестре —
            // ноль аллокаций и ноль work item'ов; при непустом — коалесцированный
            // асинхронный слив (не более одного дрейнера), так что повторные попытки
            // (берущие SerialLocker) по-прежнему НЕ исполняются внутри стека прихода.
            // ВАЖНО: вызов идёт после публикации сущности в репозитории (см. протокол
            // двойных проверок в PendingDeserializationRegistry).
            this.PendingDeserialization.RequestDrain();
        }

        // --- ЛОГИКА УДАЛЕНИЯ И ПЕРЕПОДЧИНЕНИЯ ---

        public void RemoveEntity(ECSEntity Entity)
        {
            // Входной редирект — декоратор; rescue после неудачного remove — внутри
            // репозитория; пост-remove хвост — событие EntityRemoved.
            _entities.Remove(Entity);
        }

        public void EntityRemoved(ECSEntity entity)
        {
            InternalGraphRemoval(entity);

            entity.OnDelete();
            // Контрактная реакция планируется только если для сущности есть на что реагировать —
            // иначе не тратим work item на пустую контрактную базу.
            if (this.world.contractsManager.HasEntityReactions(entity.instanceId))
            {
                TaskEx.RunAsync(() => { this.world.contractsManager.OnEntityDestroyed(entity); });
            }
        }

        private void InternalGraphRemoval(ECSEntity Entity)
        {
            // Снятие у предков по цепочке владельцев + чистка метрик и освобождение узла —
            // в индексе; переподчинение детей — здесь. Порядок фиксирован: узел снимается
            // из индекса, затем дети переподчиняются, затем узел освобождается; если узла
            // не было — вся последовательность пропускается.
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
            // Предок-сущность мог быть удалён раньше (или удаляется сейчас и уже изъят из
            // репозитория): прививать детей к нему нельзя — его собственное переподчинение
            // их уже не увидит, и они осиротеют на мёртвом объекте. Поднимаемся до живого
            // предка; не-сущности считаем живыми (их жизненный цикл не наш). Ограничитель —
            // по образцу ResolveRedirect (защита от цикла в цепочке владельцев).
            int climbGuard = 0;
            while (newParent is ECSEntity parentEnt
                   && !_entities.Contains(parentEnt.instanceId)
                   && climbGuard++ < 100)
            {
                newParent = parentEnt.ownerECSObject;
            }

            var children = new List<ECSEntity>();

            // P5: РАНЬШЕ здесь обходился childECSObjectsId — это ЗЕРКАЛО СЕРИАЛИЗАЦИИ,
            // ленивое и заполняемое только в SnapshotPass. Вне сериализации оно == null,
            // поэтому дети удаляемой сущности не переподчинялись вообще и оставались
            // с ownerECSObject на мёртвый объект. Обходим ЖИВОЕ дерево детей.
            // Детей, уже изъятых из репозитория, НЕ переподчиняем: прививка мёртвой
            // сущности к живому предку вернула бы её в его зеркало сериализации, и клиент
            // вечно резолвил бы мёртвый id.
            var liveChildren = deletedEntity.ChildrenLiveOrNull;
            if (liveChildren != null)
            {
                foreach (var kvp in liveChildren)
                {
                    if (kvp.Value is ECSEntity childEnt && _entities.Contains(childEnt.instanceId))
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

            // Симметрия удаления: сущность обязана исчезнуть и из ЖИВОГО дерева СВОЕГО
            // родителя (RemoveChildObject больше никто за нас не вызывает). Иначе мёртвый
            // объект остаётся в child-словаре родителя, попадает в его зеркало
            // childECSObjectsId при следующем SnapshotPass (клиент бесконечно ждёт мёртвый
            // id, cap 30 → «client: error deserialize»), а при последующем удалении
            // родителя этот же код привил бы «зомби» к деду. RemoveChildObject помечает
            // родителя Changed — следующий срез донесёт исчезновение ребёнка до клиента.
            var directParent = deletedEntity.ownerECSObject;
            if (directParent != null)
            {
                var siblings = directParent.ChildrenLiveOrNull;
                if (siblings != null && siblings.ContainsKey(deletedEntity.instanceId))
                {
                    directParent.RemoveChildObject(deletedEntity.instanceId, false);
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

            // Метрика — событие индексу (числовой ключ).
            if (Entity != null) QueryIndex?.OnComponentAdded(Entity, Component);
            // Контрактная реакция планируется в пул только если контрактной машинерии есть
            // на что реагировать, чтобы не тратить work item на пустые контрактные базы.
            if (Entity != null && this.world.contractsManager.HasEntityReactions(Entity.instanceId))
            {
                TaskEx.RunAsync(() => { this.world.contractsManager.OnEntityComponentAddedReaction(Entity, Component); });
            }

            // Приход компонента — тоже событие для слива отложенной десериализации
            // (на случай, когда владелец-ссылка — компонент, добавленный к уже существующей
            // сущности). Гейт пустого реестра + коалесинг — внутри RequestDrain.
            this.PendingDeserialization.RequestDrain();
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
            // Гейт пустых контрактных реакций — симметрично OnAddComponent.
            if (Entity != null && this.world.contractsManager.HasEntityReactions(Entity.instanceId))
            {
                TaskEx.RunAsync(() => { this.world.contractsManager.OnEntityComponentRemovedReaction(Entity, Component); });
            }
        }

        // --- ЭЛЕГАНТНЫЙ ПОИСК ПО ГРАФУ ---

        /// <summary>
        /// Выполняет SIMD поиск по графу.
        /// </summary>
        /// <param name="parentScope">Контекст поиска. Если указан, область аппаратно сужается до потомков этой ноды.</param>
        /// <param name="withComponentTypes">Требуемые типы компонентов.</param>
        /// <param name="withoutComponentTypes">Исключаемые типы компонентов.</param>
        [Obsolete("Используйте world.Query.Search(scope, with, without)")]
        public IEnumerable<ECSEntity> SearchGraph(
            ECSEntity parentScope = null,
            Type[] withComponentTypes = null,
            Type[] withoutComponentTypes = null)
        {
            // Тело (метрики через ITypeRegistry, scope-резолв, материализация) и
            // редирект-голова (мир мог быть сквошнут) живут в EntityQueryIndex.Search.
            if (QueryIndex == null)
                return System.Linq.Enumerable.Empty<ECSEntity>(); // мир без Attach
            return QueryIndex.Search(parentScope, withComponentTypes, withoutComponentTypes);
        }
    }
}
