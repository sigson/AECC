using System;
using System.Threading;
using AECC.Locking;

namespace AECC.Extensions.ThreadingSync
{
    public class SharedLock
    {
        /// <summary>
        /// Lock token that releases the Monitor when Dispose is called.
        /// </summary>
        public class LockToken : IDisposable
        {
            private readonly object _lockObj;
            private readonly bool _single;
            private bool _lockTaken;

            /// <summary>Uses the concurrency mode currently configured on KernelRuntime.DefaultMode.</summary>
            public LockToken(object lockObj)
                : this(lockObj, KernelRuntime.DefaultMode)
            {
            }

            public LockToken(object lockObj, ConcurrencyMode mode)
            {
                _lockObj = lockObj;
                _single = mode == ConcurrencyMode.SingleThread;
                _lockTaken = false;

                try
                {
                    if (!_single)
                        Monitor.Enter(_lockObj, ref _lockTaken);
                    else
                        _lockTaken = true;
                }
                catch (Exception)
                {
                    throw;
                }
            }

            public void ExitLock()
            {
                if (_lockTaken)
                {
                    try
                    {
                        if (!_single)
                        {
                            Monitor.Exit(_lockObj);
                            _lockTaken = false;
                        }
                        else
                            _lockTaken = false;
                    }
                    catch (SynchronizationLockException)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        throw;
                    }
                }
            }

            public void Dispose() => ExitLock();
        }

        // The object the lock is taken on (SyncRoot).
        public readonly object LockObject;

        private readonly ConcurrencyMode _mode;
        public ConcurrencyMode Mode { get { return _mode; } }

        /// <summary>Uses the concurrency mode currently configured on KernelRuntime.DefaultMode.
        /// Uses <paramref name="existingLockObject"/> if provided, otherwise creates a new object.</summary>
        public SharedLock(object existingLockObject = null)
            : this(KernelRuntime.DefaultMode, existingLockObject)
        {
        }

        public SharedLock(ConcurrencyMode mode, object existingLockObject = null)
        {
            _mode = mode;
            LockObject = existingLockObject ?? new object();
        }

        /// <summary>
        /// Acquires the lock and returns a disposable token.
        /// </summary>
        public LockToken Lock()
        {
            return new LockToken(LockObject, _mode);
        }

        /// <summary>
        /// Zero-alloc lock entry: a readonly struct meant to be consumed ONLY via a local `using`
        /// (no boxing to IDisposable, not stored in a field, not returned to a caller).
        /// In SingleThread mode no real lock is taken. Monitor is reentrant, so nested Lock calls
        /// on the same thread are safe.
        /// </summary>
        public Scope LockScoped() => new Scope(LockObject, _mode);

        public readonly struct Scope : IDisposable
        {
            private readonly object _gate;
            private readonly bool _taken;
            /// <summary>Uses the concurrency mode currently configured on KernelRuntime.DefaultMode.</summary>
            public Scope(object gate)
                : this(gate, KernelRuntime.DefaultMode)
            {
            }

            public Scope(object gate, ConcurrencyMode mode)
            {
                _gate = gate;
                if (mode == ConcurrencyMode.SingleThread) { _taken = false; return; }
                bool taken = false;
                Monitor.Enter(gate, ref taken);
                _taken = taken;
            }
            public void Dispose() { if (_taken) Monitor.Exit(_gate); }
        }

        /// <summary>
        /// Runs the action while holding the lock (syntactic sugar).
        /// </summary>
        public void ExecuteLocked(Action action)
        {
            using (Lock())
            {
                action();
            }
        }
    }
}