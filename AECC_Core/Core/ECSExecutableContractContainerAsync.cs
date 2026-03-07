using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AECC.Extensions;
using AECC.Core.Logging;
using System.Diagnostics;
using AECC.Extensions.ThreadingSync;
using AECC.Collections;

namespace AECC.Core
{
    public class ECSExecutableContractContainerAsync
    {
        public string ContractId { get; set; }
        
        // Семафор для замены lock (ContractLocker)
        protected readonly SemaphoreSlim ContractSemaphore = new SemaphoreSlim(1, 1);

        [System.NonSerialized]
        protected Type _systemType = null;
        public Type SystemType
        {
            get { ContractSemaphore.Wait(); try { return _systemType; } finally { ContractSemaphore.Release(); } }
            set { ContractSemaphore.Wait(); try { _systemType = value; } finally { ContractSemaphore.Release(); } }
        }

        protected StackTrace genTrace = null;
        public StackTrace GenerationStackTrace
        {
            get { ContractSemaphore.Wait(); try { return genTrace; } finally { ContractSemaphore.Release(); } }
            set { ContractSemaphore.Wait(); try { genTrace = value; } finally { ContractSemaphore.Release(); } }
        }

        protected ContractLoggingLevel _loggingLevel = ContractLoggingLevel.Verbose;
        public ContractLoggingLevel LoggingLevel
        {
            get { ContractSemaphore.Wait(); try { return _loggingLevel; } finally { ContractSemaphore.Release(); } }
            set { ContractSemaphore.Wait(); try { _loggingLevel = value; } finally { ContractSemaphore.Release(); } }
        }

        protected Dictionary<long, List<Func<ECSEntity, Task<bool>>>> _contractConditions = null;
        public Dictionary<long, List<Func<ECSEntity, Task<bool>>>> ContractConditions
        {
            get { ContractSemaphore.Wait(); try { return _contractConditions; } finally { ContractSemaphore.Release(); } }
            set { ContractSemaphore.Wait(); try { _contractConditions = value; } finally { ContractSemaphore.Release(); } }
        }

        protected Dictionary<long, Dictionary<long, bool>> _entityComponentPresenceSign = null;
        public Dictionary<long, Dictionary<long, bool>> EntityComponentPresenceSign
        {
            get { ContractSemaphore.Wait(); try { return _entityComponentPresenceSign; } finally { ContractSemaphore.Release(); } }
            set { ContractSemaphore.Wait(); try { _entityComponentPresenceSign = value; } finally { ContractSemaphore.Release(); } }
        }

        protected Func<ECSExecutableContractContainerAsync, ECSEntity[], Task> _contractExecutable =
            async (contract, entities) =>
            {
                foreach (var entity in entities)
                {
                    await contract.ContractExecutableSingle(contract, entity);
                }
            };
        public Func<ECSExecutableContractContainerAsync, ECSEntity[], Task> ContractExecutable
        {
            get { ContractSemaphore.Wait(); try { return _contractExecutable; } finally { ContractSemaphore.Release(); } }
            set { ContractSemaphore.Wait(); try { _contractExecutable = value; } finally { ContractSemaphore.Release(); } }
        }

        protected Func<ECSExecutableContractContainerAsync, ECSEntity, Task> _contractExecutableSingle =
            (contract, entity) => Task.CompletedTask;
        public Func<ECSExecutableContractContainerAsync, ECSEntity, Task> ContractExecutableSingle
        {
            get { ContractSemaphore.Wait(); try { return _contractExecutableSingle; } finally { ContractSemaphore.Release(); } }
            set { ContractSemaphore.Wait(); try { _contractExecutableSingle = value; } finally { ContractSemaphore.Release(); } }
        }

