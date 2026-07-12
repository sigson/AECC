using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;

namespace AECC.Locking.Benchmark
{
    /// <summary>
    /// Validation harness aimed at a 10,000,000 x 20 target. Covers the FULL transactional matrix:
    /// read/write on present components, SHARED and EXCLUSIVE absence holds, add, remove, and the
    /// multi-component atomic combinator. Memory at the target scale is too large to allocate
    /// (~12-71 GB), so it is measured on a safe sample and extrapolated; throughput runs on a
    /// bounded entity count (per-op cost is insensitive to N beyond cache effects).
    /// </summary>
    public static class Harness
    {
        private const int KeySpace = 35;     // distinct component types (as in Simulation.cs)
        private const int OpKeys = 7;        // keys touched per op
        private const int InitMin = 15, InitMax = 26;

        private const int MEM_SAMPLE_CAP = 150000; // entities actually built for the memory sample
        private const int THRU_CAP = 250000;       // entities actually built for throughput

        private static readonly object Sentinel = new object();

        public static void Run(int entities, int components, int durationMs, int threads)
        {
            Console.WriteLine("================ AECC lock-core validation harness ================");
            Console.WriteLine(">>> harness build: v6  (storage-lock elided; combinator ordered; deadlock-free) <<<");
            Console.WriteLine("TARGET scale: {0:N0} entities x {1} components = {2:N0} component cells",
                entities, components, (long)entities * components);
            Console.WriteLine("threads={0}  duration/pass={1}ms", threads, durationMs);
            Console.WriteLine();

            MemoryReport(entities, components);
            ScalingProof(components);
            PureLockCycles();
            Invariants();
            MixedWorkload(true, Math.Min(entities, THRU_CAP), durationMs, threads);
            MixedWorkload(false, Math.Min(entities, THRU_CAP), durationMs, threads);

            Console.WriteLine();
            Console.WriteLine("Done.");
        }

        // ───────────────────────── memory (sample + extrapolate) ─────────────────────────

        private static void MemoryReport(int entities, int components)
        {
            int sample = Math.Min(entities, MEM_SAMPLE_CAP);
            long sampleCells = (long)sample * components;
            long targetCells = (long)entities * components;
            Console.WriteLine("---- MEMORY (sampled at {0:N0} entities x {1} = {2:N0} cells; extrapolated to target) ----",
                sample, components, sampleCells);

            long legacy = MeasureBuild(() =>
            {
                var arr = new LegacyBag[sample];
                for (int e = 0; e < sample; e++)
                {
                    var b = new LegacyBag();
                    for (int c = 0; c < components; c++) b.TryAdd(c, Sentinel);
                    arr[e] = b;
                }
                return arr;
            });

            long bag = MeasureBuild(() =>
            {
                var arr = new ComponentBag<object>[sample];
                for (int e = 0; e < sample; e++)
                {
                    var b = new ComponentBag<object>();
                    for (int c = 0; c < components; c++) b.TryAdd(c, Sentinel);
                    arr[e] = b;
                }
                return arr;
            });

            // World dictionary: ONE dict holding all entities (its real use). HoldKeys=false.
            long worldEntries = Math.Min(entities, MEM_SAMPLE_CAP * (long)components);
            long dict = MeasureBuild(() =>
            {
                var d = new LockedDictionarySlim<long, object>(false);
                for (long k = 0; k < worldEntries; k++) d.TryAdd(k, Sentinel);
                return d;
            });

            double legacyB = (double)legacy / sampleCells;
            double bagB = (double)bag / sampleCells;
            double dictB = (double)dict / worldEntries;

            Console.WriteLine("  per-component-cell backends:");
            Console.WriteLine("    Legacy (RWLS per cell)   : {0,7:F1} B/cell  -> target {1,7:F1} GB", legacyB, legacyB * targetCells / 1e9);
            Console.WriteLine("    New ComponentBag (§4.1)  : {0,7:F1} B/cell  -> target {1,7:F1} GB   ({2:P0} of legacy)", bagB, bagB * targetCells / 1e9, bagB / legacyB);
            Console.WriteLine("  world-level dictionary ({0:N0} entries, not per-cell):", entities);
            Console.WriteLine("    New LockedDictionarySlim : {0,7:F1} B/entry -> {1,7:F1} GB for {2:N0} entities", dictB, dictB * entities / 1e9, entities);
            Console.WriteLine();
        }

