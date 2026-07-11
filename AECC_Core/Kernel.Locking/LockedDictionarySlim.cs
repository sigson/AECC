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
    /// Thread-safe dictionary with transactional semantics (lockdown, HoldKey, ExecuteOn*Locked,
    /// Unsafe*). Each cell's read/write state is a single inline <c>long</c> driven by
    /// <see cref="RWCell"/>, avoiding a heavyweight per-cell lock object. The ConcurrentDictionary
    /// keeps node identity stable across resize, so <c>ref cell.Lock</c> remains valid while a lock
    /// is held.
    ///
    /// This is the recommended vehicle for the WORLD-LEVEL dictionaries (EntityStorage,
    /// childECSObjects, PreinitializedEntities). For the hottest
    /// per-entity component set, see <see cref="ComponentBag{TValue}"/> which removes the node too.
    /// </summary>
    public sealed class LockedDictionarySlim<TKey, TValue> : LockHost, IEnumerable<KeyValuePair<TKey, TValue>>
    {
        private sealed class Cell
        {
            public TValue Value;
            public long Lock; // inline RW state (RWCell)
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
        private volatile bool _lockdown;        // SOFT lockdown (EnterLockdown): blocks add/change/hold/unsafe; get/remove still work.

        // HARD freeze (LockStorage / Clear / ClearSnapshot): a true stop-the-world for this storage.
        // Implemented as an entry barrier (NOT a lock held across cell acquisition), so it cannot
        // re-create the inner->outer inversion that the elided per-op global read lock caused.
        // Mutating ops increment _inflight at entry and decrement at exit; the freezer raises _frozen,
        // then waits for _inflight to drain. The freezing thread bypasses its own barrier (so it can
        // operate on the frozen storage, e.g. ECSWorldSquash reading/moving entries while it holds the
        // freeze). Reads are intentionally NOT gated (they cannot corrupt a freezer that only reads/moves).
        private volatile bool _frozen;
        private int _freezeOwner = -1;          // ManagedThreadId of the freezer, -1 = none
        private int _freezeDepth;               // guarded by _freezeGate (reentrant LockStorage by owner)
        private int _inflight;                  // Interlocked count of in-flight mutating ops
        private readonly object _freezeGate = new object();

        private LockedDictionarySlim<TKey, bool> _keysHolding;
        public bool HoldKeys { get; private set; }
        public bool HoldKeyStorage { get; set; }
        public bool LockValue { get; set; }
        public bool IsLockdown { get { return _lockdown; } }

        // Concurrency mode is fixed at construction time.
        private readonly ConcurrencyMode _mode;
        private bool SingleThread { get { return _mode == ConcurrencyMode.SingleThread; } }
        public ConcurrencyMode Mode { get { return _mode; } }

        /// <summary>Uses the concurrency mode currently configured on KernelRuntime.DefaultMode.</summary>
        public LockedDictionarySlim(bool preserveLockingKeys = false)
            : this(KernelRuntime.DefaultMode, preserveLockingKeys)
        {
        }

        public LockedDictionarySlim(ConcurrencyMode mode, bool preserveLockingKeys = false)
        {
            _mode = mode;
            HoldKeys = preserveLockingKeys;
            if (HoldKeys)
            {
                _keysHolding = new LockedDictionarySlim<TKey, bool>(mode);
                _keysHolding.HoldKeyStorage = true;
            }
        }

        // ───────── token plumbing (LockHost) ─────────

        internal override void ReleaseSlot(object container, int slot, byte mode)
        {
            if (slot == GLOBAL_SLOT)
            {
                ReleaseFreeze(); // LockStorage token released -> lift the hard freeze
            }
            else
            {
                Cell c = (Cell)container;
                RWCell.Exit(c, ELEMENT_SLOT, RuntimeHelpers.GetHashCode(c), ref c.Lock, _mode);
            }
        }

        // The per-operation storage READ lock is ELIDED. Holding it during cell-lock acquisition created
        // an inner->outer lock-ordering inversion (HoldKey/combinator acquire a cell lock and then
        // the storage lock) that deadlocks against a writer-favoring lockdown. ConcurrentDictionary
        // is itself thread-safe and the per-cell locks provide the granular guarantee. Stop-the-world
        // (LockStorage) is provided by the entry barrier above instead. (Graft-plan risk-audit point 7.)
        private RWToken GlobalRead() { return default(RWToken); }
        private RWToken GlobalWrite() { return default(RWToken); }

        // ───────── hard-freeze barrier ─────────

        /// <summary>Mutating-op entry. Returns true if this call incremented the in-flight count and
        /// the caller must pair it with <see cref="LeaveFreeze"/>; false means uncounted (single-thread
        /// mode, or this thread owns the freeze and is bypassing it).</summary>
        private bool EnterFreeze()
        {
            if (SingleThread) return false;
            if (!Volatile.Read(ref _frozen))
            {
                Interlocked.Increment(ref _inflight);
                if (!Volatile.Read(ref _frozen)) return true;   // confirmed open while counted
                Interlocked.Decrement(ref _inflight);           // froze under us -> back off
            }
            return EnterFreezeSlow();
        }

        private bool EnterFreezeSlow()
        {
            int me = Thread.CurrentThread.ManagedThreadId;
            while (true)
            {
                if (Volatile.Read(ref _freezeOwner) == me) return false; // owner bypass (uncounted)
                lock (_freezeGate)
                {
                    while (Volatile.Read(ref _frozen) && Volatile.Read(ref _freezeOwner) != me)
                        Monitor.Wait(_freezeGate);
                }
                if (!Volatile.Read(ref _frozen))
                {
                    Interlocked.Increment(ref _inflight);
                    if (!Volatile.Read(ref _frozen)) return true;
                    Interlocked.Decrement(ref _inflight);
                }
            }
        }

        private void LeaveFreeze(bool counted) { if (counted) Interlocked.Decrement(ref _inflight); }

        private RWToken AcquireFreeze()
        {
            if (SingleThread) return new RWToken(this, this, GLOBAL_SLOT, 1);
            int me = Thread.CurrentThread.ManagedThreadId;
            bool first;
            lock (_freezeGate)
            {
                while (_frozen && _freezeOwner != me) Monitor.Wait(_freezeGate);
                _frozen = true;
                _freezeOwner = me;
                first = (++_freezeDepth == 1);
            }
            if (first)
            {
                var sw = new SpinWait();
                while (Volatile.Read(ref _inflight) > 0) sw.SpinOnce(); // drain ops that entered before the freeze
            }
            return new RWToken(this, this, GLOBAL_SLOT, 1);
        }

        private void ReleaseFreeze()
        {
            if (SingleThread) return;
            lock (_freezeGate)
            {
                if (--_freezeDepth == 0)
                {
                    _frozen = false;
                    _freezeOwner = -1;
                    Monitor.PulseAll(_freezeGate);
                }
            }
        }

        private RWToken CellLock(Cell c, bool write)
        {
            int ph = RuntimeHelpers.GetHashCode(c);
            return RWCell.Enter(c, ELEMENT_SLOT, ph, ref c.Lock, write, _mode)
                ? new RWToken(this, c, ELEMENT_SLOT, write ? (byte)1 : (byte)0) : default(RWToken);
        }

        // ───────── lockdown (soft, flag-based) — distinct from the hard freeze above ─────────

        public void EnterLockdown() { _lockdown = true; }
        public void ExitLockdown() { _lockdown = false; }


        // ───────── core add/change (ported from LockedDictionary.TryAddOrChange) ─────────

        /// <summary>
        /// Explicit outcome of a structural add/change. This is the SOLE source of truth for
        /// "what happened", decoupled from whether a REAL <see cref="RWToken"/> was produced.
        /// In OneThreadMode every cell acquire yields a dummy (non-real) token, so the outcome
        /// MUST NOT be inferred from <see cref="RWToken.IsReal"/>. <see cref="RWToken.IsReal"/> is
        /// purely a disposal predicate ("is there anything to release"), never a control-flow signal.
        /// </summary>
        private enum AddOutcome : byte
        {
            /// <summary>Nothing happened (soft lockdown). No callback should run.</summary>
            Refused = 0,
            /// <summary>A brand-new key was inserted.</summary>
            Added = 1,
            /// <summary>An existing key's value was replaced; <c>oldValue</c> is meaningful.</summary>
            Changed = 2,
        }

        /// <summary>
        /// Structural add/change core. Returns the exact <see cref="AddOutcome"/> in BOTH threading
        /// modes. When <paramref name="lockedMode"/> is set, <paramref name="lockToken"/> carries the
        /// cell's write lock on Added/Changed (real in multi-thread, dummy in OneThreadMode) — the
        /// caller runs its callback under that lock and disposes it afterwards. On Refused the token
        /// is <c>default</c>.
        /// </summary>
        private AddOutcome TryAddOrChangeCore(TKey key, TValue value, out TValue oldValue, out RWToken lockToken,
            bool lockedMode = false, bool? overrideLockingMode = false, bool addOnly = false)
        {
            lockToken = default(RWToken);
            oldValue = default(TValue);
            bool counted = EnterFreeze();
            try
            {
                using (GlobalRead())
                {
                if (_lockdown) return AddOutcome.Refused;

                // Cell lock mode for add/change. For a REAL value store, adding or replacing the
                // value is an exclusive mutation -> WRITE lock (callbacks must run under a write
                // lock, never a shared read lock). The nested
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
                            return AddOutcome.Added;
                        }
                        newTok.Dispose();               // add lost a race
                        if (holding) holdToken.Dispose();
                        // fall through to the change path
                    }

                    // (2) change the existing cell
                    // add-only operations (TryAdd, ExecuteOnAddLocked) on an existing key must be
                    // refused WITHOUT mutating the value and without acquiring the cell lock.
                    if (addOnly) return AddOutcome.Refused;
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
                    return AddOutcome.Changed;
                }
                }
            }
            finally { LeaveFreeze(counted); }
        }

        /// <summary>
        /// Thin bool projection of <see cref="TryAddOrChangeCore"/> used by the plain-dictionary
        /// surface (<c>TryAdd</c>, indexer, <c>Add</c>, <c>TryAddChangeLockedElement</c>):
        /// <c>true</c> == a new key was added, <c>false</c> == changed or refused. Callers that must
        /// distinguish Changed from Refused (the transactional executors) call
        /// <see cref="TryAddOrChangeCore"/> directly instead of this wrapper.
        /// </summary>
        private bool TryAddOrChange(TKey key, TValue value, out TValue oldValue, out RWToken lockToken,
            bool lockedMode = false, bool? overrideLockingMode = false)
        {
            return TryAddOrChangeCore(key, value, out oldValue, out lockToken, lockedMode, overrideLockingMode)
                   == AddOutcome.Added;
        }

        private bool TryRemove(TKey key, out TValue value, Action<TKey, TValue> action = null)
        {
            value = default(TValue);
            // NOTE: regular Remove deliberately works during soft lockdown (matches the original
            // contract "Get* and Remove* keep working" — EntityComponentStorage.OnEntityDelete enters
            // lockdown and then drains components via Remove). The HARD freeze (LockStorage) does gate
            // remove, via the freeze barrier below.
            bool counted = EnterFreeze();
            try
            {
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
            finally { LeaveFreeze(counted); }
        }

        public bool TryGetLockedElement(TKey key, out TValue value, out RWToken lockToken, bool? overrideLockValue = null)
        {
            lockToken = default(RWToken);
            value = default(TValue);
            bool wlock = overrideLockValue != null ? (bool)overrideLockValue : LockValue;
            bool counted = wlock ? EnterFreeze() : false; // only write-lock acquisition is gated by a hard freeze
            try
            {
                using (GlobalRead())
                {
                while (true)
                {
                    Cell dvalue;
                    if (!_dict.TryGetValue(key, out dvalue)) return false;

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
            finally { if (wlock) LeaveFreeze(counted); }
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
            if (SingleThread) return true; // single-thread: nothing to reserve
            if (_lockdown) return false;
            if (!HoldKeys) return false;

            bool counted = EnterFreeze();
            try
            {
                RWToken rd;
                // exclusive ? write-lock the holding cell : read-lock it (shared among holders)
                _keysHolding.TryAddChangeLockedElement(key, false, true, out rd, exclusive);
                // This path is MULTI-THREAD only — OneThreadMode returned above. Here a non-real token
                // means the hold-cell lock came back as a same-thread cross-mode dummy, so the absence
                // hold cannot be guaranteed and must be refused. This mirrors ComponentBag.WriteUsable
                // (t.IsReal || OneThreadMode) with the OneThreadMode arm already peeled off by the guard.
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
            finally { LeaveFreeze(counted); }
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
            // Gate the callback on the OPERATION OUTCOME, not on the token's realness: in
            // OneThreadMode the add succeeds but the token is a dummy, so keying on IsReal would
            // skip the callback and leave a torn insert.
            if (TryAddOrChangeCore(key, value, out _, out lockToken, true, addOnly: true) == AddOutcome.Added)
            {
                try { action(key, value); } catch { }
            }
            lockToken.Dispose(); // real token -> releases the cell write lock; dummy/default -> no-op
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
            AddOutcome outcome = TryAddOrChangeCore(key, value, out oldvalue, out lockToken, true);
            if (outcome == AddOutcome.Added)
            {
                try { onAdd(key, value); } catch { }
            }
            else if (outcome == AddOutcome.Changed)
            {
                try { onChange(key, value, oldvalue); } catch { }
            }
            lockToken.Dispose(); // held across the callback in MT; no-op for the OneThreadMode dummy
        }

        public void ExecuteOnRemoveLocked(TKey key, out TValue value, Action<TKey, TValue> action)
        {
            TryRemove(key, out value, action);
        }

        public bool ExecuteOnAddChangeLocked(TKey key, TValue value, Action<TKey, TValue, TValue> action)
        {
            RWToken lockToken;
            TValue oldValue;
            AddOutcome outcome = TryAddOrChangeCore(key, value, out oldValue, out lockToken, true);
            if (outcome != AddOutcome.Refused) // add OR change -> run
            {
                try { action(key, value, oldValue); } catch { }
            }
            lockToken.Dispose();
            return outcome == AddOutcome.Added; // true iff a new key was added
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

        public RWToken LockStorage() { return AcquireFreeze(); }

        public void Clear() { using (AcquireFreeze()) { _dict.Clear(); } }

        public IDictionary<TKey, TValue> ClearSnapshot()
        {
            IDictionary<TKey, TValue> result;
            using (AcquireFreeze()) // hard freeze: drains in-flight mutators, then snapshots+clears atomically
            {
                result = new Dictionary<TKey, TValue>(_dict.Count);
                foreach (var kv in _dict)
                    result[kv.Key] = kv.Value.Value;
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

        public bool TryAdd(TKey key, TValue value) { return TryAddOrChangeCore(key, value, out _, out _, addOnly: true) == AddOutcome.Added; }

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

        /// <summary>
        /// Remove overload returning the removed value (used by IECSObject.childECSObjects).
        /// </summary>
        public bool Remove(TKey key, out TValue value) { return TryRemove(key, out value); }

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