using System;
using System.Threading;
using AECC.Locking;

namespace AECC.Core.Logging
{
    /// <summary>
    /// Реализация <see cref="IEscapeDiagnostics"/> поверх NLogger (ТЗ 4.1.2).
    /// Восстанавливает утраченную диагностику deadlock escape: сообщение уровня Error летит
    /// в лог безусловно (как в старом RWLock: "HALT! DEADLOCK ESCAPE! ..."), stack trace —
    /// только при включённом <see cref="LockDiagnostics.CaptureEscapeStackTrace"/>.
    /// С фазы 3 эта реализация переезжает в AECC.Runtime поверх INLogger.
    /// </summary>
    public sealed class NLoggerEscapeDiagnostics : IEscapeDiagnostics
    {
        public void DeadlockEscape(string message, string stackTrace)
        {
            // Канал прежний: ERRORLOCK (NLogger.LogErrorLocking), как в диагностическом
            // варианте старого RWLock; сообщение — дословно старое.
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
    /// Установка диагностики лок-ядра. Вызывается из статического конструктора Defines
    /// (т.е. при первом же касании Defines кем угодно) и из ECSWorld.InitWorldScope —
    /// избыточность намеренная: no-op сток в Kernel допустим только для юнит-бенчей.
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