        private static void ScalingProof(int components)
        {
            Console.WriteLine("---- SCALING (ComponentBag B/cell must stay flat as entity count grows) ----");
            int[] sizes = { 25000, 75000, 150000 };
            foreach (int s in sizes)
            {
                long cells = (long)s * components;
                long bytes = MeasureBuild(() =>
                {
                    var arr = new ComponentBag<object>[s];
                    for (int e = 0; e < s; e++)
                    {
                        var b = new ComponentBag<object>();
                        for (int c = 0; c < components; c++) b.TryAdd(c, Sentinel);
                        arr[e] = b;
                    }
                    return arr;
                });
                Console.WriteLine("    {0,8:N0} entities : {1,7:F1} B/cell", s, (double)bytes / cells);
            }
            Console.WriteLine();
        }

        private static long MeasureBuild(Func<object> build)
        {
            GcFull();
            long before = GC.GetTotalMemory(true);
            object root = build();
            GcFull();
            long after = GC.GetTotalMemory(true);
            GC.KeepAlive(root);
            return after - before;
        }

        // ───────────────────────── pure lock cycles ─────────────────────────

        private static void PureLockCycles()
        {
            Console.WriteLine("---- PURE LOCK COST (single thread, no held work, 5,000,000 cycles each) ----");
            const int N = 5000000;

            var legacy = new LegacyBag();
            legacy.TryAdd(0, Sentinel);
            for (int i = 0; i < 1000; i++) { IDisposable t; if (legacy.TryReadLocked(0, out t)) t.Dispose(); }
            long la0 = AllocatedBytes();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < N; i++)
            {
                IDisposable t;
                if (legacy.TryReadLocked(0, out t)) t.Dispose();
                if (legacy.TryWriteLocked(0, out t)) t.Dispose();
            }
            sw.Stop();
            long la1 = AllocatedBytes();
            Print2("Legacy RWLS        ", sw.Elapsed.TotalMilliseconds, N * 2, la0, la1);

            var bag = new ComponentBag<object>();
            bag.TryAdd(0, Sentinel);
            bag.TryAdd(1, Sentinel); // present key for read/write
            for (int i = 0; i < 1000; i++) { object v; RWToken t; if (bag.TryGetReadLocked(1, out v, out t)) t.Dispose(); }
            long na0 = AllocatedBytes();
            sw.Restart();
            for (int i = 0; i < N; i++)
            {
                object v; RWToken t;
                if (bag.TryGetReadLocked(1, out v, out t)) t.Dispose();
                if (bag.TryGetWriteLocked(1, out v, out t)) t.Dispose();
            }
            sw.Stop();
            long na1 = AllocatedBytes();
            Print2("New RWCell (struct)", sw.Elapsed.TotalMilliseconds, N * 2, na0, na1);
            Console.WriteLine();
        }

        private static void Print2(string name, double ms, long ops, long a0, long a1)
        {
            double ns = ms * 1e6 / ops;
            double alloc = a1 >= a0 ? (double)(a1 - a0) / ops : -1;
            Console.WriteLine("  {0} : {1,7:F1} ns/op   {2} B/op", name, ns, alloc < 0 ? "n/a" : alloc.ToString("F1"));
        }

        // ───────────────────────── mixed workload (full matrix) ─────────────────────────

