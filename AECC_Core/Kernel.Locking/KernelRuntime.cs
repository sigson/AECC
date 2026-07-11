namespace AECC.Locking
{
    /// <summary>
    /// Concurrency mode for the lock primitives. Each container fixes its mode at construction
    /// time and passes it to its own storages via the constructor parameter; switching the mode
    /// afterward does not affect already-constructed structures.
    /// </summary>
    public enum ConcurrencyMode : byte
    {
        MultiThread = 0,
        SingleThread = 1,
    }

    /// <summary>
    /// Process-wide default used by constructors that are not given a mode explicitly.
    ///
    /// The kernel has no dependency on the application's configuration layer; the application is
    /// responsible for keeping these values in sync with its own settings.
    /// </summary>
    public static class KernelRuntime
    {
        /// <summary>Process-wide default concurrency mode.</summary>
        public static volatile ConcurrencyMode DefaultMode = ConcurrencyMode.SingleThread;

        /// <summary>Selects which IReaderWriterLockSlim implementation RWLock uses.</summary>
        public static volatile bool ThreadsMode = true;
    }
}
