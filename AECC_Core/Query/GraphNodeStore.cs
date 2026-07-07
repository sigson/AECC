using System;
using System.Collections.Generic;
using DefaultEcs;

namespace AECC.Query
{
    /// <summary>
    /// ФАЗА 5 (ТЗ 4.6, решение 0.4.3): ГЕРМЕТИЗАЦИЯ DefaultEcs — движок остаётся штатным
    /// (осознанный выбор: пулы и запросная машинерия окупаются при росте индекса), но
    /// становится ПРИВАТНОЙ деталью Query за этим internal-интерфейсом: ни один другой
    /// модуль о нём не знает, NuGet-зависимость не расползается по графу сборок
    /// (приёмка фазы: PackageReference DefaultEcs — только у AECC.Query).
    /// Замена на плоский массив — только отдельный эксперимент с бенчмарк-гейтом
    /// «не хуже DefaultEcs по Set/Get/полному циклу Search» (ТЗ 4.6).
    /// </summary>
    internal interface IGraphNodeStore
    {
        /// <summary>Записать массив соседей узла (узел создаётся лениво при первом касании).</summary>
        void SetNeighbors(int nodeId, int[] neighborIds);

        /// <summary>Прочитать массив соседей (null/пусто = соседей нет или узла не было).
        /// Возвращённый массив ИММУТАБЕЛЕН ПО ДИСЦИПЛИНЕ: писатели заменяют его целиком
        /// (SetNeighbors), никогда не мутируют на месте — читатель работает со снимком.</summary>
        int[] GetNeighbors(int nodeId);
    }

    /// <summary>
    /// Реализация поверх DefaultEcs.World. ЧЕСТНАЯ ГРАНИЦА КОНКУРЕНТНОСТИ (закрывает №18):
    /// DefaultEcs не потокобезопасен ни для Set, ни для Set-против-чтения — прежде это
    /// прикрывал МИРОВОЙ _graphEngineLock менеджера, сериализовавший в том числе весь Search.
    /// Теперь лок — ВНУТРИ границы и держится только на время Set/Get (микросекунды);
    /// пересечение битмапов и материализация результата идут БЕЗ него (метрики — MVCC).
    ///
    /// ДЕФЕКТ 6.8 ЗАКРЫТ: узлы создаются ЛЕНИВО при первом касании (бывший конструктор
    /// жадно создавал maxGraphNodes = 1 000 000 Entity на старте каждого мира независимо
    /// от населённости).
    /// </summary>
    internal sealed class DefaultEcsGraphNodeStore : IGraphNodeStore
    {
        private readonly World _world = new World();
        private readonly Dictionary<int, Entity> _nodes = new Dictionary<int, Entity>();
        private readonly object _sync = new object();

        public void SetNeighbors(int nodeId, int[] neighborIds)
        {
            lock (_sync)
            {
                Entity node;
                if (!_nodes.TryGetValue(nodeId, out node))
                {
                    node = _world.CreateEntity(); // лениво (6.8)
                    _nodes[nodeId] = node;
                }
                node.Set(new AdjacencyList { Neighbors = neighborIds });
            }
        }

        public int[] GetNeighbors(int nodeId)
        {
            lock (_sync)
            {
                Entity node;
                if (!_nodes.TryGetValue(nodeId, out node))
                    return null;
                return node.Get<AdjacencyList>().Neighbors; // ссылка на иммутабельный снимок
            }
        }
    }
}
