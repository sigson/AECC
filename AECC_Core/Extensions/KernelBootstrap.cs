using System;
using System.Threading;
using AECC.Locking;

namespace AECC.Core.Logging
{
    /// <summary>
    /// Implementation of <see cref="IEscapeDiagnostics"/> backed by NLogger.
    /// The Error-level message is always logged; the stack trace is only appended
    /// when <see cref="LockDiagnostics.CaptureEscapeStackTrace"/> is enabled.
    /// </summary>
    public sealed class NLoggerEscapeDiagnostics : IEscapeDiagnostics
    {
        public void DeadlockEscape(string message, string stackTrace)
        {
            if (stackTrace == null)
                NLogger.LogErrorLocking(message);
            else
                NLogger.LogErrorLocking(message + Environment.NewLine + stackTrace);
        }

        public void LockingError(object content)
        {
            NLogger.LogErrorLocking(content);
        }
    }

    /// <summary>
    /// Installs the lock-core diagnostics sink. Called from Defines' static constructor
    /// (i.e. on first touch of Defines by anyone) and from ECSWorld.InitWorldScope; the
    /// redundant call sites are intentional, since a no-op sink in Kernel is only
    /// acceptable for unit benchmarks.
    /// </summary>
    public static class KernelBootstrap
    {
        private static int _installed;

        public static void EnsureInstalled()
        {
            if (Interlocked.Exchange(ref _installed, 1) == 0)
            {
                LockDiagnostics.Sink = new NLoggerEscapeDiagnostics();
            }
        }
    }
}
