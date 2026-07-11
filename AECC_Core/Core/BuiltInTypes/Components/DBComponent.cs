using AECC.Core.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using AECC.Extensions;
using System.IO;
using AECC.Extensions.ThreadingSync;
using System.Diagnostics;
using System.Runtime.Serialization;
using static AECC.Core.BuiltInTypes.Components.ComponentsDBComponent;
using AECC.Core.BuiltInTypes.Types.AtomicType;
using AECC.Collections;

namespace AECC.Core.BuiltInTypes.Components
{
    [System.Serializable]
    [TypeUid(11)]
    public class DBComponent : ECSComponent, AECC.Abstractions.ISerializationParticipant
    {
        static public new long Id { get; set; } = 11;

        [NonSerialized]
        [IgnoreDataMember]
        public DBLoggingLevel LoggingLevel = DBLoggingLevel.None;

        public Dictionary<IECSObjectPathContainer, List<dbRow>> serializedDB = new Dictionary<IECSObjectPathContainer, List<dbRow>>();

        // Мост участника сериализации: сериализатор/хранилище зовут интерфейс,
        // не зная конкретного DBComponent.
        void AECC.Abstractions.ISerializationParticipant.BeforeSnapshot(bool serializeOnlyChanged, bool clearChanged)
        {
            SerializeDB(serializeOnlyChanged, clearChanged);
        }

        void AECC.Abstractions.ISerializationParticipant.AfterSnapshot(bool clearChanged)
        {
            AfterSerializationDB(clearChanged);
        }

        void AECC.Abstractions.ISerializationParticipant.AfterRestore(bool clientRetry)
        {
            UnserializeDB(clientRetry);
        }

        public virtual void SerializeDB(bool serializeOnlyChanged = false, bool clearChanged = true)
        {
        }
        
        public virtual void AfterSerializationDB(bool clearAfterSerializaion = true)
        {

        }
        
        public virtual void UnserializeDB(bool retryNullEntityOwner = false)
        {

        }

        public virtual void AfterDeserializeDB()
        { }
    }

    [System.Serializable]
    public class dbRow
    {
        public long componentInstanceId;
        public long componentId;
        public object component;
        public ComponentState componentState;
    }

    public enum DBLoggingLevel
    {
        None = 0,           // No logging
        CountOnly = 1,      // Only element counts
        CountAndTypes = 2,  // Counts and component types
        Full = 3           // Counts, types, and operation results
    }

    [System.Serializable]
    [TypeUid(12)]
    public class ComponentsDBComponent : DBComponent
    {
        static public new long Id { get; set; } = 12;

        /// <summary>
        /// Глубина выполнения UnserializeDB (re-entrancy-safe): пока > 0, отсутствие
        /// компонента в БД во время клиентской десериализации — ожидаемо.
        /// </summary>
        [System.NonSerialized]
        private int _unserializeDepth = 0;

        /// <summary>
        /// Единый авторитет синхронизации DB — StabilizationGate сущности-владельца.
        /// До привязки к сущности (фабричный контекст) ownerEntity == null и доступ
        /// гарантированно однопоточный, поэтому реальный захват не нужен (no-op scope).
        /// </summary>
        private IDisposable DbReadScope()
        {
            var ec = this.ownerEntity?.entityComponents;
            return ec != null ? (IDisposable)ec.StabilizationGate.ReadLock() : null;
        }
        private IDisposable DbWriteScope()
        {
            var ec = this.ownerEntity?.entityComponents;
            return ec != null ? (IDisposable)ec.StabilizationGate.WriteLock() : null;
        }

        /// <summary>
        /// Debug-only инвариант: мутация DB должна выполняться под write-гейтом StabilizationGate.
        /// До привязки к сущности (ownerEntity == null) и в OneThreadMode (Mock-лок) проверка не
        /// применяется — там доступ однопоточный по контракту. В Release вырезается компилятором.
        /// </summary>
        [System.Diagnostics.Conditional("DEBUG")]
        private void AssertDbWriteGate()
        {
            if (ownerEntity == null || Defines.OneThreadMode)
                return;
            var rw = ownerEntity.entityComponents?.StabilizationGate;
            if (rw?.lockobj == null)
                return;
            System.Diagnostics.Debug.Assert(rw.lockobj.IsWriteLockHeld,
                "ComponentsDBComponent: мутация DB вне write-гейта StabilizationGate");
        }

        public enum ComponentState
        {
            Created,
            Changed,
            Removed,
            Null
        }

        // DBComponent разложен на DbStore + DbSerialization по осям изменения:
        // DbStore — живая сторона (строки-состояния, владельцы, dirty), DbSerialization —
        // restore-сторона (пути владельцев, NonEO-парковка retry, счётчик проверок).
        // serializedDB остаётся ФИЗИЧЕСКИМ сериализуемым полем компонента (wire-данные).
        // Публичный API — делегирующие свойства с прежними именами.

        public sealed class DbStore
        {
            public Dictionary<long, Dictionary<long, (ECSComponent, ComponentState)>> Rows = new Dictionary<long, Dictionary<long, (ECSComponent, ComponentState)>>();
            public Dictionary<long, long> ComponentOwners = new Dictionary<long, long>();
            public Dictionary<long, int> ChangedComponents = new Dictionary<long, int>();
        }

        public sealed class DbSerialization
        {
            public Dictionary<long, IECSObjectPathContainer> OwnerPaths = new Dictionary<long, IECSObjectPathContainer>();
            public DictionaryWrapper<IECSObjectPathContainer, (List<dbRow>, int)> NonEO = new DictionaryWrapper<IECSObjectPathContainer, (List<dbRow>, int)>();
            internal int UnserializeCheckCount = 0;

