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
    public class LockedDictionaryAsync<TKey, TValue>
    {
        public class LockedValue
        {
            public TValue Value;
            public RWLockAsync lockValue;
        }

        private LockedDictionaryAsync<TKey, bool> KeysHoldingStorage = null;
        private ConcurrentDictionary<TKey, bool> KeysHoldingLockdownCache = new ConcurrentDictionary<TKey, bool>();
        
        public bool HoldKeys = false;
        public bool HoldKeyStorage = false;
        public bool LockValue = false;
        
        private readonly ConcurrentDictionary<TKey, LockedValue> dictionary = new ConcurrentDictionary<TKey, LockedValue>();
        private readonly RWLockAsync GlobalLocker = new RWLockAsync();
        
        // Семафор для замены критической секции Monitor.Enter(dictionary), 
        // обеспечивающий асинхронную безопасность от гонок.
        private readonly SemaphoreSlim _raceConditionSemaphore = new SemaphoreSlim(1, 1);

        public LockedDictionaryAsync(bool preserveLockingKeys = false)
        {
            HoldKeys = preserveLockingKeys;
            if (HoldKeys)
            {
                KeysHoldingStorage = new LockedDictionaryAsync<TKey, bool>();
                KeysHoldingStorage.HoldKeyStorage = true;
            }
        }

        #region Base functions

        private async Task<(bool Result, TValue OldValue, IDisposable LockToken)> TryAddOrChangeAsync(
            TKey key, TValue value, bool lockedMode = false, bool? overrideLockingMode = false)
        {
            bool result = false;
            IDisposable lockToken = null;
            TValue oldValue = default(TValue);

            using (await GlobalLocker.ReadLockAsync())
            {
            checkagain:
                IDisposable token = null;
                LockedValue dvalue = null;
                bool added = false;
                bool raceSemaphoreAcquired = false;

                try
                {
                    int raceChecker = 0;
                recheckRaceOfStates:
                    bool noncontainsDetected = false;
                    
                    if (!dictionary.ContainsKey(key))
                    {
                        IDisposable holdToken = null;
                        if (HoldKeys)
                        {
                        recheckHolded:
                            var holdResult = await KeysHoldingStorage.TryAddChangeLockedElementAsync(key, false, true, true);
                            holdToken = holdResult.LockToken;
                            
                            if (this.dictionary.ContainsKey(key))
                            {
                                holdToken?.Dispose();
                                goto recheckRaceOfStates;
                            }
                        }

                        var newLockedValue = new LockedValue() { Value = value, lockValue = new RWLockAsync() };
                        if (lockedMode)
                        {
                            if ((overrideLockingMode != null ? (bool)overrideLockingMode : LockValue))
                            {
                                lockToken = await newLockedValue.lockValue.WriteLockAsync();
                            }
                            else
                            {
                                lockToken = await newLockedValue.lockValue.ReadLockAsync();
                            }
                        }

                        // Замена Monitor.Enter. Проверяем флаг, чтобы не было двойного входа из-за цикла goto
                        if (raceChecker > 5 && !raceSemaphoreAcquired)
                        {
                            await _raceConditionSemaphore.WaitAsync();
                            raceSemaphoreAcquired = true;
                        }

                        if (dictionary.TryAdd(key, newLockedValue))
                        {
                            added = true;
                            result = true;
                            
                            if (raceSemaphoreAcquired)
                            {
                                _raceConditionSemaphore.Release();
                                raceSemaphoreAcquired = false;
                            }
                            
                            if (HoldKeys) holdToken?.Dispose();
                            return (result, oldValue, lockToken);
                        }
                        else
                        {
                            noncontainsDetected = true;
                        }

                        if (HoldKeys && holdToken != null) holdToken.Dispose();
                    }

                    if (dictionary.TryGetValue(key, out dvalue))
                    {
                        if (!added)
                        {
                            if ((overrideLockingMode != null ? (bool)overrideLockingMode : LockValue))
                            {
                                token = await dvalue.lockValue.WriteLockAsync();
                            }
                            else
                            {
                                token = await dvalue.lockValue.ReadLockAsync();
                            }
                        }
                    }
                    else if (noncontainsDetected)
                    {
                        raceChecker++;
                        goto recheckRaceOfStates;
                    }
                }
                finally
                {
                    // Гарантируем освобождение семафора перед выходом из блока логики гонки
                    if (raceSemaphoreAcquired)
                    {
                        _raceConditionSemaphore.Release();
                        raceSemaphoreAcquired = false;
                    }
                }

                if (!added && dvalue != null)
                {
                    LockedValue checkdvalue = null;
                    if (!dictionary.TryGetValue(key, out checkdvalue))
                    {
                        token?.Dispose();
                        lockToken?.Dispose();
                        goto checkagain;
                    }
                    if (checkdvalue.lockValue != dvalue.lockValue)
                    {
                        token?.Dispose();
                        lockToken?.Dispose();
                        goto checkagain;
                    }

                    if (dvalue != null)
                    {
                        oldValue = dvalue.Value;
                        dvalue.Value = value;
                        result = false;
                        if (lockedMode)
                            lockToken = token;
                        else
                            token?.Dispose();
                    }
                    else
                    {
                        result = false;
                        token?.Dispose();
                    }
                }
            }
            return (result, oldValue, lockToken);
        }

        private async Task<(bool Success, TValue Value)> TryRemoveAsync(TKey key, Func<TKey, TValue, Task> action = null)
        {
            bool result = false;
            TValue value = default(TValue);

            using (await GlobalLocker.ReadLockAsync())
            {
            checkagain:
                IDisposable token = null;
                LockedValue dvalue = null;
                if (dictionary.TryGetValue(key, out dvalue))
                {
                    token = await dvalue.lockValue.WriteLockAsync();
                }

                if (dvalue != null)
                {
                    LockedValue checkdvalue;
                    if (!dictionary.TryGetValue(key, out checkdvalue))
                    {
                        token?.Dispose();
                        goto checkagain;
                    }
                    if (checkdvalue.lockValue != dvalue.lockValue)
                    {
                        token?.Dispose();
                        goto checkagain;
                    }
                    
                    LockedValue outValue = null;
                    if (dictionary.TryGetValue(key, out dvalue))
                    {
                        if (action != null)
                        {
                            await action(key, dvalue.Value);
                        }
                    tryremoveagain:
                        dictionary.TryRemove(key, out outValue);

                        LockedValue checkdeletedvalue;
                        if (dictionary.TryGetValue(key, out checkdeletedvalue) && checkdeletedvalue.lockValue == dvalue.lockValue)
                        {
                            NLogger.Error("Dothet shiet detected on TryRemove LockedDictionary, retrying remove...");
                            goto tryremoveagain;
                        }

                        value = outValue.Value;
                        result = true;
                    }
                    else
                    {
                        value = default(TValue);
                        result = false;
                    }
                    token?.Dispose();
                }
                else
                {
                    value = default(TValue);
                    result = false;
                }
            }
            return (result, value);
        }

        /// <summary>
        /// IMPORTANT!!! HALT!!! if you will trying to remove or change value on selected key - YOU ENTER TO DEADLOCK!!! USE Async* or Unsafe* operations for this element, and THINK about you doing!
        /// </summary>
        public async Task<(bool Success, TValue Value, IDisposable LockToken)> TryGetLockedElementAsync(TKey key, bool? overrideLockValue = null)
        {
            IDisposable token = null;
            bool result = false;
            TValue value = default(TValue);

            using (await GlobalLocker.ReadLockAsync())
            {
            checkagain:
                LockedValue dvalue = null;
                if (dictionary.TryGetValue(key, out dvalue))
                {
                    if (overrideLockValue != null ? (bool)overrideLockValue : LockValue)
                        token = await dvalue.lockValue.WriteLockAsync();
                    else
                        token = await dvalue.lockValue.ReadLockAsync();
                }
                if (dvalue != null)
                {
                    LockedValue checkdvalue;
                    if (!dictionary.TryGetValue(key, out checkdvalue))
                    {
                        token?.Dispose();
                        goto checkagain;
                    }
                    if (checkdvalue.lockValue != dvalue.lockValue)
                    {
                        token?.Dispose();
                        goto checkagain;
                    }
                    if (dictionary.TryGetValue(key, out dvalue))
                    {
                        value = dvalue.Value;
                        result = true;
                    }
                    else
                    {
                        value = default(TValue);
                        token?.Dispose();
                        result = false;
                    }
                }
                else
                {
                    value = default(TValue);
                    result = false;
                }
            }
            return (result, value, token);
        }

        public async Task<(bool Success, IDisposable LockToken)> HoldKeyAsync(TKey key, bool holdMode = true)
        {
            if (HoldKeys)
            {
                var holdResult = await KeysHoldingStorage.TryAddChangeLockedElementAsync(key, false, true, false);
                var rdlockToken = holdResult.LockToken;

                if (rdlockToken != null)
                {
                    if (!await this.ContainsKeyAsync(key))
                    {
                        return (true, rdlockToken);
                    }
                }
                rdlockToken?.Dispose();
                return (false, null);
            }
            else
                return (false, null);
        }

        public async Task<bool> ExecuteOnKeyHoldedAsync(TKey key, Func<Task> asyncAction)
        {
            var holdResult = await HoldKeyAsync(key);
            if (holdResult.Success)
            {
                try
                {
                    await asyncAction();
                }
                catch (Exception ex)
                {
                    NLogger.Error(ex);
                }
                finally
                {
                    holdResult.LockToken.Dispose();
                }
                return true;
            }
            return false;
        }

        public async Task<(bool Success, IDisposable LockToken)> TryAddChangeLockedElementAsync(TKey key, TValue value, bool writeLocked, bool LockingMode = false)
        {
            var result = await this.TryAddOrChangeAsync(key, value, writeLocked, LockingMode);
            return (result.Result, result.LockToken);
        }

        public async Task ExecuteOnAddLockedAsync(TKey key, TValue value, Func<TKey, TValue, Task> asyncAction)
        {
            var result = await this.TryAddOrChangeAsync(key, value, true);
            if (result.Result && result.LockToken != null)
            {
                try
                {
                    await asyncAction(key, value);
                }
                catch (Exception ex)
                {
                    NLogger.Error(ex);
                }
                finally
                {
                    result.LockToken.Dispose();
                }
            }
            else if (result.LockToken != null)
            {
                result.LockToken.Dispose();
            }
        }

        public async Task ExecuteOnChangeLockedAsync(TKey key, TValue value, Func<TKey, TValue, TValue, Task> asyncAction)
        {
            var getResult = await this.TryGetLockedElementAsync(key, true);
            if (getResult.Success)
            {
                if (this.UnsafeChange(key, value))
                {
                    try
                    {
                        await asyncAction(key, value, getResult.Value);
                    }
                    catch (Exception ex)
                    {
                        NLogger.Error(ex);
                    }
                }
                getResult.LockToken?.Dispose();
            }
        }

        public async Task ExecuteOnAddOrChangeLockedAsync(TKey key, TValue value, Func<TKey, TValue, Task> onAddAction, Func<TKey, TValue, TValue, Task> onChangeAction)
        {
            var result = await this.TryAddOrChangeAsync(key, value, true);
            if (result.Result && result.LockToken != null)
            {
                try
                {
                    await onAddAction(key, value);
                }
                catch (Exception ex)
                {
                    NLogger.Error(ex);
                }
                finally
                {
                    result.LockToken.Dispose();
                }
            }
            else if (result.LockToken != null)
            {
                try
                {
                    await onChangeAction(key, value, result.OldValue);
                }
                catch (Exception ex)
                {
                    NLogger.Error(ex);
                }
                finally
                {
                    result.LockToken.Dispose();
                }
            }
        }

        public async Task<(bool Success, TValue Value)> ExecuteOnRemoveLockedAsync(TKey key, Func<TKey, TValue, Task> asyncAction)
        {
            return await TryRemoveAsync(key, asyncAction);
        }

        public async Task<bool> ExecuteOnAddChangeLockedAsync(TKey key, TValue value, Func<TKey, TValue, TValue, Task> asyncAction)
        {
            var result = await this.TryAddOrChangeAsync(key, value, true);
            if (result.LockToken != null)
            {
                try
                {
                    await asyncAction(key, value, result.OldValue);
                }
                catch (Exception ex)
                {
                    NLogger.Error(ex);
                }
                finally
                {
                    result.LockToken.Dispose();
                }
            }
            return result.Result;
        }

        public async Task ExecuteReadLockedAsync(TKey key, Func<TKey, TValue, Task> asyncAction)
        {
            var result = await this.TryGetLockedElementAsync(key, false);
            if (result.Success)
            {
                try
                {
                    await asyncAction(key, result.Value);
                }
                catch (Exception ex)
                {
                    NLogger.Error(ex);
                }
                finally
                {
                    result.LockToken.Dispose();
                }
            }
        }

        public async Task ExecuteWriteLockedAsync(TKey key, Func<TKey, TValue, Task> asyncAction)
        {
            var result = await this.TryGetLockedElementAsync(key, true);
            if (result.Success)
            {
                try
                {
                    await asyncAction(key, result.Value);
                }
                catch (Exception ex)
                {
                    NLogger.Error(ex);
                }
                finally
                {
                    result.LockToken.Dispose();
                }
            }
        }

        public async Task<(bool Success, IDisposable Token)> ExecuteReadLockedContinuouslyAsync(TKey key, Func<TKey, TValue, Task> asyncAction)
        {
            var result = await this.TryGetLockedElementAsync(key, false);
            if (result.Success)
            {
                try
                {
                    await asyncAction(key, result.Value);
                }
                catch (Exception ex)
                {
                    NLogger.Error(ex);
                }
            }
            return (result.Success, result.LockToken);
        }

        public async Task<(bool Success, IDisposable Token)> ExecuteWriteLockedContinuouslyAsync(TKey key, Func<TKey, TValue, Task> asyncAction)
        {
            var result = await this.TryGetLockedElementAsync(key, true);
            if (result.Success)
            {
                try
                {
                    await asyncAction(key, result.Value);
                }
                catch (Exception ex)
                {
                    NLogger.Error(ex);
                }
            }
            return (result.Success, result.LockToken);
        }

        public ValueTask<IDisposable> LockStorageAsync()
        {
            return this.GlobalLocker.WriteLockAsync();
        }

        public async Task ClearAsync()
        {
            using (await GlobalLocker.WriteLockAsync())
            {
                dictionary.Clear();
            }
        }

        public async Task<IDictionary<TKey, TValue>> ClearSnapshotAsync()
        {
            IDictionary<TKey, TValue> result = null;
            using (await GlobalLocker.WriteLockAsync())
            {
                result = dictionary.ToDictionary(x => x.Key, x => x.Value.Value);
                
                // Сохранение старой логики взятия токенов. Для избежания дедлоков берём их последовательно.
                var tokens = new List<IDisposable>();
                foreach (var kvp in dictionary)
                {
                    tokens.Add(await kvp.Value.lockValue.WriteLockAsync());
                }

                dictionary.Clear();

                foreach (var token in tokens)
                {
                    token.Dispose();
                }
            }
            return result;
        }

        #endregion

        #region Unsafe functions

        // Unsafe методы не используют локов GlobalLocker или внутри элемента, поэтому остаются синхронными.
        public bool UnsafeAdd(TKey key, TValue value)
        {
            if (this.dictionary.ContainsKey(key)) return false;
            return this.dictionary.TryAdd(key, new LockedValue() { Value = value, lockValue = new RWLockAsync() });
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
            this.dictionary.TryAdd(item.Key, new LockedValue() { Value = item.Value, lockValue = new RWLockAsync() });
        }

        #endregion

        #region Default functions (Async Versions)

        public async Task<(bool Success, TValue Value)> TryGetValueAsync(TKey key)
        {
            using (await GlobalLocker.ReadLockAsync())
            {
                if (dictionary.TryGetValue(key, out var keylock))
                {
                    return (true, keylock.Value);
                }
            }
            return (false, default(TValue));
        }

        public async Task<bool> ContainsKeyAsync(TKey key)
        {
            using (await GlobalLocker.ReadLockAsync())
            {
                return dictionary.ContainsKey(key);
            }
        }

        public async Task<ICollection<TKey>> GetKeysAsync()
        {
            using (await GlobalLocker.ReadLockAsync())
            {
                return dictionary.Keys.ToList();
            }
        }

        public async Task<ICollection<TValue>> GetValuesAsync()
        {
            using (await GlobalLocker.ReadLockAsync())
            {
                return dictionary.Values.Select(x => x.Value).ToList();
            }
        }

        public async Task<int> GetCountAsync()
        {
            using (await GlobalLocker.ReadLockAsync())
            {
                return dictionary.Count;
            }
        }

        public async Task<TValue> GetValueAsync(TKey key)
        {
            var result = await TryGetValueAsync(key);
            return result.Value;
        }

        public Task SetValueAsync(TKey key, TValue value)
        {
            return TryAddOrChangeAsync(key, value);
        }

        public Task AddAsync(TKey key, TValue value)
        {
            return this.TryAddOrChangeAsync(key, value);
        }

        public async Task<bool> RemoveAsync(TKey key)
        {
            var result = await this.TryRemoveAsync(key);
            return result.Success;
        }

        public async Task<bool> RemoveAsync(KeyValuePair<TKey, TValue> item)
        {
            var result = await this.TryRemoveAsync(item.Key);
            return result.Success;
        }

        public Task AddAsync(KeyValuePair<TKey, TValue> item)
        {
            return this.TryAddOrChangeAsync(item.Key, item.Value);
        }

        public Task<bool> ContainsAsync(KeyValuePair<TKey, TValue> item)
        {
            return this.ContainsKeyAsync(item.Key);
        }

        #endregion
    }
}