using System;
using System.Diagnostics;

namespace AECC.Locking
{
    /// <summary>
    /// Канал диагностики лок-ядра (ТЗ 4.1.2, решение заказчика 0.4.5).
    ///
    /// Философия deadlock escape: обнаруженный самодедлок (кросс-режимный захват тем же
    /// потоком той же ячейки) — это ГРОМКО логируемая диагностика, а не повод повесить
    /// приложение зависшими тредами и не повод молча терять операцию. Факт детектируется,
    /// в лог летит сообщение уровня Error ("HALT! DEADLOCK ESCAPE! ..."), исполнение
    /// продолжается без захвата; разбор причины — задача программиста по логу.
    ///
    /// Kernel не зависит от конкретного логгера, поэтому репорт идёт через этот интерфейс.
    /// Реализация поверх NLogger живёт выше (сейчас — в AECC.Core, с фазы 3 — в Runtime
    /// поверх INLogger). No-op по умолчанию допустим ТОЛЬКО для юнит-бенчей.
    /// </summary>
    public interface IEscapeDiagnostics
    {
        /// <summary>
        /// Кросс-режимный самозахват (write-под-read или read-под-write той же ячейки тем же
        /// потоком) обнаружен и обойдён (escape). <paramref name="stackTrace"/> заполняется
        /// только при включённом <see cref="LockDiagnostics.CaptureEscapeStackTrace"/>, иначе null.
        /// </summary>
        void DeadlockEscape(string message, string stackTrace);

        /// <summary>Прочие ошибки лок-инфраструктуры (бывшие NLogger.Error / NLogger.LogErrorLocking в RWLock).</summary>
        void LockingError(object content);
    }

    /// <summary>No-op сток. Использовать только в юнит-бенчах.</summary>
    public sealed class NullEscapeDiagnostics : IEscapeDiagnostics
    {
        public static readonly NullEscapeDiagnostics Instance = new NullEscapeDiagnostics();
        private NullEscapeDiagnostics() { }
        public void DeadlockEscape(string message, string stackTrace) { }
        public void LockingError(object content) { }
    }

    /// <summary>
    /// Статическая точка инъекции диагностики лок-ядра. Приложение (AECC.Core / Runtime)
    /// обязано установить <see cref="Sink"/> при инициализации (см. KernelBootstrap).
    ///
    /// Вся диагностика — ТОЛЬКО на холодной escape-ветке: ноль стоимости на горячем пути
    /// (одно чтение статического поля выполняется лишь после того, как escape уже случился).
    /// </summary>
    public static class LockDiagnostics
    {
        private static IEscapeDiagnostics _sink = NullEscapeDiagnostics.Instance;

        /// <summary>Установленный сток диагностики. Никогда не null.</summary>
        public static IEscapeDiagnostics Sink
        {
            get { return _sink; }
            set { _sink = value ?? NullEscapeDiagnostics.Instance; }
        }

        /// <summary>
        /// Диагностический флаг: захватывать stack trace в точке escape. StackTrace дорог,
        /// поэтому по умолчанию выключен; Error-сообщение летит безусловно (дёшево).
        /// Соответствует варианту старого RWLock с LogErrorLocking + StackTrace.
        /// </summary>
        public static volatile bool CaptureEscapeStackTrace = false;

        /// <summary>
        /// Бывший Defines.IgnoreNonDangerousExceptions в RWLock (гейт второстепенной
        /// диагностики). Синхронизируется форвардинг-свойством Defines. Дефолт исходный: true.
        /// </summary>
        public static volatile bool IgnoreNonDangerousExceptions = true;

        // Дословные сообщения старого RWLock (ТЗ 4.1.2: диагностика восстанавливается,
        // формулировки — часть операционного контракта "программист разбирается по логу").
        internal const string EscapeWriteUnderRead =
            "HALT! DEADLOCK ESCAPE! You tried to enter write lock while read lock is held!";
        internal const string EscapeReadUnderWrite =
            "HALT! DEADLOCK ESCAPE! You tried to enter read lock inner write locked thread!";

        /// <summary>
        /// Репорт escape из точки детекта (RWCell, холодная ветка).
        /// <paramref name="wantWrite"/> — режим, который пытались захватить.
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
