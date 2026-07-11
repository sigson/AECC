using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AECC.Extensions;
using System.Collections.Concurrent;
using AECC.Extensions;
using AECC.Core.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Diagnostics.Contracts;
using System.Diagnostics;
using AECC.Extensions.ThreadingSync;
using AECC.Collections;
using AECC.Locking;

namespace AECC.Core
{
    /// <summary>
    /// Уровни логгирования для контрактов
    /// </summary>
    public enum ContractLoggingLevel
    {
        /// <summary>
        /// Без логгирования
        /// </summary>
        None = 0,
        /// <summary>
        /// Только ошибки
        /// </summary>
        ErrorsOnly = 1,
        /// <summary>
        /// Полная информация
        /// </summary>
        Verbose = 2
    }

    public class ECSExecutableContractContainer
    {
        public string ContractId { get; set; }
        public Type SystemType
        {
            get => Spec._systemType;
            set
            {
                lock (ContractLocker)
                {
                    Spec._systemType = value;
                }
            }
        }

        public StackTrace GenerationStackTrace
        {
            get => Spec.genTrace;
            set
            {
                lock (ContractLocker)
                {
                    Spec.genTrace = value;
                }
            }
        }

        // Verbose-логирование по умолчанию выключено — диагностика включается точечно
        // (LoggingLevel = Verbose на конкретном контракте).
        /// <summary>
        /// Уровень логгирования для контракта
        /// </summary>
        public ContractLoggingLevel LoggingLevel
        {
            get => Spec._loggingLevel;
            set
            {
                lock (ContractLocker)
                {
                    Spec._loggingLevel = value;
                }
            }
        }

        /// <summary>
        /// key long - entityid
        /// </summary>
        public Dictionary<long, List<Func<ECSEntity, bool>>> ContractConditions
        {
            get => Spec._contractConditions;
            set
            {
                lock (ContractLocker)
                {
                    Spec._contractConditions = value;
                }
            }
        }

        /// <summary>
        /// key long - entityownerid
        ///long - componentTypeId
        ///bool - presence state
        /// </summary>
        public Dictionary<long, Dictionary<long, bool>> EntityComponentPresenceSign
        {
            get => Spec._entityComponentPresenceSign;
            set
            {
                lock (ContractLocker)
                {
                    Spec._entityComponentPresenceSign = value;
                }
            }
        }

        public Action<ECSExecutableContractContainer, ECSEntity[]> ContractExecutable
        {
            get => Spec._contractExecutable;
            set
            {
                lock (ContractLocker)
                {
                    Spec._contractExecutable = value;
                }
            }
        }

        public Action<ECSExecutableContractContainer, ECSEntity> ContractExecutableSingle
        {
            get => Spec._contractExecutableSingle;
            set
            {
                lock (ContractLocker)
                {
                    Spec._contractExecutableSingle = value;
                }
            }
        }

        public Action<ECSExecutableContractContainer, long[]> ErrorExecution
        {
            get => Spec._errorExecution;
            set
            {
                lock (ContractLocker)
                {
                    Spec._errorExecution = value;
                }
            }
        }


        public Func<ECSWorld, bool> WorldFilter
        {
            get => Spec._worldFilter;
            set
            {
                lock (ContractLocker)
                {
                    Spec._worldFilter = value;
                }
            }
        }


        public bool TimeDependExecution
        {
            get => Spec._timeDependExecution;
            set
            {
                lock (ContractLocker)
                {
                    Spec._timeDependExecution = value;
                }
            }
        }

        /// <summary>
        /// Set FALSE if contract is time depend
        /// </summary>
        public bool NoPresenceSignAllowed
        {
            get => Spec._noPresenceSignAllowed;
            set
            {
                lock (ContractLocker)
                {
                    Spec._noPresenceSignAllowed = value;
                }
            }
        }

        public ECSWorld ECSWorldOwner
        {
            get => Runtime._ecsWorldOwner;
            set
            {
                lock (ContractLocker)
                {
                    Runtime._ecsWorldOwner = value;
                }
            }
        }

        public bool RemoveAfterExecution
        {
            get => Spec._removeAfterExecution;
            set
            {
                lock (ContractLocker)
                {
                    Spec._removeAfterExecution = value;
                }
            }
        }

        public bool BypassFinalization
        {
            get => Spec._bypassFinalization;
            set
            {
                lock (ContractLocker)
                {
                    Spec._bypassFinalization = value;
                }
            }
        }

