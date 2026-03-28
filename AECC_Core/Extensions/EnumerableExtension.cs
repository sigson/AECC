using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AECC.Collections;
using AECC.Core.Logging;
using AECC.Extensions;

//namespace AECC.Extensions
//{
public class Collections
{
    public static readonly object[] EmptyArray = new object[0];

    public static List<T> AsList<T>(params T[] values) =>
        new List<T>(values);

    //public static IList<T> EmptyList<T>() =>
    //    EmptyList<T>.Instance;

    public static void ForEach<T>(IEnumerable<T> coll, Action<T> action)
    {
        Enumerator<T> enumerator = GetEnumerator<T>(coll);
        while (enumerator.MoveNext())
        {
            action(enumerator.Current);
        }
    }

    public static IEnumerable<TSource> IntersectEnum<TSource>(HashSet<TSource> first, IEnumerable<TSource> second)
    {
        foreach (TSource element in first)
        {
            if (second.Contains(element)) yield return element;
        }
    }

    public static bool FirstIntersect<TSource, TNull>(ConcurrentDictionaryEx<TSource, TNull> first, IEnumerable<TSource> second)
    {
        foreach (KeyValuePair<TSource, TNull> element in first)
        {
            if (second.Contains(element.Key)) return true;
        }
        return false;
    }

    public static IEnumerable<TSource> IntersectEnum<TSource, TNull>(ConcurrentDictionaryEx<TSource, TNull> first, IEnumerable<TSource> second)
    {
        foreach (KeyValuePair<TSource, TNull> element in first)
        {
            if (second.Contains(element.Key)) yield return element.Key;
        }
    }

    public static bool FirstIntersect<TSource, TNull>(IDictionary<TSource, TNull> first, IEnumerable<TSource> second)
    {
        foreach (KeyValuePair<TSource, TNull> element in first)
        {
            if (second.Contains(element.Key)) return true;
        }
        return false;
    }

    public static IEnumerable<TSource> IntersectEnum<TSource, TNull>(IDictionary<TSource, TNull> first, IEnumerable<TSource> second)
    {
        foreach (KeyValuePair<TSource, TNull> element in first)
        {
            if (second.Contains(element.Key)) yield return element.Key;
        }
    }

    public static bool FirstIntersect<TSource>(HashSet<TSource> first, IEnumerable<TSource> second)
    {
        foreach (TSource element in first)
        {
            if (second.Contains(element)) return true;
        }
        return false;
    }

    public static Enumerator<T> GetEnumerator<T>(IEnumerable<T> collection) =>
        new Enumerator<T>(collection);

    public static T GetOnlyElement<T>(ICollection<T> coll)
    {
        if (coll.Count != 1)
        {
            throw new InvalidOperationException("Count: " + coll.Count);
        }
        List<T> list = coll as List<T>;
        if (list != null)
        {
            return list[0];
        }
        HashSet<T> set = coll as HashSet<T>;
        if (set != null)
        {
            HashSet<T>.Enumerator enumerator = set.GetEnumerator();
            enumerator.MoveNext();
            return enumerator.Current;
        }
        IEnumerator<T> enumerator2 = coll.GetEnumerator();
        enumerator2.MoveNext();
        return enumerator2.Current;
    }

    //public static IList<T> SingletonList<T>(T value) =>
    //    new SingletonList<T>(value);


    public struct Enumerator<T>
    {
        private IEnumerable<T> collection;
        private HashSet<T>.Enumerator hashSetEnumerator;
        private List<T>.Enumerator ListEnumerator;
        private IEnumerator<T> enumerator;
        public Enumerator(IEnumerable<T> collection)
        {
            this.collection = collection;
            this.enumerator = null;
            List<T> list = collection as List<T>;
            if (list != null)
            {
                this.ListEnumerator = list.GetEnumerator();
                HashSet<T>.Enumerator enumerator = new HashSet<T>.Enumerator();
                this.hashSetEnumerator = enumerator;
            }
            else
            {
                HashSet<T> set = collection as HashSet<T>;
                if (set != null)
                {
                    this.hashSetEnumerator = set.GetEnumerator();
                    List<T>.Enumerator enumerator2 = new List<T>.Enumerator();
                    this.ListEnumerator = enumerator2;
                }
                else
                {
                    HashSet<T>.Enumerator enumerator3 = new HashSet<T>.Enumerator();
                    this.hashSetEnumerator = enumerator3;
                    List<T>.Enumerator enumerator4 = new List<T>.Enumerator();
                    this.ListEnumerator = enumerator4;
                    this.enumerator = collection.GetEnumerator();
                }
            }
        }

