using AECC.Core.Logging;
using AECC.Extensions;
using AECC.Extensions.ThreadingSync;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AECC.Collections
{
    public class ActionGatewayInTime
    {
        // Используем ConcurrentQueue для потокобезопасного хранения действий в очереди.
        private readonly ConcurrentQueue<Action> _actions = new ConcurrentQueue<Action>();

        private ConcurrentHashSet<TimerCompat> timerCache = new ConcurrentHashSet<TimerCompat>();

        // Приватное поле для хранения состояния переключателя.
        private bool _actionSwitch = true;
        
        // Объект для синхронизации потоков, чтобы избежать состояний гонки.
        private readonly object _lock = new object();

        /// <summary>
        /// Переключатель выполнения.
        /// true: Действия выполняются немедленно. При переключении с false на true
        ///       выполняются все накопленные действия.
        /// false: Действия кешируются в очередь для последующего выполнения.
        /// </summary>
        public bool ActionSwitch
        {
            get
            {
                lock (_lock)
                {
                    return _actionSwitch;
                }
            }
            private set // Сеттер сделан приватным, чтобы управление шло только через метод SetTimeAwaitSwitchValue
            {
                List<Action> cachedActionsToRun = null;

                lock (_lock)
                {
                    // Если новое значение совпадает с текущим, ничего не делаем.
                    if (_actionSwitch == value) return;

                    _actionSwitch = value;

                    // Если мы только что ВКЛЮЧИЛИ переключатель,
                    // нужно забрать все действия из кеша для выполнения.
                    if (_actionSwitch)
                    {
                        cachedActionsToRun = new List<Action>();
                        while (_actions.TryDequeue(out Action cachedAction))
                        {
                            cachedActionsToRun.Add(cachedAction);
                        }
                    }
                }

                // Выполняем кеш за пределами lock, чтобы не блокировать
                // другие потоки на время выполнения потенциально долгих действий.
                if (cachedActionsToRun != null)
                {
                    ExecuteCachedActions(cachedActionsToRun);
                }
            }
        }

        /// <summary>
        /// Пытается выполнить действие или кеширует его в зависимости от состояния ActionSwitch.
        /// </summary>
        /// <param name="action">Действие, которое нужно выполнить.</param>
        public void ExecuteAction(Action action)
        {
            bool executeImmediately;
            
            // Блокируем, чтобы проверка и добавление в очередь были атомарной операцией.
            lock (_lock)
            {
                executeImmediately = _actionSwitch;
                if (!executeImmediately)
                {
                    _actions.Enqueue(action);
                }
            }

            if (executeImmediately)
            {
                try
                {
                    action.Invoke();
                }
                catch (Exception ex)
                {
                    // Рекомендуется логировать ошибки, чтобы выполнение одного действия
                    // не прервало всю программу.
                    NLogger.Log($"Ошибка при выполнении действия: {ex.Message} : {new StackTrace(ex).ToString()}");
                }
            }
        }

        /// <summary>
        /// Устанавливает значение переключателя и запускает таймер, 
        /// который по истечению времени вернет переключатель в ОБРАТНОЕ состояние.
        /// </summary>
        /// <param name="milliseconds">Время в миллисекундах, через которое сработает таймер.</param>
        /// <param name="switchTo">Значение, в которое нужно установить переключатель сейчас.</param>
        public void SetTimeAwaitSwitchValue(int milliseconds, bool switchTo)
        {
            // Сначала устанавливаем текущее значение.
            // Это вызовет приватный сеттер, который может запустить выполнение кеша, если switchTo = true.
            ActionSwitch = switchTo;

            // Используем Task.Delay как современную и удобную замену таймеру для одноразовой операции.
            // ContinueWith гарантирует, что следующий код выполнится после завершения задержки.
            lock (_lock)
            {
                var timer = new TimerCompat();
                timer.TimerCompatInit(milliseconds, (obj, arg) =>
                {
                    // По истечению времени инвертируем значение, которое было установлено изначально.
                    ActionSwitch = !switchTo;
                    timerCache.Remove(timer);
                }).Start();
                timerCache.Add(timer);
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                // 1. Остановить и освободить таймер
                timerCache.ForEach(x => x.Stop().Dispose());
                timerCache.Clear();

                // 2. Очистить очередь кешированных действий
                // ConcurrentQueue не имеет метода Clear(), поэтому очищаем через Dequeue
                while (_actions.TryDequeue(out _)) { }

                // 3. Установить переключатель в состояние 'true' напрямую,
                // чтобы избежать логики сеттера, которая пытается ВЫПОЛНИТЬ кеш 
                // (который мы только что очистили).
                _actionSwitch = true;
            }
        }

        /// <summary>
        /// Выполняет список кешированных действий.
        /// </summary>
        private void ExecuteCachedActions(List<Action> actionsToExecute)
        {
            foreach (var action in actionsToExecute)
            {
                try
                {
                    action.Invoke();
                }
                catch (Exception ex)
                {
                    // Важно обрабатывать исключения здесь, чтобы сбой одного
                    // действия из кеша не остановил выполнение остальных.
                    NLogger.Log($"Ошибка при выполнении кешированного действия: {ex.Message} : {new StackTrace(ex).ToString()}");
                }
            }
        }
    }
}