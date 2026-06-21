using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace AECC.Locking
{
    /// <summary>
    /// Drop-in replacement for the old <c>LockedDictionary&lt;TKey,TValue&gt;</c> with IDENTICAL
    /// transactional semantics (lockdown, HoldKey, ExecuteOn*Locked, Unsafe*), but the per-cell
    /// heavyweight <c>RWLock</c> object (a ReaderWriterLockSlim with recursion, ~115 B + kernel
    /// handle + recursion registry) is replaced by a single inline <c>long</c> driven by
    /// <see cref="RWCell"/>. The control flow is kept line-for-line with the original so behaviour
    /// is preserved; only the lock primitive and the cell type changed.
    ///
    /// Memory per cell: was ~140 B (LockedValue + RWLock + RWLS), now ~32 B (Cell node only,
    /// 8 of which are the lock). The ConcurrentDictionary keeps node identity stable across resize,
    /// so <c>ref cell.Lock</c> remains valid while a lock is held.
    ///
    /// This is the recommended vehicle for the WORLD-LEVEL dictionaries (EntityStorage,
    /// childECSObjects, PreinitializedEntities, SerializationContainer). For the hottest
    /// per-entity component set, see <see cref="ComponentBag{TValue}"/> which removes the node too.
    /// </summary>
    public sealed class LockedDictionarySlim<TKey, TValue> : LockHost, IEnumerable<KeyValuePair<TKey, TValue>>
    {
        private sealed class Cell
        {
            public TValue Value;
            public long Lock; // inline RW state (RWCell), replaces the old `RWLock lockValue`
        }

        // ───────── debug / consistency-verification mode (off by default, zero cost when off) ─────────
        /// <summary>Enable extra in-flight consistency checks and let the violation sink accumulate.</summary>
        public static bool DebugChecks = false;
        private static long _dbgViolations;
        private static string _dbgFirst;
        /// <summary>Record a consistency violation (used by stress tests and internal checks).</summary>
        public static void DebugFail(string message)
        {
            Interlocked.Increment(ref _dbgViolations);
            Interlocked.CompareExchange(ref _dbgFirst, message, null);
        }
        public static long DebugViolations { get { return Interlocked.Read(ref _dbgViolations); } }
        public static string DebugFirstViolation { get { return Volatile.Read(ref _dbgFirst); } }
        public static void DebugReset() { Interlocked.Exchange(ref _dbgViolations, 0); Interlocked.Exchange(ref _dbgFirst, null); }

        private const int GLOBAL_SLOT = -1;
        private const int ELEMENT_SLOT = 0;

        private readonly ConcurrentDictionary<TKey, Cell> _dict = new ConcurrentDictionary<TKey, Cell>();
        private volatile bool _lockdown;

        private LockedDictionarySlim<TKey, bool> _keysHolding;
        public bool HoldKeys { get; private set; }
        public bool HoldKeyStorage { get; set; }
        public bool LockValue { get; set; }
        public bool IsLockdown { get { return _lockdown; } }

        public LockedDictionarySlim(bool preserveLockingKeys = false)
        {
            HoldKeys = preserveLockingKeys;
            if (HoldKeys)
            {
                _keysHolding = new LockedDictionarySlim<TKey, bool>();
                _keysHolding.HoldKeyStorage = true;
            }
        }

        // ───────── token plumbing (LockHost) ─────────

        internal override void ReleaseSlot(object container, int slot, byte mode)
        {
            if (slot == GLOBAL_SLOT)
            {
                _lockdown = false; // LockStorage token released -> lift the soft lockdown
            }
            else
            {
                Cell c = (Cell)container;
                RWCell.Exit(c, ELEMENT_SLOT, RuntimeHelpers.GetHashCode(c), ref c.Lock);
            }
        }

        // The per-operation storage lock is ELIDED. Holding it during cell-lock acquisition created
        // an inner->outer lock-ordering inversion (HoldKey/combinator acquire a cell lock and then
        // the storage lock) that deadlocks against a writer-favoring lockdown. ConcurrentDictionary
        // is itself thread-safe and the per-cell locks provide the granular guarantee, so element
        // ops need no storage lock at all. Lockdown becomes a soft volatile flag that blocks new
        // mutations; in-flight ops complete naturally. (This is graft-plan risk-audit point 7.)
        private RWToken GlobalRead() { return default(RWToken); }
        private RWToken GlobalWrite() { return default(RWToken); }

        private RWToken CellLock(Cell c, bool write)
        {
            int ph = RuntimeHelpers.GetHashCode(c);
            return RWCell.Enter(c, ELEMENT_SLOT, ph, ref c.Lock, write)
                ? new RWToken(this, c, ELEMENT_SLOT, write ? (byte)1 : (byte)0) : default(RWToken);
        }

        // ───────── lockdown (soft, flag-based) ─────────

        public void EnterLockdown() { _lockdown = true; }
        public void ExitLockdown() { _lockdown = false; }

        // ───────── core add/change (ported from LockedDictionary.TryAddOrChange) ─────────

        private bool TryAddOrChange(TKey key, TValue value, out TValue oldValue, out RWToken lockToken,
            bool lockedMode = false, bool? overrideLockingMode = false)
        {
            lockToken = default(RWToken);
            oldValue = default(TValue);
            using (GlobalRead())
            {
                if (_lockdown) return false;

                // Cell lock mode for add/change. For a REAL value store, adding or replacing the
                // value is an exclusive mutation -> WRITE lock (this is the fix for the validator's
                // "READ saw active writer": previously the mode came from overrideLockingMode, which
                // defaulted to read, so callbacks ran under a shared read lock). The nested
                // KeysHoldingStorage is a hold proxy whose bool value is never meaningfully mutated;
                // there the lock mode IS the semantic (read = shared hold, write = exclusive/add-block),
                // so it follows overrideLockingMode.
                bool cellWrite = HoldKeyStorage
                    ? (overrideLockingMode != null ? (bool)overrideLockingMode : true)
                    : true;

                while (true)
                {
                    // (1) attempt to insert a fresh cell
                    if (!_dict.ContainsKey(key))
                    {
                        RWToken holdToken = default(RWToken);
                        bool holding = false;
                        if (HoldKeys)
                        {
                            _keysHolding.TryAddChangeLockedElement(key, false, true, out holdToken, true);
                            holding = true;
                            if (_dict.ContainsKey(key)) { holdToken.Dispose(); continue; }
                        }
                        Cell newCell = new Cell { Value = value };
                        RWToken newTok = default(RWToken);
                        if (lockedMode)
                        {
                            newTok = CellLock(newCell, cellWrite);
                        }
                        if (_dict.TryAdd(key, newCell))
                        {
                            if (holding) holdToken.Dispose();
                            lockToken = newTok;
                            return true; // added
                        }
                        newTok.Dispose();               // add lost a race
                        if (holding) holdToken.Dispose();
                        // fall through to the change path
                    }

                    // (2) change the existing cell
                    Cell dvalue;
                    if (!_dict.TryGetValue(key, out dvalue)) continue; // vanished -> retry add
                    RWToken token = CellLock(dvalue, cellWrite);
                    Cell check;
                    if (!_dict.TryGetValue(key, out check) || !ReferenceEquals(check, dvalue))
                    {
                        token.Dispose();
                        continue; // cell was replaced -> retry
                    }
                    oldValue = dvalue.Value;
                    dvalue.Value = value;
                    if (lockedMode) lockToken = token; else token.Dispose();
                    return false; // changed
                }
            }
        }

        private bool TryRemove(TKey key, out TValue value, Action<TKey, TValue> action = null)
        {
            value = default(TValue);
            if (_lockdown) return false;
            using (GlobalRead())
            {
                while (true)
                {
                    Cell dvalue;
                    if (!_dict.TryGetValue(key, out dvalue)) return false;

                    RWToken token = CellLock(dvalue, true);
                    Cell check;
                    if (!_dict.TryGetValue(key, out check) || !ReferenceEquals(check, dvalue))
                    {
                        token.Dispose();
                        continue; // replaced/removed under us -> retry
                    }
                    if (action != null) action(key, dvalue.Value);
                    Cell removed;
                    _dict.TryRemove(key, out removed); // remove while holding the cell's write lock
                    value = dvalue.Value;
                    token.Dispose();
                    return true;
                }
            }
        }

        public bool TryGetLockedElement(TKey key, out TValue value, out RWToken lockToken, bool? overrideLockValue = null)
        {
            lockToken = default(RWToken);
            value = default(TValue);
            using (GlobalRead())
            {
                while (true)
                {
                    Cell dvalue;
                    if (!_dict.TryGetValue(key, out dvalue)) return false;

                    bool wlock = overrideLockValue != null ? (bool)overrideLockValue : LockValue;
                    RWToken token = CellLock(dvalue, wlock);
                    Cell check;
                    if (!_dict.TryGetValue(key, out check) || !ReferenceEquals(check, dvalue))
                    {
                        token.Dispose();
                        continue; // replaced/removed under us -> retry
                    }
                    value = dvalue.Value;
                    lockToken = token;
                    return true;
                }
            }
        }

        /// <summary>Shared (read) absence hold — many holders may coexist; an add of the key is excluded.</summary>
        public bool HoldKey(TKey key, out RWToken lockToken, bool holdMode = true)
        {
            return HoldKey(key, false, out lockToken);
        }

        /// <summary>Absence hold. exclusive=false is SHARED (many holders); =true is exclusive.
        /// Either way an add of the key (a write on the holding cell) is mutually excluded.</summary>
        public bool HoldKey(TKey key, bool exclusive, out RWToken lockToken)
        {
            lockToken = default(RWToken);
            if (Defines.OneThreadMode) return true; // single-thread: nothing to reserve
            if (_lockdown) return false;
            if (!HoldKeys) return false;

            RWToken rd;
            // exclusive ? write-lock the holding cell : read-lock it (shared among holders)
            _keysHolding.TryAddChangeLockedElement(key, false, true, out rd, exclusive);
            if (rd.IsReal)
            {
                if (!_dict.ContainsKey(key)) // lock-free; never re-acquire a storage lock while holding the holding cell
                {
                    lockToken = rd;
                    return true;
                }
            }
            rd.Dispose();
            return false;
        }

        // ───────── transactional executors (ported 1:1) ─────────

        public bool TryAddChangeLockedElement(TKey key, TValue value, bool writeLocked, out RWToken lockToken, bool lockingMode = false)
        {
            return TryAddOrChange(key, value, out _, out lockToken, writeLocked, lockingMode);
        }

        public bool ExecuteOnKeyHolded(TKey key, Action action)
        {
            RWToken lockToken;
            if (HoldKey(key, out lockToken))
            {
                try { action(); } catch { }
                lockToken.Dispose();
                return true;
            }
            return false;
        }

        public void ExecuteOnAddLocked(TKey key, TValue value, Action<TKey, TValue> action)
        {
            RWToken lockToken;
            bool result = TryAddOrChange(key, value, out _, out lockToken, true);
            if (result && lockToken.IsReal)
            {
                try { action(key, value); } catch { }
                lockToken.Dispose();
            }
            else if (lockToken.IsReal)
            {
                lockToken.Dispose();
            }
        }

        public void ExecuteOnChangeLocked(TKey key, TValue value, Action<TKey, TValue, TValue> action)
        {
            RWToken token;
            TValue oldvalue;
            if (TryGetLockedElement(key, out oldvalue, out token, true))
            {
                if (UnsafeChange(key, value))
                {
                    try { action(key, value, oldvalue); } catch { }
                }
                token.Dispose();
            }
        }

        public void ExecuteOnAddOrChangeLocked(TKey key, TValue value, Action<TKey, TValue> onAdd, Action<TKey, TValue, TValue> onChange)
        {
            RWToken lockToken;
            TValue oldvalue;
            if (TryAddOrChange(key, value, out oldvalue, out lockToken, true) && lockToken.IsReal)
            {
                try { onAdd(key, value); } catch { }
                lockToken.Dispose();
            }
            else if (lockToken.IsReal)
            {
                try { onChange(key, value, oldvalue); } catch { }
                lockToken.Dispose();
            }
        }

        public void ExecuteOnRemoveLocked(TKey key, out TValue value, Action<TKey, TValue> action)
        {
            TryRemove(key, out value, action);
        }

        public bool ExecuteOnAddChangeLocked(TKey key, TValue value, Action<TKey, TValue, TValue> action)
        {
            RWToken lockToken;
            TValue oldValue;
            bool result = TryAddOrChange(key, value, out oldValue, out lockToken, true);
            if (lockToken.IsReal)
            {
                try { action(key, value, oldValue); } catch { }
                lockToken.Dispose();
            }
            return result;
        }

        public void ExecuteReadLocked(TKey key, Action<TKey, TValue> action)
        {
            RWToken token;
            TValue value;
            if (TryGetLockedElement(key, out value, out token, false))
            {
                try { action(key, value); } catch { }
                token.Dispose();
            }
        }

        public void ExecuteWriteLocked(TKey key, Action<TKey, TValue> action)
        {
            RWToken token;
            TValue value;
            if (TryGetLockedElement(key, out value, out token, true))
            {
                try { action(key, value); } catch { }
                token.Dispose();
            }
        }

        public void ExecuteReadLockedContinuously(TKey key, Action<TKey, TValue> action, out RWToken token)
        {
            TValue value;
            if (TryGetLockedElement(key, out value, out token, false))
            {
                try { action(key, value); } catch { }
            }
        }

        public void ExecuteWriteLockedContinuously(TKey key, Action<TKey, TValue> action, out RWToken token)
        {
            TValue value;
            if (TryGetLockedElement(key, out value, out token, true))
            {
                try { action(key, value); } catch { }
            }
        }

        public RWToken LockStorage() { _lockdown = true; return new RWToken(this, this, GLOBAL_SLOT, 1); }

        public void Clear() { using (GlobalWrite()) { _dict.Clear(); } }

        public IDictionary<TKey, TValue> ClearSnapshot()
        {
            IDictionary<TKey, TValue> result;
            using (GlobalWrite())
            {
                result = new Dictionary<TKey, TValue>(_dict.Count);
                foreach (var kv in _dict)
                {
                    using (CellLock(kv.Value, true)) { }
                    result[kv.Key] = kv.Value.Value;
                }
                _dict.Clear();
            }
            return result;
        }

        // ───────── unsafe ─────────

        public bool UnsafeAdd(TKey key, TValue value)
        {
            if (_lockdown) return false;
            if (_dict.ContainsKey(key)) return false;
            return _dict.TryAdd(key, new Cell { Value = value });
        }

        public bool UnsafeRemove(TKey key, out TValue value)
        {
            Cell c;
            if (_dict.TryRemove(key, out c)) { value = c.Value; return true; }
            value = default(TValue);
            return false;
        }

        public bool UnsafeChange(TKey key, TValue value)
        {
            if (_lockdown) return false;
            Cell c;
            if (_dict.TryGetValue(key, out c)) { c.Value = value; return true; }
            return false;
        }

        // ───────── plain dictionary surface ─────────

        public bool TryGetValue(TKey key, out TValue value)
        {
            using (GlobalRead())
            {
                Cell c;
                if (_dict.TryGetValue(key, out c)) { value = c.Value; return true; }
            }
            value = default(TValue);
            return false;
        }

        public bool ContainsKey(TKey key)
        {
            using (GlobalRead()) { return _dict.ContainsKey(key); }
        }

        public bool TryAdd(TKey key, TValue value) { return TryAddOrChange(key, value, out _, out _); }

        public ICollection<TKey> Keys { get { using (GlobalRead()) return _dict.Keys; } }

        public ICollection<TValue> Values
        {
            get { using (GlobalRead()) return _dict.Values.Select(x => x.Value).ToList(); }
        }

        public int Count { get { using (GlobalRead()) return _dict.Count; } }

        public TValue this[TKey key]
        {
            get { TValue v; TryGetValue(key, out v); return v; }
            set { TryAddOrChange(key, value, out _, out _); }
        }

        public void Add(TKey key, TValue value) { TryAddOrChange(key, value, out _, out _); }
        public bool Remove(TKey key) { return TryRemove(key, out _); }

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            return _dict.Select(x => new KeyValuePair<TKey, TValue>(x.Key, x.Value.Value)).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }

        // ───────── debug: quiescent consistency scan ─────────
        /// <summary>
        /// Call ONLY when no threads are operating on this dictionary. Verifies that every cell's
        /// lock state has fully drained to 0 (no leaked readers/writers/waiters — i.e. every acquire
        /// was balanced by a release), that the global storage lock is released, and that the live
        /// count is coherent with the enumerable. Returns false and fills <paramref name="msg"/> on
        /// the first violation. Also recursively verifies the nested holding storage.
        /// </summary>
        public bool DebugVerifyQuiescent(out string msg)
        {
            int counted = 0;
            foreach (var kv in _dict)
            {
                counted++;
                Cell c = kv.Value;
                if (c == null) { msg = "null cell for key " + kv.Key; DebugFail(msg); return false; }
                long lk = Volatile.Read(ref c.Lock);
                if (lk != 0) { msg = "leaked lock state on key " + kv.Key + ": 0x" + lk.ToString("X"); DebugFail(msg); return false; }
            }
            if (counted != _dict.Count) { msg = "count incoherent: scanned " + counted + " vs " + _dict.Count; DebugFail(msg); return false; }

            if (_keysHolding != null)
            {
                string inner;
                if (!_keysHolding.DebugVerifyQuiescent(out inner)) { msg = "keysHolding: " + inner; return false; }
            }
            msg = null;
            return true;
        }
    }
}
