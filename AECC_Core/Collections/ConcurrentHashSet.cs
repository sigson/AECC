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

namespace AECC.Collections
{
    public class ConcurrentHashSet<T> : ICollection<T>, IEnumerable<T>, System.Collections.IEnumerable, IReadOnlyCollection<T>, ISet<T>, System.Runtime.Serialization.IDeserializationCallback, System.Runtime.Serialization.ISerializable
    {
        private ConcurrentDictionary<T, int> storage = new ConcurrentDictionary<T, int>();

        public ConcurrentHashSet() { }

        public ConcurrentHashSet(ICollection<T> collection)
        {
            collection.ForEach(x => this.Add(x));
        }

        public int Count => storage.Count;

        public bool IsReadOnly => storage.Keys.IsReadOnly;

        public void Add(T item)
        {
            storage[item] = 0;
        }

        public void Clear()
        {
            storage.Clear();
        }

        public bool Contains(T item)
        {
            return storage.ContainsKey(item);
        }

        public void CopyTo(T[] array, int arrayIndex)
        {
            // Validate input parameters
            if (array == null)
                throw new ArgumentNullException(nameof(array));

            if (arrayIndex < 0 || arrayIndex > array.Length)
                throw new ArgumentOutOfRangeException(nameof(arrayIndex), "Index must be non-negative and within the bounds of the array.");

            if (array.Length - arrayIndex < Count)
                throw new ArgumentException("The array does not have enough space to copy all elements starting at the specified index.");

            // Copy elements to the array
            int index = arrayIndex;
            foreach (T item in this)
            {
                array[index] = item;
                index++;
            }
        }

        public void ExceptWith(IEnumerable<T> other)
        {
            throw new NotImplementedException();
        }

        public IEnumerator<T> GetEnumerator()
        {
            return storage.Keys.GetEnumerator();
        }

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            throw new NotImplementedException();
        }

        public void IntersectWith(IEnumerable<T> other)
        {
            throw new NotImplementedException();
        }

        public bool IsProperSubsetOf(IEnumerable<T> other)
        {
            throw new NotImplementedException();
        }

        public bool IsProperSupersetOf(IEnumerable<T> other)
        {
            throw new NotImplementedException();
        }

        public bool IsSubsetOf(IEnumerable<T> other)
        {
            throw new NotImplementedException();
        }

        public bool IsSupersetOf(IEnumerable<T> other)
        {
            throw new NotImplementedException();
        }

        public void OnDeserialization(object sender)
        {
            throw new NotImplementedException();
        }

        public bool Overlaps(IEnumerable<T> other)
        {
            throw new NotImplementedException();
        }

        public bool Remove(T item)
        {
            return storage.TryRemove(item, out _);
        }

        public bool SetEquals(IEnumerable<T> other)
        {
            throw new NotImplementedException();
        }

        public void SymmetricExceptWith(IEnumerable<T> other)
        {
            throw new NotImplementedException();
        }

        public void UnionWith(IEnumerable<T> other)
        {
            throw new NotImplementedException();
        }

        bool ISet<T>.Add(T item)
        {
            return storage.TryAdd(item, 0);
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return storage.Keys.GetEnumerator();
        }
    }
}