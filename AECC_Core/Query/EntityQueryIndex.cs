using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AECC.Core;
using AECC.Extensions;

namespace AECC.Query
{
    /// <summary>
    /// The world's query index: tracks entity/graph topology (parent-descendant
    /// relationships) and a reverse component-owner index, and answers component-set
    /// queries scoped to a subtree of the entity graph.
    ///
    /// Subscribed to via the Core interface IWorldQueryIndex (Runtime publishes events,
    /// Query implements the index on top). The index is eventually consistent: contract
    /// reactions that read it must still apply their own race-check/retry discipline,
    /// since consistency is not guaranteed by the subscription alone.
    ///
    /// Concurrency: there is no global lock over the graph engine; topology access is
    /// locked inside IGraphNodeStore, metrics use MVCC snapshots, id mappings use
    /// Concurrent* collections, and each node's descendant set has its own lock.
    /// </summary>
    public sealed class EntityQueryIndex : IWorldQueryIndex
    {
        private readonly ECSWorld _world;
        private readonly GraphSearchEngine _engine;

        private const int ROOT_WORLD_NODE_ID = 0; // Root node of the world (global context)
        private int _nextGraphId = 1;
        private readonly ConcurrentQueue<int> _freedGraphIds = new ConcurrentQueue<int>();

        // Mapping long instanceId <-> int graphNodeId
        private readonly ConcurrentDictionary<long, int> _entityToGraphId = new ConcurrentDictionary<long, int>();
        private readonly ConcurrentDictionary<int, long> _graphIdToEntity = new ConcurrentDictionary<int, long>();

        // Topology storage: each node keeps a HashSet of its descendants (direct and indirect).
        // This is a pay-on-write, read-instantly scheme: O(depth) work on structural change,
        // O(1) lookup on query.
        private readonly ConcurrentDictionary<int, HashSet<int>> _nodeDescendants = new ConcurrentDictionary<int, HashSet<int>>();

        public EntityQueryIndex(ECSWorld world)
        {
            _world = world;
            _engine = new GraphSearchEngine(new DefaultEcsGraphNodeStore());

            _nodeDescendants[ROOT_WORLD_NODE_ID] = new HashSet<int>();
            SyncNodeNeighbors(ROOT_WORLD_NODE_ID);

            TypeRegistry.Global.RegisteredTypes.ForEach(x => _componentOwners[x.Value] = new HashSet<ECSEntity>());
        }

        // Ancestor traversal is centralized here rather than duplicated at each call site.