            public void SerializeDB(ComponentsDBComponent owner, bool serializeOnlyChanged, bool clearChanged)
        {
            Dictionary<IECSObjectPathContainer, List<dbRow>> newSerializedDB = new Dictionary<IECSObjectPathContainer, List<dbRow>>();
            // Стабильность owner.DB на время сериализации обеспечивается внешним ReadLock
            // (EntityNetSerializer.SlicedSerialize) + однопоточным контрактом owner.DB-компонента.
            {
                owner.serializedDB.Clear();
                List<long> errorChanged = new List<long>();
                
                if (owner.LoggingLevel >= DBLoggingLevel.CountOnly)
                {
                    NLogger.Log($"[DB SerializeDB] Starting serialization (OnlyChanged: {serializeOnlyChanged}, ClearChanged: {clearChanged})");
                }
                
                if (serializeOnlyChanged)
                {
                    Dictionary<long, List<dbRow>> serializedComp = new Dictionary<long, List<dbRow>>();
                    Dictionary<IECSObjectPathContainer, List<dbRow>> serializedCompPath = new Dictionary<IECSObjectPathContainer, List<dbRow>>();

                    foreach (var changedComponent in owner.ChangedComponents)
                    {
                        try
                        {
                            var ownerId = owner.ComponentOwners[changedComponent.Key];
                            var component = owner.DB[ownerId][changedComponent.Key];

                            List<dbRow> components = null;
                            serializedComp.TryGetValue(ownerId, out components);
                            if (components == null)
                                components = new List<dbRow>();
                            component.Item1.EnterToSerialization();
                            components.Add(new dbRow()
                            {
                                component = component.Item1,
                                componentInstanceId = component.Item1.instanceId,
                                componentId = component.Item1.GetId(),
                                componentState = component.Item2
                            });
                            serializedComp[ownerId] = components;
                            serializedCompPath[owner.OwnerPaths[ownerId]] = components;
                        }
                        catch (Exception ex)
                        {
                            errorChanged.Add(changedComponent.Key);
                        }
                    }
                    newSerializedDB = serializedCompPath;
                }
                else
                {
                    foreach (var entityRow in owner.DB)
                    {
                        if (entityRow.Value == null)
                            continue;
                        List<dbRow> components = new List<dbRow>();
                        var entityRowValues = entityRow.Value.Values.ToList();
                        for (int i = 0; i < entityRowValues.Count; i++)
                        {
                            var ecsComponent = entityRowValues[i];
                            ecsComponent.Item1.EnterToSerialization();
                            components.Add(new dbRow()
                            {
                                component = ecsComponent.Item1,
                                componentInstanceId = ecsComponent.Item1.instanceId,
                                componentId = ecsComponent.Item1.GetId(),
                                componentState = ecsComponent.Item2
                            });

                        }
                        newSerializedDB[owner.OwnerPaths[entityRow.Key]] = components;
                    }
                }
                
                if (owner.LoggingLevel >= DBLoggingLevel.CountOnly)
                {
                    NLogger.Log($"[DB SerializeDB] Serialized {newSerializedDB.Count} owners, {errorChanged.Count} errors");
                }
                
                if (clearChanged)
                    owner.ChangedComponents.Clear();
                errorChanged.ForEach(x => owner.ChangedComponents[x] = 1);

                owner.serializedDB = newSerializedDB;
            }
            if (owner.LoggingLevel >= DBLoggingLevel.CountOnly)
            {
                var elementsOwners = new StringBuilder();
                foreach (var serializedRow in newSerializedDB)
                {
                    elementsOwners.AppendLine($"{serializedRow.Key.serializableInstanceId} " + "{");
                    foreach (var dbrow in serializedRow.Value)
                    {
                        elementsOwners.AppendLine($"        {dbrow.componentId}++{dbrow.componentInstanceId}++{dbrow.componentState}, ");
                    }
                    elementsOwners.AppendLine("}");
                }

                var elementsOwnersEO = new StringBuilder();
                foreach (var serializedRow in owner.serializedDBNonEO)
                {
                    elementsOwners.AppendLine($"{serializedRow.Key.serializableInstanceId} " + "{");
                    foreach (var dbrow in serializedRow.Value.Item1)
                    {
                        elementsOwners.AppendLine($"        {dbrow.componentId}++{dbrow.componentInstanceId}++{dbrow.componentState}, ");
                    }
                    elementsOwners.AppendLine("}");
                }
                NLogger.Log($"[DB UnserializeDB] Starting deserialization of {newSerializedDB.Count} owners with elements:\n {elementsOwners} \n AND HAS NullEntityOwner:\n {elementsOwnersEO}");
            }
        }

