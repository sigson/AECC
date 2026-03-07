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
    public class PriorityEventQueue<TKey, TEvent> where TEvent : System.Delegate
    {
        private PriorityEventQueueOneTread<TKey, TEvent> onethreadqueue;
        private PriorityEventQueueMultiThread<TKey, TEvent> multithreadqueue;
        public PriorityEventQueue(IEnumerable<TKey> priorityOrder, int gatesOpened = Int32.MaxValue, Func<int, int> gatesCounter = null, Type ownerType = null)
        {
            if (Defines.OneThreadMode)
            {
                onethreadqueue = new PriorityEventQueueOneTread<TKey, TEvent>(priorityOrder, gatesOpened, gatesCounter, ownerType);
            }
            else
            {
                multithreadqueue = new PriorityEventQueueMultiThread<TKey, TEvent>(priorityOrder, gatesOpened, gatesCounter, ownerType);
            }
        }

        public void AddEvent(TKey key, TEvent eventItem)
        {
            if (Defines.OneThreadMode)
            {
                onethreadqueue.AddEvent(key, eventItem);
            }
            else
            {
                multithreadqueue.AddEvent(key, eventItem);
            }
        }
    }

    public class PriorityEventQueueOneTread<TKey, TEvent> where TEvent : System.Delegate
    {
        // ... все поля и конструктор остаются прежними ...
        private struct ActionWrapper
        {
            public Guid actionId;
            public TEvent actionEvent;
            public bool inAction;
        }

        private class PriorityWrapper
        {
            public TKey priorityValue;
            public bool GateOpened;
        }

        private int OpenedDownGates;
        private readonly Func<int, int> GatesCounter;
        private readonly ConcurrentDictionary<TKey, SynchronizedList<ActionWrapper>> _eventLists;
        private readonly List<PriorityWrapper> _priorityOrder;
        private readonly object _lock = new object();

        private int _processing = 0;

        private readonly StackTrace creationStackTrace;
        private readonly Type ownerType;

        public PriorityEventQueueOneTread(IEnumerable<TKey> priorityOrder, int gatesOpened = Int32.MaxValue, Func<int, int> gatesCounter = null, Type ownerType = null)
        {
            if (priorityOrder == null)
                throw new ArgumentNullException(nameof(priorityOrder));

            creationStackTrace = new StackTrace();
            this.ownerType = ownerType;
            OpenedDownGates = gatesOpened;
            GatesCounter = gatesCounter ?? (x => x + 1);

            _priorityOrder = new List<PriorityWrapper>();
            if (!priorityOrder.Any())
                throw new ArgumentException("Priority order must not be empty", nameof(priorityOrder));

            _eventLists = new ConcurrentDictionary<TKey, SynchronizedList<ActionWrapper>>();
            foreach (var key in priorityOrder)
            {
                _priorityOrder.Add(new PriorityWrapper() { priorityValue = key, GateOpened = false });
                _eventLists[key] = new SynchronizedList<ActionWrapper>();
            }
        }

        public void AddEvent(TKey key, TEvent eventItem)
        {
            if (!_eventLists.ContainsKey(key))
                throw new ArgumentException("Key is not part of the priority order", nameof(key));

            var newAction = new ActionWrapper() { actionId = Guid.NewGuid(), actionEvent = eventItem, inAction = false };

            lock (_lock)
            {
                _eventLists[key].Add(newAction);
            }

            ProcessQueue();
        }


        // --- ИСПРАВЛЕННЫЙ МЕТОД ОБРАБОТКИ С ГАРАНТИЕЙ ВЫЗОВА ---
        private void ProcessQueue()
        {
            // 1. Пытаемся захватить "право на обработку". Если кто-то уже работает, выходим.
            if (Interlocked.CompareExchange(ref _processing, 1, 0) != 0)
            {
                return;
            }

            try
            {
                // 2. Вводим внешний цикл, который будет работать до тех пор,
                // пока внутренний цикл находит и обрабатывает события.
                bool wasWorkDoneInLastPass;
                do
                {
                    wasWorkDoneInLastPass = false;

                    // Внутренний цикл для поиска и обработки одного события
                    while (true)
                    {
                        ActionWrapper? eventToProcess = null;
                        PriorityWrapper priorityOfEvent = null;

                        // Блокируем, чтобы безопасно найти следующее событие
                        lock (_lock)
                        {
                            for (int i = 0; i < OpenedDownGates && i < _priorityOrder.Count; i++)
                            {
                                var currentPriority = _priorityOrder[i];
                                if (_eventLists.TryGetValue(currentPriority.priorityValue, out var eventList) && eventList.Count > 0)
                                {
                                    var wrapper = eventList[0];
                                    if (!wrapper.inAction)
                                    {
                                        wrapper.inAction = true;
                                        eventList[0] = wrapper; // Важно для struct

                                        eventToProcess = wrapper;
                                        priorityOfEvent = currentPriority;
                                        break;
                                    }
                                }
                            }
                        }

                        // Если доступных событий не найдено, выходим из внутреннего цикла
                        if (eventToProcess == null)
                        {
                            break;
                        }

                        // Нашли событие - значит, работа была проделана
                        wasWorkDoneInLastPass = true;

                        // Запускаем выполнение задачи
                        // ВАЖНО: TaskEx.Run должен быть синхронным в однопоточном режиме,
                        // иначе эта логика не будет работать так, как задумано.
                        TaskEx.RunAsync(() =>
                        {
                            try
                            {
                                eventToProcess.Value.actionEvent.DynamicInvoke();

                                // Этот код должен выполняться атомарно с удалением
                                lock (_lock)
                                {
                                    if (!priorityOfEvent.GateOpened)
                                    {
                                        priorityOfEvent.GateOpened = true;
                                        OpenedDownGates = GatesCounter(OpenedDownGates);
                                    }
                                }
                            }
                            finally
                            {
                                // Гарантированное удаление события из очереди
                                lock (_lock)
                                {
                                    try
                                    {
                                        // Проверяем, что удаляем именно то событие, которое обработали
                                        if (_eventLists[priorityOfEvent.priorityValue].Count > 0 &&
                                            _eventLists[priorityOfEvent.priorityValue][0].actionId == eventToProcess.Value.actionId)
                                        {
                                            _eventLists[priorityOfEvent.priorityValue].RemoveAt(0);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        NLogger.Log($"Error removing event from queue - {ex.Message}\n in type {this.ownerType} \n{this.creationStackTrace}");
                                    }
                                }
                            }
                        });
                    }

                    // 3. Если в последнем полном проходе была проделана работа,
                    // повторяем внешний цикл, чтобы проверить наличие новых событий,
                    // которые могли быть добавлены во время выполнения `TaskEx.Run`.
                } while (wasWorkDoneInLastPass);
            }
            finally
            {
                // 4. Только когда очередь действительно пуста, сбрасываем флаг.
                _processing = 0;
            }
        }
    }

    public class PriorityEventQueueMultiThread<TKey, TEvent> where TEvent : System.Delegate
    {
        private struct ActionWrapper
        {
            public Guid actionId;
            public TEvent actionEvent;
            public bool inAction;
        }

        private class PriorityWrapper
        {
            public TKey priorityValue;
            public bool GateOpened;
        }
        private int OpenedDownGates;
        private Func<int, int> GatesCounter;
        private readonly ConcurrentDictionary<TKey, SynchronizedList<ActionWrapper>> _eventLists;
        private readonly List<PriorityWrapper> _priorityOrder;
        private readonly object _lock = new object();
        private StackTrace creationStackTrace;
        private Type ownerType;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="priorityOrder"></param>
        /// <param name="gatesOpened">minimal gates value = 1, gates opened on first event</param>
        /// <param name="gatesCounter"> may be + 2</param>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="ArgumentException"></exception>
        public PriorityEventQueueMultiThread(IEnumerable<TKey> priorityOrder, int gatesOpened = Int32.MaxValue, Func<int, int> gatesCounter = null, Type ownerType = null)
        {
            if (priorityOrder == null)
                throw new ArgumentNullException(nameof(priorityOrder));

            creationStackTrace = new StackTrace();
            this.ownerType = ownerType;
            OpenedDownGates = gatesOpened;
            if (gatesCounter == null)
            {
                GatesCounter = x => x + 1;
            }
            else
            {
                GatesCounter = gatesCounter;
            }
            _priorityOrder = new List<PriorityWrapper>();
            if (priorityOrder.Count() == 0)
                throw new ArgumentException("Priority order must not be empty", nameof(priorityOrder));
            // if (priorityOrder.Distinct().Count() != _priorityOrder.Count)
            //     throw new ArgumentException("Priority order must contain unique keys", nameof(priorityOrder));

            _eventLists = new ConcurrentDictionary<TKey, SynchronizedList<ActionWrapper>>();
            foreach (var key in priorityOrder)
            {
                _priorityOrder.Add(new PriorityWrapper() { priorityValue = key, GateOpened = false });
                _eventLists[key] = new SynchronizedList<ActionWrapper>();
            }
        }

        public void AddEvent(TKey key, TEvent eventItem)
        {
            lock (_lock)
            {
                if (!_eventLists.ContainsKey(key))
                    throw new ArgumentException("Key is not part of the priority order", nameof(key));
                var newAction = new ActionWrapper() { actionId = Guid.NewGuid(), actionEvent = eventItem, inAction = false };
                _eventLists[key].Add(newAction);
                IncludeEvent(key, newAction);
            }
        }

        private void IncludeEvent(TKey key, ActionWrapper eventItem)
        {
            for (int i = 0; i < OpenedDownGates; i++)
            {
                if (i >= _priorityOrder.Count)
                    break;
                var prioritynow = _priorityOrder[i];
                if (_eventLists.TryGetValue(prioritynow.priorityValue, out var prioritystorage))
                {
                    if (prioritystorage.Count > 0)
                    {
                        if (prioritystorage[0].actionId == eventItem.actionId)
                        {
                            eventItem.inAction = true;
                            TaskEx.RunAsync(() =>
                            {
                                LockEx.Lock(prioritynow, () => !Defines.OneThreadMode, () =>
                                {
                                    eventItem.actionEvent.DynamicInvoke();
                                    if (!prioritynow.GateOpened)
                                    {
                                        prioritynow.GateOpened = true;
                                        OpenedDownGates = GatesCounter(OpenedDownGates);
                                    }
                                    LockEx.Lock(_lock, () => !Defines.OneThreadMode, () =>
                                    {
                                        try
                                        {
                                            _eventLists[prioritynow.priorityValue].RemoveAt(0);
                                        }
                                        catch (Exception ex)
                                        {
                                            NLogger.Log($"Error in priority event queue (Maybe you start already executed only one execution method (OnAdded as example)) - {ex.Message}\n in type {this.ownerType} \n{this.creationStackTrace}");
                                        }
                                    });
                                    ProcessEvents();
                                });
                            });
                        }
                    }
                }
            }
        }

        private void ProcessEvents()
        {
            LockEx.Lock(_lock, () => !Defines.OneThreadMode, () =>
            {
                for (int i = 0; i < OpenedDownGates; i++)
                {
                    if (i >= _priorityOrder.Count)
                        break;
                    var prioritynow = _priorityOrder[i];
                    if (_eventLists.TryGetValue(prioritynow.priorityValue, out var prioritystorage))
                    {
                        if (prioritystorage.Count > 0)
                        {
                            if (!prioritystorage[0].inAction)
                            {
                                var priorevent = prioritystorage[0];
                                priorevent.inAction = true;
                                TaskEx.RunAsync(() =>
                                {
                                    LockEx.Lock(prioritynow, () => !Defines.OneThreadMode, () =>
                                    {
                                        priorevent.actionEvent.DynamicInvoke();
                                        if (!prioritynow.GateOpened)
                                        {
                                            prioritynow.GateOpened = true;
                                            OpenedDownGates = GatesCounter(OpenedDownGates);
                                        }
                                        LockEx.Lock(_lock, () => !Defines.OneThreadMode, () =>
                                        {
                                            //_eventLists[prioritynow.priorityValue].RemoveAt(0);
                                            try
                                            {
                                                _eventLists[prioritynow.priorityValue].RemoveAt(0);
                                            }
                                            catch (Exception ex)
                                            {
                                                NLogger.Log($"Error in priority event queue (Maybe you start already executed only one execution method (OnAdded as example)) - {ex.Message}\n in type {this.ownerType} \n{this.creationStackTrace}");
                                            }
                                        });

                                        ProcessEvents();
                                    });
                                });
                            }
                        }
                    }
                }
            });
        }
    }
}