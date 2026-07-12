using System;
using System.Collections.Concurrent;
using System.Threading;

namespace AECC.Locking.Benchmark
{
    /// <summary>Common surface so the harness can drive old and new backends through the FULL matrix.</summary>
    public interface IBenchBag
    {
        bool TryReadLocked(int key, out IDisposable token);   // present, shared
        bool TryWriteLocked(int key, out IDisposable token);  // present, exclusive
        bool TryAdd(int key, object value);
        bool Remove(int key);
        bool TryHoldShared(int key, out IDisposable token);   // absence, shared (many at once)
        bool TryHoldExclusive(int key, out IDisposable token);// absence, exclusive
        int Count { get; }
    }

    /// <summary>
    /// Conservative stand-in for the CURRENT AECC per-entity storage: ConcurrentDictionary with a
    /// ReaderWriterLockSlim PER cell, a storage-level RWLS, and a lazily-created per-key RWLS for
    /// absence holds. (The real engine additionally keeps a permanent KeysHolding RWLS per added
    /// key, which this model omits — so real legacy memory is WORSE than measured here.)
    /// </summary>
    public sealed class LegacyBag : IBenchBag
    {
        private sealed class LegacyCell
        {
            public object Value;
            public readonly ReaderWriterLockSlim Lock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
        }

        private sealed class Rel : IDisposable
        {
            private ReaderWriterLockSlim _lock;
            private readonly bool _write;
            public Rel(ReaderWriterLockSlim l, bool write) { _lock = l; _write = write; }
            public void Dispose()
            {
                ReaderWriterLockSlim l = _lock;
                if (l == null) return;
                _lock = null;
                if (_write) { if (l.IsWriteLockHeld) l.ExitWriteLock(); }
                else { if (l.IsReadLockHeld) l.ExitReadLock(); }
            }
        }

        private readonly ConcurrentDictionary<int, LegacyCell> _dict = new ConcurrentDictionary<int, LegacyCell>();
        private readonly ReaderWriterLockSlim _global = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
        private readonly ConcurrentDictionary<int, ReaderWriterLockSlim> _holding = new ConcurrentDictionary<int, ReaderWriterLockSlim>();

        private bool TryLock(int key, bool write, out IDisposable token)
        {
            token = null;
            _global.EnterReadLock();
            try
            {
                LegacyCell c;
                if (_dict.TryGetValue(key, out c))
                {
                    if (write) c.Lock.EnterWriteLock(); else c.Lock.EnterReadLock();
                    token = new Rel(c.Lock, write);
                    return true;
                }
                return false;
            }
            finally { _global.ExitReadLock(); }
        }

        public bool TryReadLocked(int key, out IDisposable token) { return TryLock(key, false, out token); }
        public bool TryWriteLocked(int key, out IDisposable token) { return TryLock(key, true, out token); }

        public bool TryAdd(int key, object value)
        {
            _global.EnterReadLock();
            try { return _dict.TryAdd(key, new LegacyCell { Value = value }); }
            finally { _global.ExitReadLock(); }
        }

        public bool Remove(int key)
        {
            _global.EnterReadLock();
            try
            {
                LegacyCell c;
                if (_dict.TryGetValue(key, out c))
                {
                    c.Lock.EnterWriteLock();
                    try { LegacyCell r; return _dict.TryRemove(key, out r); }
                    finally { c.Lock.ExitWriteLock(); }
                }
                return false;
            }
            finally { _global.ExitReadLock(); }
        }

        private bool TryHold(int key, bool exclusive, out IDisposable token)
        {
            token = null;
            if (_dict.ContainsKey(key)) return false;
            ReaderWriterLockSlim hl = _holding.GetOrAdd(key, _ => new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion));
            if (exclusive) { if (!hl.TryEnterWriteLock(0)) return false; }
            else { if (!hl.TryEnterReadLock(0)) return false; }
            if (_dict.ContainsKey(key))
            {
                if (exclusive) hl.ExitWriteLock(); else hl.ExitReadLock();
                return false;
            }
            token = new Rel(hl, exclusive);
            return true;
        }

        public bool TryHoldShared(int key, out IDisposable token) { return TryHold(key, false, out token); }
        public bool TryHoldExclusive(int key, out IDisposable token) { return TryHold(key, true, out token); }

        public int Count { get { return _dict.Count; } }
    }

    /// <summary>Adapter exposing the new <see cref="ComponentBag{TValue}"/> through <see cref="IBenchBag"/>.
    /// Boxing the struct token to IDisposable costs one small alloc per op — same as the legacy Rel —
    /// so the workload comparison stays fair. Zero-alloc of the new path is shown by the pure-cycle test.</summary>
    public sealed class NewBagAdapter : IBenchBag
    {
        private readonly ComponentBag<object> _bag = new ComponentBag<object>();

        public bool TryReadLocked(int key, out IDisposable token)
        {
            object v; RWToken t;
            if (_bag.TryGetReadLocked(key, out v, out t)) { token = t; return true; }
            token = null; return false;
        }

        public bool TryWriteLocked(int key, out IDisposable token)
        {
            object v; RWToken t;
            if (_bag.TryGetWriteLocked(key, out v, out t)) { token = t; return true; }
            token = null; return false;
        }

        public bool TryAdd(int key, object value) { return _bag.TryAdd(key, value); }
        public bool Remove(int key) { object v; return _bag.Remove(key, out v); }

        public bool TryHoldShared(int key, out IDisposable token)
        {
            RWToken t;
            if (_bag.TryHoldShared(key, out t)) { token = t; return true; }
            token = null; return false;
        }

        public bool TryHoldExclusive(int key, out IDisposable token)
        {
            RWToken t;
            if (_bag.TryHoldExclusive(key, out t)) { token = t; return true; }
            token = null; return false;
        }

        public int Count { get { return _bag.Count; } }
    }
}
