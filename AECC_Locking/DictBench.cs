using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace AECC.Locking.Benchmark
{
    /// <summary>
    /// Side-by-side performance comparison of the ORIGINAL LockedDictionary locking architecture
    /// against the new LockedDictionarySlim, under multithreaded load.
    ///
    /// The legacy model below reproduces the original's PRODUCTION locking shape:
    ///   - a ReaderWriterLockSlim per entry (LockedValue.lockValue : RWLock -> ReaderWriterLockSlim),
    ///   - a global ReaderWriterLockSlim taken (read) once per operation,
    ///   - a per-key ReaderWriterLockSlim for absence holds (KeysHoldingStorage),
    ///   - a heap-allocated lock token per acquisition (RWLock.LockToken).
    /// It deliberately omits RWLock.cs's debug instrumentation (stack-trace capture, deadlock-order
    /// logging) which is gated off by Defines flags in production and would otherwise dominate the
    /// measurement. The four factors above ARE the original's memory/GC/throughput profile and are
    /// exactly what the new design targets. To benchmark the genuine AECC.Collections.LockedDictionary
    /// binary (with whatever Defines you ship), wrap it behind the same two calls used here and run
    /// this harness inside your solution.
    /// </summary>
    public static class DictBench
    {
        private static readonly object Payload = new object();

        // ───────────────────────── legacy faithful model ─────────────────────────
        private sealed class LegacyDict
        {
            private sealed class LV
            {
                public object Value;
                public readonly ReaderWriterLockSlim Lock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
            }
            private sealed class Tok : IDisposable
            {
                private readonly ReaderWriterLockSlim _l; private readonly bool _w;
                public Tok(ReaderWriterLockSlim l, bool w) { _l = l; _w = w; }
                public void Dispose() { if (_w) { if (_l.IsWriteLockHeld) _l.ExitWriteLock(); } else { if (_l.IsReadLockHeld) _l.ExitReadLock(); } }
            }

            private readonly ConcurrentDictionary<int, LV> _d = new ConcurrentDictionary<int, LV>();
            private readonly ReaderWriterLockSlim _global = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
            private readonly ConcurrentDictionary<int, ReaderWriterLockSlim> _holds = new ConcurrentDictionary<int, ReaderWriterLockSlim>();
            private readonly bool _holdKeys;
            private volatile bool _lockdown;

            public LegacyDict(bool holdKeys) { _holdKeys = holdKeys; }
            public int Count { get { _global.EnterReadLock(); try { return _d.Count; } finally { _global.ExitReadLock(); } } }

            private ReaderWriterLockSlim HoldLock(int key)
            {
                ReaderWriterLockSlim l;
                if (_holds.TryGetValue(key, out l)) return l;
                l = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
                return _holds.GetOrAdd(key, l);
            }

            public bool TryGetLocked(int key, bool write, out object value, out IDisposable token)
            {
                value = null; token = null;
                _global.EnterReadLock();
                try
                {
                    LV lv;
                    if (!_d.TryGetValue(key, out lv)) return false;
                    if (write) lv.Lock.EnterWriteLock(); else lv.Lock.EnterReadLock();
                    // revalidate (the original re-checks the entry under the cell lock)
                    LV check;
                    if (!_d.TryGetValue(key, out check) || !ReferenceEquals(check, lv))
                    {
                        if (write) lv.Lock.ExitWriteLock(); else lv.Lock.ExitReadLock();
                        return false;
                    }
                    value = lv.Value;
                    token = new Tok(lv.Lock, write); // heap alloc per op (faithful to RWLock.LockToken)
                    return true;
                }
                finally { _global.ExitReadLock(); }
            }

            public bool HoldKey(int key, out IDisposable token)
            {
                token = null;
                if (_lockdown || !_holdKeys) return false;
                _global.EnterReadLock();
                try
                {
                    ReaderWriterLockSlim h = HoldLock(key);
                    h.EnterReadLock(); // shared hold
                    if (!_d.ContainsKey(key)) { token = new Tok(h, false); return true; }
                    h.ExitReadLock();
                    return false;
                }
                finally { _global.ExitReadLock(); }
            }

            public bool TryAdd(int key, object value)
            {
                if (_lockdown) return false;
                _global.EnterReadLock();
                try
                {
                    if (_d.ContainsKey(key)) return false;
                    if (_holdKeys)
                    {
                        ReaderWriterLockSlim h = HoldLock(key);
                        h.EnterWriteLock(); // exclude shared holders during add
                        try { if (_d.ContainsKey(key)) return false; return _d.TryAdd(key, new LV { Value = value }); }
                        finally { h.ExitWriteLock(); }
                    }
                    return _d.TryAdd(key, new LV { Value = value });
                }
                finally { _global.ExitReadLock(); }
            }

            public void AddOrChange(int key, object value)
            {
                if (_lockdown) return;
                _global.EnterReadLock();
                try
                {
                    while (true)
                    {
                        LV lv;
                        if (_d.TryGetValue(key, out lv))
                        {
                            lv.Lock.EnterWriteLock();
                            try
                            {
                                LV check;
                                if (_d.TryGetValue(key, out check) && ReferenceEquals(check, lv)) { lv.Value = value; return; }
                            }
                            finally { lv.Lock.ExitWriteLock(); }
                            continue;
                        }
                        if (_holdKeys)
                        {
                            ReaderWriterLockSlim h = HoldLock(key);
                            h.EnterWriteLock();
                            try { if (!_d.ContainsKey(key)) { _d.TryAdd(key, new LV { Value = value }); return; } }
                            finally { h.ExitWriteLock(); }
                            continue;
                        }
                        if (_d.TryAdd(key, new LV { Value = value })) return;
                    }
                }
                finally { _global.ExitReadLock(); }
            }

            public bool Remove(int key)
            {
                if (_lockdown) return false;
                _global.EnterReadLock();
                try
                {
                    LV lv;
                    if (!_d.TryGetValue(key, out lv)) return false;
                    lv.Lock.EnterWriteLock();
                    try
                    {
                        LV check;
                        if (!_d.TryGetValue(key, out check) || !ReferenceEquals(check, lv)) return false;
                        LV removed; return _d.TryRemove(key, out removed);
                    }
                    finally { lv.Lock.ExitWriteLock(); }
                }
                finally { _global.ExitReadLock(); }
            }

            public bool ContainsKey(int key)
            {
                _global.EnterReadLock();
                try { return _d.ContainsKey(key); }
                finally { _global.ExitReadLock(); }
            }
        }

        // ───────────────────────── workload ─────────────────────────
        // op weights (cumulative over 100): read 28, write 16, holdShared 14, add 12, addOrChange 10, remove 12, contains 8
        private static int PickOp(Random r)
        {
            int x = r.Next(100);
            if (x < 28) return 0;            // read
            if (x < 44) return 1;            // write
            if (x < 58) return 2;            // holdShared
            if (x < 70) return 3;            // add
            if (x < 80) return 4;            // addOrChange
            if (x < 92) return 5;            // remove
            return 6;                        // contains
        }

        private struct Result { public long Ops; public double Sec; public long Bytes; public int G0, G1, G2; }

        private static Result RunNew(int dicts, int keys, int threads, int durationMs)
        {
            var maps = new LockedDictionarySlim<int, object>[dicts];
            for (int d = 0; d < dicts; d++)
            {
                var m = new LockedDictionarySlim<int, object>(true);
                for (int k = 0; k < keys; k++) if ((k & 1) == 0) m.TryAdd(k, Payload);
                maps[d] = m;
            }
            long ops = 0; bool stop = false;
            var ws = new Thread[threads];
            for (int i = 0; i < threads; i++) ws[i] = new Thread(() =>
            {
                var r = new Random(Guid.NewGuid().GetHashCode()); long local = 0;
                while (!Volatile.Read(ref stop))
                {
                    var m = maps[r.Next(dicts)]; int k = r.Next(keys);
                    switch (PickOp(r))
                    {
                        case 0: { object v; RWToken t; if (m.TryGetLockedElement(k, out v, out t, false)) t.Dispose(); break; }
                        case 1: { object v; RWToken t; if (m.TryGetLockedElement(k, out v, out t, true)) t.Dispose(); break; }
                        case 2: { RWToken t; if (m.HoldKey(k, false, out t)) t.Dispose(); break; }
                        case 3: m.TryAdd(k, Payload); break;
                        case 4: m[k] = Payload; break;
                        case 5: m.Remove(k); break;
                        default: { bool b = m.ContainsKey(k); GC.KeepAlive(b); break; }
                    }
                    local++;
                }
                Interlocked.Add(ref ops, local);
            });
            return Drive(ws, ref stop, durationMs, () => ops);
        }

        private static Result RunLegacy(int dicts, int keys, int threads, int durationMs)
        {
            var maps = new LegacyDict[dicts];
            for (int d = 0; d < dicts; d++)
            {
                var m = new LegacyDict(true);
                for (int k = 0; k < keys; k++) if ((k & 1) == 0) m.TryAdd(k, Payload);
                maps[d] = m;
            }
            long ops = 0; bool stop = false;
            var ws = new Thread[threads];
            for (int i = 0; i < threads; i++) ws[i] = new Thread(() =>
            {
                var r = new Random(Guid.NewGuid().GetHashCode()); long local = 0;
                while (!Volatile.Read(ref stop))
                {
                    var m = maps[r.Next(dicts)]; int k = r.Next(keys);
                    switch (PickOp(r))
                    {
                        case 0: { object v; IDisposable t; if (m.TryGetLocked(k, false, out v, out t)) t.Dispose(); break; }
                        case 1: { object v; IDisposable t; if (m.TryGetLocked(k, true, out v, out t)) t.Dispose(); break; }
                        case 2: { IDisposable t; if (m.HoldKey(k, out t)) t.Dispose(); break; }
                        case 3: m.TryAdd(k, Payload); break;
                        case 4: m.AddOrChange(k, Payload); break;
                        case 5: m.Remove(k); break;
                        default: { bool b = m.ContainsKey(k); GC.KeepAlive(b); break; }
                    }
                    local++;
                }
                Interlocked.Add(ref ops, local);
            });
            return Drive(ws, ref stop, durationMs, () => ops);
        }

        private static Result Drive(Thread[] ws, ref bool stop, int durationMs, Func<long> opsGetter)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            long b0 = GC.GetTotalAllocatedBytes(true);
            var sw = Stopwatch.StartNew();
            foreach (var t in ws) t.Start();
            Thread.Sleep(durationMs);
            Volatile.Write(ref stop, true);
            foreach (var t in ws) t.Join();
            sw.Stop();
            long b1 = GC.GetTotalAllocatedBytes(true);
            return new Result
            {
                Ops = opsGetter(),
                Sec = sw.Elapsed.TotalSeconds,
                Bytes = b1 - b0,
                G0 = GC.CollectionCount(0) - g0,
                G1 = GC.CollectionCount(1) - g1,
                G2 = GC.CollectionCount(2) - g2
            };
        }

        private static double BytesPerEntryNew(int k)
        {
            long m0 = GC.GetTotalMemory(true);
            var d = new LockedDictionarySlim<int, object>(true);
            for (int i = 0; i < k; i++) d.TryAdd(i, Payload);
            long m1 = GC.GetTotalMemory(true);
            GC.KeepAlive(d);
            return (m1 - m0) / (double)k;
        }

        private static double BytesPerEntryLegacy(int k)
        {
            long m0 = GC.GetTotalMemory(true);
            var d = new LegacyDict(true);
            for (int i = 0; i < k; i++) d.TryAdd(i, Payload);
            long m1 = GC.GetTotalMemory(true);
            GC.KeepAlive(d);
            return (m1 - m0) / (double)k;
        }

        public static void Run(int dicts, int keys, int threads, int durationMs)
        {
            Console.WriteLine("================ AECC dictionary performance comparison ================");
            Console.WriteLine(">>> dictbench build: v2  (RWCell reentry fast-path lightened; bench logic unchanged) <<<");
            Console.WriteLine("dicts={0}  keys={1}  threads={2}  duration={3}ms/impl", dicts, keys, threads, durationMs);
            Console.WriteLine("legacy = ReaderWriterLockSlim per cell + global RWLS + per-key hold RWLS + token-per-op");
            Console.WriteLine("new    = inline packed-long per cell (RWCell), no global lock, zero-alloc struct token");
            Console.WriteLine();

            // ── memory ──
            const int MemK = 200000;
            Console.WriteLine("---- memory (build {0:N0} entries, shared payload) ----", MemK);
            double legBpe = BytesPerEntryLegacy(MemK);
            double newBpe = BytesPerEntryNew(MemK);
            Console.WriteLine("  legacy : {0,8:N1} B/entry", legBpe);
            Console.WriteLine("  new    : {0,8:N1} B/entry   ({1:P0} of legacy)", newBpe, newBpe / legBpe);
            Console.WriteLine("  at 10,000,000 entries:  legacy ~{0:N2} GB   new ~{1:N2} GB",
                legBpe * 10000000.0 / 1e9, newBpe * 10000000.0 / 1e9);
            Console.WriteLine();

            // ── throughput (warm up each, then measure) ──
            Console.WriteLine("---- throughput under load (read/write/hold/add/change/remove/contains) ----");
            RunNew(dicts, keys, threads, 800);     // warm-up
            RunLegacy(dicts, keys, threads, 800);  // warm-up
            Result rn = RunNew(dicts, keys, threads, durationMs);
            Result rl = RunLegacy(dicts, keys, threads, durationMs);

            double newTput = rn.Ops / rn.Sec, legTput = rl.Ops / rl.Sec;
            Console.WriteLine("  {0,-8}{1,16}{2,14}{3,12}{4,16}", "impl", "ops/sec", "ns/op", "B/op", "GC g0/g1/g2");
            Console.WriteLine("  {0,-8}{1,16:N0}{2,14:N1}{3,12:N1}{4,16}", "legacy", legTput,
                1e9 / legTput * threads, rl.Bytes / (double)rl.Ops, rl.G0 + "/" + rl.G1 + "/" + rl.G2);
            Console.WriteLine("  {0,-8}{1,16:N0}{2,14:N1}{3,12:N1}{4,16}", "new", newTput,
                1e9 / newTput * threads, rn.Bytes / (double)rn.Ops, rn.G0 + "/" + rn.G1 + "/" + rn.G2);
            Console.WriteLine();
            Console.WriteLine("  throughput:  new is {0:N2}x legacy", newTput / legTput);
            Console.WriteLine("  allocation:  new {0:N1} B/op vs legacy {1:N1} B/op  ({2:N0}x less)",
                rn.Bytes / (double)rn.Ops, rl.Bytes / (double)rl.Ops,
                (rl.Bytes / (double)rl.Ops) / Math.Max(1.0, rn.Bytes / (double)rn.Ops));
            Console.WriteLine("  note: ns/op is wall-clock per op times thread count (aggregate core-time), not single-thread latency.");
            Console.WriteLine();
            Console.WriteLine("Done.");
        }
    }
}