            public void UnserializeDB(ComponentsDBComponent owner, bool retryNullEntityOwner)
        {
            owner._unserializeDepth++;
            try
            {
            lock (owner.serializedDB)
            {
                if (owner.LoggingLevel >= DBLoggingLevel.CountOnly)
                {
                    var elementsOwners = new StringBuilder();
                    foreach (var serializedRow in owner.serializedDB)
                    {
                        elementsOwners.AppendLine($"{serializedRow.Key.serializableInstanceId} " + "{");
                        foreach (var dbrow in serializedRow.Value)
                        {
                            elementsOwners.AppendLine($"        {dbrow.componentId}++{dbrow.componentInstanceId}++{dbrow.componentState}, ");
                        }
                        elementsOwners.AppendLine("}");
                    }

                    var elementsOwnersEO = new StringBuilder();
                    foreach (var serializedRow in owner.serializedDBNonEO)
                    {
                        elementsOwners.AppendLine($"{serializedRow.Key.serializableInstanceId} " + "{");
                        foreach (var dbrow in serializedRow.Value.Item1)
                        {
                            elementsOwners.AppendLine($"        {dbrow.componentId}++{dbrow.componentInstanceId}++{dbrow.componentState}, ");
                        }
                        elementsOwners.AppendLine("}");
                    }
                    NLogger.Log($"[DB UnserializeDB] Starting deserialization of {owner.serializedDB.Count} owners with elements:\n {elementsOwners} \n AND HAS NullEntityOwner:\n {elementsOwnersEO}");
                }

                if (retryNullEntityOwner)
                {
                    owner.serializedDBNonEO.ForEach(x => owner.serializedDB[x.Key] = x.Value.Item1);
                    // Строки, чей владелец так и не появился (cap 10), должны быть удалены
                    // и из NonEO, и из их слитой копии в owner.serializedDB — иначе
                    // owner.AfterDeserializeDB упадёт на них голым индексатором. Собираем
                    // ключи dead-letter'а и выносим их вместе со сливом ниже.
                    var deadLettered = new List<IECSObjectPathContainer>();

                    foreach (var serializedRow in owner.serializedDB)
                    {
                        Dictionary<long, (ECSComponent, ComponentState)> components = new Dictionary<long, (ECSComponent, ComponentState)>();
                        owner.DB.TryGetValue(serializedRow.Key.CacheInstanceId, out components);
                        if (components == null)
                            components = new Dictionary<long, (ECSComponent, ComponentState)>();
                        IECSObject entityOwner = serializedRow.Key.ECSObject;
                        if (entityOwner == null)
                        {
                            if (!owner.serializedDBNonEO.ContainsKey(serializedRow.Key))
                            {
                                owner.serializedDBNonEO[serializedRow.Key] = (serializedRow.Value, 0);
                            }

                            if (owner.serializedDBNonEO[serializedRow.Key].Item2 >= 10)
                            {
                                NLogger.Log("client: error unserialize: no entity");
                                var lostInstanceId = serializedRow.Key.serializableInstanceId;
                                if (owner.DB.ContainsKey(lostInstanceId))
                                {
                                    owner.RemoveComponentsByOwner(lostInstanceId);
                                }
                                NLogger.Log("lost components destroyed");
                                owner.serializedDBNonEO.Remove(serializedRow.Key);
                                deadLettered.Add(serializedRow.Key);
                                continue;
                            }

                            owner.serializedDBNonEO[serializedRow.Key] = (serializedRow.Value, owner.serializedDBNonEO[serializedRow.Key].Item2 + 1);

                        }
                        else
                        {
                            if (owner.serializedDBNonEO.ContainsKey(serializedRow.Key))
                            {
                                owner.serializedDBNonEO.Remove(serializedRow.Key);
                            }
                        }
                    }
                    deadLettered.ForEach(k => owner.serializedDB.Remove(k));
                    if (owner.serializedDBNonEO.Count > 0)
                    {
                        owner.serializedDBNonEO.ForEach(x => owner.serializedDB.Remove(x.Key));
                        owner.serializedDBNonEO.Where(x => x.Value.Item2 > 10).ToList().ForEach(x => owner.serializedDBNonEO.Remove(x.Key));

                        // Событийный retry: повторить при приходе недостающей сущности-владельца.
                        // Cap (10) и dead-letter (owner.RemoveComponentsByOwner) остаются в блоке
                        // retryNullEntityOwner выше и срабатывают на сливах.
                        var registry = owner.ECSWorldOwner?.entityManager?.PendingDeserialization;
                        if (registry != null)
                        {
                            // P11: ключ — Serial-стор, а НЕ owner. По ключу owner тот же реестр
                            // использует SerializationShadow, и её безусловный Unregister(owner)
                            // стирал наш ретрай (Dictionary<object,Action> — один экшен на ключ).
                            registry.Register(this, () => owner.UnserializeDB(true));
                            // register-then-recheck: владелец мог прийти во время обработки до регистрации.
                            if (owner.serializedDBNonEO.Any(x => x.Key.ECSObject != null))
                                TaskEx.RunAsync(() => owner.UnserializeDB(true));
                        }
                    }
                    else
                    {
                        owner.ECSWorldOwner?.entityManager?.PendingDeserialization.Unregister(this);
                    }
                }

                owner.ChangedComponents.Clear();
                int addedCount = 0;
                int updatedCount = 0;

                foreach (var serializedRow in owner.serializedDB)
                {
                    Dictionary<long, (ECSComponent, ComponentState)> components = new Dictionary<long, (ECSComponent, ComponentState)>();
                    owner.DB.TryGetValue(serializedRow.Key.CacheInstanceId, out components);
                    if (components == null)
                        components = new Dictionary<long, (ECSComponent, ComponentState)>();
                    IECSObject entityOwner = serializedRow.Key.ECSObject;
                    if (entityOwner != null)
                    {
                        foreach (var component in serializedRow.Value)
                        {
                            var unserComp = (ECSComponent)ReflectionCopy.MakeReverseShallowCopy(component.component);
                            component.componentInstanceId = unserComp.instanceId;
                            if (!owner.OwnerPaths.ContainsKey(entityOwner.instanceId))
                            {
                                owner.OwnerPaths[entityOwner.instanceId] = new IECSObjectPathContainer(owner.ECSWorldOwnerId, true) { ECSObject = entityOwner };
                            }
                            if (entityOwner is ECSEntity eCSEntity)
                            {
                                unserComp.ownerEntity = eCSEntity;
                            }
                            else if (entityOwner is ECSComponent eCSComponent)
                            {
                                unserComp.ownerEntity = eCSComponent.ownerEntity;
                            }
                            unserComp.ownerDB = owner;
                            // P7: как в AddComponent/AddOrChangeComponent — строка могла быть
                            // сериализована сервером до прикрепления его DB к миру (id == 0 на
                            // проводе); без штамповки клиентский MarkAsChanged у такой строки
                            // остаётся без мира. Ненулевой wire-id не трогаем: id мира — общая
                            // клиент-серверная константа и резолвится в локальный мир.
                            if (unserComp.ECSWorldOwnerId == 0)
                                unserComp.ECSWorldOwnerId = owner.ECSWorldOwnerId;
                            if (!components.ContainsKey(unserComp.instanceId))
                            {
                                components[unserComp.instanceId] = (unserComp, component.componentState);
                                owner.ComponentOwners[unserComp.instanceId] = entityOwner.instanceId;
                                unserComp.AfterDeserialization();
                                // Реакции эмитятся единожды в owner.AfterDeserializeDB по componentState
                                // (Created→Added, Changed→Change, Removed→Removing). Здесь только
                                // материализация компонента в owner.DB, без дублирующей AddedReaction.
                                addedCount++;
                            }
                            else
                            {
                                components[unserComp.instanceId] = (unserComp, component.componentState);
                                unserComp.AfterDeserialization();
                                updatedCount++;
                            }
                            owner.ChangedComponents[unserComp.instanceId] = 1;
                        }
                        owner.DB[serializedRow.Key.CacheInstanceId] = components;
                    }
                    else
                    {
                        NLogger.Error("error unserialize: no entity");
                    }
                }

                if (owner.LoggingLevel >= DBLoggingLevel.CountOnly)
                {
                    NLogger.Log($"[DB UnserializeDB] Deserialized - Added: {addedCount}, Updated: {updatedCount}");
                }

                owner.AfterDeserializeDB();
                owner.serializedDB.Clear();
            }
            }
            finally
            {
                owner._unserializeDepth--;
            }
        }