        protected Func<ECSExecutableContractContainerAsync, long[], Task> _errorExecution =
            (contract, entities) => Task.CompletedTask;
        public Func<ECSExecutableContractContainerAsync, long[], Task> ErrorExecution
        {
            get { ContractSemaphore.Wait(); try { return _errorExecution; } finally { ContractSemaphore.Release(); } }
            set { ContractSemaphore.Wait(); try { _errorExecution = value; } finally { ContractSemaphore.Release(); } }
        }

        protected Func<ECSWorld, Task<bool>> _worldFilter =
            (world) => Task.FromResult(true);
        public Func<ECSWorld, Task<bool>> WorldFilter
        {
            get { ContractSemaphore.Wait(); try { return _worldFilter; } finally { ContractSemaphore.Release(); } }
            set { ContractSemaphore.Wait(); try { _worldFilter = value; } finally { ContractSemaphore.Release(); } }
        }

        protected bool _timeDependExecution = false;
        public bool TimeDependExecution
        {
            get { ContractSemaphore.Wait(); try { return _timeDependExecution; } finally { ContractSemaphore.Release(); } }
            set { ContractSemaphore.Wait(); try { _timeDependExecution = value; } finally { ContractSemaphore.Release(); } }
        }

        protected bool _noPresenceSignAllowed = true;
        public bool NoPresenceSignAllowed
        {
            get { ContractSemaphore.Wait(); try { return _noPresenceSignAllowed; } finally { ContractSemaphore.Release(); } }
            set { ContractSemaphore.Wait(); try { _noPresenceSignAllowed = value; } finally { ContractSemaphore.Release(); } }
        }

        protected ECSWorld _ecsWorldOwner = null;
        public ECSWorld ECSWorldOwner
        {
            get { ContractSemaphore.Wait(); try { return _ecsWorldOwner; } finally { ContractSemaphore.Release(); } }
            set { ContractSemaphore.Wait(); try { _ecsWorldOwner = value; } finally { ContractSemaphore.Release(); } }
        }

        protected bool _removeAfterExecution = true;
        public bool RemoveAfterExecution
        {
            get { ContractSemaphore.Wait(); try { return _removeAfterExecution; } finally { ContractSemaphore.Release(); } }
            set { ContractSemaphore.Wait(); try { _removeAfterExecution = value; } finally { ContractSemaphore.Release(); } }
        }

        protected bool _bypassFinalization = false;
        public bool BypassFinalization
        {
            get { ContractSemaphore.Wait(); try { return _bypassFinalization; } finally { ContractSemaphore.Release(); } }
            set { ContractSemaphore.Wait(); try { _bypassFinalization = value; } finally { ContractSemaphore.Release(); } }
        }

        protected long _maxTries = long.MaxValue;
        public long MaxTries
        {
            get { ContractSemaphore.Wait(); try { return _maxTries; } finally { ContractSemaphore.Release(); } }
            set { ContractSemaphore.Wait(); try { _maxTries = value; } finally { ContractSemaphore.Release(); } }
        }

        protected long _nowTried = 0;
        public long NowTried
        {
            get { ContractSemaphore.Wait(); try { return _nowTried; } finally { ContractSemaphore.Release(); } }
            set { ContractSemaphore.Wait(); try { _nowTried = value; } finally { ContractSemaphore.Release(); } }
        }

        protected bool _timeDependActive = true;
        public bool TimeDependActive
        {
            get { ContractSemaphore.Wait(); try { return _timeDependActive; } finally { ContractSemaphore.Release(); } }
            set { ContractSemaphore.Wait(); try { _timeDependActive = value; } finally { ContractSemaphore.Release(); } }
        }

        protected bool _partialEntityFiltering = false;
        public bool PartialEntityFiltering
        {
            get { ContractSemaphore.Wait(); try { return _partialEntityFiltering; } finally { ContractSemaphore.Release(); } }
            set { ContractSemaphore.Wait(); try { _partialEntityFiltering = value; } finally { ContractSemaphore.Release(); } }
        }