        private static void MixedWorkload(bool legacy, int entities, int durationMs, int threads)
        {
            string name = legacy ? "LEGACY" : "NEW   ";
            Console.WriteLine("---- THROUGHPUT [{0}] full-matrix mix, no held sleep, {1:N0} entities ----", name, entities);

            IBenchBag[] bags = new IBenchBag[entities];
            var seed = new Random(777);
            for (int e = 0; e < entities; e++)
            {
                IBenchBag b = legacy ? (IBenchBag)new LegacyBag() : new NewBagAdapter();
                int init = seed.Next(InitMin, InitMax + 1);
                var used = new bool[KeySpace];
                int added = 0;
                while (added < init) { int k = seed.Next(KeySpace); if (!used[k]) { used[k] = true; b.TryAdd(k, Sentinel); added++; } }
                bags[e] = b;
            }

            long readOps = 0, writeOps = 0, holdSOps = 0, holdXOps = 0, swapOps = 0, comboOps = 0;
            bool stop = false;

            int g = Math.Max(1, threads / 6);
            var workers = new System.Collections.Generic.List<Thread>();

            for (int i = 0; i < g; i++) workers.Add(new Thread(() => { var r = NewRng(); while (!Volatile.Read(ref stop)) { IDisposable t; if (bags[r.Next(entities)].TryReadLocked(r.Next(KeySpace), out t)) t.Dispose(); Interlocked.Increment(ref readOps); } }));
            for (int i = 0; i < g; i++) workers.Add(new Thread(() => { var r = NewRng(); while (!Volatile.Read(ref stop)) { IDisposable t; if (bags[r.Next(entities)].TryWriteLocked(r.Next(KeySpace), out t)) t.Dispose(); Interlocked.Increment(ref writeOps); } }));
            for (int i = 0; i < g; i++) workers.Add(new Thread(() => { var r = NewRng(); while (!Volatile.Read(ref stop)) { IDisposable t; if (bags[r.Next(entities)].TryHoldShared(r.Next(KeySpace), out t)) t.Dispose(); Interlocked.Increment(ref holdSOps); } }));
            for (int i = 0; i < g; i++) workers.Add(new Thread(() => { var r = NewRng(); while (!Volatile.Read(ref stop)) { IDisposable t; if (bags[r.Next(entities)].TryHoldExclusive(r.Next(KeySpace), out t)) t.Dispose(); Interlocked.Increment(ref holdXOps); } }));
            for (int i = 0; i < g; i++) workers.Add(new Thread(() => { var r = NewRng(); while (!Volatile.Read(ref stop)) { var b = bags[r.Next(entities)]; int k = r.Next(KeySpace); b.Remove(k); b.TryAdd(k, Sentinel); Interlocked.Increment(ref swapOps); } }));
            for (int i = 0; i < g; i++) workers.Add(new Thread(() =>
            {
                var r = NewRng(); var toks = new IDisposable[OpKeys]; var ks = new int[OpKeys];
                while (!Volatile.Read(ref stop))
                {
                    var b = bags[r.Next(entities)]; int n = 0;
                    for (int k = 0; k < OpKeys; k++) ks[k] = r.Next(KeySpace);
                    Array.Sort(ks); // canonical order: multi-key acquisition must be ordered
                    for (int k = 0; k < OpKeys; k++)
                    {
                        if (k > 0 && ks[k] == ks[k - 1]) continue;
                        IDisposable t; if (b.TryReadLocked(ks[k], out t)) toks[n++] = t;
                    }
                    for (int k = n - 1; k >= 0; k--) toks[k].Dispose();
                    Interlocked.Increment(ref comboOps);
                }
            }));

            foreach (var t in workers) t.Start();
            Thread.Sleep(durationMs);
            Volatile.Write(ref stop, true);
            foreach (var t in workers) t.Join();

            double secs = durationMs / 1000.0;
            long total = readOps + writeOps + holdSOps + holdXOps + swapOps + comboOps;
            Console.WriteLine("  read={0,11:N0} write={1,11:N0} holdS={2,11:N0} holdX={3,11:N0} swap={4,11:N0} combo={5,11:N0}",
                readOps, writeOps, holdSOps, holdXOps, swapOps, comboOps);
            Console.WriteLine("  total={0:N0}   combined throughput: {1:N0} ops/sec", total, total / secs);
            Console.WriteLine();
        }