        public bool MoveNext() =>
            !(this.collection is List<T>) ? (!(this.collection is HashSet<T>) ? this.enumerator.MoveNext() : this.hashSetEnumerator.MoveNext()) : this.ListEnumerator.MoveNext();

        public T Current =>
            !(this.collection is List<T>) ? (!(this.collection is HashSet<T>) ? this.enumerator.Current : this.hashSetEnumerator.Current) : this.ListEnumerator.Current;
    }
}
public static class EnumerableExtension
{
    public static string ToStringListing<T>(this IEnumerable<T> source, string delimiter = ", ")
    {
        if (source == null)
            throw new ArgumentNullException(nameof(source));

        if (delimiter == null)
            throw new ArgumentNullException(nameof(delimiter));

        return string.Join(delimiter, source.Select(x => x?.ToString() ?? "null"));
    }

    public static string ToStringListing<T>(this IEnumerable<T> source, Func<T, string> toStringFunc, string delimiter = ", ")
    {
        if (source == null)
            throw new ArgumentNullException(nameof(source));

        if (delimiter == null)
            throw new ArgumentNullException(nameof(delimiter));

        return string.Join(delimiter, source.Select(x => toStringFunc(x) ?? "null"));
    }

    private static readonly Random rand1 = new Random();
    public static IEnumerable<T> TakeRandom<T>(this IEnumerable<T> source, int count = -1)
    {
        if (count == -1)
        {
            count = rand1.Next(1, source.Count());
        }
        if (source == null)
            throw new ArgumentNullException(nameof(source));
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Count cannot be negative");

        var list = source.ToList();
        int availableCount = list.Count;
        if (availableCount == 0)
            return Enumerable.Empty<T>();

        int actualCount = Math.Min(count, availableCount);
        return list.OrderBy(_ => rand1.Next()).Take(actualCount);
    }

    [ThreadStatic]
    private static Random _random;
    private static Random Rnd => _random ?? (_random = new Random(Guid.NewGuid().GetHashCode()));

    public static IEnumerable<T> TakeRandomOptimized<T>(this IEnumerable<T> source, int count = -1)
    {
        if (source == null)
            throw new ArgumentNullException(nameof(source));

        if (count == -1)
        {
            // Пытаемся узнать размер без перебора коллекции (замена TryGetNonEnumeratedCount)
            if (TryGetCount(source, out int totalCount))
            {
                if (totalCount == 0) return Enumerable.Empty<T>();
                count = Rnd.Next(1, totalCount + 1); 
            }
            else
            {
                // Если размер неизвестен (например, это LINQ-запрос типа Where), 
                // придется загрузить в память, чтобы узнать из какого числа брать Random.
                var list = source.ToList();
                if (list.Count == 0) return Enumerable.Empty<T>();
                
                count = Rnd.Next(1, list.Count + 1);
                return TakeRandomIterator(list, count);
            }
        }

        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Count cannot be negative");

        if (count == 0)
            return Enumerable.Empty<T>();

        return TakeRandomIterator(source, count);
    }

    // Приватный хелпер для определения размера коллекции (O(1) по времени)
    private static bool TryGetCount<T>(IEnumerable<T> source, out int count)
    {
        if (source is ICollection<T> collection) { count = collection.Count; return true; }
        if (source is IReadOnlyCollection<T> readOnlyCollection) { count = readOnlyCollection.Count; return true; }
        if (source is System.Collections.ICollection nonGenericCollection) { count = nonGenericCollection.Count; return true; }
        
        count = 0;
        return false;
    }

    private static IEnumerable<T> TakeRandomIterator<T>(IEnumerable<T> source, int count)
    {
        T[] reservoir = new T[count];
        int i = 0;

        foreach (var item in source)
        {
            if (i < count)
            {
                reservoir[i] = item;
            }
            else
            {
                int j = Rnd.Next(i + 1);
                if (j < count)
                {
                    reservoir[j] = item;
                }
            }
            i++;
        }

        int actualCount = Math.Min(count, i);

        // Перемешиваем результат, используя классический обмен через переменную
        // (совместимо даже с очень старым C#, где нет синтаксиса кортежей)
        for (int k = actualCount - 1; k > 0; k--)
        {
            int swapIndex = Rnd.Next(k + 1);
            T temp = reservoir[k];
            reservoir[k] = reservoir[swapIndex];
            reservoir[swapIndex] = temp;
        }

        for (int k = 0; k < actualCount; k++)
        {
            yield return reservoir[k];
        }
    }

