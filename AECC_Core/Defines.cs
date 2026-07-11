using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AECC;
using AECC.Locking;

public static partial class Defines
{
    // Concurrency flags below are forwarding properties onto AECC.Kernel.Locking.KernelRuntime
    // / LockDiagnostics (Kernel has zero dependencies, so it cannot depend on Defines):
    // reading/writing these properties reads/writes the kernel state directly, with no
    // synchronization window regardless of initialization order.
    //
    // Note: lock storages (LockedDictionarySlim, ComponentBag, SharedLock, RWLock) fix their
    // concurrency mode at construction time — toggling these flags does not affect structures
    // already created.

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

    /// <summary>Forwards to LockDiagnostics (gates non-critical lock-diagnostics logging). Default: true.</summary>
    public static bool IgnoreNonDangerousExceptions
    {
        get { return LockDiagnostics.IgnoreNonDangerousExceptions; }
        set { LockDiagnostics.IgnoreNonDangerousExceptions = value; }
    }

    public static bool RedirectAllLogsToExeFile = false;
    public static bool AOTMode = false;
    public static bool CutClientServerCollections = false;

    /// <summary>Tracks a per-entity log of removed components
    /// (EntitySerializationState.RemovedComponents), consumed to deliver removals during
    /// GDAP serialization (IncludeRemovedAvailable/Restricted). The log accumulates between
    /// slices and is cleared by a slice (EntityNetSerializer.SerializeEntity) or by
    /// OnEntityDelete. In worlds that are never serialized (offline simulation, benchmarks)
    /// there are no slices to clear it, so the log grows unboundedly with component churn.
    /// Setting this false disables tracking (Add is skipped; Clear safety nets remain).
    /// Read live at each Add call site.</summary>
    public static bool TrackRemovedComponents = true;

    public static int TimerMinimumMSTick = 15;

    /// <summary>Forwards to KernelRuntime.ThreadsMode. Default: true.</summary>
    public static bool ThreadsMode
    {
        get { return KernelRuntime.ThreadsMode; }
        set { KernelRuntime.ThreadsMode = value; }
    }

    /// <summary>Forwards to KernelRuntime.DefaultMode. Default: true (SingleThread).</summary>
    public static bool OneThreadMode
    {
        get { return KernelRuntime.DefaultMode == ConcurrencyMode.SingleThread; }
        set { KernelRuntime.DefaultMode = value ? ConcurrencyMode.SingleThread : ConcurrencyMode.MultiThread; }
    }
}
