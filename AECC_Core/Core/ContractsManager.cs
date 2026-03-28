
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AECC.Core.Logging;
using AECC.Extensions;
using System.Collections.Concurrent;
using AECC.Extensions;
using AECC.Core.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Diagnostics.Contracts;
using AECC.Extensions.ThreadingSync;
using AECC.Collections;
using AECC.Core.Serialization;

namespace AECC.Core
{
    public class ECSContractsManager
    {
        public IDictionary<long, ConcurrentHashSet<ECSExecutableContractContainer>> AwaitingContractDatabase = new DictionaryWrapper<long, ConcurrentHashSet<ECSExecutableContractContainer>>();
        //public IDictionary<ECSExecutableContractContainer, ConcurrentDictionary<long, bool>> ContractExecutionArgsDatabase = new ConcurrentDictionary<ECSExecutableContractContainer, ConcurrentDictionary<long, bool>>(); // list of entity id in conditions and action

        public IDictionary<ECSExecutableContractContainer, DictionaryWrapper<long, int>> TimeDependContractEntityDatabase = new DictionaryWrapper<ECSExecutableContractContainer, DictionaryWrapper<long, int>>();//List of interested entity Instance ID
        //fill all id before running ecs

        //key - component id type
        public IDictionary<long, HashSet<ECSEntity>> ComponentOwners = new Dictionary<long, HashSet<ECSEntity>>();

        public List<ECSExecutableContractContainer> AllSystems = new List<ECSExecutableContractContainer>();

        private ECSWorld world;
        private Func<Type, bool> staticContractFiltering;
        public ECSContractsManager(ECSWorld world, Func<Type, bool> staticContractFiltering)
        {
            this.world = world;
            this.staticContractFiltering = staticContractFiltering;
            if(staticContractFiltering == null)
            {
                this.staticContractFiltering = (x) => true;
            }
        }

        public bool LockSystems = false;

        public void InitializeSystems()
        {
            EntitySerializer.TypeIdStorage.ForEach(x => ComponentOwners[x.Value] = new HashSet<ECSEntity>());
            AllSystems = ECSAssemblyExtensions.GetAllSubclassOf(typeof(ECSExecutableContractContainer)).Where(x => this.staticContractFiltering(x)).Select(x => (ECSExecutableContractContainer)Activator.CreateInstance(x)).Where(x => x.WorldFilter(this.world)).ToList();
            AllSystems = AllSystems.Except(ReturnExceptedSystems()).ToList<ECSExecutableContractContainer>();
            foreach(ECSExecutableContractContainer system in AllSystems)
            {
                system.ECSWorldOwner = this.world;
                system.Initialize();
                if (system.TimeDependExecution)
                {
                    if (system.ContractConditions != null && system.EntityComponentPresenceSign != null)
                        TimeDependContractEntityDatabase.TryAdd(system, new DictionaryWrapper<long, int>());
                    else
                        NLogger.Error($"System {system.GetType().Name} has not initialized conditions.");
                }

                foreach (var CallbackData in system.ComponentsOnChangeCallbacks)
                {
                    List<Action<ECSEntity, ECSComponent>> callBack;
                    if (ECSComponentManager.OnChangeCallbacksDB.TryGetValue(CallbackData.Key, out callBack))
                    {
                        ECSComponentManager.OnChangeCallbacksDB[CallbackData.Key] = callBack.Concat(CallbackData.Value).ToList();
                    }
                    else
                    {
                        ECSComponentManager.OnChangeCallbacksDB[CallbackData.Key] = CallbackData.Value;
                    }
                }
                
            }
        }

        public void RunTimeDependContracts()
        {
            if (LockSystems)
                return;
            foreach(var SystemPair in TimeDependContractEntityDatabase)
            {
                if (SystemPair.Key.TimeDependActive && !SystemPair.Key.InWork && !SystemPair.Key.InProgress && SystemPair.Key.LastEndExecutionTimestamp + DateTimeExtensions.MillisecondToTicks
                    (SystemPair.Key.DelayRunMilliseconds) < DateTime.Now.Ticks)
                {
                    SystemPair.Key.InProgress = true;
                    if(SystemPair.Key.AsyncExecution)
                    {
                        TaskEx.RunAsync(() =>
                        {
                            TryExecuteContracts(new List<ECSExecutableContractContainer> { SystemPair.Key }, TimeDependContractEntityDatabase[SystemPair.Key].Keys.ToList());
                            SystemPair.Key.InProgress = false;
                        });
                    }
                    else
                    {
                        TryExecuteContracts(new List<ECSExecutableContractContainer> { SystemPair.Key }, TimeDependContractEntityDatabase[SystemPair.Key].Keys.ToList());
                        SystemPair.Key.InProgress = false;
                    }
                }
            }
        }

