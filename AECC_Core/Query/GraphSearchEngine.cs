using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BitCollections.Special;

namespace AECC.Query
{
    /// <summary>
    /// ФАЗА 5: движок метрик/пересечений — перенос из Core (тела дословно), два изменения
    /// по мандатам ТЗ 4.6:
    ///  1. ДЕФЕКТ 6.6 ЗАКРЫТ: метрики — ЧИСЛОВЫЕ ключи (long = type-uid компонента) вместо
    ///     строк $"Comp:{id}" — снят аллокационный налог (конкатенация + хеш строки) на
    ///     каждом Add/Remove компонента и каждом поиске.
    ///  2. DefaultEcs — за IGraphNodeStore (герметизация); собственный конкурентный режим:
    ///     метрики — MVCC (MetricIndex: volatile snapshot + CAS), топология — лок внутри
    ///     стора; внешнего мирового лока больше нет (№18).
    /// Инварианты 9(з) — пересечение по кардинальности, ранний выход, AND NOT,
    /// Flush-барьер перед точным поиском — сохранены дословно.
    /// </summary>
    internal sealed class GraphSearchEngine
    {
        private readonly IGraphNodeStore _nodes;

        // Глобальный словарь метрик: числовой ключ (type-uid) -> плотный metric-id.
        private readonly ConcurrentDictionary<long, int> _metricDictionary;
        private int _metricIdCounter = -1;

        // Массив индексов. Индекс массива = Metric_ID
        private MetricIndex[] _metricIndices;
        private readonly object _resizeLock = new object();

        public GraphSearchEngine(IGraphNodeStore nodes)
        {
            _nodes = nodes;
            _metricDictionary = new ConcurrentDictionary<long, int>();
            _metricIndices = new MetricIndex[1024];
        }

        public void SetNeighbors(int nodeId, int[] neighborIds)
        {
            _nodes.SetNeighbors(nodeId, neighborIds);
        }

        // Транслятор: изоляция и атомарный счётчик (тело дословно, ключ — long)
        private int GetOrAddMetricId(long metric)
        {
            if (!_metricDictionary.TryGetValue(metric, out int id))
            {
                // Не публикуем в словарь до завершения всех проверок!
                int tempId = Interlocked.Increment(ref _metricIdCounter);

                // 1. Сначала расширяем массив (при необходимости)
                if (tempId >= _metricIndices.Length)
                {
                    lock (_resizeLock)
                    {
                        if (tempId >= _metricIndices.Length)
                        {
                            Array.Resize(ref _metricIndices, _metricIndices.Length * 2);
                        }
                    }
                }

                // 2. Инициализируем сам индекс
                Interlocked.CompareExchange(ref _metricIndices[tempId], new MetricIndex(), null);

                // 3. Теперь безопасно публиковать в словарь!
                _metricDictionary.TryAdd(metric, tempId);
                return tempId;
            }
            return id;
        }

        public void AddMetricToNode(int nodeId, long metric)
        {
            int metricId = GetOrAddMetricId(metric);
            _metricIndices[metricId].Add(nodeId);
        }

        // Планировщик селективности и выполнение запроса (тело дословно; ключи — long)
        public IEnumerable<int> Search(int sourceNodeId, long[] withMetrics, long[] withoutMetrics)
        {
            // 1. O(1) снимок соседей из герметизированного стора
            var neighbors = _nodes.GetNeighbors(sourceNodeId);
            if (neighbors == null || neighbors.Length == 0)
                return Enumerable.Empty<int>();

            var resultBitmap = RoaringBitmap.Create(neighbors);

            // 2. Обработка позитивных условий (AND)
            if (withMetrics != null && withMetrics.Length > 0)
            {
                var withSnapshots = new List<RoaringBitmap>(withMetrics.Length);
                foreach (var metric in withMetrics)
                {
                    if (_metricDictionary.TryGetValue(metric, out int mId) && _metricIndices[mId] != null)
                    {
                        _metricIndices[mId].Flush(); // Гарантируем видимость последних изменений
                        withSnapshots.Add(_metricIndices[mId].Snapshot);
                    }
                    else
                    {
                        // Запрашиваемая метрика не существует в системе -> пересечение пустое
                        return Enumerable.Empty<int>();
                    }
                }

                // ПЛАНИРОВЩИК: сортируем битовые карты по Cardinality (от меньшего к большему)
                withSnapshots.Sort((a, b) => a.Cardinality.CompareTo(b.Cardinality));

                foreach (var snapshot in withSnapshots)
                {
                    resultBitmap &= snapshot;
                    if (resultBitmap.Cardinality == 0) return Enumerable.Empty<int>(); // Ранний выход
                }
            }

            // 3. Обработка негативных условий (AND NOT)
            if (withoutMetrics != null && withoutMetrics.Length > 0)
            {
                foreach (var metric in withoutMetrics)
                {
                    if (_metricDictionary.TryGetValue(metric, out int mId) && _metricIndices[mId] != null)
                    {
                        _metricIndices[mId].Flush();
                        resultBitmap = RoaringBitmap.AndNot(resultBitmap, _metricIndices[mId].Snapshot);
                        if (resultBitmap.Cardinality == 0) break;
                    }
                }
            }

            return resultBitmap;
        }

        public void RemoveMetricFromNode(int nodeId, long metric)
        {
            if (_metricDictionary.TryGetValue(metric, out int metricId) && _metricIndices[metricId] != null)
            {
                _metricIndices[metricId].Remove(nodeId);
            }
        }
    }
}
