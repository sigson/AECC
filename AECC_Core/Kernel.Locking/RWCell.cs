using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace AECC.Locking
{
    /// <summary>
    /// The lock core (strategy §3). Three things fused behind one tiny API:
    ///   1. Per-slot state — ONE <c>long</c>, mutated only via <see cref="Interlocked.CompareExchange(ref long,long,long)"/>.
    ///      No wrapper object, no kernel handle, no allocation. (Milazzo-style packed state.)
    ///   2. Waiting machinery — a fixed array of monitor "gates" (parking lot). Shared, constant
    ///      memory; a long held section keeps its per-slot state, not a wait object, so false
    ///      wakeups are harmless and the gate can be shared by hash.
    ///   3. Per-thread mode accounting — a thread-static set sized to what THIS thread currently
    ///      holds (units), giving R/W reentry (dummy) and cross-mode (throw/dummy) without the
    ///      weight of ReaderWriterLockSlim's recursion registry.
    ///
    /// Semantics preserved from the old RWLock:
    ///   - granular read/write per (entity, component);
    ///   - long held sections with multiple concurrent readers;
    ///   - real blocking wait (no core-burning spin) on contention;
    ///   - reentry: same mode -> dummy (depth++); cross mode -> throw or dummy by flag.
    ///
    /// Uses only netstandard2.0 primitives (Interlocked / Volatile / Monitor / [ThreadStatic]),
    /// so it runs on .NET, Unity (IL2CPP) and Godot alike.
    /// </summary>
    public static class RWCell
    {
        // ───────────────────────── packed long layout ─────────────────────────
        // bit  63        : WRITER held (exclusive)
        // bits 32..62 (31): waiting-writer count (writer-favoring; readers back off)
        // bits 0..31  (32): reader count (up to ~4.29e9 concurrent readers)
        private const long WRITER      = unchecked((long)(1UL << 63));
        private const long WAIT_ONE    = 1L << 32;
        private const long WAIT_MASK   = 0x7FFFFFFFL << 32;
        private const long READER_ONE  = 1L;
        private const long READER_MASK = 0xFFFFFFFFL;

        // Reader may enter iff no writer, no waiting writers, and reader counter not saturated.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool CanRead(long s)
        {
            return (s & (WRITER | WAIT_MASK)) == 0 && (s & READER_MASK) != READER_MASK;
        }

        // Writer may enter iff no writer and no readers (waiting writers don't block a writer).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool CanWrite(long s)
        {
            return (s & (WRITER | READER_MASK)) == 0;
        }

        // ───────────────────────── parking lot ─────────────────────────
        private const int GateCount = 1024;          // power of two
        private const int GateMask = GateCount - 1;
        private static readonly object[] _gates = InitGates();
        // Number of threads currently parked on each gate. Lets Unpark skip the monitor + PulseAll
        // entirely when nobody is waiting — the uncontended fast path that ReaderWriterLockSlim has
        // and the naive version lacked.
        private static readonly int[] _gateWaiters = new int[GateCount];
        private static object[] InitGates()
        {
            object[] g = new object[GateCount];
            for (int i = 0; i < GateCount; i++) g[i] = new object();
            return g;
        }

        // Short bounded spin before parking — absorbs sub-microsecond CAS races without
        // burning the core on the long (ms) held sections.
        private const int SpinLimit = 5;

        /// <summary>Tunable: cross-mode reentry (R under W / W under R) throws when true, else returns a dummy.</summary>
        public static bool ThrowOnOrderViolation = false;

        // ───────────────────────── thread-static accounting ─────────────────────────
        // ONE struct array holding only what THIS thread currently owns. AoS (not parallel arrays):
        // a single thread-static reference + a single contiguous scan. The previous SoA version did
        // ~5 thread-static lookups per call plus two thread-static array loads per scan iteration;
        // thread-static access is the dominant fixed cost of the uncontended fast path (and far more
        // expensive under Mono/IL2CPP than CoreCLR). Each Enter/Exit now reads the array into a local
        // ONCE and scans that local. Feature is UNCHANGED: same-mode reentry -> depth++ (dummy),
        // cross-mode -> throw/dummy by flag, order-independent disposal via the depth counter.
        private struct HeldEntry
        {
            public object Container;
            public int Slot;
            public int Depth;
            public byte Mode;       // 0 read, 1 write
        }
        [ThreadStatic] private static HeldEntry[] _held;
        [ThreadStatic] private static int _heldCount;

        // ───────────────────────── public API ─────────────────────────

        /// <summary>
        /// Acquire read or write on the slot identified by (container, slot) whose state is
        /// <paramref name="state"/>. Returns true if the caller must build a real (tracked) token
        /// — i.e. a real acquire or a same-mode reentry. Returns false for a no-op dummy
        /// (OneThreadMode, or a cross-mode reentry when not configured to throw).
        /// </summary>
        public static bool Enter(object container, int slot, int parkHash, ref long state, bool write, ConcurrencyMode mode)
        {
            if (mode == ConcurrencyMode.SingleThread) return false; // single-thread: pure no-op

            HeldEntry[] arr = _held;          // one thread-static read
            int n = _heldCount;               // one thread-static read
            if (arr == null) { arr = _held = new HeldEntry[16]; n = 0; }

            // reentry check — scan the LOCAL array backward (most recent / most likely reentry first)
            for (int i = n - 1; i >= 0; i--)
            {
                if (arr[i].Slot == slot && ReferenceEquals(arr[i].Container, container))
                {
                    byte have = arr[i].Mode;
                    byte want = write ? (byte)1 : (byte)0;
                    if (have == want)
                    {
                        arr[i].Depth++;      // same-mode reentry -> dummy, do not touch state
                        return true;         // still tracked: Dispose must decrement depth
                    }
                    if (ThrowOnOrderViolation)
                        throw new LockRecursionException(want == 1 ? "write lock under read lock" : "read lock under write lock");
                    // DEADLOCK ESCAPE (решение заказчика 0.4.5, восстановление ТЗ 4.1.2):
                    // семантика прежняя — dummy no-op и продолжение исполнения (matches old
                    // "DEADLOCK ESCAPE" в RWLock), но факт БОЛЬШЕ НЕ МОЛЧИТ: Error-репорт летит
                    // безусловно, stack trace — за LockDiagnostics.CaptureEscapeStackTrace.
                    // Ветка холодная: на горячем пути эта диагностика стоит ноль.
                    LockDiagnostics.ReportDeadlockEscape(want == 1);
                    return false;            // cross-mode -> dummy no-op (matches old "DEADLOCK ESCAPE")
                }
            }

            if (write) AcquireWrite(parkHash, ref state);
            else AcquireRead(parkHash, ref state);

            if (n == arr.Length) { Array.Resize(ref arr, n << 1); _held = arr; } // grow + publish
            arr[n].Container = container;
            arr[n].Slot = slot;
            arr[n].Mode = write ? (byte)1 : (byte)0;
            arr[n].Depth = 1;
            _heldCount = n + 1;              // one thread-static write
            return true;
        }

        /// <summary>
        /// Release a previously-acquired tracked token. Decrements depth and performs the real
        /// CAS release + unpark only when depth reaches 0. Order of disposal across nested tokens
        /// is irrelevant thanks to the depth counter.
        /// </summary>
        public static void Exit(object container, int slot, int parkHash, ref long state, ConcurrencyMode concurrency)
        {
            if (concurrency == ConcurrencyMode.SingleThread) return;
            HeldEntry[] arr = _held;          // one thread-static read
            if (arr == null) return;
            int n = _heldCount;               // one thread-static read

            for (int i = n - 1; i >= 0; i--)  // LIFO disposal -> typically the first probe
            {
                if (arr[i].Slot == slot && ReferenceEquals(arr[i].Container, container))
                {
                    if (--arr[i].Depth > 0) return;  // still nested -> no real release
                    byte mode = arr[i].Mode;
                    int last = n - 1;
                    if (i != last) arr[i] = arr[last]; // swap-remove (order irrelevant)
                    arr[last].Container = null;        // drop reference
                    _heldCount = last;                 // one thread-static write
                    if (mode == 1) ReleaseWrite(parkHash, ref state);
                    else ReleaseRead(parkHash, ref state);
                    return;
                }
            }
            // not found: released on wrong thread / double release -> defensive no-op
        }

        /// <summary>True iff the current thread holds (read or write) the given slot.</summary>
        public static bool IsHeldByCurrentThread(object container, int slot)
        {
            HeldEntry[] arr = _held;
            if (arr == null) return false;
            int n = _heldCount;
            for (int i = n - 1; i >= 0; i--)
                if (arr[i].Slot == slot && ReferenceEquals(arr[i].Container, container))
                    return true;
            return false;
        }

        // ───────────────────────── core acquire/release ─────────────────────────

        private static void AcquireRead(int parkHash, ref long state)
        {
            int spin = 0;
            while (true)
            {
                long s = Volatile.Read(ref state);
                if (CanRead(s))
                {
                    if (Interlocked.CompareExchange(ref state, s + READER_ONE, s) == s) return;
                    continue; // lost the CAS, retry immediately
                }
                if (spin < SpinLimit) { spin++; Thread.SpinWait(1 << spin); continue; }
                Park(parkHash, ref state, false);
                spin = 0;
            }
        }

        private static void AcquireWrite(int parkHash, ref long state)
        {
            bool counted = false; // have we registered ourselves in WAIT_MASK?
            int spin = 0;
            while (true)
            {
                long s = Volatile.Read(ref state);
                if (CanWrite(s))
                {
                    long ns = counted ? ((s - WAIT_ONE) | WRITER) : (s | WRITER);
                    if (Interlocked.CompareExchange(ref state, ns, s) == s) return;
                    continue;
                }
                if (!counted && (s & WAIT_MASK) != WAIT_MASK)
                {
                    // Register as a waiting writer so new readers back off (writer-favoring,
                    // protects writers from reader starvation on long sections).
                    if (Interlocked.CompareExchange(ref state, s + WAIT_ONE, s) == s) counted = true;
                    continue;
                }
                if (spin < SpinLimit) { spin++; Thread.SpinWait(1 << spin); continue; }
                Park(parkHash, ref state, true);
                spin = 0;
            }
        }

        private static void ReleaseRead(int parkHash, ref long state)
        {
            while (true)
            {
                long s = Volatile.Read(ref state);
                long ns = s - READER_ONE;
                if (Interlocked.CompareExchange(ref state, ns, s) == s)
                {
                    if ((ns & READER_MASK) == 0) Unpark(parkHash); // last reader left -> a writer may proceed
                    return;
                }
            }
        }

        private static void ReleaseWrite(int parkHash, ref long state)
        {
            while (true)
            {
                long s = Volatile.Read(ref state);
                long ns = s & ~WRITER;
                if (Interlocked.CompareExchange(ref state, ns, s) == s)
                {
                    Unpark(parkHash);
                    return;
                }
            }
        }

        // Park/Unpark — a textbook monitor condition-variable with no lost wakeups AND a cheap
        // uncontended path. The releaser updates `state` (Interlocked == full barrier) BEFORE it
        // reads the waiter count; the parker registers in the waiter count (full barrier) BEFORE
        // it re-reads `state` under the gate. Full fences on both sides give StoreLoad ordering,
        // so at least one side observes the other: no thread sleeps through a wakeup, and Unpark
        // touches the monitor only when a thread is actually parked.
        private static void Park(int parkHash, ref long state, bool write)
        {
            int idx = parkHash & GateMask;
            object gate = _gates[idx];
            Interlocked.Increment(ref _gateWaiters[idx]); // announce before re-checking
            try
            {
                lock (gate)
                {
                    long s = Volatile.Read(ref state);
                    bool blocked = write ? !CanWrite(s) : !CanRead(s);
                    if (blocked) Monitor.Wait(gate);
                }
            }
            finally { Interlocked.Decrement(ref _gateWaiters[idx]); }
        }

        private static void Unpark(int parkHash)
        {
            int idx = parkHash & GateMask;
            if (Volatile.Read(ref _gateWaiters[idx]) == 0) return; // fast path: nobody parked
            object gate = _gates[idx];
            lock (gate) { Monitor.PulseAll(gate); }
        }

        // ───────────────────────── helpers ─────────────────────────

        /// <summary>Combine two ints into a parking-bucket hash (cheap, well-mixed).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Mix(int a, int b)
        {
            unchecked
            {
                uint h = (uint)a;
                h ^= (uint)b + 0x9E3779B9u + (h << 6) + (h >> 2);
                return (int)h;
            }
        }
    }
}