        private void TryExecuteContracts(IEnumerable<ECSExecutableContractContainer> contracts, List<long> argEntities = null)
        {
            foreach (var contract in contracts)
            {
                if(contract.TimeDependExecution && argEntities != null)
                {
                    if(argEntities.Count > 0 && contract.TryExecuteContract(true, argEntities))
                    {
                        if(contract.RemoveAfterExecution)
                            RemoveContract(contract);
                    }
                }
                else
                {
                    if(contract.NowTried >= contract.MaxTries && !contract.ContractExecuted && !contract.InWork)
                    {
                        RemoveContract(contract);
                        NLogger.Log($"Contract failed to execute after {contract.MaxTries} tries.\n======================\n{contract.GenerationStackTrace}\n======================");
                    }
                    if(contract.TryExecuteContract())
                    {
                        if(contract.RemoveAfterExecution)
                            RemoveContract(contract);
                    }
                }
            }
        }

        private bool RemoveContract(ECSExecutableContractContainer contract)
        {
            bool result = false;
            foreach (var entity in contract.NeededEntities)
            {
                if (contract.RemoveAfterExecution && AwaitingContractDatabase.TryGetValue(entity, out var contracts))
                {
                    result = contracts.Remove(contract);
                }
                if (contract.RemoveAfterExecution && TimeDependContractEntityDatabase.TryGetValue(contract, out var entities))
                {
                    result = TimeDependContractEntityDatabase.Remove(contract);
                }
            }
            return result;
        }

        public void RegisterContract(ECSExecutableContractContainer contract, bool autoExecute = true)
        {
            if(contract.EntityComponentPresenceSign == null || contract.ContractConditions == null || (contract.ContractConditions.Count == 0 && contract.EntityComponentPresenceSign.Count == 0))
            {
                NLogger.Log("Contract aborted. No conditions");
                return;
            }
            contract.GenerationStackTrace = new System.Diagnostics.StackTrace();
            foreach(var entityid in contract.NeededEntities)
            {
                if(world.entityManager.ContainsEntitySyncronized(entityid))
                {
                    ConcurrentHashSet<ECSExecutableContractContainer> listContracts = null;
                    if(!AwaitingContractDatabase.TryGetValue(entityid, out listContracts))
                    {
                        listContracts = new ConcurrentHashSet<ECSExecutableContractContainer>();
                        AwaitingContractDatabase[entityid] = listContracts;
                    }
                    listContracts.Add(contract);
                }
            }
            if(autoExecute)
                TryExecuteContracts(new List<ECSExecutableContractContainer>{ contract });
        }

        public void OnEntityComponentAddedReaction(ECSEntity entity, ECSComponent component)
        {
            ComponentOwners.TryGetValue(component.GetId(), out var hentities);
            racecheckagain:
            bool added = false;
            if(entity.HasComponent(component.GetId()) || entity.entityComponents.HasComponentAsync(component.GetId()).Result)
            {
                hentities.AddI(entity, entity);
                added = true;
            }
            if(added && !entity.HasComponent(component.GetId()) && !entity.entityComponents.HasComponentAsync(component.GetId()).Result)
            {
                hentities.RemoveI(entity, entity);
                goto racecheckagain;
            }

            foreach (KeyValuePair<ECSExecutableContractContainer, DictionaryWrapper<long, int>> pair in this.TimeDependContractEntityDatabase)
            {
                if (pair.Key.TryExecuteContract(false, new List<long> { entity.instanceId }))
                {
                    DictionaryWrapper<long, int> bufDict;
                    if (TimeDependContractEntityDatabase.TryGetValue(pair.Key, out bufDict))
                        bufDict.TryAdd(entity.instanceId, 0);
                }
                else
                {
                    DictionaryWrapper<long, int> bufDict;
                    if (TimeDependContractEntityDatabase.TryGetValue(pair.Key, out bufDict))
                        bufDict.Remove(entity.instanceId, out _);
                }
            }
            if(this.AwaitingContractDatabase.TryGetValue(entity.instanceId, out var contracts))
            {
                TryExecuteContracts(contracts);
            }
        }

        public void OnEntityComponentChangedReaction(ECSEntity entity, ECSComponent component)
        {
            
        }