    public static void ForEach<TKey>(this IEnumerable<TKey> enumerable, Action<TKey> compute)
    {
        Collections.ForEach<TKey>(enumerable, compute);
    }

    public static void ForEach<TKey>(this IList<TKey> list, Action<TKey> compute)
    {
        if (list == null)
            return;
        int count = list.Count;
        for (int i = 0; i < count; i++)
        {
            compute(list[i]);
        }
    }

    public static void Clear<T>(this System.Collections.Concurrent.ConcurrentQueue<T> queue)
    {
        if (queue == null)
            return;

        // Извлекаем элементы, пока очередь не станет пустой
        while (queue.TryDequeue(out _))
        {
            // Ничего не делаем, просто выбрасываем элемент
        }
    }

    public static IEnumerable<TResult> Cast<TSource, TResult>(this IEnumerable<TSource> source, Func<TSource, TResult> converter)
    {
        if (source == null) return Enumerable.Empty<TResult>();
        
        var result = new List<TResult>();
        
        foreach (var item in source)
        {
            try
            {
                result.Add(converter(item));
            }
            catch(Exception ex)
            {
                NLogger.LogError(ex);
            }
        }
        
        return result;
    }

    public static void ForEachWithIndex<TKey>(this IEnumerable<TKey> list, Action<int> compute)
    {
        if (list == null)
            return;
        var rlist = list.ToList();
        int count = rlist.Count;
        for (int i = 0; i < count; i++)
        {
            compute(i);
        }
    }

    public static IEnumerable<TResult> CastSafe<TResult>(this IEnumerable<object> source)
    {
        // Проверка на null, чтобы метод вел себя как стандартные LINQ-методы
        if (source == null) 
            throw new ArgumentNullException(nameof(source));

        foreach (var item in source)
        {
            // Безопасное приведение: если item можно привести к TResult, 
            // он помещается в переменную result и возвращается
            if (item is TResult result)
            {
                yield return result;
            }
        }
    }

    public static void ForEachWithIndex<TKey>(this IEnumerable<TKey> list, Action<TKey, int> compute)
    {
        if (list == null)
            return;
        var rlist = list.ToList();
        int count = rlist.Count;
        for (int i = 0; i < count; i++)
        {
            compute(rlist[i], i);
        }
    }

    public static T Fill<T>(this T fillObject, System.Collections.IEnumerable fillInput, Action<T, object> fillAction) where T : System.Collections.IEnumerable
    {
        foreach (var fillObj in fillInput)
            fillAction(fillObject, fillObj);
        return fillObject;
    }