            public void AfterDeserializeDB(ComponentsDBComponent owner)
        {
            int createdCount = 0;
            int changedCount = 0;
            int removedCount = 0;
            
            foreach (var entityRow in owner.serializedDB)
            {
                var entityRowValues = entityRow.Value.ToList();
                for (int i = 0; i < entityRowValues.Count; i++)
                {
                    // Владелец мог не восстановиться (dead-letter/гонка) — тогда строка
                    // скипается с ERRORDB-логом, реакции по несуществующему владельцу не эмитим.
                    if (!owner.DB.TryGetValue(entityRow.Key.CacheInstanceId, out var ownerList) || ownerList == null)
                    {
                        NLogger.LogErrorDB($"AfterDeserializeDB: owner {entityRow.Key.CacheInstanceId} absent in live owner.DB — row skipped");
                        break;
                    }
                    if (entityRowValues[i].componentState == ComponentState.Removed && !ownerList.ContainsKey(entityRowValues[i].componentInstanceId))
                    {
                        NLogger.LogErrorDB("remove db component duplicate");
                        continue;
                    }
                    var ecsComponent = ownerList[entityRowValues[i].componentInstanceId];
                    if (ecsComponent.Item2 == ComponentState.Created)
                    {
                        ecsComponent.Item1.AddedReaction(ecsComponent.Item1.ownerEntity);
                        createdCount++;
                    }
                    if (ecsComponent.Item2 == ComponentState.Changed)
                    {
                        //ecsComponent.Item1.OnAdded(ecsComponent.Item1.ownerEntity);
                        TaskEx.RunAsync(() =>
                        {
                            ecsComponent.Item1.ChangeReaction(ecsComponent.Item1.ownerEntity);
                        });
                        changedCount++;
                    }
                    if (ecsComponent.Item2 == ComponentState.Removed)
                    {
                        ecsComponent.Item1.RemovingReaction(ecsComponent.Item1.ownerEntity);
                        ownerList.Remove(ecsComponent.Item1.instanceId);
                        owner.ComponentOwners.Remove(ecsComponent.Item1.instanceId);
                        removedCount++;
                    }
                }
            }
            
            if (owner.LoggingLevel >= DBLoggingLevel.CountOnly)
            {
                NLogger.Log($"[DB AfterDeserializeDB] Processed - Created: {createdCount}, Changed: {changedCount}, Removed: {removedCount}");
                owner.LogDBState("AfterDeserializeDB Complete");
            }
        }

        }

        [System.NonSerialized]
        [IgnoreDataMember]
        public DbStore Store = new DbStore();
        [System.NonSerialized]
        [IgnoreDataMember]
        public DbSerialization Serial = new DbSerialization();

        [IgnoreDataMember]
        public Dictionary<long, Dictionary<long, (ECSComponent, ComponentState)>> DB { get { return Store.Rows; } set { Store.Rows = value; } }
        [IgnoreDataMember]
        public Dictionary<long, long> ComponentOwners { get { return Store.ComponentOwners; } set { Store.ComponentOwners = value; } }
        [IgnoreDataMember]
        public Dictionary<long, IECSObjectPathContainer> OwnerPaths { get { return Serial.OwnerPaths; } set { Serial.OwnerPaths = value; } }
        [IgnoreDataMember]
        public Dictionary<long, int> ChangedComponents { get { return Store.ChangedComponents; } set { Store.ChangedComponents = value; } }

        #region Logging Helper Methods

