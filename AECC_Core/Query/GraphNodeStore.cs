using System;
using System.Collections.Generic;
using DefaultEcs;

namespace AECC.Query
{
    /// <summary>
    /// Изолирует DefaultEcs как приватную деталь реализации Query за этим internal-интерфейсом:
    /// ни один другой модуль о нём не знает, и NuGet-зависимость на DefaultEcs не расползается
    /// по графу сборок (PackageReference DefaultEcs — только у AECC.Query).
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
    /// Реализация поверх DefaultEcs.World. DefaultEcs не потокобезопасен ни для Set, ни для
    /// Set-против-чтения, поэтому доступ к _world/_nodes защищён локом, который держится
    /// только на время Set/Get (микросекунды); пересечение битмапов и материализация
    /// результата поиска идут без него (метрики — MVCC).
    ///
    /// Узлы создаются лениво при первом касании (SetNeighbors/GetNeighbors), а не заранее
    /// для всего диапазона возможных id.
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
                    node = _world.CreateEntity();
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