        private static Random NewRng() { return new Random(Guid.NewGuid().GetHashCode()); }

        // ───────────────────────── invariants (full matrix) ─────────────────────────

        private static void Invariants()
        {
            Console.WriteLine("---- INVARIANTS (full matrix) ----");
            WriterExclusivity();
            ReaderConcurrency();
            SharedHoldConcurrency();
            ExclusiveHoldExclusive();
            SharedHoldBlocksAdd();
            ReadBlocksWrite();
            HoldThenAddAfterRelease();
            SingleThreadHoldPredicate();
            CrossModeDummy();
            SameModeReentry();
            CombinatorAllOrRelease();
            IndependentKeys();
            Console.WriteLine();
        }

        private sealed class Counter { public long X; }

        private static void WriterExclusivity()
        {
            var bag = new ComponentBag<object>(); bag.TryAdd(0, Sentinel);
            var ctr = new Counter(); const int T = 8, M = 50000;
            RunThreads(T, () => { for (int j = 0; j < M; j++) { object v; RWToken t; if (bag.TryGetWriteLocked(0, out v, out t)) { ctr.X++; t.Dispose(); } } });
            Report("present write is exclusive (no lost increments)", ctr.X == (long)T * M, ctr.X + "==" + (long)T * M);
        }

        private static void ReaderConcurrency()
        {
            var bag = new ComponentBag<object>(); bag.TryAdd(0, Sentinel);
            int inside = 0, max = 0; const int R = 6;
            RunThreads(R, () => { object v; RWToken t; if (bag.TryGetReadLocked(0, out v, out t)) { Bump(ref inside, ref max); Thread.Sleep(80); Interlocked.Decrement(ref inside); t.Dispose(); } });
            Report("present read is shared (>=2 concurrent readers)", max >= 2, "maxConcurrent=" + max);
        }

        private static void SharedHoldConcurrency()
        {
            var bag = new ComponentBag<object>(); // key 9 absent
            int inside = 0, max = 0; const int R = 6;
            RunThreads(R, () => { RWToken t; if (bag.TryHoldShared(9, out t)) { Bump(ref inside, ref max); Thread.Sleep(80); Interlocked.Decrement(ref inside); t.Dispose(); } });
            Report("ABSENCE hold is shared (>=2 concurrent holders)", max >= 2, "maxConcurrent=" + max);
        }

        private static void ExclusiveHoldExclusive()
        {
            var bag = new ComponentBag<object>(); // key 11 absent
            var ctr = new Counter(); const int T = 8, M = 30000;
            RunThreads(T, () => { for (int j = 0; j < M; j++) { RWToken t; if (bag.TryHoldExclusive(11, out t)) { ctr.X++; t.Dispose(); } } });
            Report("exclusive absence hold is exclusive (no lost increments)", ctr.X == (long)T * M, ctr.X + "==" + (long)T * M);
        }

        private static void SharedHoldBlocksAdd()
        {
            var bag = new ComponentBag<object>(); // key 13 absent
            var ready = new ManualResetEventSlim(false);
            bool held = false;
            var holder = new Thread(() =>
            {
                RWToken t;
                held = bag.TryHoldShared(13, out t); // acquire AND release on this thread (affinity)
                ready.Set();
                if (held) { Thread.Sleep(200); t.Dispose(); }
            });
            holder.Start();
            ready.Wait();
            var sw = Stopwatch.StartNew();
            bool added = bag.TryAdd(13, Sentinel); // main thread: must wait for the shared hold to drain
            sw.Stop();
            holder.Join();
            Report("shared hold blocks add until released", held && added && sw.ElapsedMilliseconds >= 100,
                "added=" + added + " waited=" + sw.ElapsedMilliseconds + "ms");
        }

