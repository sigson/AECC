using System;

namespace AECC.Abstractions
{
    /// <summary>
    /// Thin abstraction over NLogger. Its surface mirrors the log channels actually used by
    /// the core.
    /// </summary>
    public interface INLogger
    {
        void Log(object content);
        void Warn(object content);
        void Error(object content);
        void ErrorLocking(object content);
        void Debug(object content);
    }

    /// <summary>
    /// Scheduler abstraction over TaskEx.RunAsync and TimerCompat. Allows lifecycle-queue and
    /// time-dependent tests to inject a scheduler so that Add/Change/Remove ordering can be
    /// verified deterministically.
    /// </summary>
    public interface IScheduler
    {
        /// <summary>Asynchronous execution (semantics of TaskEx.RunAsync: respects app modes).</summary>
        void Run(Action action);

        /// <summary>Periodic or one-shot tick (semantics of TimerCompat). Dispose stops the timer.</summary>
        IDisposable Schedule(int intervalMs, Action tick, bool repeating);
    }

    /// <summary>Clock abstraction over DateTime/TimerCompat.TimerDateTime, for testable time.</summary>
    public interface IClock
    {
        long UtcNowTicks { get; }
        DateTime UtcNow { get; }
    }

    /// <summary>Behavioral kind of a world; mirrors ECSWorld.WorldTypeEnum.</summary>
    public enum WorldKind
    {
        Server,
        Client,
        Offline,
    }

    /// <summary>
    /// Minimal view of a world that a model actually needs, replacing the
    /// IDObject -> static ECSWorld.GetWorld -> whole-world chain. The per-IDObject world
    /// cache (ECSWorldOwnerCache, validated by id) is still used underneath — the context is
    /// only resolved on a cache miss.
    /// </summary>
    public interface IWorldContext
    {
        long InstanceId { get; }
        WorldKind Kind { get; }
        ITypeRegistry Types { get; }
    }
}
