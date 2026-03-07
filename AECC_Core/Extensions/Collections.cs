using AECC.Core;
using AECC.Core.Logging;
using AECC.Extensions.ThreadingSync;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AECC.Extensions
{

    public static class InterlockedCollection
    {
        //private static HashSet <object> lockDB = new HashSet <object> ();
        #region dictionary
        public static bool AddI<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key, TValue value, object externalLockerObject = null)
        {
            if(externalLockerObject == null)
            {
                if(value is IECSObject)
                {
                    externalLockerObject = (value as IECSObject).SerialLocker;
                }
            }
            lock (externalLockerObject)
            {
                lock (dictionary)
                {
                    if (!dictionary.ContainsKey(key))
                        dictionary[key] = value;
                    else
                        return false;
                }
            }
            return true;
        }

        public static TValue GetI<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key, object externalLockerObject)
        {
            lock (externalLockerObject)
            {
                lock (dictionary)
                {
                    if (dictionary.ContainsKey(key))
                        return dictionary[key];
                    else
                        return default(TValue);
                }
            }
        }

        public static bool TryGetValueI<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key, out TValue value, object externalLockerObject)
        {
            lock (externalLockerObject)
            {
                lock (dictionary)
                {
                    if (dictionary.ContainsKey(key))
                        value = dictionary[key];
                    else
                    {
                        value = default(TValue);
                        return false;
                    }
                }
            }
            return true;
        }

        public static void SetI<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key, TValue value, object externalLockerObject = null)
        {
            if (externalLockerObject == null)
            {
                if (value is IECSObject)
                {
                    externalLockerObject = (value as IECSObject).SerialLocker;
                }
            }
            lock (externalLockerObject)
            {
                lock (dictionary)
                {
                    dictionary[key]=value;
                }
            }
        }

        public static bool RemoveI<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key, object externalLockerObject)
        {
            lock (externalLockerObject)
            {
                lock (dictionary)
                {
                    return dictionary.Remove(key);
                }
            }
        }

        public static bool ClearI<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, object externalLockerObject)
        {
            lock (externalLockerObject)
            {
                lock (dictionary)
                {
                    dictionary.Clear();
                }
            }
            return true;
        }

        public static IDictionary<TKey, TValue> SnapshotI<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, object externalLockerObject)
        {
            lock (externalLockerObject)
            {
                lock (dictionary)
                {
                    return new Dictionary<TKey, TValue>(dictionary);
                }
            }
        }

        public static bool ContainsKeyI<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key, object externalLockerObject)
        {
            lock(externalLockerObject)
            {
                lock (dictionary)
                {
                    return dictionary.ContainsKey(key);
                }
            }
        }
        #endregion

        #region list
        public static void AddI<TValue>(this ICollection<TValue> list, TValue value, object externalLockerObject = null)
        {
            if (externalLockerObject == null)
            {
                if (value is IECSObject)
                {
                    externalLockerObject = (value as IECSObject).SerialLocker;
                }
            }
            lock (externalLockerObject)
            {
                lock (list)
                {
                    list.Add(value);
                }
            }
        }

        public static void ClearI<TValue>(this ICollection<TValue> list, object externalLockerObject)
        {
            lock (externalLockerObject)
            {
                lock (list)
                {
                    list.Clear();
                }
            }
        }

        public static List<TValue> SnapshotI<TValue>(this ICollection<TValue> list, object externalLockerObject)
        {
            lock (externalLockerObject)
            {
                lock (list)
                {
                    return new List<TValue>(list);
                }
            }
        }

        public static bool RemoveI<TValue>(this ICollection<TValue> list, TValue value, object externalLockerObject)
        {
            lock (externalLockerObject)
            {
                lock (list)
                {
                    return list.Remove(value);
                }
            }
        }

        public static void InsertI<TValue>(this IList<TValue> list, int index, TValue insValue, object externalLockerObject)
        {
            lock (externalLockerObject)
            {
                lock (list)
                {
                    list.Insert(index, insValue);
                }
            }
        }

        public static void RemoveAtI<TValue>(this IList<TValue> list, int index, object externalLockerObject)
        {
            lock (externalLockerObject)
            {
                lock (list)
                {
                    list.RemoveAt(index);
                }
            }
        }

        public static bool ContainsI<TValue>(this ICollection<TValue> list, TValue value, object externalLockerObject)
        {
            lock (externalLockerObject)
            {
                lock (list)
                {
                    return list.Contains( value);
                }
            }
        }

        public static void SetI<TValue>(this IList<TValue> list, int index, TValue newValue, object externalLockerObject)
        {
            lock (externalLockerObject)
            {
                lock (list)
                {
                    list[index] = newValue;
                }
            }
        }


        public static void AddRange<T>(this ICollection<T> collection, IEnumerable<T> items)
        {

            if (collection is List<T> list)
            {

                list.AddRange(items);

            }
            else
            {

                foreach (T item in items)
                    collection.Add(item);

            }

        }
        public static void SetI<TValue>(this ICollection<TValue> list, int index, TValue newValue, object externalLockerObject)
        {
            lock (externalLockerObject)
            {
                lock (list)
                {
                    if (index < 0 || index > list.Count)
                        throw new ArgumentOutOfRangeException(nameof(index), "Index was out of range. Must be non-negative and less than the size of the collection.");

                    if (list is IList<TValue> ilist)
                    {
                        ilist.Insert(index, newValue);
                    }
                    else
                    {
                        List<TValue> temp = new List<TValue>(list);

                        list.Clear();

                        list.AddRange(temp.Take(index));
                        list.Add(newValue);
                        list.AddRange(temp.Skip(index));
                    }
                }
            }
        }

        public static TValue GetI<TValue>(this IList<TValue> list, int index, object externalLockerObject)
        {
            lock (externalLockerObject)
            {
                lock (list)
                {
                    return list[index];
                }
            }
        }

        public static TValue GetI<TValue>(this ICollection<TValue> list, int index, object externalLockerObject)
        {
            lock (externalLockerObject)
            {
                lock (list)
                {
                    return list.ElementAt(index);
                }
            }
        }
        #endregion

        public static HashSet<TValue> SnapshotI<TValue>(this HashSet<TValue> list, object externalLockerObject)
        {
            lock (externalLockerObject)
            {
                lock (list)
                {
                    return new HashSet<TValue>(list);
                }
            }
        }
    }

    
    public class DescComparer<T> : IComparer<T>
    {
        public int Compare(T x, T y)
        {
            if (x == null) return -1;
            if (y == null) return 1;
            return Comparer<T>.Default.Compare(y, x);
        }
    }
}
