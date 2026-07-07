namespace AECC.Locking
{
    /// <summary>
    /// Режим конкурентности лок-примитивов (ТЗ 4.1.1). Заменяет чтение глобального
    /// <c>Defines.OneThreadMode</c> из тел методов лок-ядра. Мир фиксирует режим при
    /// создании и раздаёт его своим хранилищам через параметр конструктора.
    ///
    /// BREAKING CHANGE (санкционирован ТЗ 4.1.1): переключение флага "на лету" перестаёт
    /// влиять на уже созданные структуры — режим фиксируется в момент конструирования
    /// (раньше это и так было небезопасно).
    /// </summary>
    public enum ConcurrencyMode : byte
    {
        MultiThread = 0,
        SingleThread = 1,
    }

    /// <summary>
    /// ПЕРЕХОДНЫЙ (фазы 1-2) процессный дефолт для конструкторов, не получивших режим явно,
    /// и для legacy-мест "чтения флага в момент использования" (DictionaryWrapper, DualGate).
    ///
    /// Kernel не знает про Defines (0 зависимостей); приложение синхронизирует значение:
    /// свойство <c>Defines.OneThreadMode</c> в AECC.Core теперь форвардит сюда, поэтому
    /// семантика "старый код читает Defines в момент использования" сохранена дословно.
    ///
    /// Начиная с фазы 3 мир передаёт <see cref="ConcurrencyMode"/> своим хранилищам явно
    /// (через WorldProfile), и этот дефолт остаётся только за legacy-фасадами.
    /// Дефолты повторяют исходные значения Defines: OneThreadMode = true, ThreadsMode = true.
    /// </summary>
    public static class KernelRuntime
    {
        /// <summary>Процессный дефолт режима конкурентности. Синхронизируется с Defines.OneThreadMode.</summary>
        public static volatile ConcurrencyMode DefaultMode = ConcurrencyMode.SingleThread;

        /// <summary>Синхронизируется с Defines.ThreadsMode (выбор реализации IReaderWriterLockSlim в RWLock).</summary>
        public static volatile bool ThreadsMode = true;
    }
}