        public long MaxTries
        {
            get => Spec._maxTries;
            set
            {
                lock (ContractLocker)
                {
                    Spec._maxTries = value;
                }
            }
        }

        public long NowTried
        {
            get => Runtime._nowTried;
            set
            {
                lock (ContractLocker)
                {
                    Runtime._nowTried = value;
                }
            }
        }

        public bool TimeDependActive
        {
            get => Runtime._timeDependActive;
            set
            {
                lock (ContractLocker)
                {
                    Runtime._timeDependActive = value;
                }
            }
        }

        public bool PartialEntityFiltering
        {
            get => Spec._partialEntityFiltering;
            set
            {
                lock (ContractLocker)
                {
                    Spec._partialEntityFiltering = value;
                }
            }
        }

        public bool NotAllIncludedEntitiesPresenceSign
        {
            get => Spec._notAllIncludedEntitiesPresenceSign;
            set
            {
                lock (ContractLocker)
                {
                    Spec._notAllIncludedEntitiesPresenceSign = value;
                }
            }
        }

        public bool AsyncExecution
        {
            get => Spec._asyncExecution;
            set
            {
                lock (ContractLocker)
                {
                    Spec._asyncExecution = value;
                }
            }
        }

        public bool InWork
        {
            get => Runtime._inWork;
            set
            {
                lock (ContractLocker)
                {
                    Runtime._inWork = value;
                }
            }
        }

        public bool InProgress
        {
            get => Runtime._inProgress;
            set
            {
                lock (ContractLocker)
                {
                    Runtime._inProgress = value;
                }
            }
        }

        public long LastEndExecutionTimestamp
        {
            get => Runtime._lastEndExecutionTimestamp;
            set
            {
                lock (ContractLocker)
                {
                    Runtime._lastEndExecutionTimestamp = value;
                }
            }
        }

        public long DelayRunMilliseconds
        {
            get => Spec._delayRunMilliseconds;
            set
            {
                lock (ContractLocker)
                {
                    Spec._delayRunMilliseconds = value;
                }
            }
        }

        public bool ContractExecuted
        {
            get => Runtime._contractExecuted;
            set
            {
                lock (ContractLocker)
                {
                    Runtime._contractExecuted = value;
                }
            }
        }

        public bool ManualExitFromWorkingState
        {
            get => Spec._manualExitFromWorkingState;
            set
            {
                lock (ContractLocker)
                {
                    Spec._manualExitFromWorkingState = value;
                }
            }
        }

        // Spec/Runtime split: ContractSpec — декларативная часть (ЧТО за контракт: условия,
        // presence-sign, исполняемые тела, флаги режима). ContractRuntime — изменяемое
        // состояние исполнения (счётчики/фазы) за одним локером (Runtime.Locker /
        // ContractLocker). Свойства контейнера — тонкие делегации: чтения без лока,
        // записи под ContractLocker. Локер не подменяем (нет публичного сеттера) —
        // это предотвращает рассинхрон между читателями и писателями состояния.

        public sealed class ContractSpec
        {
            internal bool _asyncExecution = true;
            internal bool _bypassFinalization = false;
            internal Dictionary<long, List<Func<ECSEntity, bool>>> _contractConditions = null;
            internal Action<ECSExecutableContractContainer, ECSEntity[]> _contractExecutable =
            (ECSExecutableContractContainer contract, ECSEntity[] entities) =>
            {
                foreach (var entity in entities)
                {
                    contract.ContractExecutableSingle(contract, entity);
                }
            };
            internal Action<ECSExecutableContractContainer, ECSEntity> _contractExecutableSingle =
            (contract, entity) => { };
            internal long _delayRunMilliseconds = 0;
            internal Dictionary<long, Dictionary<long, bool>> _entityComponentPresenceSign = null;
            internal Action<ECSExecutableContractContainer, long[]> _errorExecution =
            (ECSExecutableContractContainer contract, long[] entities) => { };
            internal ContractLoggingLevel _loggingLevel = ContractLoggingLevel.None;
            internal bool _manualExitFromWorkingState = false;
            internal long _maxTries = long.MaxValue;
            internal bool _noPresenceSignAllowed = true;
            internal bool _notAllIncludedEntitiesPresenceSign = false;
            internal bool _partialEntityFiltering = false;
            internal bool _removeAfterExecution = true;
            internal Type _systemType = null;
            internal bool _timeDependExecution = false;
            internal Func<ECSWorld, bool> _worldFilter =
            (world) => { return true; };
            internal StackTrace genTrace = null;
        }