        protected bool _notAllIncludedEntitiesPresenceSign = false;
        public bool NotAllIncludedEntitiesPresenceSign
        {
            get { ContractSemaphore.Wait(); try { return _notAllIncludedEntitiesPresenceSign; } finally { ContractSemaphore.Release(); } }
            set { ContractSemaphore.Wait(); try { _notAllIncludedEntitiesPresenceSign = value; } finally { ContractSemaphore.Release(); } }
        }

        protected bool _asyncExecution = true;
        public bool AsyncExecution
        {
            get { ContractSemaphore.Wait(); try { return _asyncExecution; } finally { ContractSemaphore.Release(); } }
            set { ContractSemaphore.Wait(); try { _asyncExecution = value; } finally { ContractSemaphore.Release(); } }
        }

        protected bool _inWork = false;
        public bool InWork
        {
            get { ContractSemaphore.Wait(); try { return _inWork; } finally { ContractSemaphore.Release(); } }
            set { ContractSemaphore.Wait(); try { _inWork = value; } finally { ContractSemaphore.Release(); } }
        }

        protected bool _inProgress = false;
        public bool InProgress
        {
            get { ContractSemaphore.Wait(); try { return _inProgress; } finally { ContractSemaphore.Release(); } }
            set { ContractSemaphore.Wait(); try { _inProgress = value; } finally { ContractSemaphore.Release(); } }
        }

        protected long _lastEndExecutionTimestamp = 0;
        public long LastEndExecutionTimestamp
        {
            get { ContractSemaphore.Wait(); try { return _lastEndExecutionTimestamp; } finally { ContractSemaphore.Release(); } }
            set { ContractSemaphore.Wait(); try { _lastEndExecutionTimestamp = value; } finally { ContractSemaphore.Release(); } }
        }

        protected long _delayRunMilliseconds = 0;
        public long DelayRunMilliseconds
        {
            get { ContractSemaphore.Wait(); try { return _delayRunMilliseconds; } finally { ContractSemaphore.Release(); } }
            set { ContractSemaphore.Wait(); try { _delayRunMilliseconds = value; } finally { ContractSemaphore.Release(); } }
        }

        protected bool _contractExecuted = false;
        public bool ContractExecuted
        {
            get { ContractSemaphore.Wait(); try { return _contractExecuted; } finally { ContractSemaphore.Release(); } }
            set { ContractSemaphore.Wait(); try { _contractExecuted = value; } finally { ContractSemaphore.Release(); } }
        }

        protected bool _manualExitFromWorkingState = false;
        public bool ManualExitFromWorkingState
        {
            get { ContractSemaphore.Wait(); try { return _manualExitFromWorkingState; } finally { ContractSemaphore.Release(); } }
            set { ContractSemaphore.Wait(); try { _manualExitFromWorkingState = value; } finally { ContractSemaphore.Release(); } }
        }

        public List<long> NeededEntities
        {
            get
            {
                ContractSemaphore.Wait();
                try
                {
                    if (_contractConditions != null && _entityComponentPresenceSign != null)
                    {
                        var allentities = _contractConditions.Keys.ToList();
                        allentities.AddRange(_entityComponentPresenceSign.Keys);
                        return new HashSet<long>(allentities).ToList();
                    }
                    return null;
                }
                finally
                {
                    ContractSemaphore.Release();
                }
            }
        }

        public IDictionary<long, List<Func<ECSEntity, ECSComponent, Task>>> ComponentsOnChangeCallbacks = new DictionaryWrapper<long, List<Func<ECSEntity, ECSComponent, Task>>>();

        public ECSExecutableContractContainerAsync()
        {
            GenerationStackTrace = new System.Diagnostics.StackTrace();
            if (this.GetType() == typeof(ECSExecutableContractContainerAsync))
            {
                this._maxTries = 1;
            }
            else
            {
                this._removeAfterExecution = false;
            }
        }