        public HashSet<ECSEntity> FilterEntitiesForComponents(List<long> components)
        {
            if (components == null || components.Count == 0)
                return new HashSet<ECSEntity>();

            var setsToIntersect = new List<HashSet<ECSEntity>>();

            foreach (var comp in components)
            {
                if (!ComponentOwners.TryGetValue(comp, out var owners) || owners.Count == 0)
                {
                    return new HashSet<ECSEntity>(); 
                }
                setsToIntersect.Add(owners);
            }

            setsToIntersect.Sort((a, b) => a.Count.CompareTo(b.Count));

            var result = setsToIntersect[0].SnapshotI(setsToIntersect[0]);

            for (int i = 1; i < setsToIntersect.Count; i++)
            {
                result.IntersectWith(setsToIntersect[i]);
                
                if (result.Count == 0) 
                    break; 
            }

            return result;
        }

        public void OnEntityComponentRemovedReaction(ECSEntity entity, ECSComponent component)
        {
            ComponentOwners.TryGetValue(component.GetId(), out var hentities);
            racecheckagain:
            bool added = false;
            if(!entity.HasComponent(component.GetId()) && !entity.entityComponents.HasComponentAsync(component.GetId()).Result)
            {
                hentities.RemoveI(entity, entity);
                added = true;
            }
            if(added && entity.HasComponent(component.GetId()) || entity.entityComponents.HasComponentAsync(component.GetId()).Result)
            {
                hentities.AddI(entity, entity);
                goto racecheckagain;
            }
            
            foreach (KeyValuePair<ECSExecutableContractContainer, DictionaryWrapper<long, int>> pair in this.TimeDependContractEntityDatabase)
            {
                if (pair.Key.TryExecuteContract(false, new List<long> { entity.instanceId }))
                {
                    DictionaryWrapper<long, int> bufDict;
                    if (TimeDependContractEntityDatabase.TryGetValue(pair.Key, out bufDict))
                        bufDict.TryAdd(entity.instanceId, 0);
                }
                else
                {
                    DictionaryWrapper<long, int> bufDict;
                    if (TimeDependContractEntityDatabase.TryGetValue(pair.Key, out bufDict))
                        bufDict.Remove(entity.instanceId, out _);
                }
            }
            if(this.AwaitingContractDatabase.TryGetValue(entity.instanceId, out var contracts))
            {
                TryExecuteContracts(contracts);
            }
        }

        public void OnEntityDestroyed(ECSEntity entity)
        {
            bool cleared = false;
            foreach (KeyValuePair<ECSExecutableContractContainer, DictionaryWrapper<long, int>> pair in this.TimeDependContractEntityDatabase)
            {
                int nulled = 0;
                DictionaryWrapper<long, int> bufDict;
                if(TimeDependContractEntityDatabase.TryGetValue(pair.Key, out bufDict))
                    if(pair.Value.Remove(entity.instanceId, out nulled))
                    {
                        cleared = true;
                    }
            }
            if(this.AwaitingContractDatabase.TryGetValue(entity.instanceId, out var contracts))
            {
                contracts.ForEach(x => RemoveContract(x));
            }
            if(!cleared)
            {
                NLogger.LogError("core system error");
            }
        }

        public void OnEntityCreated(ECSEntity entity)
        {
            foreach (KeyValuePair<ECSExecutableContractContainer, DictionaryWrapper<long, int>> pair in this.TimeDependContractEntityDatabase)
            {
                if (pair.Key.TryExecuteContract(false, new List<long> { entity.instanceId }))
                {
                    DictionaryWrapper<long, int> bufDict;
                    if (TimeDependContractEntityDatabase.TryGetValue(pair.Key, out bufDict))
                        bufDict.TryAdd(entity.instanceId, 0);
                }
                else
                {
                    DictionaryWrapper<long, int> bufDict;
                    if (TimeDependContractEntityDatabase.TryGetValue(pair.Key, out bufDict))
                        bufDict.Remove(entity.instanceId, out _);
                }
            }
            if(this.AwaitingContractDatabase.TryGetValue(entity.instanceId, out var contracts))
            {
                TryExecuteContracts(contracts);
            }
        }

        public List<ECSExecutableContractContainer> ReturnExceptedSystems()
        {
            List<ECSExecutableContractContainer> list = new List<ECSExecutableContractContainer> {

            };
            return list;
        }

        public void AppendSystemInRuntime(ECSExecutableContractContainer system)
        {
            TimeDependContractEntityDatabase.TryAdd(system, new DictionaryWrapper<long, int>());
        }

        public void UpdateSystemListOfInterestECSComponents(ECSExecutableContractContainer system, List<long> updatedIds)
        {

        }
    }
}