        public sealed class ContractRuntime
        {
            internal readonly object Locker = new object();
            internal bool _contractExecuted = false;
            internal ECSWorld _ecsWorldOwner = null;
            internal bool _inProgress = false;
            internal bool _inWork = false;
            internal long _lastEndExecutionTimestamp = 0;
            internal long _nowTried = 0;
            internal bool _timeDependActive = true;
        }

        public readonly ContractSpec Spec = new ContractSpec();
        public readonly ContractRuntime Runtime = new ContractRuntime();

        /// <summary>Единый локер состояния контракта.</summary>
        public object ContractLocker { get { return Runtime.Locker; } }

        public List<long> NeededEntities
        {
            get
            {
                if (ContractConditions != null && EntityComponentPresenceSign != null)
                {
                    var allentities = ContractConditions.Keys.ToList();
                    allentities.AddRange(this.EntityComponentPresenceSign.Keys);
                    return new HashSet<long>(allentities).ToList();
                }
                return null;
            }
        }

        /// <summary>Захват стека рождения контракта — дорогая операция (StackTrace на
        /// каждый контракт), поэтому включается только при активной диагностике;
        /// dead-letter без флага печатает подсказку вместо стека.</summary>
        public static bool CaptureGenerationStackTrace = false;

        public ECSExecutableContractContainer()
        {
            GenerationStackTrace = CaptureGenerationStackTrace ? new System.Diagnostics.StackTrace() : null;
            if(this.GetType() == typeof(ECSExecutableContractContainer))
            {
                this.MaxTries = 1;
            }
            else
            {
                this.RemoveAfterExecution = false;
            }
        }

        /// <summary>
        /// Methor running before ECS system initialliation
        /// </summary>
        /// <param name="SystemManager"></param>
        public virtual void Initialize()
        {

        }
        /// <summary>
        /// contractEntities not null if it time depend contract
        /// </summary>
        /// <param name="ExecuteContract"></param>
        /// <param name="contractEntities"></param>
        /// <returns></returns>
        public bool TryExecuteContract(bool ExecuteContract = true, List<long> contractEntities = null )
        {
            lock (ContractLocker)
            {
                NowTried++;
                BypassFinalization = false;
                if (!ContractExecuted)
                {
                    OnTryExecute();
                    if(contractEntities == null)
                    {
                        var allentities = ContractConditions.Keys.ToList();
                        allentities.AddRange(this.EntityComponentPresenceSign.Keys);

                        bool contractResult = false;
                        List<IDisposable> lockers = null;
                        List<ECSEntity> executionEntities = null;
                        if (Defines.OneThreadMode)
                        {
                            contractResult = GetContractLockersOneThread(allentities, this.ContractConditions, this.EntityComponentPresenceSign, false, out lockers, out executionEntities);
                        }
                        else
                        {
                            contractResult = GetContractLockers(allentities, this.ContractConditions, this.EntityComponentPresenceSign, false, out lockers, out executionEntities);
                        }
                        if (contractResult && lockers != null)
                        {
                            var errorState = false;
                            if (ExecuteContract)
                            {
                                this.InWork = true;
                                try
                                {
                                    ContractExecutable(this, executionEntities.ToArray());
                                }
                                catch (Exception ex)
                                {
                                    NLogger.LogError(ex);
                                    ErrorExecution(this, executionEntities.Select(x => x.instanceId).ToArray());
                                    errorState = true;
                                }
                                lockers.ForEach(x => x.Dispose());
                                if (!ManualExitFromWorkingState)
                                {
                                    this.LastEndExecutionTimestamp = DateTime.Now.Ticks;
                                    this.InWork = false;
                                }
                            }
                            else
                            {
                                lockers.ForEach(x => x.Dispose());
                            }
                            if (ExecuteContract && !errorState)
                            {
                                if(!BypassFinalization)
                                {
                                    ContractExecuted = true;
                                }
                                return true;
                            }
                        }
                        else
                        {
                            if (ExecuteContract)
                                ErrorExecution(this, allentities.ToArray());
                        }
                    }
                    else
                    {
                        var filledContractConditions = new Dictionary<long, List<Func<ECSEntity, bool>>>();
                        var filledEntityComponentPresenceSign = new Dictionary<long, Dictionary<long, bool>>();
                        foreach (var contractCond in this.ContractConditions)
                        {
                            foreach (var contractEntity in contractEntities)
                            {
                                filledContractConditions[contractEntity] = contractCond.Value;
                            }
                        }
                        foreach (var presenceSign in this.EntityComponentPresenceSign)
                        {
                            foreach (var contractEntity in contractEntities)
                            {
                                filledEntityComponentPresenceSign[contractEntity] = presenceSign.Value;
                            }
                        }
                        
                        bool contractResult = false;
                        List<IDisposable> lockers = null;
                        List<ECSEntity> executionEntities = null;
                        if (Defines.OneThreadMode)
                        {
                            contractResult = GetContractLockersOneThread(contractEntities, filledContractConditions, filledEntityComponentPresenceSign, true, out lockers, out executionEntities);
                        }
                        else
                        {
                            contractResult = GetContractLockers(contractEntities, filledContractConditions, filledEntityComponentPresenceSign, true, out lockers, out executionEntities);
                        }

                        if (contractResult && lockers != null)
                        {
                            var errorState = false;
                            if (ExecuteContract)
                            {
                                this.InWork = true;
                                try
                                {
                                    ContractExecutable(this, executionEntities.ToArray());
                                }
                                catch (Exception ex)
                                {
                                    NLogger.LogError(ex);
                                    ErrorExecution(this, executionEntities.Select(x => x.instanceId).ToArray());
                                    errorState = true;
                                }
                                lockers.ForEach(x => x.Dispose());
                                if (!ManualExitFromWorkingState)
                                {
                                    this.LastEndExecutionTimestamp = DateTime.Now.Ticks;
                                    this.InWork = false;
                                }
                            }
                            else
                            {
                                lockers.ForEach(x => x.Dispose());
                            }
                            if (!errorState)
                                return true;
                        }
                        else
                        {
                            if (ExecuteContract)
                                ErrorExecution(this, contractEntities.ToArray());
                        }
                    }
                    return false;
                }
                else
                {
                    NLogger.Log("You tried to execute contract that was already executed\n" + (this.GenerationStackTrace != null ? this.GenerationStackTrace.ToString() : "(стек не захвачен: 6.7)") + "\n================================");
                    return false;
                }
            }
        }

