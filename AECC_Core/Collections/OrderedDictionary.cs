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
    public class OrderedDictionary<TKey, TValue> : IDictionary<TKey, TValue>
    {
        private List<TKey> keys = new List<TKey>();
        private Dictionary<TKey, TValue> dictionary = new Dictionary<TKey, TValue>();

        private void AddImpl(TKey key, TValue value)
        {
            //lock (dictionary)
            {
                dictionary.Add(key, value);
                keys.Add(key);
            }
        }

        private TValue GetImpl(TKey key)
        {
            //lock (dictionary)
            return dictionary[key];
        }

        public TValue Get(int index)
        {
            //lock (dictionary)
            return dictionary[keys[index]];
        }

        private void SetImpl(TKey key, TValue value)
        {
            //lock (dictionary)
            {
                if (dictionary.ContainsKey(key))
                    dictionary[key] = value;
                else
                {
                    dictionary.Add(key, value);
                    keys.Add(key);
                }
            }
        }

        private bool RemoveImpl(TKey key)
        {
            //lock (dictionary)
            {
                keys.Remove(key);
                return dictionary.Remove(key);
            }
        }

        public bool Remove(int index)
        {
            //lock(dictionary)
            {
                var ret = dictionary.Remove(keys[index]);
                keys.RemoveAt(index);
                return ret;
            }
        }

        private void ClearImpl()
        {
            //lock (dictionary)
            {
                dictionary.Clear();
                keys.Clear();
            }
        }

        public TValue this[TKey key] { get => GetImpl(key); set => SetImpl(key, value); }

        public ICollection<TKey> Keys => keys;

        public ICollection<TValue> Values
        {
            get
            {
                List<TValue> values = new List<TValue>();
                keys.ForEach(x => values.Add(dictionary[x]));
                return values;
            }
        }

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
            throw new NotImplementedException();
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
    }
}