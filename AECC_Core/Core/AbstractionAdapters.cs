using System;
using AECC.Abstractions;
using AECC.Core.Logging;
using AECC.Extensions;
using AECC.Extensions.ThreadingSync; // TaskEx живёт здесь

namespace AECC.Core
{
    /// <summary>Реализация INLogger поверх статического NLogger (фаза 2; в Runtime — с фазы 3).</summary>
    public sealed class NLoggerAdapter : INLogger
    {
        public static readonly NLoggerAdapter Instance = new NLoggerAdapter();
        public void Log(object content) { NLogger.Log(content); }
        public void Warn(object content) { NLogger.Warn(content); }
        public void Error(object content) { NLogger.Error(content); }
        public void ErrorLocking(object content) { NLogger.LogErrorLocking(content); }
        public void Debug(object content) { NLogger.Debug(content); }
    }

    /// <summary>
    /// Реализация IScheduler поверх существующих TaskEx.RunAsync и TimerCompat (фаза 2).
    /// Семантика исполнения — дословно прежняя: Run уважает режимы приложения
    /// (OneThreadMode → синхронно, ThreadsMode → пул), Schedule — TimerCompat + Start.
    /// Детерминированная тестовая реализация подключается в сетке вторым слоем.
    /// </summary>
    public sealed class DefaultScheduler : IScheduler
    {
        public static readonly DefaultScheduler Instance = new DefaultScheduler();

        public void Run(Action action)
        {
            TaskEx.RunAsync(action);
        }

        public IDisposable Schedule(int intervalMs, Action tick, bool repeating)
        {
            var timer = new TimerCompat(intervalMs, (sender, args) => tick(), repeating);
            timer.Start();
            return timer;
        }
    }

    /// <summary>Системные часы (фаза 2). Тестовые часы подключаются через тот же интерфейс.</summary>
    public sealed class SystemClock : IClock
    {
        public static readonly SystemClock Instance = new SystemClock();
        public long UtcNowTicks { get { return DateTime.UtcNow.Ticks; } }
        public DateTime UtcNow { get { return DateTime.UtcNow; } }
    }
}
