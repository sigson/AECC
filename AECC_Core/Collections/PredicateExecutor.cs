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
    public class PredicateExecutor
    {
        // Статический кеш всех инстансов
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, PredicateExecutor> InstanceCache = 
            new System.Collections.Concurrent.ConcurrentDictionary<string, PredicateExecutor>();
        
        // Свойства класса
        public string PredicateId { get; private set; }
        public bool SuccessfullExecuted { get; private set; }
        private List<Func<bool>> predicates;
        public Action payloadAction;
        private int maxAttempts;
        private int timeoutBetweenAttempts;
        private Action<Exception, string> errorHandler;
        private StackTrace stackTrace;
        private int currentAttempt = 0;
        private TimerCompat timer;
        private bool isDisposed = false;
        
        /// <summary>
        /// Конструктор PredicateExecutor
        /// </summary>
        /// <param name="predicateId">Уникальный идентификатор предиката</param>
        /// <param name="predicates">Список предикатов для выполнения</param>
        /// <param name="payloadAction">Действие, выполняемое после успешного прохождения предикатов</param>
        /// <param name="maxAttempts">Максимальное количество попыток</param>
        /// <param name="timeoutBetweenAttempts">Таймаут между попытками в миллисекундах</param>
        /// <param name="errorHandler">Опциональный обработчик ошибок</param>
        public PredicateExecutor(
            string predicateId,
            List<Func<bool>> predicates,
            Action payloadAction,
            int timeoutBetweenAttempts = 1000,
            int maxAttempts = 3,
            Action<Exception, string> errorHandler = null, bool replaceExist = false)
        {
            // Валидация параметров
            if (string.IsNullOrWhiteSpace(predicateId))
                throw new ArgumentNullException(nameof(predicateId));
            if (predicates == null || predicates.Count == 0)
                throw new ArgumentNullException(nameof(predicates));
            if (payloadAction == null)
                throw new ArgumentNullException(nameof(payloadAction));
            if (maxAttempts <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxAttempts), "Must be greater than 0");
            if (timeoutBetweenAttempts < 0)
                throw new ArgumentOutOfRangeException(nameof(timeoutBetweenAttempts), "Must be non-negative");
            
            this.PredicateId = predicateId;
            this.predicates = new List<Func<bool>>(predicates);
            this.payloadAction = payloadAction;
            this.maxAttempts = maxAttempts;
            this.timeoutBetweenAttempts = timeoutBetweenAttempts;
            
            // Установка обработчика ошибок (стандартный или пользовательский)
            this.errorHandler = errorHandler ?? DefaultErrorHandler;

            // Сохраняем stack trace для диагностики
            this.stackTrace = new StackTrace(true);

            // Добавляем в кеш
            if (replaceExist)
            {
                if (InstanceCache.TryGetValue(predicateId, out var existPredicate))
                {
                    existPredicate.Stop();
                }
            }
            if (!InstanceCache.TryAdd(predicateId, this))
            {
                NLogger.Error($"Failed to add PredicateExecutor with ID '{predicateId}' to cache - ID already exists");
                throw new InvalidOperationException($"PredicateExecutor with ID '{predicateId}' already exists");
            }
            
            NLogger.Log($"Created PredicateExecutor with ID: {predicateId}, MaxAttempts: {maxAttempts}, Timeout: {timeoutBetweenAttempts}ms");
        }

        /// <summary>
        /// Запускает выполнение предикатов
        /// </summary>
        public PredicateExecutor Start()
        {
            if (isDisposed)
            {
                NLogger.Error($"Cannot start disposed PredicateExecutor with ID: {PredicateId}");
                return this;
            }

            NLogger.Log($"Starting PredicateExecutor with ID: {PredicateId}");
            TryExecutePredicates();
            return this;
        }
        
        /// <summary>
        /// Попытка выполнения предикатов
        /// </summary>
        private void TryExecutePredicates()
        {
            if (isDisposed) return;
            
            currentAttempt++;
            NLogger.Log($"PredicateExecutor '{PredicateId}': Attempt {currentAttempt}/{maxAttempts}");
            
            try
            {
                // Проверяем все предикаты
                bool allPredicatesPassed = true;
                for (int i = 0; i < predicates.Count; i++)
                {
                    var predicate = predicates[i];
                    bool result = false;
                    
                    try
                    {
                        result = predicate.Invoke();
                    }
                    catch (Exception ex)
                    {
                        NLogger.Error($"PredicateExecutor '{PredicateId}': Predicate {i + 1} threw exception: {ex.Message}");
                        allPredicatesPassed = false;
                        break;
                    }
                    
                    if (!result)
                    {
                        NLogger.Log($"PredicateExecutor '{PredicateId}': Predicate {i + 1} returned false");
                        allPredicatesPassed = false;
                        break;
                    }
                    
                    NLogger.Log($"PredicateExecutor '{PredicateId}': Predicate {i + 1} passed");
                }
                
                if (allPredicatesPassed)
                {
                    // Все предикаты прошли успешно - выполняем полезную нагрузку
                    ExecutePayload();
                }
                else
                {
                    // Не все предикаты прошли - планируем повторную попытку
                    ScheduleRetry();
                }
            }
            catch (Exception ex)
            {
                HandleError(ex, "Error during predicate execution");
            }
        }
        
        /// <summary>
        /// Выполняет полезную нагрузку
        /// </summary>
        private void ExecutePayload()
        {
            try
            {
                NLogger.Log($"PredicateExecutor '{PredicateId}': All predicates passed, executing payload");
                payloadAction.Invoke();
                NLogger.Log($"PredicateExecutor '{PredicateId}': Payload executed successfully");

                // Успешное выполнение - удаляем из кеша
                RemoveFromCache();
                
                SuccessfullExecuted = true;
            }
            catch (Exception ex)
            {
                HandleError(ex, $"Error during payload execution \n!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!\n{new StackTrace(ex, true)}\n!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!\n");
            }
        }
        
        /// <summary>
        /// Планирует повторную попытку
        /// </summary>
        private void ScheduleRetry()
        {
            if (currentAttempt >= maxAttempts)
            {
                var error = new Exception($"Max attempts ({maxAttempts}) reached for PredicateExecutor '{PredicateId}'");
                HandleError(error, "Max attempts exceeded");
                return;
            }
            
            if (timeoutBetweenAttempts > 0)
            {
                NLogger.Log($"PredicateExecutor '{PredicateId}': Scheduling retry in {timeoutBetweenAttempts}ms");
                
                // Создаем и запускаем таймер для повторной попытки
                timer = new TimerCompat();
                timer.TimerCompatInit(timeoutBetweenAttempts, (obj, arg) =>
                {
                    timer.Stop();
                    timer.Dispose();
                    timer = null;
                    TryExecutePredicates();
                }, loop: false);
                timer.Start();
            }
            else
            {
                // Немедленная повторная попытка
                TryExecutePredicates();
            }
        }
        
        /// <summary>
        /// Обработка ошибок
        /// </summary>
        private void HandleError(Exception ex, string context)
        {
            NLogger.Error($"PredicateExecutor '{PredicateId}' - {context}: {ex.Message}");
            NLogger.Error($"Stack trace from creation:\n{stackTrace}");
            
            // Вызываем обработчик ошибок
            errorHandler?.Invoke(ex, context);
            
            // Удаляем из кеша при ошибке
            RemoveFromCache();
        }
        
        /// <summary>
        /// Стандартный обработчик ошибок
        /// </summary>
        private void DefaultErrorHandler(Exception ex, string context)
        {
            NLogger.Error($"[DEFAULT ERROR HANDLER] PredicateExecutor '{PredicateId}': {context}");
            NLogger.Error($"Exception details: {ex}");
        }
        
        /// <summary>
        /// Удаляет экземпляр из кеша
        /// </summary>
        private void RemoveFromCache()
        {
            if (InstanceCache.TryRemove(PredicateId, out _))
            {
                NLogger.Log($"PredicateExecutor '{PredicateId}' removed from cache");
            }
            Dispose();
        }

        /// <summary>
        /// Обновляет предикаты
        /// </summary>
        public PredicateExecutor UpdatePredicates(List<Func<bool>> newPredicates)
        {
            if (newPredicates == null || newPredicates.Count == 0)
                throw new ArgumentNullException(nameof(newPredicates));

            this.predicates = new List<Func<bool>>(newPredicates);
            NLogger.Log($"PredicateExecutor '{PredicateId}': Updated predicates (count: {newPredicates.Count})");
            return this;
        }

        /// <summary>
        /// Добавляет предикат
        /// </summary>
        public PredicateExecutor AddPredicate(Func<bool> predicate)
        {
            if (predicate == null)
                throw new ArgumentNullException(nameof(predicate));

            predicates.Add(predicate);
            NLogger.Log($"PredicateExecutor '{PredicateId}': Added predicate (total: {predicates.Count})");
            return this;
        }

        /// <summary>
        /// Останавливает выполнение и удаляет из кеша
        /// </summary>
        public PredicateExecutor Stop()
        {
            NLogger.Log($"Stopping PredicateExecutor '{PredicateId}'");
            RemoveFromCache();
            return this;
        }
        
        /// <summary>
        /// Освобождает ресурсы
        /// </summary>
        public void Dispose()
        {
            if (isDisposed) return;
            
            isDisposed = true;
            
            if (timer != null)
            {
                timer.Stop();
                timer.Dispose();
                timer = null;
            }
            
            predicates?.Clear();
            payloadAction = null;
            errorHandler = null;
            
            NLogger.Log($"PredicateExecutor '{PredicateId}' disposed");
        }
        
        // Статические методы для работы с кешем
        
        /// <summary>
        /// Получает экземпляр из кеша по ID
        /// </summary>
        public static PredicateExecutor GetFromCache(string predicateId)
        {
            InstanceCache.TryGetValue(predicateId, out var instance);
            return instance;
        }
        
        /// <summary>
        /// Получает все активные экземпляры
        /// </summary>
        public static IEnumerable<PredicateExecutor> GetAllActive()
        {
            return InstanceCache.Values.ToList();
        }
        
        /// <summary>
        /// Очищает весь кеш
        /// </summary>
        public static void ClearCache()
        {
            var instances = InstanceCache.Values.ToList();
            foreach (var instance in instances)
            {
                instance.Dispose();
            }
            InstanceCache.Clear();
            NLogger.Log("PredicateExecutor cache cleared");
        }
        
        /// <summary>
        /// Получает количество активных экземпляров
        /// </summary>
        public static int GetActiveCount()
        {
            return InstanceCache.Count;
        }
    }
}