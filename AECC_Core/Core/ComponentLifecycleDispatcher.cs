using System;
using System.Collections.Generic;
using System.Threading;
using AECC.Abstractions;

namespace AECC.Core
{
    /// <summary>
    /// Диспетчер lifecycle-реакций компонента:
    ///
    ///   • слоты: единственный PendingAdd, очередь PendingChanges (FIFO), единственный
    ///     PendingRemove;
    ///   • строгий приоритет исполнения Add → Change* → Remove — на каждом витке дрейна
    ///     заново выбирается самый приоритетный непустой слот;
    ///   • единственный дрейнер: CAS по флагу Processing; повторные Drain при активном
    ///     дрейнере — no-op (новые элементы подхватит активный цикл);
    ///   • исполнение дрейна — через инжектируемый IScheduler (DefaultScheduler даёт
    ///     синхронный дрейн при OneThreadMode). Инжекция планировщика позволяет тестовому
    ///     планировщику откладывать дрейн и накапливать очередь для детерминированных тестов.
    ///
    /// Диспетчер не знает ни компонента, ни логгера (ошибки — через onError вызывающего).
    /// Гейт AlreadyRemovedReaction — состояние КОМПОНЕНТА и остаётся в ECSComponent;
    /// компонент проверяет его под <see cref="SyncRoot"/>.
    /// </summary>
    public sealed class ComponentLifecycleDispatcher
    {
        /// <summary>Замок очереди И замок исполнения реакций (замыкания компонента берут его же —
        /// дисциплина исходника: OnAdded/OnChanged/OnRemoved исполняются под этим замком).</summary>
        public readonly object SyncRoot = new object();

        private int _processing;
        private Action _pendingAdd;
        private Queue<Action> _pendingChanges;
        private Action _pendingRemove;

        /// <summary>Требует захваченного SyncRoot (как исходное `state.PendingAdd = ...` под lock).</summary>
        public void SetPendingAdd(Action action) { _pendingAdd = action; }

        /// <summary>Требует захваченного SyncRoot.</summary>
        public void EnqueueChange(Action action)
        {
            if (_pendingChanges == null) _pendingChanges = new Queue<Action>();
            _pendingChanges.Enqueue(action);
        }

        /// <summary>Требует захваченного SyncRoot.</summary>
        public void SetPendingRemove(Action action) { _pendingRemove = action; }

        /// <summary>Очередь пуста и дрейнер не активен (для ожиданий в сетке и диагностики).</summary>
        public bool IsIdle
        {
            get
            {
                lock (SyncRoot)
                {
                    return _processing == 0 && _pendingAdd == null && _pendingRemove == null
                           && (_pendingChanges == null || _pendingChanges.Count == 0);
                }
            }
        }

        /// <summary>
        /// Запуск дрейна. CAS-защита не допускает параллельных дрейнеров.
        /// <paramref name="onError"/> — обработчик исключений реакции (компонент передаёт
        /// NLogger-логирование, диспетчер логгера не знает).
        /// </summary>
        public void Drain(IScheduler scheduler, Action<Exception> onError)
        {
            if (Interlocked.CompareExchange(ref _processing, 1, 0) != 0)
            {
                return;
            }

            scheduler.Run(() =>
            {
                while (true)
                {
                    Action actionToRun = null;

                    lock (SyncRoot)
                    {
                        // СТРОГИЙ ПРИОРИТЕТ ВЫПОЛНЕНИЯ: Add -> Change -> Remove
                        if (_pendingAdd != null)
                        {
                            actionToRun = _pendingAdd;
                            _pendingAdd = null;
                        }
                        else if (_pendingChanges != null && _pendingChanges.Count > 0)
                        {
                            actionToRun = _pendingChanges.Dequeue();
                        }
                        else if (_pendingRemove != null)
                        {
                            actionToRun = _pendingRemove;
                            _pendingRemove = null;
                        }
                        else
                        {
                            // Очередь пуста, снимаем флаг и выходим
                            _processing = 0;
                            return;
                        }
                    }

                    try
                    {
                        if (actionToRun != null) actionToRun.Invoke();
                    }
                    catch (Exception ex)
                    {
                        if (onError != null) onError(ex);
                    }
                }
            });
        }

        /// <summary>Сброс для переиспользования компонента.</summary>
        public void Reset()
        {
            lock (SyncRoot)
            {
                _pendingAdd = null;
                if (_pendingChanges != null) _pendingChanges.Clear();
                _pendingRemove = null;
                _processing = 0;
            }
        }
    }
}