        public virtual Task InitializeAsync()
        {
            return Task.CompletedTask;
        }

        public async Task<bool> TryExecuteContractAsync(bool ExecuteContract = true, List<long> contractEntities = null)
        {
            await ContractSemaphore.WaitAsync();
            try
            {
                _nowTried++;
                _bypassFinalization = false;
                if (!_contractExecuted)
                {
                    await OnTryExecuteAsync();
                    if (contractEntities == null)
                    {
                        var allentities = _contractConditions?.Keys.ToList() ?? new List<long>();
                        if (_entityComponentPresenceSign != null)
                        {
                            allentities.AddRange(_entityComponentPresenceSign.Keys);
                        }

                        bool contractResult = false;
                        List<IDisposable> lockers = null;
                        List<ECSEntity> executionEntities = null;

                        var tupleResult = await GetContractLockersAsync(allentities, _contractConditions, _entityComponentPresenceSign, false);
                        contractResult = tupleResult.Success;
                        lockers = tupleResult.LockTokens;
                        executionEntities = tupleResult.ExecutionEntities;

                        if (contractResult && lockers != null)
                        {
                            var errorState = false;
                            if (ExecuteContract)
                            {
                                _inWork = true;
                                try
                                {
                                    await _contractExecutable(this, executionEntities.ToArray());
                                }
                                catch (Exception ex)
                                {
                                    NLogger.LogError(ex);
                                    await _errorExecution(this, executionEntities.Select(x => x.instanceId).ToArray());
                                    errorState = true;
                                }
                                lockers.ForEach(x => x?.Dispose());
                                if (!_manualExitFromWorkingState)
                                {
                                    _lastEndExecutionTimestamp = DateTime.Now.Ticks;
                                    _inWork = false;
                                }
                            }
                            else
                            {
                                lockers.ForEach(x => x?.Dispose());
                            }

                            if (ExecuteContract && !errorState)
                            {
                                if (!_bypassFinalization)
                                {
                                    _contractExecuted = true;
                                }
                                return true;
                            }
                        }
                        else
                        {
                            if (ExecuteContract)
                                await _errorExecution(this, allentities.ToArray());
                        }
                    }
                    else
                    {
                        var filledContractConditions = new Dictionary<long, List<Func<ECSEntity, Task<bool>>>>();
                        var filledEntityComponentPresenceSign = new Dictionary<long, Dictionary<long, bool>>();
                        
                        if (_contractConditions != null)
                        {
                            foreach (var contractCond in _contractConditions)
                            {
                                foreach (var contractEntity in contractEntities)
                                {
                                    filledContractConditions[contractEntity] = contractCond.Value;
                                }
                            }
                        }
                        
                        if (_entityComponentPresenceSign != null)
                        {
                            foreach (var presenceSign in _entityComponentPresenceSign)
                            {
                                foreach (var contractEntity in contractEntities)
                                {
                                    filledEntityComponentPresenceSign[contractEntity] = presenceSign.Value;
                                }
                            }
                        }

                        bool contractResult = false;
                        List<IDisposable> lockers = null;
                        List<ECSEntity> executionEntities = null;
                        
                        var tupleResult = await GetContractLockersAsync(contractEntities, filledContractConditions, filledEntityComponentPresenceSign, true);
                        contractResult = tupleResult.Success;
                        lockers = tupleResult.LockTokens;
                        executionEntities = tupleResult.ExecutionEntities;

                        if (contractResult && lockers != null)
                        {
                            var errorState = false;
                            if (ExecuteContract)
                            {
                                _inWork = true;
                                try
                                {
                                    await _contractExecutable(this, executionEntities.ToArray());
                                }
                                catch (Exception ex)
                                {
                                    NLogger.LogError(ex);
                                    await _errorExecution(this, executionEntities.Select(x => x.instanceId).ToArray());
                                    errorState = true;
                                }
                                lockers.ForEach(x => x?.Dispose());
                                if (!_manualExitFromWorkingState)
                                {
                                    _lastEndExecutionTimestamp = DateTime.Now.Ticks;
                                    _inWork = false;
                                }
                            }
                            else
                            {
                                lockers.ForEach(x => x?.Dispose());
                            }
                            
                            if (!errorState)
                                return true;
                        }
                        else
                        {
                            if (ExecuteContract)
                                await _errorExecution(this, contractEntities.ToArray());
                        }
                    }
                    return false;
                }
                else
                {
                    NLogger.Log("You tried to execute contract that was already executed\n" + this.genTrace?.ToString() + "\n================================");
                    return false;
                }
            }
            finally
            {
                ContractSemaphore.Release();
            }
        }

