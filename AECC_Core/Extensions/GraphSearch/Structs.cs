using DefaultEcs;
using BitCollections.Special;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

// Структура-компонент, хранящаяся плотно в памяти (Data Locality)
public struct AdjacencyList
{
    public int[] Neighbors;
}