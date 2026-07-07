using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AECC;
using AECC.Locking;

public static partial class Defines
{
    // Фаза 1 (ТЗ 4.1.1): флаги конкурентности переехали в AECC.Kernel.Locking.KernelRuntime /
    // LockDiagnostics (Kernel не может зависеть от Defines — у него 0 зависимостей).
    // Здесь остаются ФОРВАРДИНГ-СВОЙСТВА со старыми именами: весь legacy-код, читающий и
    // ПИШУЩИЙ Defines.OneThreadMode/ThreadsMode/IgnoreNonDangerousExceptions, продолжает
    // работать дословно — присваивание мгновенно синхронизирует kernel-дефолт, чтение видит
    // актуальное значение. Никакого окна рассинхронизации при любой очерёдности инициализации.
    //
    // BREAKING CHANGE (санкционирован ТЗ 4.1.1): лок-хранилища (LockedDictionarySlim,
    // ComponentBag, SharedLock, RWLock) фиксируют режим В МОМЕНТ СОЗДАНИЯ — переключение
    // флага на лету больше не влияет на уже созданные структуры.

    static Defines()
    {
        AECC.Core.Logging.KernelBootstrap.EnsureInstalled();
    }

    public static bool SerializationResultPrint = false;
    public static bool ECSNetworkTypeLogging = false;
    public static bool ServiceSetupLogging = false;
    public static bool LowLevelNetworkEventsLogging = false;
    public static bool DBEventsLogging = false;
    public static bool LogECSEntitySerializationComponents = false;
    public static bool SerializatorTypesLog = true;
    public static bool HiddenKeyNotFoundLog = false;

    /// <summary>Форвардинг в LockDiagnostics (гейт второстепенной диагностики лок-ядра). Дефолт исходный: true.</summary>
    public static bool IgnoreNonDangerousExceptions
    {
        get { return LockDiagnostics.IgnoreNonDangerousExceptions; }
        set { LockDiagnostics.IgnoreNonDangerousExceptions = value; }
    }

    public static bool RedirectAllLogsToExeFile = false;
    public static bool AOTMode = false;
    public static bool CutClientServerCollections = false;
    public static int TimerMinimumMSTick = 15;

    /// <summary>Форвардинг в KernelRuntime.ThreadsMode. Дефолт исходный: true.</summary>
    public static bool ThreadsMode
    {
        get { return KernelRuntime.ThreadsMode; }
        set { KernelRuntime.ThreadsMode = value; }
    }

    /// <summary>Форвардинг в KernelRuntime.DefaultMode. Дефолт исходный: true (SingleThread).</summary>
    public static bool OneThreadMode
    {
        get { return KernelRuntime.DefaultMode == ConcurrencyMode.SingleThread; }
        set { KernelRuntime.DefaultMode = value ? ConcurrencyMode.SingleThread : ConcurrencyMode.MultiThread; }
    }
}