    public static bool Remove<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key, out TValue value)
    {
        if (dictionary.TryGetValue(key, out value))
            return dictionary.Remove(key);
        return false;
    }

    public static bool TryAdd<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key, TValue value)
    {
        if (dictionary.TryGetValue(key, out _))
            return false;
        else
        {
            try
            {
                dictionary.Add(key, value);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static void ForEach(this Array array, Action<Array, int[]> action)
    {
        if (array.LongLength == 0) return;
        ArrayTraverse walker = new ArrayTraverse(array);
        do action(array, walker.Position);
        while (walker.Step());
    }
    public static T[] SubArray<T>(this T[] data, int index, int length)
    {
        T[] result = new T[length];
        Array.Copy(data, index, result, 0, length);
        return result;
    }
    /// <summary>
    /// Выбирает элементы с коэффициентами, наиболее близкими к 0.0
    /// </summary>
    public static IEnumerable<T> TakeLowestByCoefficient<T>(
        this IEnumerable<T> source,
        Func<T, double> coefficientSelector,
        int count)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (coefficientSelector == null) throw new ArgumentNullException(nameof(coefficientSelector));

        return source
            .OrderBy(coefficientSelector)
            .Take(count);
    }

    /// <summary>
    /// Выбирает элементы с коэффициентами, наиболее близкими к 1.0
    /// </summary>
    public static IEnumerable<T> TakeHighestByCoefficient<T>(
        this IEnumerable<T> source,
        Func<T, double> coefficientSelector,
        int count)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (coefficientSelector == null) throw new ArgumentNullException(nameof(coefficientSelector));

        return source
            .OrderByDescending(coefficientSelector)
            .Take(count);
    }

    /// <summary>
    /// Выбирает элементы с обоих концов: близкие к 0 и близкие к 1.0
    /// </summary>
    public static (IEnumerable<T> Lowest, IEnumerable<T> Highest) TakeExtremesByCoefficient<T>(
        this IEnumerable<T> source,
        Func<T, double> coefficientSelector,
        int countEach)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (coefficientSelector == null) throw new ArgumentNullException(nameof(coefficientSelector));

        var ordered = source
            .Select(item => (Item: item, Coefficient: coefficientSelector(item)))
            .OrderBy(x => x.Coefficient)
            .ToList();

        var lowest = ordered.Take(countEach).Select(x => x.Item);
        var highest = ordered.TakeLast(countEach).Select(x => x.Item);

        return (lowest, highest);
    }

    public static IEnumerable<T> TakeLast<T>(this IEnumerable<T> source, int count)
    {
        if (source == null)
            throw new ArgumentNullException(nameof(source));

        if (count <= 0)
            yield break;

        var queue = new Queue<T>(count + 1);

        foreach (var item in source)
        {
            queue.Enqueue(item);
            if (queue.Count > count)
                queue.Dequeue();
        }

        foreach (var item in queue)
            yield return item;
    }

    // /// <summary>
    // /// Оптимизированная версия для больших коллекций с использованием приоритетной очереди
    // /// </summary>
    // public static IEnumerable<T> TakeLowestByCoefficientOptimized<T>(
    //     this IEnumerable<T> source,
    //     Func<T, double> coefficientSelector,
    //     int count)
    // {
    //     if (source == null) throw new ArgumentNullException(nameof(source));
    //     if (keySelector == null) throw new ArgumentNullException(nameof(keySelector));
    //     if (valueSelector == null) throw new ArgumentNullException(nameof(valueSelector));

    //     if (count == 0)
    //         return [];

    //     // Max-heap для хранения N минимальных элементов
    //     var heap = new PriorityQueue<T, double>();

    //     foreach (var item in source)
    //     {
    //         var coefficient = coefficientSelector(item);

    //         if (heap.Count < count)
    //         {
    //             heap.Enqueue(item, -coefficient); // отрицательный приоритет для max-heap
    //         }
    //         else if (heap.TryPeek(out _, out var maxPriority) && coefficient < -maxPriority)
    //         {
    //             heap.DequeueEnqueue(item, -coefficient);
    //         }
    //     }

    //     // Извлекаем и сортируем результат
    //     var result = new List<T>(heap.Count);
    //     while (heap.Count > 0)
    //     {
    //         result.Add(heap.Dequeue());
    //     }
    //     result.Reverse();
    //     return result;
    // }

    // /// <summary>
    // /// Оптимизированная версия для выбора максимальных элементов
    // /// </summary>
    // public static IEnumerable<T> TakeHighestByCoefficientOptimized<T>(
    //     this IEnumerable<T> source,
    //     Func<T, double> coefficientSelector,
    //     int count)
    // {
    //     if (source == null) throw new ArgumentNullException(nameof(source));
    //     if (keySelector == null) throw new ArgumentNullException(nameof(keySelector));
    //     if (valueSelector == null) throw new ArgumentNullException(nameof(valueSelector));

    //     if (count == 0)
    //         return [];

    //     // Min-heap для хранения N максимальных элементов
    //     var heap = new PriorityQueue<T, double>();

    //     foreach (var item in source)
    //     {
    //         var coefficient = coefficientSelector(item);

    //         if (heap.Count < count)
    //         {
    //             heap.Enqueue(item, coefficient);
    //         }
    //         else if (heap.TryPeek(out _, out var minPriority) && coefficient > minPriority)
    //         {
    //             heap.DequeueEnqueue(item, coefficient);
    //         }
    //     }

    //     // Извлекаем и сортируем результат (от большего к меньшему)
    //     var result = new List<T>(heap.Count);
    //     while (heap.Count > 0)
    //     {
    //         result.Add(heap.Dequeue());
    //     }
    //     result.Reverse();
    //     return result;
    // }
}

public class ArrayTraverse
{
    public int[] Position;
    private int[] maxLengths;

    public ArrayTraverse(Array array)
    {
        maxLengths = new int[array.Rank];
        for (int i = 0; i < array.Rank; ++i)
        {
            maxLengths[i] = array.GetLength(i) - 1;
        }
        Position = new int[array.Rank];
    }

    public bool Step()
    {
        for (int i = 0; i < Position.Length; ++i)
        {
            if (Position[i] < maxLengths[i])
            {
                Position[i]++;
                for (int j = 0; j < i; j++)
                {
                    Position[j] = 0;
                }
                return true;
            }
        }
        return false;
    }
}
//}
