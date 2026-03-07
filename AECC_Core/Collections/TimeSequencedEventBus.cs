using AECC.Core.Logging;
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
    /// <summary>
    /// Представляет Kafka-подобную очередь событий с индивидуальным отслеживанием для каждого обработчика.
    /// Работа с состоянием очереди синхронизируется через внутренний цикл событий (Event Loop) для обеспечения потокобезопасности.
    /// Добавлена функциональность TTL (Time-To-Live) для автоматического удаления устаревших событий.
    /// </summary>
    /// <typeparam name="TEvent">Тип событий, хранимых в очереди.</typeparam>
    public class TimeSequencedEventBus<TEvent>
    {
        /// <summary>
        /// Определяет результат обработки события обработчиком.
        /// </summary>
        public enum ProcessingResult
        {
            /// <summary>
            /// Событие успешно обработано. Счетчик обработанных событий будет увеличен.
            /// </summary>
            Processed,
            /// <summary>
            /// Событие было пропущено. Оно будет добавлено в список пропущенных для этого обработчика.
            /// </summary>
            Skipped
        }

        #region Внутренние классы для Event Loop

        /// <summary>
        /// Обертка для хранения события вместе с его временной меткой.
        /// </summary>
        private class EventWrapper
        {
            public TEvent Payload { get; }
            public long TimestampTicks { get; }
            public StackTrace sTrace;

            public EventWrapper(TEvent payload, StackTrace creationTrace)
            {
                Payload = payload;
                sTrace = creationTrace;
                TimestampTicks = DateTime.UtcNow.Ticks;
            }
        }

        /// <summary>
        /// Базовый класс для всех событий, обрабатываемых внутренней шиной.
        /// </summary>
        private abstract class BusEvent { }

        /// <summary>
        /// Событие для добавления нового события в общую очередь.
        /// </summary>
        private class PublishEventCommand : BusEvent
        {
            public TEvent EventPayload { get; }
            public StackTrace sTrace;
            public PublishEventCommand(TEvent eventPayload)
            {
                sTrace = new StackTrace();
                EventPayload = eventPayload;
            }
        }

        /// <summary>
        /// Событие для регистрации нового обработчика (подписчика).
        /// </summary>
        private class SubscribeHandlerCommand : BusEvent
        {
            public string Key { get; }
            public Func<TEvent, ProcessingResult> Handler { get; }
            public SubscribeHandlerCommand(string key, Func<TEvent, ProcessingResult> handler)
            {
                Key = key;
                Handler = handler;
            }
        }

        #endregion

        /// <summary>
        /// Хранит индивидуальное состояние для каждого подключенного обработчика.
        /// </summary>
        public class HandlerState
        {
            public string Key { get; }
            public Func<TEvent, ProcessingResult> Handler { get; }
            public int ProcessedEventsCount { get; internal set; }
            public List<TEvent> SkippedEvents { get; }

            internal HandlerState(string key, Func<TEvent, ProcessingResult> handler)
            {
                Key = key;
                Handler = handler;
                ProcessedEventsCount = 0;
                SkippedEvents = new List<TEvent>();
            }
        }

        // --- Основное состояние ---

        /// <summary>
        /// Мастер-лог всех событий в том порядке, в котором они были получены.
        /// Хранит обертки с временными метками.
        /// </summary>
        private readonly List<EventWrapper> _masterEventLog = new List<EventWrapper>();

        /// <summary>
        /// Словарь состояний для каждого обработчика, индексированный по уникальному ключу.
        /// </summary>
        private readonly Dictionary<string, HandlerState> _handlerStates = new Dictionary<string, HandlerState>();

        /// <summary>
        /// Потокобезопасная очередь команд для цикла событий.
        /// </summary>
        private readonly ConcurrentQueue<BusEvent> _eventQueue = new ConcurrentQueue<BusEvent>();
        
        /// <summary>
        /// Время жизни события в тиках. Если 0 или меньше, TTL отключен.
        /// </summary>
        private readonly long _ttlTicks;

        /// <summary>
        /// Флаг, предотвращающий одновременный запуск нескольких циклов обработки.
        /// 0 - не в обработке, 1 - в обработке.
        /// </summary>
        private int _isProcessing = 0;

        public bool Logging = false;

        /// <summary>
        /// Инициализирует новый экземпляр шины событий с указанным временем жизни событий.
        /// </summary>
        /// <param name="ttl">Время жизни для событий в очереди. События старше этого значения будут удалены при вызове Update.</param>
        public TimeSequencedEventBus(TimeSpan ttl)
        {
            _ttlTicks = ttl.Ticks;
        }

        /// <summary>
        /// Выполняет один цикл обработки событий. Этот метод должен вызываться циклически извне.
        /// Сначала удаляет устаревшие события, затем обрабатывает новые.
        /// Если предыдущий вызов еще не завершился, метод немедленно выйдет.
        /// </summary>
        public void Update()
        {
            // Проверяем, не выполняется ли уже обработка.
            if (Interlocked.CompareExchange(ref _isProcessing, 1, 0) != 0)
            {
                return;
            }

            try
            {
                // 1. Удаляем все протухшие события
                CleanupExpiredEvents();

                // 2. Обрабатываем все накопившиеся команды (публикации и подписки)
                while (_eventQueue.TryDequeue(out var busEvent))
                {
                    ProcessCommand(busEvent);
                }
            }
            finally
            {
                // Гарантированно сбрасываем флаг.
                Volatile.Write(ref _isProcessing, 0);
            }
        }

        /// <summary>
        /// Публикует новое событие в шину. Метод является потокобезопасным.
        /// </summary>
        /// <param name="eventPayload">Событие для добавления.</param>
        public void Publish(TEvent eventPayload)
        {
            _eventQueue.Enqueue(new PublishEventCommand(eventPayload));
        }

        /// <summary>
        /// Регистрирует новый обработчик событий. Метод является потокобезопасным.
        /// Если обработчик с таким ключом уже существует, он будет заменен.
        /// </summary>
        /// <param name="key">Уникальный ключ для идентификации обработчика.</param>
        /// <param name="handler">Лямбда-функция для обработки событий.</param>
        public void Subscribe(string key, Func<TEvent, ProcessingResult> handler)
        {
            _eventQueue.Enqueue(new SubscribeHandlerCommand(key, handler));
        }

        /// <summary>
        /// Возвращает снимок текущего состояния указанного обработчика.
        /// </summary>
        public HandlerState GetHandlerState(string key)
        {
            _handlerStates.TryGetValue(key, out var state);
            return state;
        }
        
        /// <summary>
        /// Удаляет устаревшие события из начала мастер-лога и корректирует счетчики обработчиков.
        /// </summary>
        private void CleanupExpiredEvents()
        {
            if (_ttlTicks <= 0 || _masterEventLog.Count == 0) return;

            var nowTicks = DateTime.UtcNow.Ticks;
            int expiredCount = 0;

            foreach (var evtWrapper in _masterEventLog)
            {
                if (nowTicks - evtWrapper.TimestampTicks > _ttlTicks)
                {
                    expiredCount++;
                }
                else
                {
                    // События отсортированы по времени, так что можно остановиться на первом "свежем".
                    break;
                }
            }

            if (expiredCount > 0)
            {
                _masterEventLog.RemoveRange(0, expiredCount);
                if(Logging)
                    NLogger.Log($"[Bus TTL Cleaner] Cleared {expiredCount} expired events.");

                // Корректируем состояние всех обработчиков
                foreach (var handlerState in _handlerStates.Values)
                {
                    // Сдвигаем "окно" обработанных событий.
                    // Math.Max гарантирует, что счетчик не станет отрицательным.
                    handlerState.ProcessedEventsCount = Math.Max(0, handlerState.ProcessedEventsCount - expiredCount);
                }
            }
        }

        /// <summary>
        /// Маршрутизатор команд для внутренней обработки (выполняется синхронно в Update).
        /// </summary>
        private void ProcessCommand(BusEvent busEvent)
        {
            switch (busEvent)
            {
                case PublishEventCommand cmd:
                    ProcessNewEvent(cmd);
                    break;
                case SubscribeHandlerCommand cmd:
                    ProcessNewSubscription(cmd);
                    break;
            }
        }

        /// <summary>
        /// Обрабатывает регистрацию нового обработчика.
        /// </summary>
        private void ProcessNewSubscription(SubscribeHandlerCommand cmd)
        {
            if (string.IsNullOrEmpty(cmd.Key) || cmd.Handler == null) return;

            var newState = new HandlerState(cmd.Key, cmd.Handler);
            _handlerStates[cmd.Key] = newState;
            if(Logging)
                NLogger.Log($"[Bus] Subscriber '{cmd.Key}' registered.");

            // Сразу же пытаемся обработать все уже существующие события для нового подписчика
            ProcessEventsForHandler(newState);
        }

        /// <summary>
        /// Обрабатывает добавление нового события в мастер-лог.
        /// </summary>
        private void ProcessNewEvent(PublishEventCommand cmd)
        {
            _masterEventLog.Add(new EventWrapper(cmd.EventPayload, cmd.sTrace));
            if(Logging)
                NLogger.Log($"[Bus] Published new event: '{cmd.EventPayload}'. All events: {_masterEventLog.Count}.");

            // После добавления нового события, запускаем обработку для ВСЕХ подписчиков.
            foreach (var handlerState in _handlerStates.Values)
            {
                ProcessEventsForHandler(handlerState);
            }
        }

        /// <summary>
        /// Основная логика: запускает лямбду обработчика для всех событий, которые он еще не видел.
        /// </summary>
        private void ProcessEventsForHandler(HandlerState state)
        {
            // Начинаем с первого необработанного события
            int startFromIndex = state.ProcessedEventsCount;

            if (startFromIndex >= _masterEventLog.Count)
            {
                return; // Все события уже обработаны
            }
            if(Logging)
                NLogger.Log($"[Bus] Start process for '{state.Key}' with event index #{startFromIndex + 1}...");

            for (int i = startFromIndex; i < _masterEventLog.Count; i++)
            {
                var currentEventWrapper = _masterEventLog[i];
                var currentEventPayload = currentEventWrapper.Payload;
                try
                {
                    var result = state.Handler(currentEventPayload);
                    switch (result)
                    {
                        case ProcessingResult.Processed:
                            // Только в случае успешной обработки мы двигаем счетчик вперед
                            state.ProcessedEventsCount++;
                            break;
                        case ProcessingResult.Skipped:
                            state.SkippedEvents.Add(currentEventPayload);
                            if(Logging)
                                NLogger.Log($"[Handler: {state.Key}]  Event '{currentEventPayload}' skipped.");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    // Если лямбда бросает исключение, считаем событие пропущенным.
                    state.SkippedEvents.Add(currentEventPayload);
                    if(Logging)
                        NLogger.Log($"[Handler: {state.Key}] Error processing event '{currentEventPayload}': {ex.Message}. Event skipped. Stacktrace: {ex.StackTrace} \n -=-=-=-=-Creation trace=-=-=-=-=-\n{currentEventWrapper.sTrace}\n -=-=-=-=-=-=-=-=-=-=-=-");
                }
            }
            if(Logging)
                NLogger.Log($"[Bus] Process for '{state.Key}' finalized. Proceed: {state.ProcessedEventsCount}, Skipped: {state.SkippedEvents.Count}.");
        }
    }
}