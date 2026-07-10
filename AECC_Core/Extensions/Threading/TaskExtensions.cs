using System;
using System.Threading;
using System.Threading.Tasks;
using AECC.Core.Logging;

namespace AECC.Extensions.ThreadingSync
{
    public class TaskEx : Task
    {
        public TaskEx(Action action) : base(action)
        {
        }

        public TaskEx(Action action, CancellationToken cancellationToken) : base(action, cancellationToken)
        {
        }

        public TaskEx(Action action, TaskCreationOptions creationOptions) : base(action, creationOptions)
        {
        }

        public TaskEx(Action<object> action, object state) : base(action, state)
        {
        }

        public TaskEx(Action action, CancellationToken cancellationToken, TaskCreationOptions creationOptions) : base(action, cancellationToken, creationOptions)
        {
        }

        public TaskEx(Action<object> action, object state, CancellationToken cancellationToken) : base(action, state, cancellationToken)
        {
        }

        public TaskEx(Action<object> action, object state, TaskCreationOptions creationOptions) : base(action, state, creationOptions)
        {
        }

        public TaskEx(Action<object> action, object state, CancellationToken cancellationToken, TaskCreationOptions creationOptions) : base(action, state, cancellationToken, creationOptions)
        {
        }

        public static void RunAsyncTask(Action action)
        {
#if UNITY_5_3_OR_NEWER
            Func<Task> asyncUpd = async () =>
            {
                await Task.Run(() => {
                    action();
                }).LogExceptionIfFaulted().ConfigureAwait(false);
            };
            asyncUpd();
#else

#endif
        }

        /// <summary>Единственный на процесс делегат исполнения action-as-state для
        /// ThreadPool-ветки RunAsync (см. комментарий в теле): try/catch-семантика
        /// дословно прежняя, но без пер-вызовных замыканий.</summary>
        private static readonly WaitCallback RunActionFromState = state =>
        {
            try
            {
                ((Action)state)();
            }
            catch (Exception ex)
            {
                NLogger.ErrorThread(ex);
            }
        };

        public static void RunAsync(Action action, bool forceThreadmode = false, bool forceAsync = false)
        {
#if UNITY_5_3_OR_NEWER
            Thread thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    NLogger.LogError(ex);
                }
            });
            thread.Start();
#else
            if (Defines.OneThreadMode && !forceThreadmode && !forceAsync)
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    NLogger.LogError(ex);
                }
            }
            else
            {
                if ((Defines.ThreadsMode || forceThreadmode) && !forceAsync)
                {
                    // ОПТИМИЗАЦИЯ ПАМЯТИ (work-item flood): прежняя лямбда-обёртка
                    // `_ => { try { action(); } ... }` захватывала action и на КАЖДЫЙ вызов
                    // аллоцировала DisplayClass + Action + WaitCallback (в снапшоте — по ~4M
                    // каждого). Теперь action передаётся как state в ЕДИНСТВЕННЫЙ кэшированный
                    // WaitCallback — на вызов остаётся только внутренний work item рантайма.
                    ThreadPool.QueueUserWorkItem(RunActionFromState, action);
                    // Thread thread = new Thread(() =>
                    // {
                    //     try
                    //     {
                    //         action();
                    //     }
                    //     catch (Exception ex)
                    //     {
                    //         NLogger.LogError(ex);
                    //     }
                    // });
                    // thread.Start();
                }
                else
                {
                    if(!Defines.ThreadsMode || forceAsync)
                    {
                        Func<Task> asyncUpd = async () =>
                        {
                            await Task.Run(() =>
                            {
                                try
                                {
                                    action();
                                }
                                catch (Exception ex)
                                {
                                    NLogger.LogError(ex);
                                }
                            }).ConfigureAwait(false);
                        };
                        asyncUpd();
                    }
                }
            }
#endif
        }

        public static void Delay(double milliseconds)
        {
            var endTime = DateTime.Now.AddMilliseconds(milliseconds);
            while (DateTime.Now < endTime)
            {
                Task.Yield();
            }
        }
    }

#if UNITY_5_3_OR_NEWER
    public static class Lambda
    {
        public static UnityEngine.Events.UnityEvent<T> AddListener<T>(this UnityEngine.Events.UnityEvent<T> unityEvent, System.Action<T> action)
        {
            UnityEngine.Events.UnityAction<T> uaction = (T arg) => action(arg);
            unityEvent.AddListener(uaction);
            return unityEvent;
        }

        public static UnityEngine.Events.UnityEvent AddListener(this UnityEngine.Events.UnityEvent unityEvent, System.Action action)
        {
            UnityEngine.Events.UnityAction uaction = () => action();
            unityEvent.AddListener(uaction);
            return unityEvent;
        }

        public static bool TryExecute(Action act)
        {
            try
            {
                act();
                return true;
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        public static T LineFunction<T>(this T obj, Action<T> action)
        {
            action(obj);
            return obj;
        }

        public static T LineFunction<T>(Func<T> action)
        {
            try
            {
                return action();
            }
            catch(Exception ex)
            {
                NLogger.LogError(ex);
            }
            return default(T);
        }
    }
#else
    public static class Lambda
    {
        public static Action<T> AddListener<T>(this Action<T> unityEvent, System.Action<T> action)
        {
            Action<T> uaction = (T arg) => action(arg);
            unityEvent.AddListener(uaction);
            return unityEvent;
        }

        public static Action AddListener(this Action unityEvent, System.Action action)
        {
            Action uaction = () => action();
            unityEvent.AddListener(uaction);
            return unityEvent;
        }

        public static bool TryExecute(Action act)
        {
            try
            {
                act();
                return true;
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        public static T LineAction<T>(this T obj, Action<T> action)
        {
            action(obj);
            return obj;
        }

        public static T LineFunction<T>(Func<T> action)
        {
            return action();
        }
    }

#endif

#if UNITY_5_3_OR_NEWER
    public static class TaskExts
    {
        public static Task LogExceptionIfFaulted(this Task task)
        {
            task.ContinueWith(t =>
            {
                if (t.IsFaulted || t.Exception != null)
                {
                    NetworkingService.instance.ExecuteInstruction(() => UnityEngine.Debug.LogException(t.Exception.Flatten().InnerException));
                }
            });//, TaskScheduler.FromCurrentSynchronizationContext());
            return task;
        }
    }
#endif

    public static class LockEx
    {
        public static void Lock(object lockobj, Func<bool> conditionlocking, Action action)
        {
            if (conditionlocking())
            {
                lock (lockobj)
                {
                    action();
                }
            }
            else
            {
                action();
            }
        }
    }

}