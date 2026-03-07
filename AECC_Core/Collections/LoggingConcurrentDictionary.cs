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
    public class LoggingConcurrentDictionary<TKey, TValue> : IDictionary<TKey, TValue>, IDictionary
    {
        private readonly ConcurrentDictionary<TKey, TValue> _dictionary;

        // Сделали публичным, чтобы можно было использовать в LogAction при формировании сообщения
        public string InstanceId { get; } = Guid.NewGuid().ToString("N").Substring(0, 8);

        // ИЗМЕНЕНИЕ: Теперь Action принимает название операции, Ключ и Значение.
        // Используем TKey? и TValue?, так как в некоторых операциях (Clear, Ctor) их может не быть.
        public Action<string, LoggingConcurrentDictionary<TKey, TValue>, TKey, TValue> LogAction { get; set; }

        // ИЗМЕНЕНИЕ: Метод Log теперь принимает типизированные аргументы
        private void Log(string operation, TKey key = default, TValue value = default)
        {
            if (LogAction == null) return;

            // Мы передаем сырые объекты. Форматирование строки и фильтрация теперь 
            // лежат на плечах того, кто задает LogAction.
            LogAction.Invoke(operation, this, key, value);
        }

        #region Constructors

        public LoggingConcurrentDictionary()
        {
            _dictionary = new ConcurrentDictionary<TKey, TValue>();
            Log("Ctor");
        }

        public LoggingConcurrentDictionary(int capacity)
        {
            int concurrencyLevel = Environment.ProcessorCount * 4;
            _dictionary = new ConcurrentDictionary<TKey, TValue>(concurrencyLevel, capacity);
            Log("Ctor(capacity)");
        }

        public LoggingConcurrentDictionary(IEqualityComparer<TKey> comparer)
        {
            _dictionary = new ConcurrentDictionary<TKey, TValue>(comparer ?? EqualityComparer<TKey>.Default);
            Log("Ctor(comparer)");
        }

        public LoggingConcurrentDictionary(IEnumerable<KeyValuePair<TKey, TValue>> collection)
        {
            _dictionary = new ConcurrentDictionary<TKey, TValue>(collection ?? throw new ArgumentNullException(nameof(collection)));
            Log("Ctor(collection)");
        }

        public LoggingConcurrentDictionary(IEnumerable<KeyValuePair<TKey, TValue>> collection, IEqualityComparer<TKey> comparer)
        {
            _dictionary = new ConcurrentDictionary<TKey, TValue>(collection, comparer ?? EqualityComparer<TKey>.Default);
            Log("Ctor(collection, comparer)");
        }

        public LoggingConcurrentDictionary(int concurrencyLevel, int capacity)
        {
            _dictionary = new ConcurrentDictionary<TKey, TValue>(concurrencyLevel, capacity);
            Log("Ctor(concurrencyLevel, capacity)");
        }

        #endregion

        #region ConcurrentDictionary Specific Methods

        public bool IsEmpty => _dictionary.IsEmpty;

        public TValue GetOrAdd(TKey key, TValue value)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            
            var result = _dictionary.GetOrAdd(key, value);
            // Передаем и ключ, и итоговое значение
            Log("GetOrAdd(Value)", key, result);
            return result;
        }

        public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (valueFactory == null) throw new ArgumentNullException(nameof(valueFactory));

            var result = _dictionary.GetOrAdd(key, (k) => 
            {
                var val = valueFactory(k);
                Log("GetOrAdd (Factory Executed)", k, val);
                return val;
            });
            
            Log("GetOrAdd(Factory) Result", key, result);
            return result;
        }

        public TValue GetOrAdd<TArg>(TKey key, Func<TKey, TArg, TValue> valueFactory, TArg factoryArgument)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (valueFactory == null) throw new ArgumentNullException(nameof(valueFactory));

            var result = _dictionary.GetOrAdd(key, (k, arg) =>
            {
                var val = valueFactory(k, arg);
                Log("GetOrAdd (FactoryArg Executed)", k, val);
                return val;
            }, factoryArgument);

            Log("GetOrAdd(FactoryArg) Result", key, result);
            return result;
        }

        public TValue AddOrUpdate(TKey key, Func<TKey, TValue> addValueFactory, Func<TKey, TValue, TValue> updateValueFactory)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (addValueFactory == null) throw new ArgumentNullException(nameof(addValueFactory));
            if (updateValueFactory == null) throw new ArgumentNullException(nameof(updateValueFactory));

            var result = _dictionary.AddOrUpdate(key, 
                addValueFactory, 
                (k, v) => updateValueFactory(k, v));
            
            Log("AddOrUpdate(Factory)", key, result);
            return result;
        }

        public TValue AddOrUpdate(TKey key, TValue addValue, Func<TKey, TValue, TValue> updateValueFactory)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (updateValueFactory == null) throw new ArgumentNullException(nameof(updateValueFactory));

            var result = _dictionary.AddOrUpdate(key, addValue, updateValueFactory);
            Log("AddOrUpdate(Value)", key, result);
            return result;
        }

        public bool TryAdd(TKey key, TValue value)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            
            bool result = _dictionary.TryAdd(key, value);
            Log(result ? "TryAdd (Success)" : "TryAdd (Fail)", key, value);
            return result;
        }

        public bool TryUpdate(TKey key, TValue newValue, TValue comparisonValue)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            bool result = _dictionary.TryUpdate(key, newValue, comparisonValue);
            // Логируем новый value, который мы пытались установить
            Log(result ? "TryUpdate (Success)" : "TryUpdate (Fail)", key, newValue);
            return result;
        }

        public bool TryRemove(TKey key, out TValue value)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            bool result = _dictionary.TryRemove(key, out value);
            // Если удаление успешно, value будет содержать удаленный объект, иначе default
            Log(result ? "TryRemove (Success)" : "TryRemove (Fail)", key, value);
            return result;
        }

        public bool TryRemove(KeyValuePair<TKey, TValue> item)
        {
            bool result = ((ICollection<KeyValuePair<TKey, TValue>>)_dictionary).Remove(item);
            Log(result ? "TryRemove(KVP) (Success)" : "TryRemove(KVP) (Fail)", item.Key, item.Value);
            return result;
        }

        public KeyValuePair<TKey, TValue>[] ToArray()
        {
            Log("ToArray");
            return _dictionary.ToArray();
        }

        #endregion

        #region IDictionary<TKey, TValue> Implementation

        public TValue this[TKey key]
        {
            get
            {
                var val = _dictionary[key];
                Log("Indexer[Get]", key, val);
                return val;
            }
            set
            {
                _dictionary[key] = value;
                Log("Indexer[Set]", key, value);
            }
        }

        public ICollection<TKey> Keys
        {
            get
            {
                Log("Keys[Get]");
                return _dictionary.Keys;
            }
        }

        public ICollection<TValue> Values
        {
            get
            {
                Log("Values[Get]");
                return _dictionary.Values;
            }
        }

        public int Count => _dictionary.Count;

        public bool IsReadOnly => false;

        public void Add(TKey key, TValue value)
        {
            if (!_dictionary.TryAdd(key, value))
            {
                throw new ArgumentException($"An item with the same key has already been added. Key: {key}");
            }
            Log("Add", key, value);
        }

        public void Add(KeyValuePair<TKey, TValue> item)
        {
            Add(item.Key, item.Value);
        }

        public void Clear()
        {
            _dictionary.Clear();
            Log("Clear");
        }

        public bool Contains(KeyValuePair<TKey, TValue> item)
        {
            return _dictionary.Contains(item);
        }

        public bool ContainsKey(TKey key)
        {
            return _dictionary.ContainsKey(key);
        }

        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
        {
            Log("CopyTo");
            ((IDictionary<TKey, TValue>)_dictionary).CopyTo(array, arrayIndex);
        }

        public bool Remove(TKey key)
        {
            bool result = _dictionary.TryRemove(key, out var val);
            Log(result ? "Remove (Success)" : "Remove (Fail)", key, val);
            return result;
        }

        public bool Remove(KeyValuePair<TKey, TValue> item)
        {
            bool result = ((ICollection<KeyValuePair<TKey, TValue>>)_dictionary).Remove(item);
            Log(result ? "Remove(KVP) (Success)" : "Remove(KVP) (Fail)", item.Key, item.Value);
            return result;
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            return _dictionary.TryGetValue(key, out value);
        }

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            Log("GetEnumerator");
            return _dictionary.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        #endregion

        #region IDictionary Implementation (Non-generic)

        void IDictionary.Add(object key, object value)
        {
            if (key is TKey k && (value is TValue || value == null))
            {
                Add(k, (TValue)value);
            }
            else
            {
                throw new ArgumentException("Invalid key or value type");
            }
        }

        void IDictionary.Clear() => Clear();

        bool IDictionary.Contains(object key)
        {
            if (key is TKey k) return ContainsKey(k);
            return false;
        }

        IDictionaryEnumerator IDictionary.GetEnumerator()
        {
            return ((IDictionary)_dictionary).GetEnumerator();
        }

        bool IDictionary.IsFixedSize => false;
        bool IDictionary.IsReadOnly => false;

        ICollection IDictionary.Keys => (ICollection)Keys;
        ICollection IDictionary.Values => (ICollection)Values;

        void IDictionary.Remove(object key)
        {
            if (key is TKey k) Remove(k);
        }

        object IDictionary.this[object key]
        {
            get
            {
                if (key is TKey k && TryGetValue(k, out var val)) return val;
                return null;
            }
            set
            {
                if (key is TKey k && (value is TValue || value == null))
                {
                    this[k] = (TValue)value;
                }
            }
        }

        void ICollection.CopyTo(Array array, int index)
        {
            Log("CopyTo (Non-generic)");
            ((ICollection)_dictionary).CopyTo(array, index);
        }

        bool ICollection.IsSynchronized => ((ICollection)_dictionary).IsSynchronized;
        object ICollection.SyncRoot => ((ICollection)_dictionary).SyncRoot;

        #endregion
    }
}