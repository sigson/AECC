using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BitCollections.Special;
using DefaultEcs;

public class GraphSearchEngine
{
    private readonly World _world;
    private readonly Entity[] _nodes; // O(1) доступ к Entity по Object_ID

    // Глобальный словарь для строковых метрик
    private readonly ConcurrentDictionary<string, int> _metricDictionary;
    private int _metricIdCounter = -1;

    // Массив индексов. Индекс массива = Metric_ID
    private MetricIndex[] _metricIndices;
    private readonly object _resizeLock = new object();

    public GraphSearchEngine(int maxGraphNodes)
    {
        _world = new World();
        _nodes = new Entity[maxGraphNodes];
        _metricDictionary = new ConcurrentDictionary<string, int>();
        _metricIndices = new MetricIndex[1024];

        // Инициализация графа
        for (int i = 0; i < maxGraphNodes; i++)
        {
            _nodes[i] = _world.CreateEntity();
        }
    }

    // Загрузка связей узла
    public void SetNeighbors(int nodeId, int[] neighborIds)
    {
        _nodes[nodeId].Set(new AdjacencyList { Neighbors = neighborIds });
    }

    // Транслятор: Изоляция и атомарный счетчик (Шаг 1)
    private int GetOrAddMetricId(string metric)
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

    // Входная точка асинхронного добавления метрики (Жизненный цикл мутации)
    public void AddMetricToNode(int nodeId, string metric)
    {
        int metricId = GetOrAddMetricId(metric);
        _metricIndices[metricId].Add(nodeId);
    }

    // Планировщик селективности и выполнение запроса (Жизненный цикл поиска)
    public IEnumerable<int> Search(int sourceNodeId, string[] withMetrics, string[] withoutMetrics)
    {
        // 1. Быстро получаем O(1) кэш-дружелюбный массив соседей
        ref var adjacency = ref _nodes[sourceNodeId].Get<AdjacencyList>();
        if (adjacency.Neighbors == null || adjacency.Neighbors.Length == 0) 
            return Enumerable.Empty<int>();

        var resultBitmap = RoaringBitmap.Create(adjacency.Neighbors);

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

            // ПЛАНИРОВЩИК: Сортируем битовые карты по Cardinality (от меньшего к большему)
            withSnapshots.Sort((a, b) => a.Cardinality.CompareTo(b.Cardinality));

            // Аппаратное SIMD-пересечение
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

        // RoaringBitmap реализует IEnumerable<int>, можно возвращать напрямую
        return resultBitmap;
    }

    public void RemoveMetricFromNode(int nodeId, string metric)
    {
        if (_metricDictionary.TryGetValue(metric, out int metricId) && _metricIndices[metricId] != null)
        {
            _metricIndices[metricId].Remove(nodeId);
        }
    }
}