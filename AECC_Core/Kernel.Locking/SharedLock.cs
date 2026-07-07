using System;
using System.Threading;
using AECC.Locking;

namespace AECC.Extensions.ThreadingSync
{
    public class SharedLock
    {
        /// <summary>
        /// Токен блокировки, который освобождает Monitor при вызове Dispose.
        /// </summary>
        public class LockToken : IDisposable
        {
            private readonly object _lockObj;
            private readonly bool _single;
            private bool _lockTaken;

            /// <summary>Переходный конструктор: режим из KernelRuntime.DefaultMode в момент захвата.</summary>
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
                    // Пытаемся захватить эксклюзивную блокировку
                    if (!_single)
                        Monitor.Enter(_lockObj, ref _lockTaken);
                    else
                        _lockTaken = true;
                }
                catch (Exception e)
                {
                    // Здесь можно добавить ваше логирование NLogger.Error(e);
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
                    catch (SynchronizationLockException e)
                    {
                        // Попытка освободить лок, которым поток не владеет
                        // NLogger.Error($"Error exiting lock: {e.Message}");
                        throw;
                    }
                    catch (Exception e)
                    {
                        // NLogger.Error(e);
                        throw;
                    }
                }
            }

            public void Dispose() => ExitLock();
        }

        // Объект, на котором происходит блокировка (SyncRoot)
        public readonly object LockObject;

        /// <summary>
        /// Конструктор.
        /// Если передан existingLockObject, использует его.
        /// Если null, создает новый object.
        /// </summary>
        // Режим конкурентности (ТЗ 4.1.1): фиксируется при конструировании.
        private readonly ConcurrencyMode _mode;
        public ConcurrencyMode Mode { get { return _mode; } }

        /// <summary>Переходный конструктор: режим из KernelRuntime.DefaultMode в момент создания.</summary>
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
        /// Захватывает блокировку и возвращает Disposable токен.
        /// </summary>
        public LockToken Lock()
        {
            return new LockToken(LockObject, _mode);
        }

        /// <summary>
        /// Zero-alloc вход в блокировку: readonly struct, потребляется ТОЛЬКО через using
        /// по месту (без боксинга в IDisposable, без хранения в поле, без возврата наружу).
        /// В OneThreadMode реального захвата нет. Monitor реентрантен — вложенные Lock корректны.
        /// </summary>
        public Scope LockScoped() => new Scope(LockObject, _mode);

        public readonly struct Scope : IDisposable
        {
            private readonly object _gate;
            private readonly bool _taken;
            /// <summary>Переходный конструктор: режим читается из KernelRuntime.DefaultMode
            /// в момент входа (дословная замена прежнего чтения глобального флага по месту).</summary>
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
        /// Выполняет действие внутри блокировки (синтаксический сахар).
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