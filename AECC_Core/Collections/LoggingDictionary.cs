using AECC.Core.Logging;
using AECC.Extensions;
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
    public class LoggingDictionary<TKey, TValue> : IDictionary<TKey, TValue>
    {
        // Внутренний, "настоящий" словарь, который мы оборачиваем
        private long instanceId = Guid.NewGuid().GuidToLong();
        private readonly IDictionary<TKey, TValue> _dictionary;

        // Приватный метод для логирования текущего состояния
        private void LogState(string prefix = "")
        {
            // Environment.StackTrace дает более полную информацию, чем new StackTrace()
            string stackTrace = Environment.StackTrace;
            
            NLogger.Log($"{prefix}+{instanceId}+Elements count: {_dictionary.Count}\nStack Trace:\n{stackTrace}");
        }

        #region Constructors
        // Повторяем конструкторы оригинального Dictionary
        public LoggingDictionary()
        {
            _dictionary = new Dictionary<TKey, TValue>();
            LogState("Ctor()");
        }

        public LoggingDictionary(int capacity)
        {
            _dictionary = new Dictionary<TKey, TValue>(capacity);
            LogState("Ctor(capacity)");
        }

        public LoggingDictionary(IEqualityComparer<TKey> comparer)
        {
            _dictionary = new Dictionary<TKey, TValue>(comparer);
            LogState("Ctor(comparer)");
        }

        public LoggingDictionary(IDictionary<TKey, TValue> dictionary)
        {
            _dictionary = new Dictionary<TKey, TValue>(dictionary);
            LogState("Ctor(dictionary)");
        }
        #endregion

        #region IDictionary<TKey, TValue> Implementation

        public TValue this[TKey key]
        {
            get
            {
                var value = _dictionary[key];
                LogState("Indexer[get]");
                return value;
            }
            set
            {
                _dictionary[key] = value;
                LogState("Indexer[set]");
            }
        }

        public ICollection<TKey> Keys
        {
            get
            {
                var keys = _dictionary.Keys;
                LogState("Keys[get]");
                return keys;
            }
        }

        public ICollection<TValue> Values
        {
            get
            {
                var values = _dictionary.Values;
                LogState("Values[get]");
                return values;
            }
        }

        public int Count
        {
            get
            {
                var count = _dictionary.Count;
                LogState("Count[get]");
                return count;
            }
        }

        public bool IsReadOnly => _dictionary.IsReadOnly;

        public void Add(TKey key, TValue value)
        {
            _dictionary.Add(key, value);
            LogState("Add(key, value)");
        }

        public void Add(KeyValuePair<TKey, TValue> item)
        {
            _dictionary.Add(item);
            LogState("Add(item)");
        }

        public void Clear()
        {
            _dictionary.Clear();
            LogState("Clear()");
        }

        public bool Contains(KeyValuePair<TKey, TValue> item)
        {
            bool result = _dictionary.Contains(item);
            LogState("Contains(item)");
            return result;
        }

        public bool ContainsKey(TKey key)
        {
            bool result = _dictionary.ContainsKey(key);
            LogState("ContainsKey(key)");
            return result;
        }

        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
        {
            _dictionary.CopyTo(array, arrayIndex);
            LogState("CopyTo()");
        }

        public bool Remove(TKey key)
        {
            bool result = _dictionary.Remove(key);
            LogState("Remove(key)");
            return result;
        }

        public bool Remove(KeyValuePair<TKey, TValue> item)
        {
            bool result = _dictionary.Remove(item);
            LogState("Remove(item)");
            return result;
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            bool result = _dictionary.TryGetValue(key, out value);
            LogState("TryGetValue()");
            return result;
        }

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            // Логируем сам факт получения итератора. 
            // Логирование каждого шага итерации (MoveNext) потребовало бы создания
            // обертки и для IEnumerator, что усложнило бы код.
            var enumerator = _dictionary.GetEnumerator();
            LogState("GetEnumerator()");
            return enumerator;
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return this.GetEnumerator();
        }

        #endregion
    }
}