using AECC.Core.Logging;
using AECC.Extensions.ThreadingSync;
using Microsoft.Extensions.ObjectPool;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;


//у меня есть реализация надстройки над словарем, основная задача которой - обеспечить гарантию неблокируемых консистентных операций со словарем наподобие транзакций. Из-за того что юнит синхронизации RWLock сам по себе достаточно тяжеловесен - я столкнулся с потребностью создать пул (ObjectPool от microsoft) "лизинговых" юнитов синхронизации вместо текущего варианта, где у всех элементов словаря присутствует свой собственный юнит синхронизации. Таким образом нужно, чтобы пока над элементом происходят некоторые процессы (его добавляют, его удаляют, выполняют код пока он заблокирован, либо выполняется код, пока ключ заблокирован для добавления в KeysHoldingStorage), в общем, пока над элементом идет хоть какой-либо процесс, удерживающий блокировку (счетчики обьекта блокирования) - элементу положено наличие своего юнита. Как только все процессы проходят и последний из них обнаруживает, что более никто не удерживает блокировку - нужно забрать юнит у обьекта и вернуть его в пул. Проанализируй реализованные подходы для консистентности операций в текущей реализации, и на том же уровне безопасности реализуй запрашиваемый функционал.//

namespace AECC.Collections
{
    public class LockedDictionaryPooled<TKey, TValue> : ILockedDictionary<TKey, TValue>
    {
        public class LockedValue
        {
            public TValue Value;
            // Поле lockValue удалено - элементы "в покое" больше не удерживают тяжелые блокировки.
        }

        // --- НОВЫЙ БЛОК: Key-Level Lock Manager ---
        public class KeyLockInfo
        {
            public int RefCount;
            public RWLock Lock;
        }

        public class RWLockPooledObjectPolicy : PooledObjectPolicy<RWLock>
        {
            public override RWLock Create() => new RWLock();
            public override bool Return(RWLock obj) => true;
        }

        private readonly ConcurrentDictionary<TKey, KeyLockInfo> _activeKeyLocks = new ConcurrentDictionary<TKey, KeyLockInfo>();
        private static ObjectPool<RWLock> _lockPool = new DefaultObjectPool<RWLock>(new RWLockPooledObjectPolicy());

        // Обертка токена для автоматического возврата лока в пул после пользовательского Dispose()
        public class PooledKeyLockToken : RWLock.LockToken
        {
            private readonly RWLock.LockToken _innerToken;
            private readonly LockedDictionaryPooled<TKey, TValue> _dict;
            private readonly TKey _key;
            public readonly KeyLockInfo _info;
            private int _disposed = 0;

            public PooledKeyLockToken(RWLock.LockToken innerToken, LockedDictionaryPooled<TKey, TValue> dict, TKey key, KeyLockInfo info)
            {
                _innerToken = innerToken;
                _dict = dict;
                _key = key;
                _info = info;
            }

