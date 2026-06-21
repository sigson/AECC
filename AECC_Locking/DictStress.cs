using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace AECC.Locking.Benchmark
{
    /// <summary>
    /// Maximum-contention consistency stress for LockedDictionarySlim. A SMALL number of
    /// dictionaries over a SMALL key space are bombarded by many threads doing every locked
    /// transactional operation at random, to surface consistency-corruption edge cases.
    ///
    /// Two independent nets catch corruption:
    ///   1. In-flight mutual-exclusion validator. Each value is a <see cref="Box"/> carrying live
    ///      reader/writer counters and a stamp. Inside a READ lock the validator asserts no writer
    ///      is active and the stamp is stable; inside a WRITE lock it asserts it is the sole writer
    ///      with no readers and bumps the stamp. The dictionary's locks must make these hold for
    ///      EVERY interleaving — any violation means the lock engine corrupted consistency.
    ///   2. Post-run quiescent scan (LockedDictionarySlim.DebugVerifyQuiescent): after all threads
    ///      stop, every cell's lock state must have drained to 0 (no leaked/unbalanced locks) and
    ///      counts must be coherent.
    /// Plus a watchdog (stuck worker => deadlock) and an exception counter.
    ///
    /// Unsafe* operations are intentionally excluded: they are documented as providing no locking
    /// guarantee, so racing them is expected and is not a consistency bug.
    /// </summary>
    public static class DictStress
    {
        private sealed class Box
        {
            public long Stamp;
            public int W;   // active writers (must never exceed 1, never >0 while readers active)
            public int R;   // active readers (must be 0 while a writer is active)
            public int Key;
        }

        private static void ValidateWrite(Box b)
        {
            if (b == null) { LockedDictionarySlim<int, Box>.DebugFail("write: null value under write lock"); return; }
            int w = Interlocked.Increment(ref b.W);
            int r = Volatile.Read(ref b.R);
            if (w != 1) LockedDictionarySlim<int, Box>.DebugFail("WRITE not exclusive (W=" + w + ") key=" + b.Key);
            if (r != 0) LockedDictionarySlim<int, Box>.DebugFail("WRITE with active readers (R=" + r + ") key=" + b.Key);
            long s = Volatile.Read(ref b.Stamp);
            Volatile.Write(ref b.Stamp, s + 1); // safe: we are the exclusive writer
            Thread.SpinWait(30);
            if (Volatile.Read(ref b.R) != 0) LockedDictionarySlim<int, Box>.DebugFail("WRITE saw reader mid-section key=" + b.Key);
            if (Volatile.Read(ref b.W) != 1) LockedDictionarySlim<int, Box>.DebugFail("WRITE saw second writer key=" + b.Key);
            Interlocked.Decrement(ref b.W);
        }

        private static void ValidateRead(Box b)
        {
            if (b == null) { LockedDictionarySlim<int, Box>.DebugFail("read: null value under read lock"); return; }
            Interlocked.Increment(ref b.R);
            if (Volatile.Read(ref b.W) != 0) LockedDictionarySlim<int, Box>.DebugFail("READ saw active writer key=" + b.Key);
            long s1 = Volatile.Read(ref b.Stamp);
            Thread.SpinWait(30);
            long s2 = Volatile.Read(ref b.Stamp);
            if (s1 != s2) LockedDictionarySlim<int, Box>.DebugFail("READ stamp changed under read lock key=" + b.Key);
            if (Volatile.Read(ref b.W) != 0) LockedDictionarySlim<int, Box>.DebugFail("READ saw writer (post) key=" + b.Key);
            Interlocked.Decrement(ref b.R);
        }

        public static void Run(int dicts, int keys, int threads, int durationMs)
        {
            Console.WriteLine("================ AECC dictionary consistency stress ================");
            Console.WriteLine(">>> dictstress build: v4  (LockStorage hard-freeze barrier validated) <<<");
            Console.WriteLine("dicts={0}  keys={1}  threads={2}  duration={3}ms", dicts, keys, threads, durationMs);
            Console.WriteLine("(small dicts + small key space + many threads = maximum collision rate)");
            Console.WriteLine();

            LockedDictionarySlim<int, Box>.DebugReset();
            LockedDictionarySlim<int, Box>.DebugChecks = true;

            var maps = new LockedDictionarySlim<int, Box>[dicts];
            for (int d = 0; d < dicts; d++)
            {
                var m = new LockedDictionarySlim<int, Box>(true); // HoldKeys=true to exercise holds
                for (int k = 0; k < keys; k++)
                    if ((k & 1) == 0) m.TryAdd(k, new Box { Key = k }); // ~half present, half absent
                maps[d] = m;
            }

            long[] opCounts = new long[14];
            long exceptions = 0;
            bool stop = false;

            var workers = new Thread[threads];
            for (int i = 0; i < threads; i++)
            {
                workers[i] = new Thread(() =>
                {
                    var rng = new Random(Guid.NewGuid().GetHashCode());
                    var multi = new RWToken[8];
                    while (!Volatile.Read(ref stop))
                    {
                        var m = maps[rng.Next(dicts)];
                        int k = rng.Next(keys);
                        int op = rng.Next(14);
                        try
                        {
                            switch (op)
                            {
                                case 0: // read present (validate exclusion)
                                    m.ExecuteReadLocked(k, (kk, box) => ValidateRead(box));
                                    break;
                                case 1: // write present (validate exclusion)
                                    m.ExecuteWriteLocked(k, (kk, box) => ValidateWrite(box));
                                    break;
                                case 2: // TryGetLockedElement read
                                {
                                    Box v; RWToken t;
                                    if (m.TryGetLockedElement(k, out v, out t, false))
                                    {
                                        if (v == null) LockedDictionarySlim<int, Box>.DebugFail("TryGet(read) returned null key=" + k);
                                        else ValidateRead(v);
                                        t.Dispose();
                                    }
                                    break;
                                }
                                case 3: // TryGetLockedElement write
                                {
                                    Box v; RWToken t;
                                    if (m.TryGetLockedElement(k, out v, out t, true))
                                    {
                                        if (v == null) LockedDictionarySlim<int, Box>.DebugFail("TryGet(write) returned null key=" + k);
                                        else ValidateWrite(v);
                                        t.Dispose();
                                    }
                                    break;
                                }
                                case 4: // shared hold of absence
                                {
                                    RWToken t;
                                    if (m.HoldKey(k, false, out t))
                                    {
                                        if (m.ContainsKey(k)) LockedDictionarySlim<int, Box>.DebugFail("present under SHARED hold key=" + k);
                                        Thread.SpinWait(30);
                                        if (m.ContainsKey(k)) LockedDictionarySlim<int, Box>.DebugFail("became present under SHARED hold key=" + k);
                                        t.Dispose();
                                    }
                                    break;
                                }
                                case 5: // exclusive hold of absence
                                {
                                    RWToken t;
                                    if (m.HoldKey(k, true, out t))
                                    {
                                        if (m.ContainsKey(k)) LockedDictionarySlim<int, Box>.DebugFail("present under EXCLUSIVE hold key=" + k);
                                        Thread.SpinWait(30);
                                        t.Dispose();
                                    }
                                    break;
                                }
                                case 6: // add (may fail if present or held)
                                    m.TryAdd(k, new Box { Key = k });
                                    break;
                                case 7: // add-or-change (under lock)
                                    m.ExecuteOnAddChangeLocked(k, new Box { Key = k }, (kk, nv, ov) => { });
                                    break;
                                case 8: // remove (public bool API)
                                    m.Remove(k);
                                    break;
                                case 9: // execute-on-remove (action under write lock)
                                {
                                    Box v;
                                    m.ExecuteOnRemoveLocked(k, out v, (kk, box) => { if (box != null) ValidateWrite(box); });
                                    break;
                                }
                                case 10: // execute-on-add (action under the new cell's write lock)
                                    m.ExecuteOnAddLocked(k, new Box { Key = k }, (kk, box) => ValidateWrite(box));
                                    break;
                                case 11: // contains + count (read coherence)
                                {
                                    bool present = m.ContainsKey(k);
                                    int cnt = m.Count;
                                    if (cnt < 0 || cnt > keys) LockedDictionarySlim<int, Box>.DebugFail("Count out of range: " + cnt);
                                    GC.KeepAlive(present);
                                    break;
                                }
                                case 12: // multi-key combinator: lock several keys in CANONICAL (sorted) order
                                {
                                    int take = 1 + rng.Next(4);
                                    int[] ks = new int[take];
                                    for (int j = 0; j < take; j++) ks[j] = rng.Next(keys);
                                    Array.Sort(ks); // canonical order prevents lock-ordering deadlock
                                    int n = 0;
                                    for (int j = 0; j < take && n < multi.Length; j++)
                                    {
                                        if (j > 0 && ks[j] == ks[j - 1]) continue; // skip duplicates (reentry not the point here)
                                        Box v; RWToken t;
                                        if (m.TryGetLockedElement(ks[j], out v, out t, false)) // all read locks
                                        {
                                            if (v != null) ValidateRead(v);
                                            multi[n++] = t;
                                        }
                                    }
                                    for (int j = n - 1; j >= 0; j--) { var t = multi[j]; t.Dispose(); }
                                    break;
                                }
                                default: // 13: rare destructive ops — lockdown cycle / snapshot
                                {
                                    int sub = rng.Next(50);
                                    if (sub == 0)
                                    {
                                        m.EnterLockdown();
                                        m.TryAdd(k, new Box { Key = k }); // expected to fail under lockdown
                                        m.ExitLockdown();
                                    }
                                    else if (sub == 1)
                                    {
                                        var snap = m.ClearSnapshot();
                                        GC.KeepAlive(snap);
                                    }
                                    else if (sub == 2)
                                    {
                                        // HARD freeze: acquire stop-the-world, verify no mutation slips
                                        // through the barrier while it is held, then release. A deadlock
                                        // here is caught by the watchdog; a count change means a mutator
                                        // bypassed the freeze (barrier bug).
                                        using (m.LockStorage())
                                        {
                                            long c1 = m.Count;
                                            Thread.SpinWait(300);
                                            long c2 = m.Count;
                                            if (c1 != c2)
                                                LockedDictionarySlim<int, Box>.DebugFail("Count changed under LockStorage freeze: " + c1 + " -> " + c2);
                                        }
                                    }
                                    else
                                    {
                                        // enumerate for read coherence
                                        int seen = 0;
                                        foreach (var kv in m) { seen++; if (kv.Value == null) LockedDictionarySlim<int, Box>.DebugFail("enumerate null value key=" + kv.Key); }
                                        GC.KeepAlive(seen);
                                    }
                                    break;
                                }
                            }
                            Interlocked.Increment(ref opCounts[op]);
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref exceptions);
                            LockedDictionarySlim<int, Box>.DebugFail("exception in op " + op + ": " + ex.GetType().Name + " " + ex.Message);
                        }
                    }
                });
            }

            var sw = Stopwatch.StartNew();
            foreach (var t in workers) t.Start();
            Thread.Sleep(durationMs);
            Volatile.Write(ref stop, true);

            bool deadlock = false;
            foreach (var t in workers)
            {
                if (!t.Join(15000)) { deadlock = true; }
            }
            sw.Stop();

            // ── report ──
            long total = 0;
            string[] names = { "read", "write", "tryGetR", "tryGetW", "holdShared", "holdExcl", "add", "addOrChange", "remove", "onRemove", "onAdd", "contains/count", "combinator", "lockdown/snap/enum" };
            Console.WriteLine("---- operation counts ----");
            for (int i = 0; i < opCounts.Length; i++) { total += opCounts[i]; Console.WriteLine("  {0,-20} {1,14:N0}", names[i], opCounts[i]); }
            Console.WriteLine("  {0,-20} {1,14:N0}", "TOTAL", total);
            Console.WriteLine("  throughput: {0:N0} ops/sec", total / (durationMs / 1000.0));
            Console.WriteLine();

            Console.WriteLine("---- consistency verdict ----");
            bool quiescentOk = true;
            for (int d = 0; d < dicts; d++)
            {
                string msg;
                if (!maps[d].DebugVerifyQuiescent(out msg)) { quiescentOk = false; Console.WriteLine("  [FAIL] dict#{0} quiescent: {1}", d, msg); }
            }
            if (quiescentOk) Console.WriteLine("  [PASS] quiescent scan: no leaked locks, counts coherent");
            Console.WriteLine("  [{0}] no deadlock (all workers joined)", deadlock ? "FAIL" : "PASS");
            Console.WriteLine("  [{0}] unexpected exceptions: {1}", exceptions == 0 ? "PASS" : "FAIL", exceptions);

            long viol = LockedDictionarySlim<int, Box>.DebugViolations;
            if (viol == 0 && !deadlock && quiescentOk)
                Console.WriteLine("  [PASS] mutual-exclusion validator: 0 violations across {0:N0} ops", total);
            else
            {
                Console.WriteLine("  [FAIL] consistency violations: {0}", viol);
                string first = LockedDictionarySlim<int, Box>.DebugFirstViolation;
                if (first != null) Console.WriteLine("         first: {0}", first);
            }

            LockedDictionarySlim<int, Box>.DebugChecks = false;
            Console.WriteLine();
            Console.WriteLine("Done.");
        }
    }
}
