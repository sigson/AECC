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
using AECC.Core.Serialization; // Требуется для EntitySerializator

namespace AECC.Core
{
    public class ECSEntityManager
    {
        private ECSWorld world;
        public ILockedDictionary<long, ECSEntity> EntityStorage = new LockedDictionary<long, ECSEntity>();
        public LockedDictionaryAsync<long, ECSEntity> EntityStorageAsync = new LockedDictionaryAsync<long, ECSEntity>();
        public ILockedDictionary<string, ECSEntity> PreinitializedEntities = new LockedDictionary<string, ECSEntity>();

        public GraphSearchEngine graphSearchEngine;

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

        // --- БАЗОВЫЕ МЕТОДЫ ПОИСКА (БЕЗ ИЗМЕНЕНИЙ) ---

        public bool TryGetEntitySyncronized(long instanceEntityId, out ECSEntity rentity)
        {
            if(EntityStorage.TryGetValue(instanceEntityId, out rentity)) return true;
            var asyncResult = EntityStorageAsync.TryGetValueAsync(instanceEntityId).Result;
            if(asyncResult.Success && asyncResult.Value != null)
            {
                rentity = asyncResult.Value;
                return true;
            }
            return false;
        }

        public bool ContainsEntitySyncronized(long instanceEntityId)
        {
            if (EntityStorage.ContainsKey(instanceEntityId)) return true;
            return EntityStorageAsync.ContainsKeyAsync(instanceEntityId).Result;
        } 

        public async Task<ECSEntity> TryGetEntityAsync(long instanceEntityId)
        {
            var asyncResult = await EntityStorageAsync.TryGetValueAsync(instanceEntityId);
            if (asyncResult.Success && asyncResult.Value != null) return asyncResult.Value;
            if (EntityStorage.TryGetValue(instanceEntityId, out var rentity)) return rentity;
            return null;
        }

        public async Task<bool> ContainsEntityAsync(long instanceEntityId)
        {
            if (await EntityStorageAsync.ContainsKeyAsync(instanceEntityId)) return true;
            return EntityStorage.ContainsKey(instanceEntityId);
        }

        public ECSEntity TryGetEntity(long instanceEntityId)
        {
            if (EntityStorage.TryGetValue(instanceEntityId, out var rentity)) return rentity;
            return null;
        }

        public bool ContainsEntity(long instanceEntityId)
        {
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
            Entity.manager = this;
            Entity.ECSWorldOwner = world;
            if (!EntityStorage.TryAdd(Entity.instanceId, Entity))
            {
                NLogger.Error($"error add entity {Entity.instanceId} to storage");
                return false;
            }
            AddNewEntityReaction(Entity, silent);
            return true;
        }

        public async Task<bool> AddNewEntityAsync(ECSEntity Entity, bool silent = false)
        {
            bool added = false;
            Entity.manager = this;
            Entity.ECSWorldOwner = world;
            if (!await this.EntityStorageAsync.ContainsKeyAsync(Entity.instanceId))
            {
                await EntityStorageAsync.ExecuteOnAddLockedAsync(Entity.instanceId, Entity, async (key, newcomponent) =>
                {
                    added = true;
                    AddNewEntityReactionAsync(Entity, silent);
                });
            }
            return added;
        }

        public void AddNewEntityReaction(ECSEntity Entity, bool silent = false)
        {
            int graphId = AllocateGraphId(Entity.instanceId);
            AddNodeToAncestors(Entity, graphId);

            ICollection<ECSComponent> result = Entity.entityComponents.isAsync 
                ? Entity.entityComponents.GetComponentsAsync().Result 
                : Entity.entityComponents.Components;

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
        }

        public async void AddNewEntityReactionAsync(ECSEntity Entity, bool silent = false)
        {
            int graphId = AllocateGraphId(Entity.instanceId);
            AddNodeToAncestors(Entity, graphId);

            ICollection<ECSComponent> result = Entity.entityComponents.isAsync 
                ? await Entity.entityComponents.GetComponentsAsync()
                : Entity.entityComponents.Components;

            foreach (var comp in result)
            {
                graphSearchEngine.AddMetricToNode(graphId, $"Comp:{comp.GetId()}");
            }

            if(!silent)
            {
                await Entity.entityComponents.RegisterAllComponentsAsync();
                this.world.contractsManager.OnEntityCreated(Entity);
            }
        }

        // --- ЛОГИКА УДАЛЕНИЯ И ПЕРЕПОДЧИНЕНИЯ ---

        public void RemoveEntity(ECSEntity Entity)
        {
            EntityStorage.ExecuteOnRemoveLocked(Entity.instanceId, out Entity, (longv, entt) => {});
            InternalGraphRemoval(Entity);
            
            Entity.OnDelete();
            TaskEx.RunAsync(() => { this.world.contractsManager.OnEntityDestroyed(Entity); });
        }

        public async void RemoveEntityAsync(ECSEntity Entity)
        {
            await EntityStorageAsync.ExecuteOnRemoveLockedAsync(Entity.instanceId, async (longv, entt) => {});
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
                ICollection<ECSComponent> components = Entity.entityComponents.isAsync 
                    ? Entity.entityComponents.GetComponentsAsync().Result 
                    : Entity.entityComponents.Components;

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
            if (Entity != null && _entityToGraphId.TryGetValue(Entity.instanceId, out int graphId))
            {
                graphSearchEngine.AddMetricToNode(graphId, $"Comp:{Component.GetId()}");
            }
            TaskEx.RunAsync(() => { this.world.contractsManager.OnEntityComponentAddedReaction(Entity, Component); });
        }

        public void OnRemoveComponent(ECSEntity Entity, ECSComponent Component)
        {
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