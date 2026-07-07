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
    /// ФАЗА 5 (ТЗ 4.6, идея 1.10): индекс запросов мира. Первый шаг слияния «двух индексов»:
    /// сюда ДОСЛОВНО перенесена вся графовая половина (бывшая «ИНФРАСТРУКТУРА ГРАФА»
    /// ECSEntityManager: аллокатор graph-id с реюзом через очередь, маппинги
    /// instanceId↔graphId, транзитивные множества потомков «плати при записи — читай
    /// мгновенно» (O(глубина) на добавление — осознанная схема, документирована), обходы
    /// предков в ОДНОМ месте, метрики компонентов). Вторая половина (обратный индекс
    /// ComponentOwners из ContractsManager) вливается на следующем шаге фазы — она
    /// сплетена с реакциями контрактов (фаза 6), см. журнал.
    ///
    /// Подписка — Core-интерфейс IWorldQueryIndex (Runtime публикует события, Query
    /// имплементирует сверху). Индекс eventually-consistent (честная оговорка ТЗ 4.6);
    /// дисциплина goto-race-check'ов реакций контрактов — инвариант, не устраняемый подпиской.
    ///
    /// Конкурентный режим (закрывает №18): мировой _graphEngineLock снят; топология —
    /// лок внутри IGraphNodeStore, метрики — MVCC, маппинги — Concurrent*, множества
    /// потомков — прежние точечные lock(descendants).
    /// </summary>
    public sealed class EntityQueryIndex : IWorldQueryIndex
    {
        private readonly ECSWorld _world;
        private readonly GraphSearchEngine _engine;

        // --- ИНФРАСТРУКТУРА ГРАФА (дословно из ECSEntityManager) ---
        private const int ROOT_WORLD_NODE_ID = 0; // Корневой узел мира (глобальный контекст)
        private int _nextGraphId = 1;
        private readonly ConcurrentQueue<int> _freedGraphIds = new ConcurrentQueue<int>();

        // Маппинг long instanceId <-> int graphNodeId
        private readonly ConcurrentDictionary<long, int> _entityToGraphId = new ConcurrentDictionary<long, int>();
        private readonly ConcurrentDictionary<int, long> _graphIdToEntity = new ConcurrentDictionary<int, long>();

        // Хранение топологии: для каждой ноды храним HashSet её потомков (прямых и косвенных)
        private readonly ConcurrentDictionary<int, HashSet<int>> _nodeDescendants = new ConcurrentDictionary<int, HashSet<int>>();

        public EntityQueryIndex(ECSWorld world)
        {
            _world = world;
            _engine = new GraphSearchEngine(new DefaultEcsGraphNodeStore());

            // Инициализация Супер-Корня (дословно из конструктора менеджера)
            _nodeDescendants[ROOT_WORLD_NODE_ID] = new HashSet<int>();
            SyncNodeNeighbors(ROOT_WORLD_NODE_ID);

            // Предзасев обратного индекса (дословно из бывшего InitializeSystems).
            TypeRegistry.Global.RegisteredTypes.ForEach(x => _componentOwners[x.Value] = new HashSet<ECSEntity>());
        }

        // --- ОБХОДЫ (дословно; «вынести обход предков в одно место» — стратегия 3.6) ---

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

        // --- СОБЫТИЯ RUNTIME (IWorldQueryIndex) ---

        public void OnEntityAdded(ECSEntity entity)
        {
            // Дословно бывшая графовая часть AddNewEntityReaction.
            int graphId = AllocateGraphId(entity.instanceId);
            AddNodeToAncestors(entity, graphId);

            ICollection<ECSComponent> result = entity.entityComponents.Components;

            foreach (var comp in result)
            {
                _engine.AddMetricToNode(graphId, comp.GetId()); // числовой ключ (6.6)
                OwnersOf(comp.GetId()).AddI(entity, entity);    // обратный индекс: наличный состав
            }
        }

        public bool OnEntityRemoving(ECSEntity entity)
        {
            // Шаги 1–2 бывшего InternalGraphRemoval — строго ДО переподчинения детей
            // (снятие у предков идёт по ещё старой цепочке владельцев — анти-бомба 7.9).
            if (!_entityToGraphId.TryGetValue(entity.instanceId, out int deletedGraphId))
                return false; // гейт: нет узла — вся последовательность удаления пропускается (дословно прежний if)

            // 1. Убираем удаляемый узел из списков соседей его предков
            RemoveNodeFromAncestors(entity, deletedGraphId);

            // 2. Очищаем индекс от метрик компонентов этой сущности, перед тем как освободить её ID
            ICollection<ECSComponent> components = entity.entityComponents.Components;

            foreach (var comp in components)
            {
                _engine.RemoveMetricFromNode(deletedGraphId, comp.GetId());
                OwnersOf(comp.GetId()).RemoveI(entity, entity); // и из обратного индекса
            }

            return true;
        }

        public void OnEntityRemoved(ECSEntity entity)
        {
            // Шаг 4 бывшего InternalGraphRemoval — после переподчинения: освобождаем узел.
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

            // Обратный индекс 1.10(а) (перенос из ContractsManager, слияние — остаток
            // фазы 5). Goto-race-check-дисциплина — ДОСЛОВНО (индекс eventually-consistent;
            // корректность контрактов держат локи+перепроверка в TryExecuteContract).
            var hentities = OwnersOf(component.GetId()); // ФИКС №14: гард вместо голого TryGetValue
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

        // ─── обратный индекс componentTypeId → владельцы (1.10(а)) ───

        private readonly ConcurrentDictionary<long, HashSet<ECSEntity>> _componentOwners =
            new ConcurrentDictionary<long, HashSet<ECSEntity>>();

        public IDictionary<long, HashSet<ECSEntity>> ComponentOwnersView { get { return _componentOwners; } }

        /// <summary>ДЕФЕКТ №14 ЗАКРЫТ: прежняя реакция делала голый TryGetValue и падала
        /// NRE на компоненте, чей тип не был в реестре при InitializeSystems. Множество
        /// создаётся по требованию (предзасев из RegisteredTypes сохранён в конструкторе
        /// ниже — горячему пути не нужен GetOrAdd-аллокатор в 99% случаев).</summary>
        private HashSet<ECSEntity> OwnersOf(long componentTypeId)
        {
            return _componentOwners.GetOrAdd(componentTypeId, _ => new HashSet<ECSEntity>());
        }

        public HashSet<ECSEntity> FilterEntitiesForComponents(List<long> components)
        {
            // Тело ДОСЛОВНО из ContractsManager (пересечение от меньшей кардинальности,
            // ранний выход — идея 1.10(а)).
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

        // --- ПОИСК (тело дословно из ECSEntityManager.SearchGraph; метрики — числовые) ---

        public IEnumerable<ECSEntity> Search(
            ECSEntity parentScope = null,
            Type[] withComponentTypes = null,
            Type[] withoutComponentTypes = null)
        {
            // Редирект-голова (мир мог быть сквошнут) — дословно.
            var redirect = _world.entityManager.ResolveRedirect();
            if (redirect != null && redirect.QueryIndex != null)
                return redirect.QueryIndex.Search(parentScope, withComponentTypes, withoutComponentTypes);

            var withMetrics = new List<long>();
            var withoutMetrics = new List<long>();

            // Метрики — через ITypeRegistry (мандат ТЗ 4.6), числовой ключ = type-uid.
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