        // GetContractLockersOneThread / GetContractLockers are thin shims over
        // AcquireContractTargets: a single implementation for the entity-check core
        // (presence-sign, conditions, diagnostics, rollback discipline, strict finalization).
        // Mode-specific points are marked [T] (token acquisition) and [D1]/[D2]/[D3]
        // (single-thread vs multi-thread semantic differences, preserved verbatim
        // behind the mode flag so ST and MT behavior stays intentionally distinct).

        private bool GetContractLockersOneThread(
            List<long> contractEntities,
            IDictionary<long, List<Func<ECSEntity, bool>>> localContractConditions,
            IDictionary<long, Dictionary<long, bool>> localEntityComponentPresenceSign,
            bool partialEntityTargetListLockingAllowed,
            out List<IDisposable> lockTokens,
            out List<ECSEntity> executionEntities)
        {
            return AcquireContractTargets(contractEntities, localContractConditions, localEntityComponentPresenceSign,
                partialEntityTargetListLockingAllowed, takeTokens: false, out lockTokens, out executionEntities);
        }

        private bool GetContractLockers(
            List<long> contractEntities,
            IDictionary<long, List<Func<ECSEntity, bool>>> LocalContractConditions,
            IDictionary<long, Dictionary<long, bool>> LocalEntityComponentPresenceSign,
            bool partialEntityTargetListLockingAllowed,
            out List<IDisposable> lockTokens,
            out List<ECSEntity> executionEntities)
        {
            return AcquireContractTargets(contractEntities, LocalContractConditions, LocalEntityComponentPresenceSign,
                partialEntityTargetListLockingAllowed, takeTokens: true, out lockTokens, out executionEntities);
        }