        private static void ReadBlocksWrite()
        {
            var bag = new ComponentBag<object>(); bag.TryAdd(15, Sentinel);
            var ready = new ManualResetEventSlim(false);
            bool gotRead = false;
            var holder = new Thread(() =>
            {
                object v; RWToken t;
                gotRead = bag.TryGetReadLocked(15, out v, out t); // acquire AND release on this thread
                ready.Set();
                if (gotRead) { Thread.Sleep(200); t.Dispose(); }
            });
            holder.Start();
            ready.Wait();
            var sw = Stopwatch.StartNew();
            object wv; RWToken wt; bool gotW = bag.TryGetWriteLocked(15, out wv, out wt); // main: must wait
            sw.Stop();
            if (gotW) wt.Dispose();
            holder.Join();
            Report("present read blocks present write until released", gotRead && gotW && sw.ElapsedMilliseconds >= 100,
                "waited=" + sw.ElapsedMilliseconds + "ms");
        }

        private static void HoldThenAddAfterRelease()
        {
            var bag = new ComponentBag<object>();
            RWToken h; bool held = bag.TryHoldShared(17, out h); // absent -> shared hold
            h.Dispose();                                          // release first (no overlap)
            bool addAfter = bag.TryAdd(17, Sentinel);             // now add
            object rv; bool removed = bag.Remove(17, out rv);     // remove -> absent again
            RWToken h2; bool holdAgain = bag.TryHoldShared(17, out h2); // can hold absence again
            if (holdAgain) h2.Dispose();
            Report("lifecycle: hold->release->add->remove->hold-absent",
                held && addAfter && removed && holdAgain,
                "held=" + held + " add=" + addAfter + " rem=" + removed + " reHold=" + holdAgain);
        }

        private static void SingleThreadHoldPredicate()
        {
            // ST-режим: absence-hold обязан отказывать на ПРИСУТСТВУЮЩЕМ ключе и
            // успевать на отсутствующем (паритет предиката с MT минус резервирование).
            bool prev = Defines.OneThreadMode;
            Defines.OneThreadMode = true;
            try
            {
                var bag = new ComponentBag<object>();
                bag.TryAdd(1, Sentinel);
                bool ranOnPresent = false, ranOnAbsent = false;
                bool grantedPresent = bag.ExecuteHoldRead(1, () => ranOnPresent = true);
                bool grantedAbsent = bag.ExecuteHoldRead(2, () => ranOnAbsent = true);

                var dict = new LockedDictionarySlim<int, object>(preserveLockingKeys: true);
                dict.TryAdd(1, Sentinel);
                RWToken dt;
                bool dictPresent = dict.HoldKey(1, out dt); if (dictPresent) dt.Dispose();
                bool dictAbsent = dict.HoldKey(2, out dt); if (dictAbsent) dt.Dispose();

                Report("ST hold honors absence predicate (bag + dict)",
                    !grantedPresent && !ranOnPresent && grantedAbsent && ranOnAbsent &&
                    !dictPresent && dictAbsent,
                    "bag: present=" + grantedPresent + " absent=" + grantedAbsent +
                    " dict: present=" + dictPresent + " absent=" + dictAbsent);
            }
            finally { Defines.OneThreadMode = prev; }
        }

        private static void CrossModeDummy()
        {
            RWCell.ThrowOnOrderViolation = false;
            var bag = new ComponentBag<object>(); bag.TryAdd(0, Sentinel);
            bool ok = RunWithTimeout(() => { object v; RWToken r, w; bag.TryGetReadLocked(0, out v, out r); bag.TryGetWriteLocked(0, out v, out w); w.Dispose(); r.Dispose(); }, 3000);
            Report("cross-mode (W under R) returns dummy, no deadlock", ok, ok ? "ok" : "TIMEOUT");
        }

