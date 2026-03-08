using System;
using System.Collections.Generic;

namespace AECC.Collections
{
    // Тип хука: до выполнения метода или после
    public enum HookType
    {
        Before,
        After
    }
    public class HookableDictionary<TKey, TValue> : IDictionary<TKey, TValue>, IDisposable
    {
        private readonly IDictionary<TKey, TValue> _dictionary;

        // Хранилище хуков теперь использует string в качестве ключа (имя метода)
        // Структура: "MethodName" -> List<Action>
        private readonly Dictionary<string, List<Action>> _beforeHooks = new Dictionary<string, List<Action>>();
        private readonly Dictionary<string, List<Action>> _afterHooks = new Dictionary<string, List<Action>>();

        private bool _disposed = false;

        // Константа для имени индексатора, т.к. nameof(this[]) невозможен
        public string IndexerNameGet = "ItemGet";
        public string IndexerNameSet = "ItemSet";

        

        #region Constructors
        public HookableDictionary() : this(new Dictionary<TKey, TValue>()) { }
        public HookableDictionary(int capacity) : this(new Dictionary<TKey, TValue>(capacity)) { }
        public HookableDictionary(IEqualityComparer<TKey> comparer) : this(new Dictionary<TKey, TValue>(comparer)) { }
        
        public HookableDictionary(IDictionary<TKey, TValue> dictionary)
        {
            _dictionary = new Dictionary<TKey, TValue>(dictionary);
        }
        #endregion

        #region Hook Management

        // Метод регистрации принимает строковое имя метода (используйте nameof(Add), nameof(Clear) и т.д.)
        public void RegisterHook(string methodName, HookType type, Action callback)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(HookableDictionary<TKey, TValue>));
            if (string.IsNullOrEmpty(methodName) || callback == null) return;

            var targetDict = type == HookType.Before ? _beforeHooks : _afterHooks;

            if (!targetDict.ContainsKey(methodName))
            {
                targetDict[methodName] = new List<Action>();
            }

            targetDict[methodName].Add(callback);
        }

        public void UnregisterHook(string methodName, HookType type, Action callback)
        {
            if (_disposed) return;
            if (string.IsNullOrEmpty(methodName) || callback == null) return;

            var targetDict = type == HookType.Before ? _beforeHooks : _afterHooks;

            if (targetDict.TryGetValue(methodName, out var list))
            {
                list.Remove(callback);
            }
        }

        private void ExecuteHooks(string methodName, HookType type)
        {
            if (_disposed) return;

            var targetDict = type == HookType.Before ? _beforeHooks : _afterHooks;

            if (targetDict.TryGetValue(methodName, out var actions))
            {
                // Создаем копию списка перед итерацией для безопасности (если хук попытается отписаться сам)
                foreach (var action in actions.ToArray())
                {
                    action?.Invoke();
                }
            }
        }

        #endregion

        #region IDisposable Implementation

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Очистка всех хуков
                    _beforeHooks.Clear();
                    _afterHooks.Clear();
                }
                _disposed = true;
            }
        }

        #endregion

        #region IDictionary Implementation

        public TValue this[TKey key]
        {
            get
            {
                // Для индексатора используем константу "Item"
                ExecuteHooks(IndexerNameGet, HookType.Before); 
                var value = _dictionary[key];
                ExecuteHooks(IndexerNameGet, HookType.After);
                return value;
            }
            set
            {
                ExecuteHooks(IndexerNameSet, HookType.Before);
                _dictionary[key] = value;
                ExecuteHooks(IndexerNameSet, HookType.After);
            }
        }

        public ICollection<TKey> Keys
        {
            get
            {
                ExecuteHooks(nameof(Keys), HookType.Before);
                var keys = _dictionary.Keys;
                ExecuteHooks(nameof(Keys), HookType.After);
                return keys;
            }
        }

        public ICollection<TValue> Values
        {
            get
            {
                ExecuteHooks(nameof(Values), HookType.Before);
                var values = _dictionary.Values;
                ExecuteHooks(nameof(Values), HookType.After);
                return values;
            }
        }

        public int Count
        {
            get
            {
                ExecuteHooks(nameof(Count), HookType.Before);
                var count = _dictionary.Count;
                ExecuteHooks(nameof(Count), HookType.After);
                return count;
            }
        }

        public bool IsReadOnly => _dictionary.IsReadOnly;

        public void Add(TKey key, TValue value)
        {
            ExecuteHooks(nameof(Add), HookType.Before);
            _dictionary.Add(key, value);
            ExecuteHooks(nameof(Add), HookType.After);
        }

        public void Add(KeyValuePair<TKey, TValue> item)
        {
            ExecuteHooks(nameof(Add), HookType.Before);
            _dictionary.Add(item);
            ExecuteHooks(nameof(Add), HookType.After);
        }

        public void Clear()
        {
            ExecuteHooks(nameof(Clear), HookType.Before);
            _dictionary.Clear();
            ExecuteHooks(nameof(Clear), HookType.After);
        }

        public bool Contains(KeyValuePair<TKey, TValue> item)
        {
            ExecuteHooks(nameof(Contains), HookType.Before);
            bool result = _dictionary.Contains(item);
            ExecuteHooks(nameof(Contains), HookType.After);
            return result;
        }

        public bool ContainsKey(TKey key)
        {
            ExecuteHooks(nameof(ContainsKey), HookType.Before);
            bool result = _dictionary.ContainsKey(key);
            ExecuteHooks(nameof(ContainsKey), HookType.After);
            return result;
        }

        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
        {
            ExecuteHooks(nameof(CopyTo), HookType.Before);
            _dictionary.CopyTo(array, arrayIndex);
            ExecuteHooks(nameof(CopyTo), HookType.After);
        }

        public bool Remove(TKey key)
        {
            ExecuteHooks(nameof(Remove), HookType.Before);
            bool result = _dictionary.Remove(key);
            ExecuteHooks(nameof(Remove), HookType.After);
            return result;
        }

        public bool Remove(KeyValuePair<TKey, TValue> item)
        {
            ExecuteHooks(nameof(Remove), HookType.Before);
            bool result = _dictionary.Remove(item);
            ExecuteHooks(nameof(Remove), HookType.After);
            return result;
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            ExecuteHooks(nameof(TryGetValue), HookType.Before);
            bool result = _dictionary.TryGetValue(key, out value);
            ExecuteHooks(nameof(TryGetValue), HookType.After);
            return result;
        }

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            ExecuteHooks(nameof(GetEnumerator), HookType.Before);
            var enumerator = _dictionary.GetEnumerator();
            ExecuteHooks(nameof(GetEnumerator), HookType.After);
            return enumerator;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return this.GetEnumerator();
        }

        #endregion
    }
}