        internal void LogDBState(string operation, List<(ECSComponent component, ComponentState state, string action)> changes = null)
        {
            if (LoggingLevel == DBLoggingLevel.None) return;

            int totalComponents = 0;
            Dictionary<Type, int> componentCounts = new Dictionary<Type, int>();
            Dictionary<Type, Dictionary<ComponentState, int>> statesByType = new Dictionary<Type, Dictionary<ComponentState, int>>();

            // Count all components and their states
            foreach (var owner in DB)
            {
                foreach (var comp in owner.Value)
                {
                    if (comp.Value.Item2 != ComponentState.Removed)
                    {
                        totalComponents++;
                        Type compType = comp.Value.Item1.GetType();
                        
                        if (!componentCounts.ContainsKey(compType))
                            componentCounts[compType] = 0;
                        componentCounts[compType]++;

                        if (!statesByType.ContainsKey(compType))
                            statesByType[compType] = new Dictionary<ComponentState, int>();
                        
                        if (!statesByType[compType].ContainsKey(comp.Value.Item2))
                            statesByType[compType][comp.Value.Item2] = 0;
                        statesByType[compType][comp.Value.Item2]++;
                    }
                }
            }

            StringBuilder logMessage = new StringBuilder();
            logMessage.AppendLine($"[DB Operation: {operation}]");

            // Level 1: Count only
            logMessage.AppendLine($"Total Components: {totalComponents}");
            logMessage.AppendLine($"Total Owners: {DB.Count}");
            logMessage.AppendLine($"Changed Components: {ChangedComponents.Count}");

            // Level 2: Count and types
            if (LoggingLevel >= DBLoggingLevel.CountAndTypes)
            {
                logMessage.AppendLine("Components by Type:");
                foreach (var kvp in componentCounts.OrderBy(x => x.Key.Name))
                {
                    logMessage.AppendLine($"  - {kvp.Key.Name}: {kvp.Value}");
                }
            }

            // Level 3: Full details with operation results
            if (LoggingLevel >= DBLoggingLevel.Full)
            {
                if (changes != null && changes.Count > 0)
                {
                    logMessage.AppendLine("Operation Results:");
                    
                    var grouped = changes.GroupBy(x => new { Type = x.component.GetType(), Action = x.action });
                    foreach (var group in grouped)
                    {
                        logMessage.AppendLine($"  - {group.Key.Action} {group.Key.Type.Name} <{group.Count()}>");
                    }
                }

                logMessage.AppendLine("Component States by Type:");
                foreach (var typeStates in statesByType.OrderBy(x => x.Key.Name))
                {
                    logMessage.Append($"  - {typeStates.Key.Name}: ");
                    var states = typeStates.Value.Select(x => $"{x.Key}={x.Value}");
                    logMessage.AppendLine(string.Join(", ", states));
                }
            }

            NLogger.Log(logMessage.ToString());
        }

        private string GetComponentTypeName(ECSComponent component)
        {
            return component?.GetType().Name ?? "Unknown";
        }

        #endregion

        #region add methods

        public virtual void AddComponent(IECSObject ownerComponent, ECSComponent component)
        {
            Dictionary<long, (ECSComponent, ComponentState)> components = new Dictionary<long, (ECSComponent, ComponentState)>();
            ECSComponent addedComponent = null;
            var changes = new List<(ECSComponent, ComponentState, string)>();
            
            using(ownerEntity.entityComponents.StabilizationGate.WriteLock())
            {
                {
                    this.AssertDbWriteGate();
                    DB.TryGetValue(ownerComponent.instanceId, out components);
                    if (components == null)
                        components = new Dictionary<long, (ECSComponent, ComponentState)>();
                    if (ownerComponent is ECSEntity eCSEntity)
                    {
                        component.ownerEntity = eCSEntity;
                    }
                    else if (ownerComponent is ECSComponent eCSComponent)
                    {
                        component.ownerEntity = eCSComponent.ownerEntity;
                    }
                    component.ownerDB = this;
                    // P7: вложенному компоненту надо проставить мир, иначе ECSWorldOwner == null
                    // и MarkAsChanged()/DirectiveSetChanged() падают на .Profile.
                    if (component.ECSWorldOwnerId == 0)
                        component.ECSWorldOwnerId = this.ECSWorldOwnerId;
                    components[component.instanceId] = (component, ComponentState.Created);
                    DB[ownerComponent.instanceId] = components;
                    ComponentOwners[component.instanceId] = ownerComponent.instanceId;
                    if(!OwnerPaths.ContainsKey(ownerComponent.instanceId))
                    {
                        OwnerPaths[ownerComponent.instanceId] = new IECSObjectPathContainer(this.ECSWorldOwnerId, true){ECSObject = ownerComponent};
                    }
                    ChangedComponents[component.instanceId] = 1;
                    addedComponent = component;
                    changes.Add((component, ComponentState.Created, "Added"));
                    
                    LogDBState($"AddComponent({GetComponentTypeName(component)})", changes);
                }
            }
            addedComponent.AddedReaction(addedComponent.ownerEntity);
        }

