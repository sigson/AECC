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
    public class DictionaryWrapper<TKey, TValue> : IDictionary<TKey, TValue>
    {
        private Dictionary<TKey, TValue> SimpleDictionary = null;
        private ConcurrentDictionary<TKey, TValue> ConcurrentDictionary = null;

        public IDictionary<TKey, TValue> dictionary
        {
            get
            {
                if (Defines.OneThreadMode)
                {
                    if (SimpleDictionary == null)
                    {
                        SimpleDictionary = new Dictionary<TKey, TValue>();
                    }
                    return SimpleDictionary;
                }
                else
                {
                    if (ConcurrentDictionary == null)
                    {
                        ConcurrentDictionary = new ConcurrentDictionary<TKey, TValue>();
                    }
                    return ConcurrentDictionary;
                }
            }
        }

        private void AddImpl(TKey key, TValue value)
        {
            //lock (dictionary)
            {
                dictionary.Add(key, value);
            }
        }

        private TValue GetImpl(TKey key)
        {
            //lock (dictionary)
            return dictionary[key];
        }

        private void SetImpl(TKey key, TValue value)
        {
            try
            {
                dictionary[key] = value;
            }
            catch
            {
                //ignore addition error
            }
        }

        private bool RemoveImpl(TKey key)
        {
            try
            {
                return dictionary.Remove(key);
            }
            catch
            {
                //ignore addition error
                return false;
            }
        }

        private void ClearImpl()
        {
            //lock (dictionary)
            {
                dictionary.Clear();
            }
        }

        public TValue this[TKey key] { get => GetImpl(key); set => SetImpl(key, value); }

        public ICollection<TKey> Keys => dictionary.Keys;

        public ICollection<TValue> Values => dictionary.Values;

        public int Count => dictionary.Count;

        public bool IsReadOnly => false;

        public void Add(TKey key, TValue value)
        {
            AddImpl(key, value);
        }

        public void Add(KeyValuePair<TKey, TValue> item)
        {
            AddImpl(item.Key, item.Value);
        }

        public void Clear()
        {
            ClearImpl();
        }

        public bool Contains(KeyValuePair<TKey, TValue> item)
        {
            return dictionary.ContainsKey(item.Key);
        }

        public bool ContainsKey(TKey key)
        {
            return dictionary.ContainsKey(key);
        }

        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
        {
            dictionary.CopyTo(array, arrayIndex);
        }

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            return dictionary.GetEnumerator();
        }

        public bool Remove(TKey key)
        {
            return RemoveImpl(key);
        }

        public bool Remove(KeyValuePair<TKey, TValue> item)
        {
            return RemoveImpl(item.Key);
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            return dictionary.TryGetValue(key, out value);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return dictionary.GetEnumerator();
        }

        public TValue GetOrAdd(TKey key, Func<TKey, TValue> value)
        {
            if (dictionary is ConcurrentDictionary<TKey, TValue> concurrentDictionary)
            {
                return concurrentDictionary.GetOrAdd(key, value);
            }
            else if (!dictionary.TryGetValue(key, out var invalue))
            {
                invalue = value(key);
                dictionary.Add(key, invalue);
                return invalue;
            }
            return default(TValue);
        }
    }
}