        private static void SameModeReentry()
        {
            var bag = new ComponentBag<object>(); bag.TryAdd(0, Sentinel);
            bool ok = RunWithTimeout(() =>
            {
                object v; RWToken a, b; bag.TryGetReadLocked(0, out v, out a); bag.TryGetReadLocked(0, out v, out b); b.Dispose(); a.Dispose();
                RWToken c, d; bag.TryGetWriteLocked(0, out v, out c); bag.TryGetWriteLocked(0, out v, out d); d.Dispose(); c.Dispose();
            }, 3000);
            Report("same-mode reentry (R/R, W/W) no deadlock", ok, ok ? "ok" : "TIMEOUT");
        }

        private static void CombinatorAllOrRelease()
        {
            var bag = new ComponentBag<object>();
            for (int k = 0; k < OpKeys; k++) bag.TryAdd(k, Sentinel);
            bool ok = RunWithTimeout(() =>
            {
                var toks = new RWToken[OpKeys]; int n = 0;
                for (int k = 0; k < OpKeys; k++) { object v; RWToken t; if (bag.TryGetReadLocked(k, out v, out t)) toks[n++] = t; }
                bool all = n == OpKeys;
                for (int k = 0; k < n; k++) toks[k].Dispose();
                if (!all) throw new Exception("not all acquired: " + n);
            }, 3000);
            Report("N-component combinator: acquire all 7 read locks + release", ok, ok ? "ok" : "FAIL");
        }

        private static void IndependentKeys()
        {
            var bag = new ComponentBag<object>(); bag.TryAdd(1, Sentinel); bag.TryAdd(2, Sentinel);
            var holder = new Thread(() => { object v; RWToken t; if (bag.TryGetWriteLocked(1, out v, out t)) { Thread.Sleep(300); t.Dispose(); } });
            holder.Start(); Thread.Sleep(30);
            var sw = Stopwatch.StartNew();
            object v2; RWToken t2; bool got = bag.TryGetReadLocked(2, out v2, out t2);
            sw.Stop(); if (got) t2.Dispose(); holder.Join();
            Report("long write on key X does not block read on key Y", got && sw.ElapsedMilliseconds < 150, "elapsed=" + sw.ElapsedMilliseconds + "ms");
        }

        // ───────────────────────── plumbing ─────────────────────────

        private static void Bump(ref int inside, ref int max)
        {
            int cur = Interlocked.Increment(ref inside);
            int prev;
            do { prev = Volatile.Read(ref max); if (cur <= prev) break; } while (Interlocked.CompareExchange(ref max, cur, prev) != prev);
        }

        private static void RunThreads(int n, Action body)
        {
            var ts = new Thread[n];
            for (int i = 0; i < n; i++) ts[i] = new Thread(() => body());
            foreach (var t in ts) t.Start();
            foreach (var t in ts) t.Join();
        }

        private static bool RunWithTimeout(Action a, int ms)
        {
            Exception err = null;
            var t = new Thread(() => { try { a(); } catch (Exception ex) { err = ex; } });
            t.IsBackground = true; t.Start();
            bool finished = t.Join(ms);
            return finished && err == null;
        }

        private static void Report(string name, bool pass, string detail)
        {
            Console.WriteLine("  [{0}] {1}  ({2})", pass ? "PASS" : "FAIL", name, detail);
        }

        private static void GcFull() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }

        private static readonly MethodInfo _allocMethod =
            typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", BindingFlags.Public | BindingFlags.Static);

        private static long AllocatedBytes()
        {
            if (_allocMethod == null) return -1;
            try { return (long)_allocMethod.Invoke(null, null); } catch { return -1; }
        }
    }
}