        private bool AcquireContractTargets(
            List<long> contractEntities,
            IDictionary<long, List<Func<ECSEntity, bool>>> localContractConditions,
            IDictionary<long, Dictionary<long, bool>> localEntityComponentPresenceSign,
            bool partialEntityTargetListLockingAllowed,
            bool takeTokens,
            out List<IDisposable> lockTokens,
            out List<ECSEntity> executionEntities)
        {
            var collectedTokens = new List<IDisposable>();
            var localExecutionEntities = new List<ECSEntity>();
            bool globalViolationSeizure = false;

            // Dedup applies to both modes.
            foreach (var entityId in new HashSet<long>(contractEntities))
            {
                var entityWorld = ECSWorldOwner;
                if (entityWorld == null || entityWorld.entityManager == null)
                {
                    if (LoggingLevel == ContractLoggingLevel.Verbose)
                        NLogger.Log($"Contract {this.GetType().Name} (ID: {this.ContractId}): Entity {entityId} - world or entity manager not found");
                    continue;
                }
                var entityManager = entityWorld.entityManager;

                var entityTokens = new List<IDisposable>();
                bool keepEntity = false;

                // ── пер-сущностное ядро (единое) ──
                void CheckOne(long entid, ECSEntity contentity)
                {
                    bool violationSeizure = false;
                    bool yescomponent = false; // used only by the ST partial-match formula below
                    Dictionary<long, bool> neededComponents = null;

                    if (localEntityComponentPresenceSign.TryGetValue(entid, out neededComponents))
                    {
                        var expectedPresent = new List<Type>();
                        var expectedAbsent = new List<Type>();
                        var actualComponents = new List<Type>();
                        var missingExpected = new List<Type>();
                        var unexpectedPresent = new List<Type>();

                        if (LoggingLevel == ContractLoggingLevel.Verbose)
                            actualComponents = contentity.entityComponents.ComponentClasses.ToList();

                        foreach (var component in neededComponents)
                        {
                            var componentType = component.Key.IdToECSType();

                            if (takeTokens) // [T] транзакционные холды (present→read, absent→absence-hold с re-check'ом)
                            {
                                if (component.Value)
                                {
                                    if (contentity.entityComponents.GetReadLockedComponent(componentType, out var componentInstance, out var token))
                                    {
                                        expectedPresent.Add(componentType);
                                        entityTokens.Add(token);
                                        continue;
                                    }
                                    missingExpected.Add(componentType);
                                }
                                else
                                {
                                    if (contentity.entityComponents.HoldComponentAddition(componentType, out var token))
                                    {
                                        if (!contentity.entityComponents.HasComponent(componentType))
                                        {
                                            expectedAbsent.Add(componentType);
                                            entityTokens.Add(token);
                                            continue;
                                        }
                                        token.Dispose();
                                        unexpectedPresent.Add(componentType);
                                    }
                                }
                                violationSeizure = true;
                                globalViolationSeizure = true;
                            }
                            else // [T] ST: сверка без холдов
                            {
                                bool hasComponent = contentity.entityComponents.HasComponent(componentType);
                                if (component.Value != hasComponent)
                                {
                                    violationSeizure = true;
                                    globalViolationSeizure = true;
                                    if (component.Value) missingExpected.Add(componentType);
                                    else unexpectedPresent.Add(componentType);
                                }
                                else
                                {
                                    if (component.Value) expectedPresent.Add(componentType);
                                    else expectedAbsent.Add(componentType);
                                }
                                if (!violationSeizure)
                                    yescomponent = true; // дословно ST: взводится, лишь пока нарушений не было
                            }
                        }

                        if (violationSeizure && LoggingLevel == ContractLoggingLevel.Verbose)
                        {
                            var logMessage = new StringBuilder();
                            logMessage.AppendLine($"Contract {this.GetType().Name} (ID: {this.ContractId}): Component requirements violation for Entity {entid}:");
                            if (missingExpected.Count > 0)
                                logMessage.AppendLine($"  Missing expected components: {string.Join(", ", missingExpected.Select(t => t.Name))}");
                            if (unexpectedPresent.Count > 0)
                                logMessage.AppendLine($"  Unexpected present components: {string.Join(", ", unexpectedPresent.Select(t => t.Name))}");
                            logMessage.AppendLine($"  Expected present: {string.Join(", ", expectedPresent.Select(t => t.Name))}");
                            logMessage.AppendLine($"  Expected absent: {string.Join(", ", expectedAbsent.Select(t => t.Name))}");

                            var rawRules = new StringBuilder();
                            this.EntityComponentPresenceSign.ForEach(x => {
                                rawRules.Append($"EntityId: {x.Key} = ");
                                x.Value.ForEach(y =>
                                {
                                    rawRules.Append($"{y.Key.IdToECSType().Name} = {y.Value}; ");
                                });
                                rawRules.AppendLine();
                            });
                            logMessage.AppendLine($"  Raw contract presence rules: {rawRules.ToString()}\n==!!==!!==!!==!!==!!==!!==!!==");
                            logMessage.AppendLine($"  All entity components: {string.Join(", ", actualComponents.Select(t => t.Name))}");
                            NLogger.Log(logMessage.ToString());
                        }
                    }

                    // [D1] MT checks user conditions always (even after a presence violation —
                    // predicates run with their side effects); ST only when presence is clean.
                    if ((takeTokens || !violationSeizure) && localContractConditions.TryGetValue(entid, out var conditions))
                    {
                        for (int i = 0; i < conditions.Count; i++)
                        {
                            bool condiresult = false;
                            Exception condiex = null;
                            try
                            {
                                condiresult = conditions[i](contentity);
                            }
                            catch (Exception ex)
                            {
                                condiex = ex;
                                condiresult = false;
                            }
                            if (!condiresult)
                            {
                                violationSeizure = true;
                                globalViolationSeizure = true;
                                if (LoggingLevel == ContractLoggingLevel.Verbose)
                                    NLogger.Log($"Contract {this.GetType().Name} (ID: {this.ContractId}): Condition #{i} failed for Entity {entid} {(condiex != null ? "with exception: " + condiex.ToString() + "\n/*/*/*/*/*/*/*/*/*/*/*/*/*/\n" + new System.Diagnostics.StackTrace(condiex, true) : "")}");
                            }
                        }
                    }

                    if (!violationSeizure)
                    {
                        keepEntity = true;
                        return;
                    }

                    // [D2] Partial-match formula differs between MT and ST. neededComponents
                    // can be null when there is no presence-sign for this entity — guarded below.
                    bool partialTake = partialEntityTargetListLockingAllowed && this.Spec._partialEntityFiltering &&
                        (takeTokens
                            ? (entityTokens.Count > 1 || (NoPresenceSignAllowed && neededComponents != null && neededComponents.Count > 0))
                            : (yescomponent || (NoPresenceSignAllowed && !yescomponent)));
                    keepEntity = partialTake;
                }

                if (takeTokens)
                {
                    entityManager.Repository.ExecuteReadLockedContinuously(entityId, (entid, contentity) =>
                    {
                        CheckOne(entid, contentity);
                        if (keepEntity)
                            localExecutionEntities.Add(contentity);
                    }, out var entitytoken);

                    if (keepEntity)
                    {
                        if (entitytoken.IsReal)
                            entityTokens.Add(entitytoken);
                        collectedTokens.AddRange(entityTokens);
                    }
                    else
                    {
                        entityTokens.ForEach(x => x.Dispose());
                        if (entitytoken.IsReal)
                            entitytoken.Dispose();
                    }
                }
                else
                {
                    if (!entityManager.Repository.TryGetValue(entityId, out var entity))
                    {
                        if (LoggingLevel == ContractLoggingLevel.Verbose)
                            NLogger.Log($"Contract {this.GetType().Name} (ID: {this.ContractId}): Entity {entityId} not found in EntityStorage");
                        continue;
                    }
                    CheckOne(entityId, entity);
                    if (keepEntity)
                    {
                        localExecutionEntities.Add(entity);
                        collectedTokens.AddRange(entityTokens);
                    }
                    else
                    {
                        entityTokens.ForEach(token => token.Dispose());
                    }
                }
            }

            // Strict finalization — shared by both modes.
            if (globalViolationSeizure && !partialEntityTargetListLockingAllowed && !NotAllIncludedEntitiesPresenceSign)
            {
                collectedTokens.ForEach(token => token.Dispose());
                lockTokens = new List<IDisposable>();
                executionEntities = new List<ECSEntity>();
                return false;
            }
            if (localExecutionEntities.Count == 0)
            {
                // [D3] The verbose "no entities passed" message is only logged on the MT path.
                if (takeTokens && LoggingLevel == ContractLoggingLevel.Verbose)
                    NLogger.Log($"Contract {this.GetType().Name} (ID: {this.ContractId}): No entities passed contract requirements");
                lockTokens = new List<IDisposable>();
                executionEntities = new List<ECSEntity>();
                return false;
            }
            lockTokens = collectedTokens;
            executionEntities = localExecutionEntities;
            return true;
        }

        /// <summary>
        /// Method running every ECS tick
        /// </summary>
        /// <param name="entities"></param>
        public virtual void Run(long[] entities)
        {

        }

        /// <summary>
        /// overridable function for debug purposes in tryexecution process
        /// </summary>
        public virtual void OnTryExecute()
        {
            
        }
    }
}