        public virtual void AddOrChangeComponent(IECSObject ownerComponent, ECSComponent component)
        {
            Dictionary<long, (ECSComponent, ComponentState)> components = new Dictionary<long, (ECSComponent, ComponentState)>();
            bool change = false;
            var changes = new List<(ECSComponent, ComponentState, string)>();
            
            using(ownerEntity.entityComponents.StabilizationGate.WriteLock())
            {
                {
                    this.AssertDbWriteGate();
                    DB.TryGetValue(ownerComponent.instanceId, out components);
                    if (components == null)
                        components = new Dictionary<long, (ECSComponent, ComponentState)>();
                    if (components.ContainsKey(component.instanceId))
                    {
                        change = true;
                        changes.Add((component, ComponentState.Changed, "Changed"));
                    }
                    else
                    {
                        if(ownerComponent is ECSEntity eCSEntity)
                        {
                            component.ownerEntity = eCSEntity;
                        }
                        else if (ownerComponent is ECSComponent eCSComponent)
                        {
                            component.ownerEntity = eCSComponent.ownerEntity;
                        }
                        component.ownerDB = this;
                        // P7: см. AddComponent — без мира MarkAsChanged() бросит NRE.
                        if (component.ECSWorldOwnerId == 0)
                            component.ECSWorldOwnerId = this.ECSWorldOwnerId;
                        components[component.instanceId] = (component, ComponentState.Created);
                        ComponentOwners[component.instanceId] = ownerComponent.instanceId;
                        if(!OwnerPaths.ContainsKey(ownerComponent.instanceId))
                        {
                            OwnerPaths[ownerComponent.instanceId] = new IECSObjectPathContainer(this.ECSWorldOwnerId, true){ECSObject = ownerComponent};
                        }
                        ChangedComponents[component.instanceId] = 1;
                        changes.Add((component, ComponentState.Created, "Added"));
                    }
                    DB[ownerComponent.instanceId] = components;
                    
                    LogDBState($"AddOrChangeComponent({GetComponentTypeName(component)})", changes);
                }
            }
            if(change)
                ChangeComponent(component, ownerComponent);
            else
            {
                if (ownerComponent is ECSEntity eCSEntity)
                {
                    component.AddedReaction(eCSEntity);
                }
                else if (ownerComponent is ECSComponent eCSComponent)
                {
                    component.AddedReaction(eCSComponent.ownerEntity);
                }
            }
        }

        public virtual void AddComponents(IECSObject ownerComponent, params ECSComponent[] component)
        {
            var changes = new List<(ECSComponent, ComponentState, string)>();
            foreach(var comp in component)
            {
                AddComponent(ownerComponent, comp);
            }
        }

        public virtual void AddComponents(IECSObject ownerComponent, List<ECSComponent> component)
        {
            foreach (var comp in component)
            {
                AddComponent(ownerComponent, comp);
            }
        }

        public virtual void AddComponent(IECSObjectPathContainer ownerComponentId, ECSComponent component)
        {
            AddComponent(ownerComponentId.ECSObject, component);
        }

        public virtual void AddOrChangeComponent(IECSObjectPathContainer ownerComponentId, ECSComponent component)
        {
            AddOrChangeComponent(ownerComponentId.ECSObject, component);
        }

        public virtual void AddComponents(IECSObjectPathContainer ownerComponentId, params ECSComponent[] component)
        {
            foreach (var comp in component)
            {
                AddComponent(ownerComponentId.ECSObject, comp);
            }
        }

        public virtual void AddComponents(IECSObjectPathContainer ownerComponentId, List<ECSComponent> component)
        {
            foreach (var comp in component)
            {
                AddComponent(ownerComponentId.ECSObject, comp);
            }
        }

        #endregion

        #region edit methods
        
        public virtual (ECSComponent, ComponentState) GetComponent(long componentId, IECSObject ownerComponent = null)
        {
            using (this.DbReadScope())
            {
                long owner = 0;
                if (ownerComponent == null)
                {
                    if (!ComponentOwners.TryGetValue(componentId, out owner))
                    {
                        if(this.ECSWorldOwner.Profile.ClientRetryOnMissingRefs && _unserializeDepth > 0) // профиль (идея 1.15)
                        {
                            NLogger.Log("SETUP_UNSERIALIZE error get component from db");
                        }
                        else
                        {
                            NLogger.LogErrorDB("error get component from db");
                        }
                        return (null, ComponentState.Null);
                    }
                }
                else
                {
                    owner = ownerComponent.instanceId;
                }
                (ECSComponent, ComponentState) comp;
                if (DB[owner].TryGetValue(componentId, out comp) && comp.Item2 != ComponentState.Removed)
                {
                    if (LoggingLevel >= DBLoggingLevel.Full)
                    {
                        NLogger.Log($"[DB GetComponent] Retrieved {GetComponentTypeName(comp.Item1)} (State: {comp.Item2})");
                    }
                    return comp;
                }
                else
                {
                    NLogger.LogErrorDB("error get component from db");
                    return (null, ComponentState.Null);
                }
            }
        }

        public virtual List<(ECSComponent, ComponentState)> GetComponentsByType<T>(IECSObject ownerComponent = null)
        {
            return this.GetComponentsByType(new List<long>() { typeof(T).IdToECSType() }, ownerComponent);
        }

        public virtual List<(ECSComponent, ComponentState)> GetComponentsByType(List<long> componentTypeId, IECSObject ownerComponent = null)
        {
            List<(ECSComponent, ComponentState)> result = new List<(ECSComponent, ComponentState)>();
            using (this.DbReadScope())
            {
                List<long> owners = new List<long>();
                if (ownerComponent == null)
                {
                    owners = DB.Keys.ToList();
                }
                else
                {
                    if (DB.ContainsKey(ownerComponent.instanceId))
                        owners.Add(ownerComponent.instanceId);
                }
                foreach (var dbOwner in owners)
                {
                    var components = DB[dbOwner];
                    foreach(var comp in components)
                    {
                        if(comp.Value.Item2 != ComponentState.Removed && componentTypeId.Contains(comp.Value.Item1.GetId()))
                        {
                            result.Add(comp.Value);
                        }
                    }
                }
                
                if (LoggingLevel >= DBLoggingLevel.Full)
                {
                    NLogger.Log($"[DB GetComponentsByType] Found {result.Count} components");
                }
            }
            return result;
        }

