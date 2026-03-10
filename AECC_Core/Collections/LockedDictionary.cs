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
    public class LockedDictionary<TKey, TValue> : ILockedDictionary<TKey, TValue>
    {
        public class LockedValue
        {
            public TValue Value;
            public RWLock lockValue;
        }
        private LockedDictionary<TKey, bool> KeysHoldingStorage = null;
        //private ConcurrentDictionary<TKey, bool> KeysHoldingLockdownCache = new ConcurrentDictionary<TKey, bool>();
        public bool HoldKeys { get; set; } = false;
        public bool HoldKeyStorage { get; set; } = false;
        private readonly ConcurrentDictionary<TKey, LockedValue> dictionary = new ConcurrentDictionary<TKey, LockedValue>();
        public bool LockValue { get; set; } = false;
        private readonly RWLock GlobalLocker = new RWLock();

        public LockedDictionary(bool preserveLockingKeys = false)
        {
            HoldKeys = preserveLockingKeys;
            if(HoldKeys)
            {
                KeysHoldingStorage = new LockedDictionary<TKey, bool>();
                KeysHoldingStorage.HoldKeyStorage = true;
            }
        }

        #region Base functions
        private bool TryAddOrChange(TKey key, TValue value, out TValue oldValue, out RWLock.LockToken lockToken, bool lockedMode = false, bool? overrideLockingMode = false)
        {
            var result = false;
            lockToken = null;
            oldValue = default(TValue);
            using (GlobalLocker.ReadLock())
            {
                checkagain:
                RWLock.LockToken token = null;
                LockedValue dvalue = null;
                bool added = false;
                
                {
                    int raceChecker = 0;
                    recheckRaceOfStates:
                    bool noncontainsDetected = false;
                    if(!dictionary.ContainsKey(key))
                    {
                        RWLock.LockToken holdToken = null;
                        if(HoldKeys)
                        {
                            recheckHolded:
                            KeysHoldingStorage.TryAddChangeLockedElement(key, false, true, out holdToken, true);
                            if(this.dictionary.ContainsKey(key))
                            {
                                holdToken.Dispose();
                                goto recheckRaceOfStates;
                            }
                        }
                        var newLockedValue = new LockedValue() { Value = value, lockValue = new RWLock() };
                        if(lockedMode) 
                        {
                            if((overrideLockingMode != null ? (bool)overrideLockingMode : LockValue))
                            {
                                lockToken = newLockedValue.lockValue.WriteLock();
                            }
                            else 
                            {
                                lockToken = newLockedValue.lockValue.ReadLock();
                            }
                        }
                        if(raceChecker > 5)
                            Monitor.Enter(dictionary);
                        if(dictionary.TryAdd(key, newLockedValue))
                        {
                            added = true;
                            result = true;
                            if(raceChecker > 5)
                                Monitor.Exit(dictionary);
                            if(HoldKeys)
                                holdToken.Dispose();
                            return result;
                        }
                        else
                        {
                            noncontainsDetected = true;
                        }
                        if(HoldKeys && holdToken != null)
                            holdToken.Dispose();
                    }
                    if (dictionary.TryGetValue(key, out dvalue))
                    {
                        if (!added)
                        {
                            if((overrideLockingMode != null ? (bool)overrideLockingMode : LockValue))
                            {
                                token = dvalue.lockValue.WriteLock();
                            }
                            else 
                            {
                                token = dvalue.lockValue.ReadLock();
                            }
                        }
                    }
                    else if(noncontainsDetected)
                    {
                        raceChecker++;
                        goto recheckRaceOfStates;
                    }
                    if(raceChecker > 5)
                        Monitor.Exit(dictionary);
                }
                if(!added && dvalue != null)
                {
                    LockedValue checkdvalue = null;
                    if (!dictionary.TryGetValue(key, out checkdvalue))
                    {
                        if(token != null)
                            token.Dispose();
                        if(lockToken != null)
                            lockToken.Dispose();
                        goto checkagain;
                    }
                    if (checkdvalue.lockValue != dvalue.lockValue)
                    {
                        if(token != null)
                            token.Dispose();
                        if(lockToken != null)
                            lockToken.Dispose();
                        goto checkagain;
                    }

                    if (dvalue != null)
                    {
                        oldValue = dvalue.Value;
                        dvalue.Value = value;
                        result = false;
                        if(lockedMode)
                            lockToken = token;
                        else
                            token.Dispose();
                    }
                    else
                    {
                        result = false;
                        token.Dispose();
                    }
                }
            }
            return result;
        }

        private bool TryRemove(TKey key, out TValue value, Action<TKey, TValue> action = null)
        {
            bool result = false;
            using (GlobalLocker.ReadLock())
            {
                checkagain:
                RWLock.LockToken token = null;
                LockedValue dvalue = null;
                if (dictionary.TryGetValue(key, out dvalue))
                {
                    token = dvalue.lockValue.WriteLock();
                }
                
                if(dvalue != null)
                {
                    LockedValue checkdvalue;
                    if (!dictionary.TryGetValue(key, out checkdvalue))
                    {
                        if(token != null)
                            token.Dispose();
                        goto checkagain;
                    }
                    if (checkdvalue.lockValue != dvalue.lockValue)
                    {
                        if(token != null)
                            token.Dispose();
                        goto checkagain;
                    }
                    LockedValue outValue = null;
                    if (dictionary.TryGetValue(key, out dvalue))
                    {
                        if(action != null)
                        {
                            action(key, dvalue.Value);
                        }
                        tryremoveagain:
                        dictionary.Remove(key, out outValue);

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
                    token.Dispose();
                }
                else
                {
                    value = default(TValue);
                    result = false;
                }
            }
            return result;
        }

        /// <summary>
        /// IMPORTANT!!! HALT!!! if you will trying to remove or change value on selected key - YOU ENTER TO DEADLOCK!!! USE Async* or Unsafe* operations for this element, and THINK about you doing!
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <param name="lockToken"></param>
        /// <param name="overrideLockValue"></param>
        /// <returns></returns>
        public bool TryGetLockedElement(TKey key, out TValue value, out RWLock.LockToken lockToken, bool? overrideLockValue = null)
        {
            RWLock.LockToken token = null;
            bool result = false;
            using (GlobalLocker.ReadLock())
            {
                checkagain:
                LockedValue dvalue = null;
                if (dictionary.TryGetValue(key, out dvalue))
                {
                    if (overrideLockValue != null ? (bool)overrideLockValue : LockValue)
                        token = dvalue.lockValue.WriteLock();
                    else
                        token = dvalue.lockValue.ReadLock();
                }
                if(dvalue != null)
                {
                    LockedValue checkdvalue;
                    if (!dictionary.TryGetValue(key, out checkdvalue))
                    {
                        if(token != null)
                            token.Dispose();
                        goto checkagain;
                    }
                    if (checkdvalue.lockValue != dvalue.lockValue)
                    {
                        if(token != null)
                            token.Dispose();
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
                        token.Dispose();
                        result = false;
                    }
                }
                else
                {
                    value = default(TValue);
                    result = false;
                }
            }
            lockToken = token;
            return result;
        }

        public bool HoldKey(TKey key, out RWLock.LockToken lockToken, bool holdMode = true)
        {
            lockToken = null;
            if (HoldKeys)
            {
                KeysHoldingStorage.TryAddChangeLockedElement(key, false, true, out var rdlockToken, false);
                if(rdlockToken != null)
                {
                    if (!this.ContainsKey(key))
                    {
                        lockToken = rdlockToken;
                        return true;
                    }
                }
                rdlockToken?.Dispose();
                return false;
            }
            else
                return false;
        }

        public bool ExecuteOnKeyHolded(TKey key, Action action)
        {
            if(HoldKey(key, out var lockToken))
            {
                try
                {
                    action();
                }
                catch(Exception ex)
                {
                    NLogger.Error(ex);
                }
                lockToken.Dispose();
                return true;
            }
            return false;
        }

        public bool TryAddChangeLockedElement(TKey key, TValue value, bool writeLocked, out RWLock.LockToken lockToken, bool LockingMode = false)
        {
            return this.TryAddOrChange(key, value, out _, out lockToken, writeLocked, LockingMode);
        }

        public void ExecuteOnAddLocked(TKey key, TValue value, Action<TKey,TValue> action)
        {
            var result = this.TryAddOrChange(key, value, out _, out var lockToken, true);
            if (result && lockToken != null)
            {
                try
                {
                    action(key, value);
                }
                catch(Exception ex)
                {
                    NLogger.Error(ex);
                }
                lockToken.Dispose();
            }
            else if(lockToken != null)
            {
                lockToken.Dispose();
            }
        }
        /// <summary>
        /// input change action has key, value, oldvalue params
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <param name="action">key, value, oldvalue</param>
        /// <returns></returns>
        public void ExecuteOnChangeLocked(TKey key, TValue value, Action<TKey,TValue,TValue> action)
        {
            if(this.TryGetLockedElement(key, out var oldvalue, out var token, true))
            {
                if(this.UnsafeChange(key, value))
                {
                    try
                    {
                        action(key, value, oldvalue);
                    }
                    catch(Exception ex)
                    {
                        NLogger.Error(ex);
                    }
                }
                if(token != null)
                    token?.Dispose();
            }
        }

        /// <summary>
        /// input change action has key, value, oldvalue params
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <param name="action">key, value, oldvalue</param>
        /// <returns></returns>
        public void ExecuteOnAddOrChangeLocked(TKey key, TValue value, Action<TKey,TValue> onAddaction, Action<TKey,TValue,TValue> onChangeaction)
        {
            if (this.TryAddOrChange(key, value, out var oldvalue, out var lockToken, true) && lockToken != null)
            {
                try
                {
                    onAddaction(key, value);
                }
                catch (Exception ex)
                {
                    NLogger.Error(ex);
                }
                lockToken.Dispose();
            }
            else if (lockToken != null)
            {
                try
                {
                    onChangeaction(key, value, oldvalue);
                }
                catch (Exception ex)
                {
                    NLogger.Error(ex);
                }
                lockToken.Dispose();
            }
        }

        public void ExecuteOnRemoveLocked(TKey key, out TValue value, Action<TKey,TValue> action)
        {
            TryRemove(key, out value, action);
        }
        /// <summary>
        /// input change action has key, value, oldvalue params
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <param name="action">key, value, oldvalue</param>
        /// <returns></returns>
        public bool ExecuteOnAddChangeLocked(TKey key, TValue value, Action<TKey,TValue,TValue> action)
        {
            var result = this.TryAddOrChange(key, value, out var oldValue, out var lockToken, true);
            if(lockToken != null)
            {
                try
                {
                    action(key, value, oldValue);
                }
                catch (Exception ex)
                {
                    NLogger.Error(ex);
                }
                lockToken.Dispose();
            }
            return result;
        }

        public void ExecuteReadLocked(TKey key, Action<TKey,TValue> action)
        {
            if(this.TryGetLockedElement(key, out var value, out var token, false))
            {
                try
                {
                    action(key, value);
                }
                catch(Exception ex)
                {
                    NLogger.Error(ex);
                }
                token.Dispose();
            }
        }

        public void ExecuteWriteLocked(TKey key, Action<TKey,TValue> action)
        {
            if(this.TryGetLockedElement(key, out var value, out var token, true))
            {
                try
                {
                    action(key, value);
                }
                catch(Exception ex)
                {
                    NLogger.Error(ex);
                }
                token.Dispose();
            }
        }

        public void ExecuteReadLockedContinuously(TKey key, Action<TKey,TValue> action, out RWLock.LockToken token)
        {
            if(this.TryGetLockedElement(key, out var value, out token, false))
            {
                try
                {
                    action(key, value);
                }
                catch(Exception ex)
                {
                    NLogger.Error(ex);
                }
            }
        }

        public void ExecuteWriteLockedContinuously(TKey key, Action<TKey,TValue> action, out RWLock.LockToken token)
        {
            if(this.TryGetLockedElement(key, out var value, out token, true))
            {
                try
                {
                    action(key, value);
                }
                catch(Exception ex)
                {
                    NLogger.Error(ex);
                }
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
                var tokens = dictionary.Select(x => x.Value.lockValue.WriteLock());
                dictionary.Clear();
                tokens.ForEach(x => x.Dispose());
            }
            return result;
        }

        #endregion

        
        #region Unsafe functions

        public bool UnsafeAdd(TKey key, TValue value)
        {
            if(this.dictionary.ContainsKey(key)) return false;
            return this.dictionary.TryAdd(key, new LockedValue(){Value = value, lockValue = new RWLock()});
        }

        public bool UnsafeRemove(TKey key, out TValue value)
        {
            if(this.dictionary.Remove(key, out var dicvalue))
            {
                value = dicvalue.Value;
                return true;
            }
            value = default(TValue);
            return false;
        }

        public bool UnsafeChange(TKey key, TValue value)
        {
            if(this.dictionary.TryGetValue(key, out var oldvalue))
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
            return this.dictionary.Remove(item.Key, out _);
        }

        public void UnsafeAdd(KeyValuePair<TKey, TValue> item)
        {
            this.dictionary.TryAdd(item.Key, new LockedValue(){Value = item.Value, lockValue = new RWLock()});
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

        public ICollection<TValue> Values{
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