            public override void ExitLock()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    try { _innerToken.ExitLock(); }
                    finally { _dict.ReleaseKeyLock(_key, _info); }
                }
            }
        }

        private KeyLockInfo AcquireKeyLock(TKey key)
        {
            while (true)
            {
                // 1. Пытаемся получить существующий (самый частый сценарий, не аллоцирует память)
                if (_activeKeyLocks.TryGetValue(key, out var info))
                {
                    lock (info)
                    {
                        if (info.RefCount < 0) continue; // Объект "мертв", идем на следующий круг
                        info.RefCount++;
                        return info;
                    }
                }

                // 2. Если лока нет, берем новый из пула
                var newLock = _lockPool.Get();
                var newInfo = new KeyLockInfo { Lock = newLock, RefCount = 1 };

                // 3. Пытаемся атомарно вставить его
                if (_activeKeyLocks.TryAdd(key, newInfo))
                {
                    return newInfo; // Успешно добавили, возвращаем
                }
                else
                {
                    // Другой поток опередил нас на наносекунду и вставил свой лок первым!
                    // Возвращаем НАШ неиспользованный лок обратно в пул, чтобы не было утечек
                    _lockPool.Return(newLock);
                    
                    // Цикл while(true) повторится и на следующем шаге мы 100% заберем лок победившего потока
                }
            }
        }

        internal void ReleaseKeyLock(TKey key, KeyLockInfo info)
        {
            lock (info)
            {
                info.RefCount--;
                if (info.RefCount == 0)
                {
                    info.RefCount = -1; // Маркируем как "мертвый"
                    _activeKeyLocks.TryRemove(key, out _);
                    _lockPool.Return(info.Lock); // Возвращаем в ObjectPool
                }
            }
        }
        // --- КОНЕЦ БЛОКА Key-Level Lock Manager ---


        private LockedDictionaryPooled<TKey, bool> KeysHoldingStorage = null;
        public bool HoldKeys { get; set; } = false;
        public bool HoldKeyStorage { get; set; } = false;
        private readonly ConcurrentDictionary<TKey, LockedValue> dictionary = new ConcurrentDictionary<TKey, LockedValue>();
        public bool LockValue { get; set; } = false;
        private readonly RWLock GlobalLocker = new RWLock();

        public LockedDictionaryPooled(bool preserveLockingKeys = false)
        {
            HoldKeys = preserveLockingKeys;
            if (HoldKeys)
            {
                KeysHoldingStorage = new LockedDictionaryPooled<TKey, bool>();
                KeysHoldingStorage.HoldKeyStorage = true;
            }
        }

        #region Base functions
        private bool TryAddOrChange(TKey key, TValue value, out TValue oldValue, out RWLock.LockToken lockToken, bool lockedMode = false, bool? overrideLockingMode = false)
        {
            bool result = false;
            lockToken = null;
            oldValue = default(TValue);

            using (GlobalLocker.ReadLock())
            {
                var keyLockInfo = AcquireKeyLock(key);
                bool keyLockReleased = false; // Отслеживаем, передано ли управление локом вызывающему коду

                try
                {
                    bool writeLockRequired = overrideLockingMode ?? LockValue;
                    // Блокируем ключ ДО обращения к словарю. Это полностью защищает от RaceCondition подмены элементов
                    RWLock.LockToken token = null;
                    if(writeLockRequired)
                    {
                        token = keyLockInfo.Lock.WriteLock();
                    }
                    else
                    {
                        token = keyLockInfo.Lock.ReadLock();
                    }
                    RWLock.LockToken holdToken = null;

                    if (!dictionary.TryGetValue(key, out var dvalue))
                    {
                        if (HoldKeys)
                        {
                            KeysHoldingStorage.TryAddChangeLockedElement(key, false, true, out holdToken, true);
                            if (dictionary.ContainsKey(key))
                            {
                                holdToken?.Dispose();
                                holdToken = null;
                                dictionary.TryGetValue(key, out dvalue); // Безопасно обновляем, так как мы держим KeyLock
                            }
                        }

                        if (dvalue == null)
                        {
                            dvalue = new LockedValue { Value = value };
                            if (dictionary.TryAdd(key, dvalue))
                            {
                                result = true;
                                holdToken?.Dispose();

                                if (lockedMode)
                                {
                                    lockToken = new PooledKeyLockToken(token, this, key, keyLockInfo);
                                    keyLockReleased = true; // Управление передано
                                }
                                else
                                {
                                    token.Dispose();
                                }
                                return result;
                            }
                        }
                    }

                    if (dvalue != null && !result)
                    {
                        oldValue = dvalue.Value;
                        dvalue.Value = value;
                        result = false;

                        if (lockedMode)
                        {
                            lockToken = new PooledKeyLockToken(token, this, key, keyLockInfo);
                            keyLockReleased = true; // Управление передано
                        }
                        else
                        {
                            token.Dispose();
                        }
                    }
                }
                finally
                {
                    if (!keyLockReleased)
                    {
                        ReleaseKeyLock(key, keyLockInfo);
                    }
                }
            }
            return result;
        }

        private bool TryRemove(TKey key, out TValue value, Action<TKey, TValue> action = null)
        {
            bool result = false;
            value = default(TValue);

            using (GlobalLocker.ReadLock())
            {
                var keyLockInfo = AcquireKeyLock(key);
                try
                {
                    // Гарантируем, что никто не читает ключ при удалении
                    using (var token = keyLockInfo.Lock.WriteLock())
                    {
                        if (dictionary.TryGetValue(key, out var dvalue))
                        {
                            action?.Invoke(key, dvalue.Value);

                            if (dictionary.TryRemove(key, out var outValue))
                            {
                                value = outValue.Value;
                                result = true;
                            }
                        }
                    }
                }
                finally
                {
                    ReleaseKeyLock(key, keyLockInfo);
                }
            }
            return result;
        }

        public bool TryGetLockedElement(TKey key, out TValue value, out RWLock.LockToken lockToken, bool? overrideLockValue = null)
        {
            bool result = false;
            lockToken = null;
            value = default(TValue);

            using (GlobalLocker.ReadLock())
            {
                var keyLockInfo = AcquireKeyLock(key);
                bool keyLockReleased = false;

                try
                {
                    bool writeLock = overrideLockValue ?? LockValue;
                    RWLock.LockToken token = null;
                    
                    if(writeLock)
                    {
                        token = keyLockInfo.Lock.WriteLock();
                    }
                    else
                    {
                        token = keyLockInfo.Lock.ReadLock();
                    }

                    if (dictionary.TryGetValue(key, out var dvalue))
                    {
                        value = dvalue.Value;
                        result = true;
                        
                        lockToken = new PooledKeyLockToken(token, this, key, keyLockInfo);
                        keyLockReleased = true;
                    }
                    else
                    {
                        token.Dispose();
                    }
                }
                finally
                {
                    if (!keyLockReleased)
                    {
                        ReleaseKeyLock(key, keyLockInfo);
                    }
                }
            }
            return result;
        }

        public bool HoldKey(TKey key, out RWLock.LockToken lockToken, bool holdMode = true)
        {
            lockToken = null;
            if (HoldKeys)
            {
                KeysHoldingStorage.TryAddChangeLockedElement(key, false, true, out var rdlockToken, false);
                if (rdlockToken != null)
                {
                    if (!this.ContainsKey(key))
                    {
                        lockToken = rdlockToken;
                        return true;
                    }
                    rdlockToken.Dispose();
                }
                return false;
            }
            else
                return false;
        }

        public bool ExecuteOnKeyHolded(TKey key, Action action)
        {
            if (HoldKey(key, out var lockToken))
            {
                try { action(); }
                catch (Exception ex) { NLogger.Error(ex); }
                finally { lockToken.Dispose(); }
                return true;
            }
            return false;
        }

        public bool TryAddChangeLockedElement(TKey key, TValue value, bool writeLocked, out RWLock.LockToken lockToken, bool LockingMode = false)
        {
            return this.TryAddOrChange(key, value, out _, out lockToken, writeLocked, LockingMode);
        }

        public void ExecuteOnAddLocked(TKey key, TValue value, Action<TKey, TValue> action)
        {
            var result = this.TryAddOrChange(key, value, out _, out var lockToken, true);
            if (result && lockToken != null)
            {
                try { action(key, value); }
                catch (Exception ex) { NLogger.Error(ex); }
                finally { lockToken.Dispose(); }
            }
            else if (lockToken != null)
            {
                lockToken.Dispose();
            }
        }

        public void ExecuteOnChangeLocked(TKey key, TValue value, Action<TKey, TValue, TValue> action)
        {
            if (this.TryGetLockedElement(key, out var oldvalue, out var token, true))
            {
                if (this.UnsafeChange(key, value))
                {
                    try { action(key, value, oldvalue); }
                    catch (Exception ex) { NLogger.Error(ex); }
                }
                token?.Dispose();
            }
        }

        public void ExecuteOnAddOrChangeLocked(TKey key, TValue value, Action<TKey, TValue> onAddaction, Action<TKey, TValue, TValue> onChangeaction)
        {
            if (this.TryAddOrChange(key, value, out var oldvalue, out var lockToken, true) && lockToken != null)
            {
                try { onAddaction(key, value); }
                catch (Exception ex) { NLogger.Error(ex); }
                finally { lockToken.Dispose(); }
            }
            else if (lockToken != null)
            {
                try { onChangeaction(key, value, oldvalue); }
                catch (Exception ex) { NLogger.Error(ex); }
                finally { lockToken.Dispose(); }
            }
        }

        public void ExecuteOnRemoveLocked(TKey key, out TValue value, Action<TKey, TValue> action)
        {
            TryRemove(key, out value, action);
        }

        public bool ExecuteOnAddChangeLocked(TKey key, TValue value, Action<TKey, TValue, TValue> action)
        {
            var result = this.TryAddOrChange(key, value, out var oldValue, out var lockToken, true);
            if (lockToken != null)
            {
                try { action(key, value, oldValue); }
                catch (Exception ex) { NLogger.Error(ex); }
                finally { lockToken.Dispose(); }
            }
            return result;
        }

        public void ExecuteReadLocked(TKey key, Action<TKey, TValue> action)
        {
            if (this.TryGetLockedElement(key, out var value, out var token, false))
            {
                try { action(key, value); }
                catch (Exception ex) { NLogger.Error(ex); }
                finally { token.Dispose(); }
            }
        }

        public void ExecuteWriteLocked(TKey key, Action<TKey, TValue> action)
        {
            if (this.TryGetLockedElement(key, out var value, out var token, true))
            {
                try { action(key, value); }
                catch (Exception ex) { NLogger.Error(ex); }
                finally { token.Dispose(); }
            }
        }

        public void ExecuteReadLockedContinuously(TKey key, Action<TKey, TValue> action, out RWLock.LockToken token)
        {
            if (this.TryGetLockedElement(key, out var value, out token, false))
            {
                try { action(key, value); }
                catch (Exception ex) { NLogger.Error(ex); }
            }
        }

        public void ExecuteWriteLockedContinuously(TKey key, Action<TKey, TValue> action, out RWLock.LockToken token)
        {
            if (this.TryGetLockedElement(key, out var value, out token, true))
            {
                try { action(key, value); }
                catch (Exception ex) { NLogger.Error(ex); }
            }
        }

        public RWLock.LockToken LockStorage()
        {
            return this.GlobalLocker.WriteLock();
        }

        public void Clear()
        {
            using (GlobalLocker.WriteLock())
            {
                dictionary.Clear();
            }
        }

        public IDictionary<TKey, TValue> ClearSnapshot()
        {
            IDictionary<TKey, TValue> result = null;
            using (GlobalLocker.WriteLock())
            {
                result = dictionary.ToDictionary(x => x.Key, x => x.Value.Value);
                
                // Чтобы дождаться потоков, которые удерживают элементы извне через Execute*LockedContinuously
                var tokensAndInfos = new List<(IDisposable token, KeyLockInfo info, TKey key)>();
                
                try
                {
                    foreach (var kvp in dictionary)
                    {
                        var key = kvp.Key;
                        var info = AcquireKeyLock(key);
                        var token = info.Lock.WriteLock();
                        tokensAndInfos.Add((token, info, key));
                    }

                    dictionary.Clear();
                }
                finally
                {
                    foreach (var item in tokensAndInfos)
                    {
                        item.token.Dispose();
                        ReleaseKeyLock(item.key, item.info);
                    }
                }
            }
            return result;
        }

        #endregion

        #region Unsafe functions

        public bool UnsafeAdd(TKey key, TValue value)
        {
            if (this.dictionary.ContainsKey(key)) return false;
            return this.dictionary.TryAdd(key, new LockedValue() { Value = value });
        }

        public bool UnsafeRemove(TKey key, out TValue value)
        {
            if (this.dictionary.TryRemove(key, out var dicvalue))
            {
                value = dicvalue.Value;
                return true;
            }
            value = default(TValue);
            return false;
        }

        public bool UnsafeChange(TKey key, TValue value)
        {
            if (this.dictionary.TryGetValue(key, out var oldvalue))
            {
                oldvalue.Value = value;
                return true;
            }
            else
            {
                return false;
            }
        }

        public bool UnsafeRemove(KeyValuePair<TKey, TValue> item)
        {
            return this.dictionary.TryRemove(item.Key, out _);
        }

        public void UnsafeAdd(KeyValuePair<TKey, TValue> item)
        {
            this.dictionary.TryAdd(item.Key, new LockedValue() { Value = item.Value });
        }

        #endregion

        #region Default functions

        public bool TryGetValue(TKey key, out TValue value)
        {
            using (GlobalLocker.ReadLock())
            {
                if (dictionary.TryGetValue(key, out var keylock))
                {
                    value = keylock.Value;
                    return true;
                }
            }
            value = default(TValue);
            return false;
        }

        public bool ContainsKey(TKey key)
        {
            using (GlobalLocker.ReadLock())
            {
                return dictionary.ContainsKey(key);
            }
        }

        public ICollection<TKey> Keys
        {
            get
            {
                using (GlobalLocker.ReadLock())
                    return dictionary.Keys;
            }
        }

        public ICollection<TValue> Values
        {
            get
            {
                using (GlobalLocker.ReadLock())
                    return dictionary.Values.Select(x => x.Value).ToList();
            }
        }

        public int Count
        {
            get
            {
                using (GlobalLocker.ReadLock())
                    return dictionary.Count;
            }
        }

        public bool IsReadOnly => false;

        public TValue this[TKey key]
        {
            get
            {
                TryGetValue(key, out var value);
                return value;
            }
            set
            {
                TryAddOrChange(key, value, out _, out _);
            }
        }

        public void Add(TKey key, TValue value)
        {
            this.TryAddOrChange(key, value, out _, out _);
        }

        public bool Remove(TKey key)
        {
            return this.TryRemove(key, out _);
        }

        public bool Remove(KeyValuePair<TKey, TValue> item)
        {
            return this.TryRemove(item.Key, out _);
        }

        public void Add(KeyValuePair<TKey, TValue> item)
        {
            this.TryAddOrChange(item.Key, item.Value, out _, out _);
        }

        public bool Contains(KeyValuePair<TKey, TValue> item)
        {
            return this.ContainsKey(item.Key);
        }

        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
        {
            throw new NotImplementedException();
        }

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            return dictionary.Select(x => new KeyValuePair<TKey, TValue>(x.Key, x.Value.Value)).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return this.GetEnumerator();
        }

        #endregion
    }
}