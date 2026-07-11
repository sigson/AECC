using System;
using System.Diagnostics;

namespace AECC.Locking
{
    /// <summary>
    /// Diagnostics channel for the lock core.
    ///
    /// Deadlock-escape philosophy: a detected self-deadlock (a cross-mode reentry by the same
    /// thread on the same cell) is a LOUDLY logged diagnostic, not a reason to hang the application
    /// with stuck threads, and not a reason to silently drop the operation. The condition is
    /// detected, an Error-level message is emitted ("HALT! DEADLOCK ESCAPE! ..."), and execution
    /// continues without acquiring the lock; diagnosing the root cause is left to the programmer via
    /// the log.
    ///
    /// The kernel does not depend on a concrete logger, so reports go through this interface. A
    /// no-op default is acceptable ONLY for unit benchmarks.
    /// </summary>
    public interface IEscapeDiagnostics
    {
        /// <summary>
        /// A cross-mode self-acquisition (write-under-read or read-under-write on the same cell by
        /// the same thread) was detected and bypassed (escape). <paramref name="stackTrace"/> is
        /// populated only when <see cref="LockDiagnostics.CaptureEscapeStackTrace"/> is enabled,
        /// otherwise null.
        /// </summary>
        void DeadlockEscape(string message, string stackTrace);

        /// <summary>Other lock-infrastructure errors.</summary>
        void LockingError(object content);
    }

    /// <summary>No-op sink. Use only in unit benchmarks.</summary>
    public sealed class NullEscapeDiagnostics : IEscapeDiagnostics
    {
        public static readonly NullEscapeDiagnostics Instance = new NullEscapeDiagnostics();
        private NullEscapeDiagnostics() { }
        public void DeadlockEscape(string message, string stackTrace) { }
        public void LockingError(object content) { }
    }

    /// <summary>
    /// Static injection point for lock-core diagnostics. The application must set
    /// <see cref="Sink"/> during initialization (see KernelBootstrap).
    ///
    /// All diagnostics run ONLY on the cold escape branch: zero cost on the hot path (the static
    /// field is read only after an escape has already occurred).
    /// </summary>
    public static class LockDiagnostics
    {
        private static IEscapeDiagnostics _sink = NullEscapeDiagnostics.Instance;

        /// <summary>The installed diagnostics sink. Never null.</summary>
        public static IEscapeDiagnostics Sink
        {
            get { return _sink; }
            set { _sink = value ?? NullEscapeDiagnostics.Instance; }
        }

        /// <summary>
        /// Diagnostic flag: capture a stack trace at the escape point. StackTrace capture is
        /// expensive, so it is off by default; the Error message itself is always emitted (cheap).
        /// </summary>
        public static volatile bool CaptureEscapeStackTrace = false;

        /// <summary>
        /// Gate for non-dangerous/secondary lock diagnostics. Default: true.
        /// </summary>
        public static volatile bool IgnoreNonDangerousExceptions = true;

        internal const string EscapeWriteUnderRead =
            "HALT! DEADLOCK ESCAPE! You tried to enter write lock while read lock is held!";
        internal const string EscapeReadUnderWrite =
            "HALT! DEADLOCK ESCAPE! You tried to enter read lock inner write locked thread!";

        /// <summary>
        /// Reports an escape from the detection point (RWCell, cold branch).
        /// <paramref name="wantWrite"/> is the mode that was being acquired.
        /// </summary>
        public static void ReportDeadlockEscape(bool wantWrite)
        {
            string st = null;
            if (CaptureEscapeStackTrace)
            {
                try { st = new StackTrace(true).ToString(); }
                catch (Exception e) { st = "Stack trace capture failed: " + e.Message; }
            }
            _sink.DeadlockEscape(wantWrite ? EscapeWriteUnderRead : EscapeReadUnderWrite, st);
        }
    }
}