        public virtual void ChangeComponent(ECSComponent component, IECSObject ownerComponent = null)
        {
            if(!ComponentOwners.ContainsKey(component.instanceId))
            {
                NLogger.LogErrorDB("error change component from db");
                return;
            }
            
            var changes = new List<(ECSComponent, ComponentState, string)>();
            
            using(ownerEntity.entityComponents.StabilizationGate.WriteLock())
            {
                {
                    this.AssertDbWriteGate();
                    long owner = 0;
                    if (ownerComponent == null)
                    {
                        if (!ComponentOwners.TryGetValue(component.instanceId, out owner))
                        {
                            NLogger.LogErrorDB("error change component from db");
                        }
                    }
                    else
                        owner = ownerComponent.instanceId;
                    DB[owner][component.instanceId] = (component, ComponentState.Changed);
                    ChangedComponents[component.instanceId] = 1;
                    changes.Add((component, ComponentState.Changed, "Changed"));
                    
                    LogDBState($"ChangeComponent({GetComponentTypeName(component)})", changes);
                }
            }
            
        }
        
        #endregion

        #region remove methods

        public virtual void RemoveComponent(long componentId, IECSObject ownerComponent = null)
        {
            if (!ComponentOwners.ContainsKey(componentId))
            {
                NLogger.LogErrorDB("error remove component from db");
                return;
            }
            ECSComponent removedComponent = null;
            var changes = new List<(ECSComponent, ComponentState, string)>();
            
            using(ownerEntity.entityComponents.StabilizationGate.WriteLock())
            {
                {
                    this.AssertDbWriteGate();
                    long owner = 0;
                    if (ownerComponent == null)
                    {
                        if (!ComponentOwners.TryGetValue(componentId, out owner))
                        {
                            NLogger.LogErrorDB("error remove component from db");
                        }
                    }
                    else
                        owner = ownerComponent.instanceId;
                    (ECSComponent, ComponentState) comp;
                    if (DB[owner].TryGetValue(componentId, out comp))
                    {
                        DB[owner][componentId] = (comp.Item1, ComponentState.Removed);
                        ChangedComponents[componentId] = 1;
                        removedComponent = comp.Item1;
                        changes.Add((comp.Item1, ComponentState.Removed, "Removed"));
                        
                        LogDBState($"RemoveComponent({GetComponentTypeName(comp.Item1)})", changes);
                    }
                    else
                    {
                        NLogger.LogErrorDB("error remove component from db");
                    }
                }
            }
            if(removedComponent != null)
            {
                removedComponent.RemovingReaction(removedComponent.ownerEntity);
            }
        }

        public virtual void RemoveComponent(params long[] componentsId)
        {
            foreach(var comp in componentsId)
            {
                RemoveComponent(comp);
            }
        }

        public virtual void RemoveComponent(List<long> componentsId, IECSObject ownerComponent = null)
        {
            foreach (var comp in componentsId)
            {
                RemoveComponent(comp);
            }
        }

        public virtual void RemoveComponent(List<ECSComponent> components, IECSObject ownerComponent = null)
        {
            foreach (var comp in components)
            {
                RemoveComponent(comp.instanceId);
            }
        }

        public virtual void RemoveComponentsByType(List<Type> componentTypeId, bool includeInherit, List<IECSObject> ownerComponent = null)
        {
            List<ECSComponent> removedComponents = new List<ECSComponent>();
            var changes = new List<(ECSComponent, ComponentState, string)>();
            
            using(ownerEntity.entityComponents.StabilizationGate.WriteLock())
            {
                {
                    this.AssertDbWriteGate();
                    List<long> owners = new List<long>();
                    if (ownerComponent == null)
                    {
                        owners = DB.Keys.ToList();
                    }
                    else
                    {
                        ownerComponent.ForEach(x =>
                        {
                            if (DB.ContainsKey(x.instanceId))
                                owners.Add(x.instanceId);
                        });
                    }
                    foreach (var dbOwner in owners)
                    {
                        var components = DB[dbOwner];
                        List<(ECSComponent, ComponentState)> removeList = new List<(ECSComponent, ComponentState)>();
                        foreach (var comp in components)
                        {
                            foreach (var removableType in componentTypeId)
                            {
                                if (comp.Value.Item1.GetType() == removableType || (includeInherit && comp.Value.Item1.GetType().IsSubclassOf(removableType)))
                                {
                                    removeList.Add(comp.Value);
                                }
                            }
                        }
                        foreach (var removedComp in removeList)
                        {
                            components[removedComp.Item1.instanceId] = (removedComp.Item1, ComponentState.Removed);
                            ChangedComponents[removedComp.Item1.instanceId] = 1;
                            removedComponents.Add(removedComp.Item1);
                            changes.Add((removedComp.Item1, ComponentState.Removed, "Removed"));
                        }
                        DB[dbOwner] = components;
                    }
                    
                    LogDBState($"RemoveComponentsByType({string.Join(", ", componentTypeId.Select(t => t.Name))})", changes);
                }
            }
            removedComponents.ForEach(x => {
                x.RemovingReaction(x.ownerEntity);
            });
        }