        private void SyncNodeNeighbors(int graphId)
        {
            if (_nodeDescendants.TryGetValue(graphId, out var descendants))
            {
                int[] neighborsArray;
                lock (descendants)
                {
                    neighborsArray = descendants.ToArray();
                }
                _engine.SetNeighbors(graphId, neighborsArray);
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

        public void OnEntityAdded(ECSEntity entity)
        {
            int graphId = AllocateGraphId(entity.instanceId);
            AddNodeToAncestors(entity, graphId);

            ICollection<ECSComponent> result = entity.entityComponents.Components;

            foreach (var comp in result)
            {
                _engine.AddMetricToNode(graphId, comp.GetId());
                OwnersOf(comp.GetId()).AddI(entity, entity);
            }
        }

        public bool OnEntityRemoving(ECSEntity entity)
        {
            // Must run before children are reparented: unlinking from ancestors relies on
            // the still-current ownership chain.
            if (!_entityToGraphId.TryGetValue(entity.instanceId, out int deletedGraphId))
                return false;

            RemoveNodeFromAncestors(entity, deletedGraphId);

            ICollection<ECSComponent> components = entity.entityComponents.Components;

            foreach (var comp in components)
            {
                _engine.RemoveMetricFromNode(deletedGraphId, comp.GetId());
                OwnersOf(comp.GetId()).RemoveI(entity, entity);
            }

            return true;
        }

        public void OnEntityRemoved(ECSEntity entity)
        {
            // Runs after reparenting: frees the graph node/id for reuse.
            if (_entityToGraphId.TryGetValue(entity.instanceId, out int deletedGraphId))
            {
                _nodeDescendants.TryRemove(deletedGraphId, out _);
                _entityToGraphId.TryRemove(entity.instanceId, out _);
                _graphIdToEntity.TryRemove(deletedGraphId, out _);
                _freedGraphIds.Enqueue(deletedGraphId);
            }
        }

        public void OnComponentAdded(ECSEntity entity, ECSComponent component)
        {
            if (_entityToGraphId.TryGetValue(entity.instanceId, out int graphId))
            {
                _engine.AddMetricToNode(graphId, component.GetId());
            }

            // The index is eventually consistent, so membership is re-verified after the
            // add/remove: contract correctness relies on this recheck loop, not on the
            // index alone.
            var hentities = OwnersOf(component.GetId());
            racecheckagain:
            bool added = false;
            if (entity.HasComponent(component.GetId()))
            {
                hentities.AddI(entity, entity);
                added = true;
            }
            if (added && !entity.HasComponent(component.GetId()))
            {
                hentities.RemoveI(entity, entity);
                goto racecheckagain;
            }
        }

        public void OnComponentRemoved(ECSEntity entity, ECSComponent component)
        {
            if (_entityToGraphId.TryGetValue(entity.instanceId, out int graphId))
            {
                _engine.RemoveMetricFromNode(graphId, component.GetId());
            }

            var hentities = OwnersOf(component.GetId());
            racecheckagain:
            bool removed = false;
            if (!entity.HasComponent(component.GetId()))
            {
                hentities.RemoveI(entity, entity);
                removed = true;
            }
            if (removed && entity.HasComponent(component.GetId()))
            {
                hentities.AddI(entity, entity);
                goto racecheckagain;
            }
        }

        // Reverse index: componentTypeId -> owning entities.

        private readonly ConcurrentDictionary<long, HashSet<ECSEntity>> _componentOwners =
            new ConcurrentDictionary<long, HashSet<ECSEntity>>();

        public IDictionary<long, HashSet<ECSEntity>> ComponentOwnersView { get { return _componentOwners; } }

        /// <summary>The owner set is created on demand for component types not seen at
        /// construction time (pre-seeded from RegisteredTypes), so a component type
        /// registered later doesn't hit a missing-key error; the common case still avoids
        /// the GetOrAdd factory allocation since the set already exists.</summary>
        private HashSet<ECSEntity> OwnersOf(long componentTypeId)
        {
            return _componentOwners.GetOrAdd(componentTypeId, _ => new HashSet<ECSEntity>());
        }

        public HashSet<ECSEntity> FilterEntitiesForComponents(List<long> components)
        {
            // Intersects starting from the smallest-cardinality owner set, with early exit
            // once the running intersection is empty.
            if (components == null || components.Count == 0)
                return new HashSet<ECSEntity>();

            var setsToIntersect = new List<HashSet<ECSEntity>>();

            foreach (var comp in components)
            {
                if (!_componentOwners.TryGetValue(comp, out var owners) || owners.Count == 0)
                {
                    return new HashSet<ECSEntity>();
                }
                setsToIntersect.Add(owners);
            }

            setsToIntersect.Sort((a, b) => a.Count.CompareTo(b.Count));

            var result = setsToIntersect[0].SnapshotI(setsToIntersect[0]);

            for (int i = 1; i < setsToIntersect.Count; i++)
            {
                result.IntersectWith(setsToIntersect[i]);

                if (result.Count == 0)
                    break;
            }

            return result;
        }

        public IEnumerable<ECSEntity> Search(
            ECSEntity parentScope = null,
            Type[] withComponentTypes = null,
            Type[] withoutComponentTypes = null)
        {
            // The world may have been redirected (e.g. squashed); follow the redirect head
            // before querying.
            var redirect = _world.entityManager.ResolveRedirect();
            if (redirect != null && redirect.QueryIndex != null)
                return redirect.QueryIndex.Search(parentScope, withComponentTypes, withoutComponentTypes);

            var withMetrics = new List<long>();
            var withoutMetrics = new List<long>();

            // Component types are resolved to numeric type-uids via ITypeRegistry.
            if (withComponentTypes != null)
            {
                foreach (var t in withComponentTypes)
                {
                    if (TypeRegistry.Global.TryGetRegisteredId(t, out long compId))
                    {
                        withMetrics.Add(compId);
                    }
                }
            }

            if (withoutComponentTypes != null)
            {
                foreach (var t in withoutComponentTypes)
                {
                    if (TypeRegistry.Global.TryGetRegisteredId(t, out long compId))
                    {
                        withoutMetrics.Add(compId);
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

            // Материализация ОБЯЗАНА произойти здесь: Search движка возвращает ленивый
            // enumerable поверх снимков; прежний мировой лок снят (№18) — консистентность
            // держат снимок соседей (иммутабельный массив) и MVCC-снимки метрик.
            List<int> matchedNodeIds = _engine.Search(
                sourceNodeId: sourceNodeId,
                withMetrics: withMetrics.ToArray(),
                withoutMetrics: withoutMetrics.ToArray()
            ).ToList();

            var results = new List<ECSEntity>();
            foreach (var graphId in matchedNodeIds)
            {
                if (_graphIdToEntity.TryGetValue(graphId, out long instanceId))
                {
                    if (_world.entityManager.TryGetEntitySyncronized(instanceId, out ECSEntity ent))
                    {
                        results.Add(ent);
                    }
                }
            }

            return results;
        }
    }

    /// <summary>Монтаж индекса на мир (паттерн SerializationBootstrap фазы 4).</summary>
    public static class QueryBootstrap
    {
        public static EntityQueryIndex Attach(ECSWorld world)
        {
            var index = new EntityQueryIndex(world);
            world.Query = index;
            world.entityManager.QueryIndex = index;
            return index;
        }
    }
}