        private async Task<(bool Success, List<IDisposable> LockTokens, List<ECSEntity> ExecutionEntities)> GetContractLockersAsync(
            List<long> contractEntities, 
            IDictionary<long, List<Func<ECSEntity, Task<bool>>>> LocalContractConditions, 
            IDictionary<long, Dictionary<long, bool>> LocalEntityComponentPresenceSign, 
            bool partialEntityTargetListLockingAllowed)
        {
            Dictionary<long, List<IDisposable>> Lockers = new Dictionary<long, List<IDisposable>>();
            var lockTokens = new List<IDisposable>();
            var localExecutionEntities = new List<ECSEntity>();
            bool globalViolationSeizure = false;

            foreach (var entityid in new HashSet<long>(contractEntities))
            {
                var entityWorld = _ecsWorldOwner;
                if (entityWorld == null || entityWorld.entityManager == null)
                {
                    if (_loggingLevel == ContractLoggingLevel.Verbose)
                    {
                        NLogger.Log($"Contract {this.GetType().Name} (ID: {this.ContractId}): Entity {entityid} - world or entity manager not found");
                    }
                    continue;
                }

                // В асинхронном мире мы используем TryGetLockedElementAsync для блокировки сущности
                var entityLookup = await entityWorld.entityManager.EntityStorageAsync.TryGetLockedElementAsync(entityid, false);
                if (!entityLookup.Success) continue;
                
                var contentity = entityLookup.Value as ECSEntity;
                var entitytoken = entityLookup.LockToken;

                bool violationSeizure = false;
                var currentTokens = new List<IDisposable>();

                Dictionary<long, bool> neededComponents = null;

                if (LocalEntityComponentPresenceSign != null && LocalEntityComponentPresenceSign.TryGetValue(entityid, out neededComponents))
                {
                    var expectedPresent = new List<Type>();
                    var expectedAbsent = new List<Type>();
                    var actualComponents = new List<Type>();
                    var missingExpected = new List<Type>();
                    var unexpectedPresent = new List<Type>();

                    if (_loggingLevel == ContractLoggingLevel.Verbose)
                    {
                        var compClasses = await contentity.entityComponents.GetComponentClassesAsync();
                        actualComponents = compClasses.ToList();
                    }

                    foreach (var component in neededComponents)
                    {
                        var componentType = component.Key.IdToECSType();

                        if (component.Value)
                        {
                            var compLockResult = await contentity.entityComponents.GetReadLockedComponentAsync(componentType);
                            if (compLockResult.Success)
                            {
                                expectedPresent.Add(componentType);
                                currentTokens.Add(compLockResult.Token);
                                continue;
                            }
                            else
                            {
                                missingExpected.Add(componentType);
                                violationSeizure = true;
                                globalViolationSeizure = true;
                            }
                        }
                        else
                        {
                            var holdLockResult = await contentity.entityComponents.HoldComponentAdditionAsync(componentType);
                            if (holdLockResult.Success)
                            {
                                if (!await contentity.entityComponents.HasComponentAsync(componentType))
                                {
                                    expectedAbsent.Add(componentType);
                                    currentTokens.Add(holdLockResult.LockToken);
                                    continue;
                                }
                                else
                                {
                                    holdLockResult.LockToken?.Dispose();
                                    unexpectedPresent.Add(componentType);
                                    violationSeizure = true;
                                    globalViolationSeizure = true;
                                }
                            }
                        }
                        violationSeizure = true;
                        globalViolationSeizure = true;
                    }

                    if (violationSeizure && _loggingLevel == ContractLoggingLevel.Verbose)
                    {
                        var logMessage = new StringBuilder();
                        logMessage.AppendLine($"Contract {this.GetType().Name} (ID: {this.ContractId}): Component requirements violation for Entity {entityid}:");

                        if (missingExpected.Count > 0)
                            logMessage.AppendLine($"  Missing expected components: {string.Join(", ", missingExpected.Select(t => t.Name))}");

                        if (unexpectedPresent.Count > 0)
                            logMessage.AppendLine($"  Unexpected present components: {string.Join(", ", unexpectedPresent.Select(t => t.Name))}");

                        logMessage.AppendLine($"  Expected present: {string.Join(", ", expectedPresent.Select(t => t.Name))}");
                        logMessage.AppendLine($"  Expected absent: {string.Join(", ", expectedAbsent.Select(t => t.Name))}");

                        var rawRules = new StringBuilder();
                        _entityComponentPresenceSign?.ForEach(x => {
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

                if (LocalContractConditions != null && LocalContractConditions.TryGetValue(entityid, out var conditions))
                {
                    for (int i = 0; i < conditions.Count; i++)
                    {
                        bool condiresult = false;
                        Exception condiex = null;
                        try
                        {
                            condiresult = await conditions[i](contentity);
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

                            if (_loggingLevel == ContractLoggingLevel.Verbose)
                            {
                                NLogger.Log($"Contract {this.GetType().Name} (ID: {this.ContractId}): Condition #{i} failed for Entity {entityid} {(condiex != null ? "with exception: " + condiex.ToString() + "\n/*/*/*/*/*/*/*/*/*/*/*/*/*/\n" + new System.Diagnostics.StackTrace(condiex, true) : "")}");
                            }
                        }
                    }
                }

                if (violationSeizure)
                {
                    if (partialEntityTargetListLockingAllowed && this._partialEntityFiltering && (currentTokens.Count > 0 || (_noPresenceSignAllowed && (neededComponents?.Count ?? 0) > 0)))
                    {
                        localExecutionEntities.Add(contentity);
                        if (entitytoken != null) currentTokens.Add(entitytoken);
                        Lockers.Add(entityid, currentTokens);
                    }
                    else
                    {
                        currentTokens.ForEach(x => x?.Dispose());
                        entitytoken?.Dispose();
                    }
                }
                else
                {
                    localExecutionEntities.Add(contentity);
                    if (entitytoken != null) currentTokens.Add(entitytoken);
                    Lockers.Add(entityid, currentTokens);
                }
            }

            if (globalViolationSeizure && !partialEntityTargetListLockingAllowed && !_notAllIncludedEntitiesPresenceSign)
            {
                Lockers.ForEach(x => x.Value.ForEach(y => y?.Dispose()));
                return (!globalViolationSeizure, null, null);
            }
            if (localExecutionEntities.Count == 0)
            {
                if (_loggingLevel == ContractLoggingLevel.Verbose)
                {
                    NLogger.Log($"Contract {this.GetType().Name} (ID: {this.ContractId}): No entities passed contract requirements");
                }
                return (false, null, null);
            }
            
            lockTokens = Lockers.Values.SelectMany(x => x).ToList();
            return (true, lockTokens, localExecutionEntities);
        }

        public virtual Task RunAsync(long[] entities)
        {
            return Task.CompletedTask;
        }

        public virtual Task OnTryExecuteAsync()
        {
            return Task.CompletedTask;
        }
    }
}