        public virtual void RemoveComponentsByType(List<long> componentTypeId, List<IECSObject> ownerComponent = null)
        {
            List<ECSComponent> removedComponents = new List<ECSComponent>();
            var changes = new List<(ECSComponent, ComponentState, string)>();
            
            using(ownerEntity.entityComponents.StabilizationGate.WriteLock())
            {
                {
                    this.AssertDbWriteGate();
                    List<long> owners = new List<long>();
                    if (ownerComponent == null)
                    {
                        owners = DB.Keys.ToList();
                    }
                    else
                    {
                        ownerComponent.ForEach(x =>
                        {
                            if (DB.ContainsKey(x.instanceId))
                                owners.Add(x.instanceId);
                        });
                    }
                    foreach (var dbOwner in owners)
                    {
                        var components = DB[dbOwner];
                        List<(ECSComponent, ComponentState)> removeList = new List<(ECSComponent, ComponentState)>();
                        foreach (var comp in components)
                        {
                            if (componentTypeId.Contains(comp.Value.Item1.GetId()))
                            {
                                removeList.Add(comp.Value);
                            }
                        }
                        foreach (var removedComp in removeList)
                        {
                            components[removedComp.Item1.instanceId] = (removedComp.Item1, ComponentState.Removed);
                            ChangedComponents[removedComp.Item1.instanceId] = 1;
                            removedComponents.Add(removedComp.Item1);
                            changes.Add((removedComp.Item1, ComponentState.Removed, "Removed"));
                        }
                        DB[dbOwner] = components;
                    }
                    
                    LogDBState($"RemoveComponentsByType(IDs: {string.Join(", ", componentTypeId)})", changes);
                }
            }
            removedComponents.ForEach(x => {
                x.RemovingReaction(x.ownerEntity);
            });
        }

        public void RemoveComponentsByOwner(long instanceId)
        {
            try
            {
                using (this.DbWriteScope())
                {
                    var changes = new List<(ECSComponent, ComponentState, string)>();
                    if(this.DB.SnapshotI(null).TryGetValue(instanceId, out var dbsnap))
                    {
                        foreach (var inter in dbsnap.ToList())
                        {
                            changes.Add((inter.Value.Item1, ComponentState.Removed, "Removed"));
                            this.RemoveComponent(inter.Value.Item1.instanceId);
                        }

                        if (LoggingLevel >= DBLoggingLevel.CountAndTypes)
                        {
                            LogDBState($"RemoveComponentsByOwner(Owner: {instanceId})", changes);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                NLogger.LogErrorDB($"error remove components from db by owner {ex.Message} \n [[[[[[[[[{ex.StackTrace}]]]]]]]]]");
            }
        }

        public virtual void ClearDB()
        {
            try
            {
                using (this.DbWriteScope())
                {
                    var changes = new List<(ECSComponent, ComponentState, string)>();
                    var dbsnap = this.DB.SnapshotI(null);

                    foreach (var dbinter in dbsnap)
                    {
                        foreach (var inter in dbinter.Value.SnapshotI(null))
                        {
                            changes.Add((inter.Value.Item1, ComponentState.Removed, "Cleared"));
                            this.RemoveComponent(inter.Value.Item1.instanceId);
                        }
                    }

                    LogDBState("ClearDB", changes);
                }
            }
            catch (Exception e)
            {
                NLogger.LogErrorDB("error remove components from db by owner");
            }
        }

        #endregion

        // Тела Serialize/Unserialize/AfterDeserializeDB живут в DbSerialization (owner
        // передаётся параметром); эти методы — тонкие шеллы, держащие virtual-диспатч
        // и публичный API.
        public override void SerializeDB(bool serializeOnlyChanged = false, bool clearChanged = true)
        {
            Serial.SerializeDB(this, serializeOnlyChanged, clearChanged);
        }

        public override void AfterSerializationDB(bool clearAfterSerializaion = true)
        {
            {
                if (clearAfterSerializaion)
                {
                    int removedCount = 0;

                    HashSet<long> removedOwners = new HashSet<long>();

                    foreach (var entityRow in new Dictionary<IECSObjectPathContainer, List<dbRow>>(serializedDB))
                    {
                        var entityRowValues = entityRow.Value.ToList();
                        for (int i = 0; i < entityRowValues.Count; i++)
                        {
                            var ownerList = DB[entityRow.Key.ECSObject.instanceId];
                            var ecsComponent = ownerList[entityRowValues[i].componentInstanceId];
                            if (ecsComponent.Item2 == ComponentState.Removed)
                            {
                                ecsComponent.Item1.RemovingReaction(ecsComponent.Item1.ownerEntity);
                                ownerList.Remove(ecsComponent.Item1.instanceId);
                                removedCount++;
                            }
                            if(ownerList.Count == 0)
                            {
                                removedOwners.Add(entityRow.Key.ECSObject.instanceId);
                            }
                        }
                    }

                    removedOwners.ForEach(x => DB.Remove(x));
                    
                    if (LoggingLevel >= DBLoggingLevel.CountOnly)
                    {
                        NLogger.Log($"[DB AfterSerializationDB] Cleaned up {removedCount} removed components");
                    }
                }
            }
        }

        private int unserializeCheckCount { get { return Serial.UnserializeCheckCount; } set { Serial.UnserializeCheckCount = value; } }

        [IgnoreDataMember]
        public DictionaryWrapper<IECSObjectPathContainer, (List<dbRow>, int)> serializedDBNonEO { get { return Serial.NonEO; } set { Serial.NonEO = value; } }

        public override void UnserializeDB(bool retryNullEntityOwner = false)
        {
            Serial.UnserializeDB(this, retryNullEntityOwner);
        }

        public override void AfterDeserializeDB()
        {
            Serial.AfterDeserializeDB(this);
        }
    }
}
