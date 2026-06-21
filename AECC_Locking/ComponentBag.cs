using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace AECC.Locking
{
    /// <summary>
    /// Compact per-entity component bag (strategy §4.1) with the FULL transactional matrix and
    /// full parity with the operations EntityComponentStorage drives on the old LockedDictionary:
    ///
    ///   present + read  : many concurrent readers of an existing component;
    ///   present + write : exclusive mutation / change of an existing component;
    ///   absent  + read  : SHARED hold of absence — many threads may simultaneously guarantee a
    ///                     component stays absent while their code runs;
    ///   absent  + write : exclusive hold of absence;
    ///   add  (absent->present)  : WRITE lock on the slot, mutually exclusive with shared holds
    ///                             (writer-favoring: a pending add makes new holds back off);
    ///   addOrChange / change    : add if absent, else replace value under the write lock;
    ///   remove (present->absent): exclusive.
    ///
    /// One ~40-byte node per touched key carries the lock inline as a <c>long</c> (RWCell). Slots
    /// persist in the ABSENT state and are reclaimed for another key only when their lock has fully
    /// drained — no separate lock object, no nested KeysHoldingStorage, no unbounded growth.
    /// Node references are stable; callers re-validate State/Key after acquiring, so reuse is safe.
    ///
    /// Cross-mode safety: if a structural mutator's write lock comes back as a dummy because the
    /// SAME thread already holds a conflicting lock on the SAME cell (the legacy "deadlock escape"),
    /// the mutation is REFUSED rather than performed without exclusivity. Set
    /// <see cref="RWCell.ThrowOnOrderViolation"/> = true in tests to surface such call sites.
    /// </summary>
    /// <typeparam name="TValue">Reference type stored per key (e.g. ECSComponent).</typeparam>
    public sealed class ComponentBag<TValue> : LockHost where TValue : class
    {
        private const byte FREE = 0, PRESENT = 1, ABSENT = 2;

        private sealed class Cell
        {
            public int Key;
            public TValue Value;
            public long Lock;   // inline RW state (RWCell)
            public byte State;
        }

        private const int GLOBAL_SLOT = -1;
        private const int ELEMENT_SLOT = 0;

        private Cell[] _slots = new Cell[8];
        private int _count;                              // high-water of allocated slots
        private readonly object _struct = new object();  // serializes slot alloc/reclaim/publish only
        private long _global;
        private readonly int _globalParkHash;
        private volatile bool _lockdown;

        public ComponentBag()
        {
            _globalParkHash = RWCell.Mix(RuntimeHelpers.GetHashCode(this), GLOBAL_SLOT);
        }

        public bool IsLockdown { get { return _lockdown; } }

        // ───────── token plumbing ─────────

        internal override void ReleaseSlot(object container, int slot, byte mode)
        {
            if (slot == GLOBAL_SLOT)
            {
                RWCell.Exit(this, GLOBAL_SLOT, _globalParkHash, ref _global);
                return;
            }
            Cell c = (Cell)container;
            RWCell.Exit(c, ELEMENT_SLOT, RuntimeHelpers.GetHashCode(c), ref c.Lock);
        }

        private RWToken GlobalRead()
        {
            return RWCell.Enter(this, GLOBAL_SLOT, _globalParkHash, ref _global, false)
                ? new RWToken(this, this, GLOBAL_SLOT, 0) : default(RWToken);
        }

        private RWToken GlobalWrite()
        {
            return RWCell.Enter(this, GLOBAL_SLOT, _globalParkHash, ref _global, true)
                ? new RWToken(this, this, GLOBAL_SLOT, 1) : default(RWToken);
        }

        private RWToken CellLock(Cell c, bool write)
        {
            int ph = RuntimeHelpers.GetHashCode(c);
            return RWCell.Enter(c, ELEMENT_SLOT, ph, ref c.Lock, write)
                ? new RWToken(this, c, ELEMENT_SLOT, write ? (byte)1 : (byte)0) : default(RWToken);
        }

        // A write lock for a structural mutation is usable iff it is real, or we are single-thread.
        // A non-real token in multi-thread mode means a cross-mode same-thread conflict -> refuse.
        private static bool WriteUsable(RWToken t) { return t.IsReal || Defines.OneThreadMode; }

        // ───────── structural helpers (Find is lock-free; mutators run under lock(_struct)) ─────────

        private Cell Find(int key)
        {
            Cell[] slots = Volatile.Read(ref _slots);
            int n = Volatile.Read(ref _count);
            if (n > slots.Length) n = slots.Length;
            for (int i = 0; i < n; i++)
            {
                Cell c = slots[i];
                if (c != null && c.State != FREE && c.Key == key) return c;
            }
            return null;
        }

        private Cell AllocOrReclaim() // under lock(_struct)
        {
            Cell[] slots = _slots;
            int n = _count;
            Cell absentIdle = null;
            for (int i = 0; i < n; i++)
            {
                Cell c = slots[i];
                if (c == null) continue;
                if (c.State == FREE && Volatile.Read(ref c.Lock) == 0L) return c;
                if (absentIdle == null && c.State == ABSENT && Volatile.Read(ref c.Lock) == 0L) absentIdle = c;
            }
            if (absentIdle != null) return absentIdle;
            if (n == slots.Length)
            {
                Cell[] grown = new Cell[slots.Length * 2];
                Array.Copy(slots, grown, n);
                Volatile.Write(ref _slots, grown);
                slots = grown;
            }
            Cell nc = new Cell();
            slots[n] = nc;
            Volatile.Write(ref _count, n + 1);
            return nc;
        }

        // ───────── present: read / write ─────────

        public bool TryGetLocked(int key, bool write, out TValue value, out RWToken token)
        {
            token = default(RWToken);
            value = default(TValue);
            using (GlobalRead())
            {
                while (true)
                {
                    Cell c = Find(key);
                    if (c == null || c.State != PRESENT) return false;
                    RWToken t = CellLock(c, write);
                    if (c.State == PRESENT && c.Key == key)
                    {
                        value = c.Value;
                        token = t;
                        return true;
                    }
                    t.Dispose();
                }
            }
        }

        public bool TryGetReadLocked(int key, out TValue value, out RWToken token) { return TryGetLocked(key, false, out value, out token); }
        public bool TryGetWriteLocked(int key, out TValue value, out RWToken token) { return TryGetLocked(key, true, out value, out token); }

        public void ExecuteReadLocked(int key, Action<int, TValue> action)
        {
            TValue v; RWToken t;
            if (TryGetLocked(key, false, out v, out t)) { try { action(key, v); } catch { } t.Dispose(); }
        }

        public void ExecuteWriteLocked(int key, Action<int, TValue> action)
        {
            TValue v; RWToken t;
            if (TryGetLocked(key, true, out v, out t)) { try { action(key, v); } catch { } t.Dispose(); }
        }

        // ───────── absence hold: shared (read) or exclusive (write) ─────────

        public bool Hold(int key, bool exclusive, out RWToken token)
        {
            token = default(RWToken);
            if (Defines.OneThreadMode) return true;
            using (GlobalRead())
            {
                if (_lockdown) return false;
                while (true)
                {
                    Cell c;
                    bool created = false;
                    RWToken createdTok = default(RWToken);
                    lock (_struct)
                    {
                        c = Find(key);
                        if (c == null)
                        {
                            c = AllocOrReclaim();
                            c.Key = key;
                            c.Value = null;
                            createdTok = CellLock(c, exclusive); // uncontended
                            c.State = ABSENT;
                            created = true;
                        }
                        else if (c.State == PRESENT)
                        {
                            return false;
                        }
                    }
                    if (created) { token = createdTok; return true; }

                    RWToken t = CellLock(c, exclusive);
                    if (c.Key == key && c.State == ABSENT) { token = t; return true; }
                    t.Dispose();
                    if (c.Key == key && c.State == PRESENT) return false;
                }
            }
        }

        public bool TryHoldShared(int key, out RWToken token) { return Hold(key, false, out token); }
        public bool TryHoldExclusive(int key, out RWToken token) { return Hold(key, true, out token); }

        public bool ExecuteHoldRead(int key, Action action)
        {
            RWToken t;
            if (Hold(key, false, out t)) { try { action(); } catch { } t.Dispose(); return true; }
            return false;
        }

        public bool ExecuteHoldWrite(int key, Action action)
        {
            RWToken t;
            if (Hold(key, true, out t)) { try { action(); } catch { } t.Dispose(); return true; }
            return false;
        }

        // ───────── add / addOrChange / change (absent->present or value replace) ─────────

        public bool TryAdd(int key, TValue value)
        {
            using (GlobalRead())
            {
                if (_lockdown) return false;
                while (true)
                {
                    Cell c;
                    lock (_struct)
                    {
                        c = Find(key);
                        if (c == null)
                        {
                            c = AllocOrReclaim();
                            c.Key = key; c.Value = value; c.State = PRESENT;
                            return true;
                        }
                        if (c.State == PRESENT) return false;
                    }
                    RWToken t = CellLock(c, true);
                    if (!WriteUsable(t)) { t.Dispose(); return false; } // cross-mode same-thread: refuse
                    bool added = false, done = false;
                    lock (_struct)
                    {
                        if (c.Key == key && c.State == ABSENT) { c.Value = value; c.State = PRESENT; added = true; done = true; }
                        else if (c.Key == key && c.State == PRESENT) { done = true; }
                    }
                    t.Dispose();
                    if (done) return added;
                }
            }
        }

        /// <summary>Add if absent, else replace the value. Returns true if a new entry was added.</summary>
        public bool AddOrChange(int key, TValue value, out TValue oldValue)
        {
            oldValue = default(TValue);
            using (GlobalRead())
            {
                if (_lockdown) return false;
                while (true)
                {
                    Cell c;
                    lock (_struct)
                    {
                        c = Find(key);
                        if (c == null)
                        {
                            c = AllocOrReclaim();
                            c.Key = key; c.Value = value; c.State = PRESENT;
                            return true; // added
                        }
                    }
                    RWToken t = CellLock(c, true);
                    if (!WriteUsable(t)) { t.Dispose(); return false; }
                    bool done = false, added = false; TValue old = default(TValue);
                    lock (_struct)
                    {
                        if (c.Key == key && c.State == ABSENT) { c.Value = value; c.State = PRESENT; added = true; done = true; }
                        else if (c.Key == key && c.State == PRESENT) { old = c.Value; c.Value = value; done = true; }
                    }
                    t.Dispose();
                    if (done) { oldValue = old; return added; }
                }
            }
        }

        /// <summary>Add and run <paramref name="action"/> while holding the new component's write lock.</summary>
        public bool ExecuteOnAddLocked(int key, TValue value, Action<int, TValue> action)
        {
            using (GlobalRead())
            {
                if (_lockdown) return false;
                while (true)
                {
                    Cell c;
                    RWToken tok = default(RWToken);
                    bool ready = false;
                    lock (_struct)
                    {
                        c = Find(key);
                        if (c == null)
                        {
                            c = AllocOrReclaim();
                            c.Key = key; c.Value = value;
                            tok = CellLock(c, true); // uncontended
                            c.State = PRESENT;
                            ready = true;
                        }
                        else if (c.State == PRESENT)
                        {
                            return false;
                        }
                    }
                    if (!ready)
                    {
                        RWToken t = CellLock(c, true);
                        if (!WriteUsable(t)) { t.Dispose(); return false; }
                        bool ok = false;
                        lock (_struct)
                        {
                            if (c.Key == key && c.State == ABSENT) { c.Value = value; c.State = PRESENT; ok = true; }
                            else if (c.Key == key && c.State == PRESENT) { t.Dispose(); return false; }
                        }
                        if (!ok) { t.Dispose(); continue; }
                        tok = t;
                    }
                    try { action(key, value); } catch { }
                    tok.Dispose();
                    return true;
                }
            }
        }

        /// <summary>If present, replace the value under the write lock and run onChange(key,new,old).</summary>
        public bool ExecuteOnChangeLocked(int key, TValue value, Action<int, TValue, TValue> onChange)
        {
            using (GlobalRead())
            {
                while (true)
                {
                    Cell c = Find(key);
                    if (c == null || c.State != PRESENT) return false;
                    RWToken t = CellLock(c, true);
                    if (!WriteUsable(t)) { t.Dispose(); return false; }
                    if (c.Key == key && c.State == PRESENT)
                    {
                        TValue old = c.Value;
                        c.Value = value; // safe: exclusive write lock, no concurrent readers
                        if (onChange != null) { try { onChange(key, value, old); } catch { } }
                        t.Dispose();
                        return true;
                    }
                    t.Dispose();
                }
            }
        }

        public bool ExecuteOnAddOrChangeLocked(int key, TValue value, Action<int, TValue> onAdd, Action<int, TValue, TValue> onChange)
        {
            TValue old;
            bool added = AddOrChange(key, value, out old);
            if (added) { if (onAdd != null) { try { onAdd(key, value); } catch { } } }
            else { if (onChange != null) { try { onChange(key, value, old); } catch { } } }
            return added;
        }

        // ───────── remove (present->absent, exclusive) ─────────

        public bool Remove(int key, out TValue value)
        {
            return ExecuteOnRemoveLocked(key, out value, null);
        }

        /// <summary>Remove under the write lock, running <paramref name="action"/> on the value first.</summary>
        public bool ExecuteOnRemoveLocked(int key, out TValue value, Action<int, TValue> action)
        {
            value = default(TValue);
            using (GlobalRead())
            {
                while (true)
                {
                    Cell c = Find(key);
                    if (c == null || c.State != PRESENT) return false;
                    RWToken t = CellLock(c, true);
                    if (!WriteUsable(t)) { t.Dispose(); return false; }
                    if (c.Key == key && c.State == PRESENT)
                    {
                        TValue v = c.Value;
                        if (action != null) { try { action(key, v); } catch { } } // under write lock
                        lock (_struct) { c.State = ABSENT; c.Value = null; }
                        t.Dispose();
                        value = v;
                        return true;
                    }
                    if (c.Key == key && c.State == ABSENT) { t.Dispose(); return false; }
                    t.Dispose();
                }
            }
        }

        // ───────── queries ─────────

        public bool ContainsKey(int key)
        {
            using (GlobalRead()) { Cell c = Find(key); return c != null && c.State == PRESENT; }
        }

        public bool TryGetValueUnsafe(int key, out TValue value)
        {
            using (GlobalRead())
            {
                Cell c = Find(key);
                if (c != null && c.State == PRESENT) { value = c.Value; return true; }
            }
            value = default(TValue);
            return false;
        }

        public int Count
        {
            get
            {
                using (GlobalRead())
                {
                    int cnt = 0, n = Volatile.Read(ref _count);
                    Cell[] slots = Volatile.Read(ref _slots);
                    if (n > slots.Length) n = slots.Length;
                    for (int i = 0; i < n; i++) { Cell c = slots[i]; if (c != null && c.State == PRESENT) cnt++; }
                    return cnt;
                }
            }
        }

        public List<KeyValuePair<int, TValue>> Snapshot()
        {
            var list = new List<KeyValuePair<int, TValue>>();
            using (GlobalRead())
            {
                int n = Volatile.Read(ref _count);
                Cell[] slots = Volatile.Read(ref _slots);
                if (n > slots.Length) n = slots.Length;
                for (int i = 0; i < n; i++)
                {
                    Cell c = slots[i];
                    if (c != null && c.State == PRESENT) list.Add(new KeyValuePair<int, TValue>(c.Key, c.Value));
                }
            }
            return list;
        }

        public void EnterLockdown() { using (GlobalWrite()) { _lockdown = true; } }
        public void ExitLockdown() { using (GlobalWrite()) { _lockdown = false; } }
        public RWToken LockStorage() { return GlobalWrite(); }
    }
}
