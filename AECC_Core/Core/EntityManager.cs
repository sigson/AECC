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
    public class ECSEntityManager
    {
        private ECSWorld world;
        // PHASE 1: world-level sync dictionaries moved to LockedDictionarySlim — the per-cell
        // ReaderWriterLockSlim is gone, the cell is a ~32 B node with an inline `long` lock.
        // HoldKeys is OFF here (entity storage never holds keys by absence). Returns RWToken now.
        public LockedDictionarySlim<long, ECSEntity> EntityStorage = new LockedDictionarySlim<long, ECSEntity>();
        public LockedDictionarySlim<string, ECSEntity> PreinitializedEntities = new LockedDictionarySlim<string, ECSEntity>();

        // Событийная замена ретрай-таймеров десериализации: объекты, ждущие прихода
        // ещё не пришедшей сущности, перепроверяются при AddNewEntityReaction вместо опроса.
        public readonly PendingDeserializationRegistry PendingDeserialization = new PendingDeserializationRegistry();

        public GraphSearchEngine graphSearchEngine;

        // --- ИНФРАСТРУКТУРА СКВОШ-РЕДИРЕКТА ---
        // Когда мир сквошируется, все операции перенаправляются на целевой менеджер.
        // volatile гарантирует видимость для потоков, проснувшихся после разблокировки EntityStorage.
        private volatile ECSEntityManager _squashRedirectTarget = null;

        /// <summary>
        /// Активирует перенаправление всех операций на целевой менеджер.
        /// Вызывается из SquashWorlds ПЕРЕД освобождением блокировок,
        /// чтобы проснувшиеся потоки сразу увидели редирект.
        /// </summary>
        internal void ActivateSquashRedirect(ECSEntityManager target)
        {
            _squashRedirectTarget = target;
        }

        /// <summary>
        /// Возвращает конечный целевой менеджер с учётом цепочки редиректов.
        /// Например: мир A → B → C — вернёт менеджер мира C.
        /// </summary>
        private ECSEntityManager ResolveRedirect()
        {
            var target = _squashRedirectTarget;
            if (target == null) return null;
            // Проходим по цепочке до конца (защита от зацикливания — максимум 100 шагов)
            int safetyCounter = 0;
            while (target._squashRedirectTarget != null && safetyCounter < 100)
            {
                target = target._squashRedirectTarget;
                safetyCounter++;
            }
            return target;
        }

        // --- ИНФРАСТРУКТУРА ГРАФА ---
        private const int ROOT_WORLD_NODE_ID = 0; // Корневой узел мира (глобальный контекст)
        private int _nextGraphId = 1;
        private readonly ConcurrentQueue<int> _freedGraphIds = new ConcurrentQueue<int>();
        
        // Маппинг long instanceId <-> int graphNodeId
        private readonly ConcurrentDictionary<long, int> _entityToGraphId = new ConcurrentDictionary<long, int>();
        private readonly ConcurrentDictionary<int, long> _graphIdToEntity = new ConcurrentDictionary<int, long>();
        
        // Хранение топологии: для каждой ноды храним HashSet её потомков (прямых и косвенных)
        private readonly ConcurrentDictionary<int, HashSet<int>> _nodeDescendants = new ConcurrentDictionary<int, HashSet<int>>();

        public ECSEntityManager(ECSWorld world)
        {
            this.world = world;
            graphSearchEngine = new GraphSearchEngine(maxGraphNodes: 1000000); 
            
            // Инициализация Супер-Корня
            _nodeDescendants[ROOT_WORLD_NODE_ID] = new HashSet<int>();
            SyncNodeNeighbors(ROOT_WORLD_NODE_ID);
        }

        // --- БАЗОВЫЕ МЕТОДЫ ПОИСКА ---

        public bool TryGetEntitySyncronized(long instanceEntityId, out ECSEntity rentity)
        {
            var redirect = ResolveRedirect();
            if (redirect != null) return redirect.TryGetEntitySyncronized(instanceEntityId, out rentity);

            if(EntityStorage.TryGetValue(instanceEntityId, out rentity)) return true;
            // PHASE 3a: async storage is no longer populated (it is removed entirely in 3b).
            // Dropping the sync-over-async fallback removes a .Result hazard on every cache miss.
            return false;
        }

        public bool ContainsEntitySyncronized(long instanceEntityId)
        {
            var redirect = ResolveRedirect();
            if (redirect != null) return redirect.ContainsEntitySyncronized(instanceEntityId);

            return EntityStorage.ContainsKey(instanceEntityId);
        } 

        public ECSEntity TryGetEntity(long instanceEntityId)
        {
            var redirect = ResolveRedirect();
            if (redirect != null) return redirect.TryGetEntity(instanceEntityId);

            if (EntityStorage.TryGetValue(instanceEntityId, out var rentity)) return rentity;
            return null;
        }

        public bool ContainsEntity(long instanceEntityId)
        {
            var redirect = ResolveRedirect();
            if (redirect != null) return redirect.ContainsEntity(instanceEntityId);

            return EntityStorage.ContainsKey(instanceEntityId);
        }

        // --- ТОПОЛОГИЯ ГРАФА ---

        private void SyncNodeNeighbors(int graphId)
        {
            if (_nodeDescendants.TryGetValue(graphId, out var descendants))
            {
                int[] neighborsArray;
                lock (descendants)
                {
                    neighborsArray = descendants.ToArray();
                }
                graphSearchEngine.SetNeighbors(graphId, neighborsArray);
            }
        }

        private void AddNodeToAncestors(ECSEntity entity, int childGraphId)
        {
            var currentParent = entity.ownerECSObject;
            while (currentParent != null)
            {
                if (_entityToGraphId.TryGetValue(currentParent.instanceId, out int parentGraphId))
                {
                    if (_nodeDescendants.TryGetValue(parentGraphId, out var pDescendants))
                    {
                        lock (pDescendants) { pDescendants.Add(childGraphId); }
                        SyncNodeNeighbors(parentGraphId);
                    }
                }
                currentParent = currentParent.ownerECSObject;
            }

            // Добавляем в глобальный корень мира
            var rootDescendants = _nodeDescendants[ROOT_WORLD_NODE_ID];
            lock (rootDescendants) { rootDescendants.Add(childGraphId); }
            SyncNodeNeighbors(ROOT_WORLD_NODE_ID);
        }

        private void RemoveNodeFromAncestors(ECSEntity entity, int childGraphId)
        {
            var currentParent = entity.ownerECSObject;
            while (currentParent != null)
            {
                if (_entityToGraphId.TryGetValue(currentParent.instanceId, out int parentGraphId))
                {
                    if (_nodeDescendants.TryGetValue(parentGraphId, out var pDescendants))
                    {
                        lock (pDescendants) { pDescendants.Remove(childGraphId); }
                        SyncNodeNeighbors(parentGraphId);
                    }
                }
                currentParent = currentParent.ownerECSObject;
            }

            var rootDescendants = _nodeDescendants[ROOT_WORLD_NODE_ID];
            lock (rootDescendants) { rootDescendants.Remove(childGraphId); }
            SyncNodeNeighbors(ROOT_WORLD_NODE_ID);
        }

        private int AllocateGraphId(long instanceId)
        {
            int id = _freedGraphIds.TryDequeue(out int freedId) ? freedId : Interlocked.Increment(ref _nextGraphId);
            _entityToGraphId[instanceId] = id;
            _graphIdToEntity[id] = instanceId;
            _nodeDescendants[id] = new HashSet<int>();
            SyncNodeNeighbors(id);
            return id;
        }

        // --- ЛОГИКА ДОБАВЛЕНИЯ СУЩНОСТЕЙ ---

        public bool AddNewEntity(ECSEntity Entity, bool silent = false)
        {
            // Проверка 1: Редирект для НОВЫХ вызовов (вошли после сквоша)
            var redirect = ResolveRedirect();
            if (redirect != null)
                return redirect.AddNewEntity(Entity, silent);

            Entity.manager = this;
            Entity.ECSWorldOwner = world;
            if (!EntityStorage.TryAdd(Entity.instanceId, Entity))
            {
                // Проверка 1b: Возможно, мы были заблокированы на TryAdd, сквош произошёл,
                // сущность с таким ID уже перенесена в целевой мир, и TryAdd вернул false
                // потому что в старом storage ключ не добавился по иной причине.
                redirect = ResolveRedirect();
                if (redirect != null)
                    return redirect.AddNewEntity(Entity, silent);

                NLogger.Error($"error add entity {Entity.instanceId} to storage");
                return false;
            }

            // Проверка 2: Rescue для ПРОСНУВШИХСЯ потоков.
            // Поток был заблокирован на EntityStorage.TryAdd (на GlobalLocker.ReadLock()).
            // Пока он ждал, сквош:
            //   - захватил WriteLock на этом EntityStorage
            //   - перенёс все сущности в целевой мир
            //   - установил _squashRedirectTarget
            //   - освободил WriteLock
            // Поток проснулся, TryAdd успешно добавил сущность в СТАРЫЙ (мёртвый) storage.
            // Ловим этот случай: забираем сущность из мёртвого storage и делегируем в целевой.
            redirect = ResolveRedirect();
            if (redirect != null)
            {
                // Удаляем из нашего мёртвого хранилища (добавилась строкой выше)
                EntityStorage.UnsafeRemove(Entity.instanceId, out _);
                // Делегируем в целевой менеджер — он установит правильные manager/world и зарегистрирует
                return redirect.AddNewEntity(Entity, silent);
            }

            AddNewEntityReaction(Entity, silent);
            return true;
        }

        public void AddNewEntityReaction(ECSEntity Entity, bool silent = false)
        {
            int graphId = AllocateGraphId(Entity.instanceId);
            AddNodeToAncestors(Entity, graphId);

            ICollection<ECSComponent> result = Entity.entityComponents.Components;

            foreach (var comp in result)
            {
                graphSearchEngine.AddMetricToNode(graphId, $"Comp:{comp.GetId()}");
            }

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
            // Проверка 1: Редирект для НОВЫХ вызовов
            var redirect = ResolveRedirect();
            if (redirect != null)
            {
                redirect.RemoveEntity(Entity);
                return;
            }

            // Сохраняем ссылку, т.к. out Entity перезапишет при неудаче
            var entityRef = Entity;

            EntityStorage.ExecuteOnRemoveLocked(Entity.instanceId, out Entity, (longv, entt) => {});

            // Проверка 2: Rescue для проснувшихся потоков.
            // Сценарий: поток вызвал RemoveEntity, заблокировался на ExecuteOnRemoveLocked.
            // Сквош переместил сущность в целевой мир и установил редирект.
            // Поток проснулся: ExecuteOnRemoveLocked не нашёл сущность (Entity == null из out),
            // т.к. она уже была удалена из этого storage при сквоше.
            // Без этой проверки следующая строка (InternalGraphRemoval) получит null → NPE.
            redirect = ResolveRedirect();
            if (redirect != null)
            {
                // Делегируем удаление в целевой менеджер, где сущность реально живёт.
                // Используем оригинальную ссылку entityRef, т.к. out мог обнулить Entity.
                redirect.RemoveEntity(entityRef);
                return;
            }

            if(Entity == null)
            {
                return;
            }

            InternalGraphRemoval(Entity);
            
            Entity.OnDelete();
            TaskEx.RunAsync(() => { this.world.contractsManager.OnEntityDestroyed(Entity); });
        }

        private void InternalGraphRemoval(ECSEntity Entity)
        {
            if (_entityToGraphId.TryGetValue(Entity.instanceId, out int deletedGraphId))
            {
                // 1. Убираем удаляемый узел из списков соседей его предков
                RemoveNodeFromAncestors(Entity, deletedGraphId);

                // 2. Очищаем индекс от метрик компонентов этой сущности, перед тем как освободить её ID
                ICollection<ECSComponent> components = Entity.entityComponents.Components;

                foreach (var comp in components)
                {
                    graphSearchEngine.RemoveMetricFromNode(deletedGraphId, $"Comp:{comp.GetId()}");
                }

                // 3. Выполняем переподчинение (логическое, графу больше ничего не надо)
                ReparentChildrenUpwards(Entity);

                // 4. Освобождаем узел
                _nodeDescendants.TryRemove(deletedGraphId, out _);
                _entityToGraphId.TryRemove(Entity.instanceId, out _);
                _graphIdToEntity.TryRemove(deletedGraphId, out _);
                _freedGraphIds.Enqueue(deletedGraphId);
            }
        }

        private void ReparentChildrenUpwards(ECSEntity deletedEntity)
        {
            var newParent = deletedEntity.ownerECSObject;
            var children = new List<ECSEntity>();

            foreach (var kvp in deletedEntity.childECSObjectsId)
            {
                if (deletedEntity.TryGetChildObject(kvp.Key, out var childObj) && childObj is ECSEntity childEnt)
                {
                    children.Add(childEnt);
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

            if (Entity != null && _entityToGraphId.TryGetValue(Entity.instanceId, out int graphId))
            {
                graphSearchEngine.AddMetricToNode(graphId, $"Comp:{Component.GetId()}");
            }
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

            if (Entity != null && _entityToGraphId.TryGetValue(Entity.instanceId, out int graphId))
            {
                graphSearchEngine.RemoveMetricFromNode(graphId, $"Comp:{Component.GetId()}");
            }
            TaskEx.RunAsync(() => { this.world.contractsManager.OnEntityComponentRemovedReaction(Entity, Component); });
        }

        // --- ЭЛЕГАНТНЫЙ ПОИСК ПО ГРАФУ ---

        /// <summary>
        /// Выполняет SIMD поиск по графу.
        /// </summary>
        /// <param name="parentScope">Контекст поиска. Если указан, область аппаратно сужается до потомков этой ноды.</param>
        /// <param name="withComponentTypes">Требуемые типы компонентов.</param>
        /// <param name="withoutComponentTypes">Исключаемые типы компонентов.</param>
        public IEnumerable<ECSEntity> SearchGraph(
            ECSEntity parentScope = null, 
            Type[] withComponentTypes = null, 
            Type[] withoutComponentTypes = null)
        {
            var redirect = ResolveRedirect();
            if (redirect != null)
                return redirect.SearchGraph(parentScope, withComponentTypes, withoutComponentTypes);

            var withMetrics = new List<string>();
            var withoutMetrics = new List<string>();

            // Используем безопасный TryGetValue для формирования метрик
            if (withComponentTypes != null)
            {
                foreach (var t in withComponentTypes)
                {
                    if (EntitySerializer.TypeIdStorage.TryGetValue(t, out long compId))
                    {
                        withMetrics.Add($"Comp:{compId}");
                    }
                }
            }

            if (withoutComponentTypes != null)
            {
                foreach (var t in withoutComponentTypes)
                {
                    if (EntitySerializer.TypeIdStorage.TryGetValue(t, out long compId))
                    {
                        withoutMetrics.Add($"Comp:{compId}");
                    }
                }
            }

            int sourceNodeId = ROOT_WORLD_NODE_ID;
            
            if (parentScope != null)
            {
                if (_entityToGraphId.TryGetValue(parentScope.instanceId, out int parentGraphId))
                {
                    sourceNodeId = parentGraphId;
                }
                else
                {
                    // Если переданной сущности-контейнера больше не существует
                    return Enumerable.Empty<ECSEntity>(); 
                }
            }

            // Магия SIMD: Пересекаются только метрики нод, которые принадлежат массиву Neighbors sourceNodeId
            var matchedNodeIds = graphSearchEngine.Search(
                sourceNodeId: sourceNodeId, 
                withMetrics: withMetrics.ToArray(), 
                withoutMetrics: withoutMetrics.ToArray()
            );

            var results = new List<ECSEntity>();
            foreach (var graphId in matchedNodeIds)
            {
                if (_graphIdToEntity.TryGetValue(graphId, out long instanceId))
                {
                    if (TryGetEntitySyncronized(instanceId, out ECSEntity ent))
                    {
                        results.Add(ent);
                    }
                }
            }

            return results;
        }
    }
}
