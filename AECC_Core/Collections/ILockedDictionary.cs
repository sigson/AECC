using AECC.Extensions.ThreadingSync;
using System;
using System.Collections.Generic;

namespace AECC.Collections
{
    /// <summary>
    /// Интерфейс потокобезопасного словаря с поддержкой транзакционных операций
    /// и гранулярных неблокирующих (или ожидающих) блокировок по ключам.
    /// </summary>
    public interface ILockedDictionary<TKey, TValue> : IDictionary<TKey, TValue>
    {
        #region Properties
        bool HoldKeys { get; set; }
        bool HoldKeyStorage { get; set; }
        bool LockValue { get; set; }
        #endregion

        #region Locking and Element Access
        bool TryGetLockedElement(TKey key, out TValue value, out RWLock.LockToken lockToken, bool? overrideLockValue = null);
        bool HoldKey(TKey key, out RWLock.LockToken lockToken, bool holdMode = true);
        bool TryAddChangeLockedElement(TKey key, TValue value, bool writeLocked, out RWLock.LockToken lockToken, bool LockingMode = false);
        #endregion

        #region Transactional / Delegate Executions
        bool ExecuteOnKeyHolded(TKey key, Action action);
        void ExecuteOnAddLocked(TKey key, TValue value, Action<TKey, TValue> action);
        
        /// <summary>
        /// Выполняет действие при изменении заблокированного элемента.
        /// Action принимает параметры: key, value, oldvalue
        /// </summary>
        void ExecuteOnChangeLocked(TKey key, TValue value, Action<TKey, TValue, TValue> action);
        
        /// <summary>
        /// Выполняет действие при добавлении или изменении элемента.
        /// onAddaction принимает: key, value
        /// onChangeaction принимает: key, value, oldvalue
        /// </summary>
        void ExecuteOnAddOrChangeLocked(TKey key, TValue value, Action<TKey, TValue> onAddaction, Action<TKey, TValue, TValue> onChangeaction);
        
        void ExecuteOnRemoveLocked(TKey key, out TValue value, Action<TKey, TValue> action);
        bool ExecuteOnAddChangeLocked(TKey key, TValue value, Action<TKey, TValue, TValue> action);
        
        void ExecuteReadLocked(TKey key, Action<TKey, TValue> action);
        void ExecuteWriteLocked(TKey key, Action<TKey, TValue> action);
        
        void ExecuteReadLockedContinuously(TKey key, Action<TKey, TValue> action, out RWLock.LockToken token);
        void ExecuteWriteLockedContinuously(TKey key, Action<TKey, TValue> action, out RWLock.LockToken token);
        #endregion

        #region Storage Level Actions
        RWLock.LockToken LockStorage();
        IDictionary<TKey, TValue> ClearSnapshot();
        #endregion

        #region Unsafe functions
        bool UnsafeAdd(TKey key, TValue value);
        void UnsafeAdd(KeyValuePair<TKey, TValue> item);
        bool UnsafeRemove(TKey key, out TValue value);
        bool UnsafeRemove(KeyValuePair<TKey, TValue> item);
        bool UnsafeChange(TKey key, TValue value);
        #endregion
    }
}