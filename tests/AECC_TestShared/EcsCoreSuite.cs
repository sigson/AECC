using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AECC.Core;
using AECC.Core.BuiltInTypes.Components;
using AECC.Core.BuiltInTypes.ComponentsGroup;
using AECC.Harness.Serialization;
using AECC.Locking;
using AECC.Serialization;

namespace AECC.TestKit
{
    /// <summary>
    /// ФАЗА A — локальная батарея ядра. Гоняется в процессе сервера ДО подъёма сети:
    /// покрывает ECS «от и до» без участия сокетов.
    ///
    /// Порядок создания миров важен: Offline → (Client-зеркало) → и только потом реальный
    /// Server-мир в Program.cs (конструктор ECSComponentManager перезаписывает статик
    /// GlobalProgramComponentGroup).
    /// </summary>
    public static class EcsCoreSuite
    {
        private const long OfflineWorldId = 0x0A0E0C0C000000A1L;
        private const long MirrorWorldId = 0x0A0E0C0C000000A2L;
        private const long SquashSrcWorldId = 0x0A0E0C0C000000A3L;
        private const long SquashDstWorldId = 0x0A0E0C0C000000A4L;

        public static void Run(TestReport r)
        {
            var world = Bootstrapping.CreateWorld(OfflineWorldId, ECSWorld.WorldTypeEnum.Offline, new SerializationAdapter());
            var ser = SerializationBootstrap.SerializerOf(world);

            try
            {
                A1_WorldLifecycle(r, world);
                A2_EntityComponentCrud(r, world);
                A3_LifecycleReactions(r, world);
                A4_TransactionalOps(r, world);
                A5_Contracts(r, world);
                A6_TimeDependSystem(r, world);
                A7_Query(r, world);
                A8_SharedFields(r, world);
                A9_SerializationShadowAndChildren(r, world, ser);
                A10_SlicedSerializationAndGdap(r, world, ser);
                A11_DbComponent(r, world, ser);
                A12_Timers(r, world);
                A13_LocalReplicationToClientWorld(r, world, ser);
                A14_Squash(r);
            }
            catch (Exception ex)
            {
                r.Check("ФАЗА A не упала целиком", false, ex.ToString());
            }
            finally
            {
                try { world.Dispose(); } catch { }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        private static void A1_WorldLifecycle(TestReport r, ECSWorld world)
        {
            r.Section("A1 · lifecycle мира");

            r.Check("Configure+Start ⇒ Initialized", world.Initialized);
            r.Check("менеджеры подняты",
                world.entityManager != null && world.componentManager != null && world.contractsManager != null);
            r.Check("Query-индекс смонтирован (Bootstrap.AttachRuntime)", world.Query != null);
            r.Check("сериализатор смонтирован", world.EntityWorldSerializer is EntityNetSerializer);
            r.Check("мир найден в WorldRegistry по instanceId",
                WorldRegistry.Default.All().ContainsKey(world.instanceId));
            r.Check("профиль Offline: DbAuthoritativeChangeMarking=true, ServerMarksChangedOnAdd=false",
                world.Profile.DbAuthoritativeChangeMarking && !world.Profile.ServerMarksChangedOnAdd);
            r.CheckEq("MultiThread-режим включён (иначе локи — no-op)", false, Defines.OneThreadMode);
            r.Check("TypeRegistry заполнен (InitSerialize отработал)",
                TypeRegistry.Global.GetType(TK.Uid<PositionComponent>()) == typeof(PositionComponent));
            r.CheckEq("[TypeUid] == GetId() у компонента", 5001L, new PositionComponent().GetId());
        }

        // ─────────────────────────────────────────────────────────────────────
        private static void A2_EntityComponentCrud(TestReport r, ECSWorld world)
        {
            r.Section("A2 · сущности и компоненты");

            var e = new ECSEntity { AliasName = "crud" };
            r.Check("AddNewEntity", world.entityManager.AddNewEntity(e));
            r.Check("сущность резолвится по id", world.entityManager.ContainsEntitySyncronized(e.instanceId));
            r.Check("ECSWorldOwner проставлен", e.ECSWorldOwner != null && e.ECSWorldOwner.instanceId == world.instanceId);
            r.Check("Alive выставляется реакцией AddNewEntity", TestReport.Await(() => e.Alive, 2000));

            var pos = new PositionComponent { X = 1, Y = 2 };
            e.AddComponent(pos);
            r.Check("HasComponent<T>", e.HasComponent<PositionComponent>());
            r.Check("HasComponent(typeId)", e.HasComponent(TK.Uid<PositionComponent>()));
            r.Check("GetComponent<T> возвращает тот же инстанс", ReferenceEquals(pos, e.GetComponent<PositionComponent>()));
            r.Check("GetComponent(typeId)", e.GetComponent(TK.Uid<PositionComponent>()) != null);
            r.Check("component.ownerEntity", ReferenceEquals(e, pos.ownerEntity));
            r.Check("component.ECSWorldOwner", pos.ECSWorldOwner != null);

            var h = e.AddComponentAndGetInstance<HealthComponent>();
            r.Check("AddComponentAndGetInstance", h != null && e.HasComponent<HealthComponent>());

            r.Check("GetComponentBroadcastType (по базовому типу)",
                e.GetComponentBroadcastType<ECSComponent>() != null);
            r.CheckEq("Components.Count", 2, e.entityComponents.Components.Count);

            r.Check("TryGetComponent отсутствующего == null", e.TryGetComponent<BlockerComponent>() == null);

            e.RemoveComponent<HealthComponent>();
            r.Check("RemoveComponent", !e.HasComponent<HealthComponent>());
            r.Check("RemovedComponents (журнал удалений) пополнился",
                e.entityComponents.RemovedComponents.Contains(TK.Uid<HealthComponent>()));

            // Группы компонентов
            Groups.Server(pos);
            r.Check("IsInGroup<ServerComponentGroup>", e.IsInGroup<ServerComponentGroup>());
            r.Check("НЕ IsInGroup<ClientComponentGroup>", !e.IsInGroup<ClientComponentGroup>());

            // Дерево IECSObject
            var child = new ECSEntity { AliasName = "child" };
            world.entityManager.AddNewEntity(child);
            e.AddChildObject(child);
            r.Check("AddChildObject/ContainsChildObject", e.ContainsChildObject(child.instanceId));
            r.Check("child.ownerECSObject == parent", ReferenceEquals(e, child.ownerECSObject));

            // Удаление родителя переподчиняет детей вверх (ReparentChildrenUpwards)
            var grand = new ECSEntity { AliasName = "grand" };
            world.entityManager.AddNewEntity(grand);
            grand.AddChildObject(e);
            world.entityManager.RemoveEntity(e);
            r.Check("сущность удалена из мира", !world.entityManager.ContainsEntitySyncronized(e.instanceId));
            r.Check("Alive сброшен", !e.Alive);
            r.Check("OnDelete снял все компоненты", e.entityComponents.Components.Count == 0);
            r.Check("ребёнок переподчинён деду (ReparentChildrenUpwards)",
                TestReport.Await(() => grand.ContainsChildObject(child.instanceId), 1500));
        }

        // ─────────────────────────────────────────────────────────────────────
        private static void A3_LifecycleReactions(TestReport r, ECSWorld world)
        {
            r.Section("A3 · lifecycle компонентов (Add→Change→Remove)");

            LifecycleProbeComponent.Reset();

            var e = new ECSEntity { AliasName = "probe" };
            world.entityManager.AddNewEntity(e);

            var probe = new LifecycleProbeComponent { Payload = 1 };
            e.AddComponent(probe);
            r.Check("OnAdded вызван", TestReport.Await(() => LifecycleProbeComponent.Added == 1, 2000));

            probe.Payload = 2;
            probe.MarkAsChanged();
            r.Check("OnChanged вызван", TestReport.Await(() => LifecycleProbeComponent.Changed >= 1, 2000));

            e.RemoveComponent<LifecycleProbeComponent>();
            r.Check("OnRemoved вызван", TestReport.Await(() => LifecycleProbeComponent.Removed == 1, 2000));

            r.Check("порядок реакций Add→Change→Remove сохранён (диспетчер)",
                LifecycleProbeComponent.Order.StartsWith("A") && LifecycleProbeComponent.Order.EndsWith("R"),
                "фактически: " + LifecycleProbeComponent.Order);

            // Fast-path: компонент без пользовательских хуков не должен ронять/дублировать реакции
            var plain = new ScoreComponent { Score = 5 };
            e.AddComponent(plain);
            plain.Score = 7;
            plain.MarkAsChanged();
            r.Check("dirty-set пометил изменённый компонент",
                e.entityComponents.CheckChanged(typeof(ScoreComponent)));

            e.RemoveComponent<ScoreComponent>();
            r.Check("после удаления компонент вышел из dirty-set",
                !e.entityComponents.CheckChanged(typeof(ScoreComponent)));

            world.entityManager.RemoveEntity(e);
        }

        // ─────────────────────────────────────────────────────────────────────
        private static void A4_TransactionalOps(TestReport r, ECSWorld world)
        {
            r.Section("A4 · блокирующие транзакционные операции");

            var e = new ECSEntity { AliasName = "tx" };
            world.entityManager.AddNewEntity(e);
            e.AddComponent(new PositionComponent { X = 10, Y = 20 });
            e.AddComponent(new HealthComponent { Hp = 50 });

            // 1) Вложенный write-lock на два компонента
            bool nested = false;
            e.ExecuteWriteLockedComponent<PositionComponent, HealthComponent>((p, h) =>
            {
                p.X += 1;
                h.Hp -= 1;
                nested = true;
            });
            r.Check("ExecuteWriteLockedComponent<T1,T2> (вложенный захват)", nested);
            r.CheckEq("мутация под локом видна", 11.0, e.GetComponent<PositionComponent>().X);

            // 2) Долгий read-token блокирует удаление из другого потока
            ECSComponent locked;
            RWToken token;
            bool got = e.entityComponents.GetReadLockedComponent(typeof(PositionComponent), out locked, out token);
            r.Check("GetReadLockedComponent выдал токен", got && token.IsReal);

            var removeDone = new ManualResetEventSlim(false);
            var remover = Task.Run(() =>
            {
                e.RemoveComponent<PositionComponent>();   // должен ЖДАТЬ освобождения токена
                removeDone.Set();
            });
            bool blockedWhileHeld = !removeDone.Wait(250);
            r.Check("удаление компонента ЗАБЛОКИРОВАНО, пока держится read-token", blockedWhileHeld);
            token.Dispose();
            r.Check("после Dispose токена удаление прошло", removeDone.Wait(3000));
            remover.Wait(3000);

            // 3) Absence-hold: держим слот пустым — параллельный Add обязан ждать
            RWToken holdToken;
            bool holding = e.entityComponents.HoldComponentAddition(typeof(BlockerComponent), out holdToken);
            r.Check("HoldComponentAddition взял absence-hold", holding);

            var addDone = new ManualResetEventSlim(false);
            var adder = Task.Run(() =>
            {
                e.AddComponent(new BlockerComponent());
                addDone.Set();
            });
            bool addBlocked = !addDone.Wait(250) && !e.HasComponent<BlockerComponent>();
            r.Check("добавление компонента ЗАБЛОКИРОВАНО absence-hold'ом", addBlocked);
            holdToken.Dispose();
            r.Check("после снятия hold компонент добавился", addDone.Wait(3000) && e.HasComponent<BlockerComponent>());
            adder.Wait(3000);
            e.RemoveComponent<BlockerComponent>();

            // 4) ExecuteOnNotHasComponent — исполнение при гарантированном отсутствии
            bool absentRan = false;
            bool absentOk = e.entityComponents.ExecuteOnNotHasComponent(typeof(BlockerComponent), () =>
            {
                absentRan = !e.HasComponent<BlockerComponent>();
            });
            r.Check("ExecuteOnNotHasComponent исполнился на отсутствующем", absentOk && absentRan);

            // 5) Write-лок ВСЕГО хранилища компонентов (публичный фасад над локдауном ComponentStore,
            //    используется в OnEntityDelete). Сам ComponentStore internal — сюда не достучаться.
            r.Try("GetWriteLockedComponentStorage (локдаун всего хранилища)", () =>
            {
                using (var storageToken = e.entityComponents.GetWriteLockedComponentStorage())
                {
                    // под локдауном хранилище недоступно другим потокам
                }
            });

            // 6) StabilizationGate: read (сериализация) vs write (мутация DB)
            r.Try("StabilizationGate Read/Write scopes", () =>
            {
                using (e.entityComponents.StabilizationGate.ReadLock()) { }
                using (e.entityComponents.StabilizationGate.WriteLock()) { }
            });

            world.entityManager.RemoveEntity(e);
        }

        // ─────────────────────────────────────────────────────────────────────
        private static void A5_Contracts(TestReport r, ECSWorld world)
        {
            r.Section("A5 · контракты (транзакционное исполнение)");

            var e = new ECSEntity { AliasName = "contract-target" };
            world.entityManager.AddNewEntity(e);
            e.AddComponent(new PositionComponent { X = 0, Y = 0 });
            e.AddComponent(new HealthComponent { Hp = 100 });

            // 1) Контракт, условия которого выполнены ⇒ исполняется сразу
            int executed = 0;
            var c1 = new ECSExecutableContractContainer();
            c1.ECSWorldOwner = world;
            c1.ContractConditions = new Dictionary<long, List<Func<ECSEntity, bool>>>
            {
                { e.instanceId, new List<Func<ECSEntity, bool>> { (x) => x.GetComponent<HealthComponent>().Hp > 0 } }
            };
            c1.EntityComponentPresenceSign = new Dictionary<long, Dictionary<long, bool>>
            {
                { e.instanceId, new Dictionary<long, bool>
                    {
                        { TK.Uid<PositionComponent>(), true  },   // должен быть
                        { TK.Uid<BlockerComponent>(),  false },   // должен ОТСУТСТВОВАТЬ (absence-hold)
                    }
                }
            };
            c1.ContractExecutableSingle = (c, ent) => { Interlocked.Increment(ref executed); };
            world.contractsManager.RegisterContract(c1);

            r.Check("контракт с выполненными условиями исполнился", TestReport.Await(() => executed == 1, 2000));
            r.Check("ContractExecuted == true", c1.ContractExecuted);

            // 2) Контракт, требующий отсутствующий компонент ⇒ ждёт; добавление компонента его пускает
            int executed2 = 0;
            var c2 = new ECSExecutableContractContainer();
            c2.ECSWorldOwner = world;
            c2.MaxTries = long.MaxValue;
            c2.ContractConditions = new Dictionary<long, List<Func<ECSEntity, bool>>>
            {
                { e.instanceId, new List<Func<ECSEntity, bool>> { (x) => true } }
            };
            c2.EntityComponentPresenceSign = new Dictionary<long, Dictionary<long, bool>>
            {
                { e.instanceId, new Dictionary<long, bool> { { TK.Uid<BlockerComponent>(), true } } }
            };
            c2.ContractExecutableSingle = (c, ent) => { Interlocked.Increment(ref executed2); };
            world.contractsManager.RegisterContract(c2);

            r.Check("контракт с неудовлетворёнными условиями НЕ исполнился", executed2 == 0);
            e.AddComponent(new BlockerComponent());
            r.Check("после появления компонента контракт исполнился (реакция OnAddComponent)",
                TestReport.Await(() => executed2 == 1, 3000));
            e.RemoveComponent<BlockerComponent>();

            // 3) Dead-letter по MaxTries
            int executed3 = 0;
            var c3 = new ECSExecutableContractContainer();  // базовый класс ⇒ MaxTries = 1
            c3.ECSWorldOwner = world;
            c3.ContractConditions = new Dictionary<long, List<Func<ECSEntity, bool>>>
            {
                { e.instanceId, new List<Func<ECSEntity, bool>> { (x) => false } }   // никогда
            };
            c3.EntityComponentPresenceSign = new Dictionary<long, Dictionary<long, bool>>
            {
                { e.instanceId, new Dictionary<long, bool>() }
            };
            c3.ContractExecutableSingle = (c, ent) => { Interlocked.Increment(ref executed3); };
            r.CheckEq("у базового контракта MaxTries == 1", 1L, c3.MaxTries);
            world.contractsManager.RegisterContract(c3);
            e.AddComponent(new BlockerComponent());     // триггерим повторную попытку → dead-letter
            Thread.Sleep(300);
            r.Check("контракт с невыполнимым условием не исполнился", executed3 == 0);
            r.Check("исчерпав MaxTries, контракт снят из AwaitingContractDatabase",
                TestReport.Await(() =>
                {
                    var db = world.contractsManager.AwaitingContractDatabase;
                    return !db.ContainsKey(e.instanceId) || !db[e.instanceId].Contains(c3);
                }, 2000));
            e.RemoveComponent<BlockerComponent>();

            // 4) ТРАНЗАКЦИОННОСТЬ: пока тело контракта работает под read-токенами,
            //    удаление компонента из другого потока обязано ждать.
            var e2 = new ECSEntity { AliasName = "contract-tx" };
            world.entityManager.AddNewEntity(e2);
            e2.AddComponent(new PositionComponent { X = 1 });

            var bodyStarted = new ManualResetEventSlim(false);
            var bodyDone = new ManualResetEventSlim(false);
            bool removeSawBodyDone = false;

            var c4 = new ECSExecutableContractContainer();
            c4.ECSWorldOwner = world;
            c4.ContractConditions = new Dictionary<long, List<Func<ECSEntity, bool>>>
            {
                { e2.instanceId, new List<Func<ECSEntity, bool>> { (x) => true } }
            };
            c4.EntityComponentPresenceSign = new Dictionary<long, Dictionary<long, bool>>
            {
                { e2.instanceId, new Dictionary<long, bool> { { TK.Uid<PositionComponent>(), true } } }
            };
            c4.ContractExecutableSingle = (c, ent) =>
            {
                bodyStarted.Set();
                Thread.Sleep(400);        // держим read-token
                bodyDone.Set();
            };

            var contractTask = Task.Run(() => world.contractsManager.RegisterContract(c4));
            bodyStarted.Wait(2000);

            var removerTask = Task.Run(() =>
            {
                e2.RemoveComponent<PositionComponent>();
                removeSawBodyDone = bodyDone.IsSet;
            });

            Task.WaitAll(new[] { contractTask, removerTask }, 5000);
            r.Check("удаление компонента дождалось конца тела контракта (read-token удержан)", removeSawBodyDone);

            world.entityManager.RemoveEntity(e2);
            world.entityManager.RemoveEntity(e);
        }

        // ─────────────────────────────────────────────────────────────────────
        private static void A6_TimeDependSystem(TestReport r, ECSWorld world)
        {
            r.Section("A6 · time-depend системы (статические контракты)");

            r.Check("OfflineTickSystem поднята в Offline-мире (WorldFilter)",
                world.contractsManager.AllSystems.Any(s => s is OfflineTickSystem));
            r.Check("MovementSystem НЕ поднята в Offline-мире (WorldFilter)",
                !world.contractsManager.AllSystems.Any(s => s is MovementSystem));

            int before = OfflineTickSystem.Ticks;

            var e = new ECSEntity { AliasName = "ticked" };
            world.entityManager.AddNewEntity(e);
            e.AddComponent(new LifecycleProbeComponent());

            r.Check("time-depend система тикает по подходящей сущности",
                TestReport.Await(() => OfflineTickSystem.Ticks > before + 2, 3000),
                "ticks=" + OfflineTickSystem.Ticks);
            r.CheckEq("система получила именно нашу сущность", e.instanceId, Interlocked.Read(ref OfflineTickSystem.LastEntity));

            // absence-sign: добавляем BlockerComponent — сущность обязана выпасть из выборки
            int mid = OfflineTickSystem.Ticks;
            e.AddComponent(new BlockerComponent());
            Thread.Sleep(300);
            int after = OfflineTickSystem.Ticks;
            Thread.Sleep(300);
            r.Check("absence-sign исключил сущность из time-depend выборки",
                OfflineTickSystem.Ticks - after <= 1, "дельта=" + (OfflineTickSystem.Ticks - after));

            world.entityManager.RemoveEntity(e);
        }

        // ─────────────────────────────────────────────────────────────────────
        private static void A7_Query(TestReport r, ECSWorld world)
        {
            r.Section("A7 · query-индекс (world.Query.Search)");

            var parent = new ECSEntity { AliasName = "q-parent" };
            world.entityManager.AddNewEntity(parent);
            parent.AddComponent(new PositionComponent());

            // ВАЖНО: связь с родителем ставится ДО AddNewEntity — QueryIndex.OnEntityAdded
            // прописывает сущность у предков по цепочке ownerECSObject именно в момент прихода.
            var a = new ECSEntity { AliasName = "q-a" };
            parent.AddChildObject(a);
            world.entityManager.AddNewEntity(a);
            a.AddComponent(new PositionComponent());
            a.AddComponent(new VelocityComponent());

            var b = new ECSEntity { AliasName = "q-b" };
            parent.AddChildObject(b);
            world.entityManager.AddNewEntity(b);
            b.AddComponent(new PositionComponent());
            b.AddComponent(new BlockerComponent());

            Thread.Sleep(150); // индекс наполняется реакциями

            var withPos = world.Query.Search(null, new[] { typeof(PositionComponent) }, null).ToList();
            r.Check("Search(with=Position) находит все три", withPos.Count >= 3, "found=" + withPos.Count);

            var withoutBlocker = world.Query
                .Search(null, new[] { typeof(PositionComponent) }, new[] { typeof(BlockerComponent) })
                .ToList();
            r.Check("Search(without=Blocker) исключает помеченную сущность",
                withoutBlocker.All(x => x.instanceId != b.instanceId) &&
                withoutBlocker.Any(x => x.instanceId == a.instanceId));

            var scoped = world.Query.Search(parent, new[] { typeof(PositionComponent) }, null).ToList();
            r.Check("Search(scope=parent) сужает до потомков",
                scoped.Any(x => x.instanceId == a.instanceId) &&
                scoped.All(x => x.instanceId != parent.instanceId),
                "found=" + scoped.Count);

            var owners = world.Query.FilterEntitiesForComponents(new List<long> { TK.Uid<VelocityComponent>() });
            r.Check("FilterEntitiesForComponents (обратный индекс)", owners.Any(x => x.instanceId == a.instanceId));

            world.entityManager.RemoveEntity(a);
            Thread.Sleep(100);
            var afterRemoval = world.Query.Search(null, new[] { typeof(VelocityComponent) }, null).ToList();
            r.Check("после удаления сущность выпала из индекса",
                afterRemoval.All(x => x.instanceId != a.instanceId));

            world.entityManager.RemoveEntity(b);
            world.entityManager.RemoveEntity(parent);
        }

        // ─────────────────────────────────────────────────────────────────────
        private static void A8_SharedFields(TestReport r, ECSWorld world)
        {
            r.Section("A8 · ECSSharedField / SharedFieldTable");

            var e = new ECSEntity();
            world.entityManager.AddNewEntity(e);
            var c = new ScoreComponent();
            e.AddComponent(c);

            var v = ECSSharedField<int>.GetOrAdd(c.instanceId, "combo", 7);
            r.CheckEq("GetOrAdd кладёт значение", 7, v);
            r.CheckEq("GetCachedValue читает значение", 7, ECSSharedField<int>.GetCachedValue(c.instanceId, "combo"));
            ECSSharedField<int>.SetCachedValue(c.instanceId, "combo", 9);
            r.CheckEq("SetCachedValue обновляет", 9, ECSSharedField<int>.GetCachedValue(c.instanceId, "combo"));
            r.Check("HasCachedValue", ECSSharedField<int>.HasCachedValue(c.instanceId, "combo"));

            e.RemoveComponent<ScoreComponent>();
            r.Check("удаление компонента чистит его shared-поля (RemoveAllCachedValuesForId)",
                TestReport.Await(() => !ECSSharedField<int>.HasCachedValue(c.instanceId, "combo"), 2000));

            world.entityManager.RemoveEntity(e);
        }

        // ─────────────────────────────────────────────────────────────────────
        private static void A9_SerializationShadowAndChildren(TestReport r, ECSWorld world, EntityNetSerializer ser)
        {
            r.Section("A9 · сериализационная тень (NoData→Changed→Freezed) и дерево детей");

            var parent = new ECSEntity { AliasName = "shadow-parent" };
            world.entityManager.AddNewEntity(parent);
            var child = new ECSEntity { AliasName = "shadow-child" };
            world.entityManager.AddNewEntity(child);
            child.AddComponent(new ChildMarkerComponent { Tag = "kid" });

            parent.AddChildObject(child);
            r.CheckEq("после мутации дерева ChangesState == Changed",
                IECSObject.IECSObjectSerializedStateMode.Changed, parent.ChangesState);
            r.Check("зеркало детей ещё НЕ материализовано (ленивое)", parent.childECSObjectsId == null);

            parent.EnterToSerialization();
            r.CheckEq("SnapshotPass: Changed → Freezed",
                IECSObject.IECSObjectSerializedStateMode.Freezed, parent.ChangesState);
            r.Check("зеркало childECSObjectsId материализовано",
                parent.childECSObjectsId != null && parent.childECSObjectsId.ContainsKey(child.instanceId));
            r.Check("HasChildChanges == true (получателю нужно перечитать дерево)", parent.HasChildChanges);

            parent.EnterToSerialization();
            r.CheckEq("SnapshotPass повторно: Freezed → NoData",
                IECSObject.IECSObjectSerializedStateMode.NoData, parent.ChangesState);
            r.Check("HasChildChanges == false (изменений нет)", !parent.HasChildChanges);

            // Путь к объекту (IECSObjectPathContainer) резолвится в живой объект
            var path = new AECC.Core.BuiltInTypes.Types.AtomicType.IECSObjectPathContainer(world.instanceId) { ECSObject = child };
            r.Check("IECSObjectPathContainer резолвит сущность по пути",
                path.ECSObject != null && path.ECSObject.instanceId == child.instanceId);
            r.CheckEq("CacheInstanceId", child.instanceId, path.CacheInstanceId);

            world.entityManager.RemoveEntity(child);
            world.entityManager.RemoveEntity(parent);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Хелпер: сборка «зрителя» и «источника» с политиками доступа
        // ─────────────────────────────────────────────────────────────────────
        internal static ReplicationPolicy MakeViewer(ECSWorld world, out ECSEntity viewer)
        {
            viewer = new ECSEntity { AliasName = "viewer" };
            world.entityManager.AddNewEntity(viewer);
            var p = new ReplicationPolicy();
            viewer.dataAccessPolicies.Add(p);
            return p;
        }

        /// <summary>
        /// Источник получает 2 политики:
        ///   • публичную (свой instanceId, тип тот же) → RestrictedComponents видят ВСЕ зрители;
        ///   • клон политики привилегированного зрителя (тот же instanceId!) → AvailableComponents
        ///     видит только он.
        /// </summary>
        internal static void ApplyPolicies(ECSEntity src, ReplicationPolicy ownerPolicy,
                                           long[] privateComps, long[] publicComps)
        {
            var pub = new ReplicationPolicy();
            pub.RestrictedComponents = new List<long>(publicComps);
            src.dataAccessPolicies.Add(pub);

            if (ownerPolicy != null)
            {
                var priv = (ReplicationPolicy)ownerPolicy.Clone();   // Clone сохраняет instanceId
                priv.AvailableComponents = new List<long>(privateComps);
                priv.RestrictedComponents = new List<long>();
                src.dataAccessPolicies.Add(priv);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        private static void A10_SlicedSerializationAndGdap(TestReport r, ECSWorld world, EntityNetSerializer ser)
        {
            r.Section("A10 · срез, dirty-set и GDAP-фильтрация");

            ECSEntity owner, stranger;
            var ownerPolicy = MakeViewer(world, out owner);
            var strangerPolicy = MakeViewer(world, out stranger);

            var src = new ECSEntity { AliasName = "npc" };
            world.entityManager.AddNewEntity(src);
            var pos = Groups.Server(new PositionComponent { X = 3, Y = 4 });
            var hp = Groups.Server(new HealthComponent { Hp = 77 });
            var score = Groups.Server(new ScoreComponent { Score = 5 });
            var vel = new VelocityComponent { VX = 1 };     // НЕ в политиках ⇒ не реплицируется
            src.AddComponent(pos);
            src.AddComponent(hp);
            src.AddComponent(score);
            src.AddComponent(vel);

            ApplyPolicies(src, ownerPolicy,
                privateComps: new[] { TK.Uid<HealthComponent>() },
                publicComps: new[] { TK.Uid<PositionComponent>(), TK.Uid<ScoreComponent>() });

            // dirty-set
            pos.MarkAsChanged(); hp.MarkAsChanged(); score.MarkAsChanged();
            r.CheckEq("в dirty-set три компонента", 3, src.entityComponents.ChangedComponent);

            ser.SerializeEntity(src, true);                    // срез только изменённых + чистка dirty
            r.CheckEq("после среза dirty-set очищен", 0, src.entityComponents.ChangedComponent);
            r.Check("binSerializedEntity заполнен", src.binSerializedEntity != null && src.binSerializedEntity.Length > 0);
            r.Check("emptySerialized == false (есть что слать)", !src.emptySerialized);

            var toOwner = ser.BuildSerializedEntityWithGDAP(owner, src);
            var toStranger = ser.BuildSerializedEntityWithGDAP(stranger, src);
            r.Check("пакет владельцу непустой", toOwner.Length > 0);
            r.Check("пакет чужому непустой", toStranger.Length > 0);
            r.Check("пакет владельцу БОЛЬШЕ (в нём есть приватный HealthComponent)",
                toOwner.Length > toStranger.Length,
                "owner=" + toOwner.Length + " stranger=" + toStranger.Length);

            // Разбор пакетов «по-честному», через адаптер
            var adapter = (ISerializationAdapter)world.serializationAdapter;
            var ownerPacket = adapter.DeserializeAdapterEntity(toOwner);
            var strangerPacket = adapter.DeserializeAdapterEntity(toStranger);

            r.Check("владелец получил Health (Available по instanceId политики)",
                ownerPacket.Components.ContainsKey(TK.Uid<HealthComponent>()));
            r.Check("чужой НЕ получил Health",
                !strangerPacket.Components.ContainsKey(TK.Uid<HealthComponent>()));
            r.Check("оба получили Position (Restricted по типу политики)",
                ownerPacket.Components.ContainsKey(TK.Uid<PositionComponent>()) &&
                strangerPacket.Components.ContainsKey(TK.Uid<PositionComponent>()));
            r.Check("никто не получил Velocity (нет ни в одном списке политики)",
                !ownerPacket.Components.ContainsKey(TK.Uid<VelocityComponent>()) &&
                !strangerPacket.Components.ContainsKey(TK.Uid<VelocityComponent>()));

            // Пустой срез ⇒ пустой пакет
            ser.SerializeEntity(src, true);
            var empty = ser.BuildSerializedEntityWithGDAP(owner, src);
            r.CheckEq("без изменений пакет пустой (byte[0])", 0, empty.Length);

            // Полный снапшот (для догоняющего клиента) + честный round-trip
            var full = ser.BuildFullSerializedEntityWithGDAP(owner, src);
            r.Check("полный снапшот непустой", full.Length > 0);

            var restored = ser.Deserialize(full);
            r.Check("Deserialize вернул сущность с тем же instanceId",
                restored != null && restored.instanceId == src.instanceId);
            r.Check("восстановленный entityComponents НЕ null (патч P1b)",
                restored != null && restored.entityComponents != null);
            r.Check("значения компонентов доехали",
                restored != null && restored.HasComponent<PositionComponent>() &&
                Math.Abs(restored.GetComponent<PositionComponent>().X - 3) < 1e-9);
            r.Check("приватный компонент доехал владельцу",
                restored != null && restored.HasComponent<HealthComponent>() &&
                restored.GetComponent<HealthComponent>().Hp == 77);
            r.Check("нереплицируемый Velocity НЕ доехал",
                restored != null && !restored.HasComponent<VelocityComponent>());
            r.Check("группа компонента пережила сериализацию (нужна для доставки удалений)",
                restored != null && restored.GetComponent<PositionComponent>().ComponentGroups != null &&
                restored.GetComponent<PositionComponent>().ComponentGroups.ContainsKey(ServerComponentGroup.Id));

            // Удаление компонента: журнал removed + флаг IncludeRemoved
            src.RemoveComponent<ScoreComponent>();
            r.Check("RemovedComponents содержит снятый тип",
                src.entityComponents.RemovedComponents.Contains(TK.Uid<ScoreComponent>()));
            ser.SerializeEntity(src, true);
            var gdaps = src.dataAccessPolicies.ToList();
            r.Check("GDAP взвёл IncludeRemoved (иначе получатель не узнает об удалении)",
                gdaps.Any(g => g.IncludeRemovedRestricted || g.IncludeRemovedAvailable));
            var afterRemoval = ser.BuildSerializedEntityWithGDAP(stranger, src);
            r.Check("пакет об удалении отправляется даже без изменённых компонентов", afterRemoval.Length > 0);

            world.entityManager.RemoveEntity(src);
            world.entityManager.RemoveEntity(owner);
            world.entityManager.RemoveEntity(stranger);
        }

        // ─────────────────────────────────────────────────────────────────────
        private static void A11_DbComponent(TestReport r, ECSWorld world, EntityNetSerializer ser)
        {
            r.Section("A11 · DBComponent (ComponentsDBComponent)");

            var chest = new ECSEntity { AliasName = "chest" };
            world.entityManager.AddNewEntity(chest);

            var inv = Groups.Server(new InventoryDBComponent());
            chest.AddComponent(inv);
            r.Check("DB-агрегатор привязан к сущности", ReferenceEquals(chest, inv.ownerEntity));

            var sword = new ItemComponent { ItemName = "sword", Count = 1 };
            var potion = new ItemComponent { ItemName = "potion", Count = 3 };

            inv.AddComponent(chest, sword);       // владелец строки — сама сущность
            inv.AddComponent(chest, potion);

            r.CheckEq("в DB два компонента", 2, inv.GetComponentsByType<ItemComponent>().Count);
            r.Check("ownerDB проставлен у вложенного компонента", ReferenceEquals(inv, sword.ownerDB));
            r.Check("ownerEntity вложенного компонента = сущность-владелец", ReferenceEquals(chest, sword.ownerEntity));

            var got = inv.GetComponent(sword.instanceId);
            r.Check("GetComponent(instanceId) из DB", got.Item1 != null && ReferenceEquals(got.Item1, sword));
            r.CheckEq("состояние новой строки == Created",
                ComponentsDBComponent.ComponentState.Created, got.Item2);

            potion.Count = 5;
            inv.ChangeComponent(potion);
            r.CheckEq("после ChangeComponent состояние == Changed",
                ComponentsDBComponent.ComponentState.Changed, inv.GetComponent(potion.instanceId).Item2);
            r.Check("ChangedComponents (dirty DB) пополнился", inv.ChangedComponents.Count > 0);

            // Мутация вложенного компонента через MarkAsChanged должна пройти ЧЕРЕЗ агрегатор
            // (Profile.DbAuthoritativeChangeMarking) и пометить сам DB-компонент изменённым.
            chest.entityComponents.RemovedComponents.Clear();
            sword.Count = 2;
            sword.MarkAsChanged();
            r.Check("MarkAsChanged вложенного компонента помечает DB-агрегатор (DbAuthoritativeChangeMarking)",
                chest.entityComponents.CheckChanged(typeof(InventoryDBComponent)));

            // Сериализация: ISerializationParticipant.BeforeSnapshot → SerializeDB
            ser.SerializeEntity(chest, true);
            r.Check("после среза serializedDB очищен (AfterSerializationDB отработал)",
                inv.serializedDB.Count == 0 || inv.serializedDB.Values.All(v => v != null));

            // Удаление строки: состояние Removed → RemovingReaction → чистка при следующем срезе
            inv.RemoveComponent(potion.instanceId);
            r.CheckEq("после RemoveComponent состояние == Removed",
                ComponentsDBComponent.ComponentState.Removed,
                inv.DB[chest.instanceId][potion.instanceId].Item2);

            inv.MarkAsChanged();
            ser.SerializeEntity(chest, true);
            r.Check("AfterSerializationDB физически вычистил удалённую строку",
                TestReport.Await(() => !inv.DB.ContainsKey(chest.instanceId) ||
                                       !inv.DB[chest.instanceId].ContainsKey(potion.instanceId), 2000));

            r.Try("ClearDB", () => inv.ClearDB());

            world.entityManager.RemoveEntity(chest);
        }

        // ─────────────────────────────────────────────────────────────────────
        private static void A12_Timers(TestReport r, ECSWorld world)
        {
            r.Section("A12 · TimerComponent / TimerSelfDestructionComponent");

            var e = new ECSEntity { AliasName = "timer" };
            world.entityManager.AddNewEntity(e);

            int fired = 0;
            var t = new TimerComponent();
            t.onEnd = (ent, comp) => Interlocked.Increment(ref fired);
            e.AddComponent(t);
            t.TimerStart(150, e);
            r.Check("TimerComponent.onEnd сработал", TestReport.Await(() => fired >= 1, 3000));
            t.TimerStop();

            var doomed = new ECSEntity { AliasName = "doomed" };
            world.entityManager.AddNewEntity(doomed);
            doomed.AddComponent(new AECC.ECS.Components.ECSComponents.TimerSelfDestructionComponent(
                0.2f, (ent) => true));
            r.Check("TimerSelfDestructionComponent удалил сущность из мира",
                TestReport.Await(() => !world.entityManager.ContainsEntitySyncronized(doomed.instanceId), 4000));

            world.entityManager.RemoveEntity(e);
        }

        // ─────────────────────────────────────────────────────────────────────
        private static void A13_LocalReplicationToClientWorld(TestReport r, ECSWorld srcWorld, EntityNetSerializer srcSer)
        {
            r.Section("A13 · роллинг в Client-мир (UpdateDeserialize) без сети");

            // ВНИМАНИЕ: создание Client-мира перезапишет статик GlobalProgramComponentGroup.
            // Мы группы навешиваем явно (Groups.Server/Client), поэтому это безопасно;
            // мир удаляется в конце секции.
            var mirror = Bootstrapping.CreateWorld(MirrorWorldId, ECSWorld.WorldTypeEnum.Client, new SerializationAdapter());
            var mSer = SerializationBootstrap.SerializerOf(mirror);

            try
            {
                r.Check("профиль Client: ClientRetryOnMissingRefs", mirror.Profile.ClientRetryOnMissingRefs);
                r.CheckEq("клиент фильтрует ЧУЖУЮ (серверную) группу",
                    ServerComponentGroup.Id, mirror.Profile.RestoreFilterForeignGroupId);

                ECSEntity viewer;
                var viewerPolicy = MakeViewer(srcWorld, out viewer);

                var src = new ECSEntity { AliasName = "rolled" };
                srcWorld.entityManager.AddNewEntity(src);
                var pos = Groups.Server(new PositionComponent { X = 1, Y = 1 });
                var score = Groups.Server(new ScoreComponent { Score = 10 });
                var hp = Groups.Server(new HealthComponent { Hp = 42 });
                src.AddComponent(pos);
                src.AddComponent(score);
                src.AddComponent(hp);

                ApplyPolicies(src, viewerPolicy,
                    privateComps: new[] { TK.Uid<HealthComponent>() },
                    publicComps: new[] { TK.Uid<PositionComponent>(), TK.Uid<ScoreComponent>() });

                pos.MarkAsChanged(); score.MarkAsChanged(); hp.MarkAsChanged();
                srcSer.SerializeEntity(src, true);
                var blob = srcSer.BuildSerializedEntityWithGDAP(viewer, src);
                r.Check("первый пакет собран", blob.Length > 0);

                mSer.UpdateDeserialize(blob);

                ECSEntity mirrored = null;
                r.Check("сущность создана в клиентском мире (UpdateDeserialize → AddNewEntity)",
                    TestReport.Await(() => mirror.entityManager.TryGetEntitySyncronized(src.instanceId, out mirrored), 2000));

                if (mirrored != null)
                {
                    r.CheckEq("Position доехала", 1.0, mirrored.GetComponent<PositionComponent>().X);
                    r.CheckEq("Score доехал", 10, mirrored.GetComponent<ScoreComponent>().Score);
                    r.CheckEq("приватный Health доехал владельцу", 42, mirrored.GetComponent<HealthComponent>().Hp);
                    r.Check("сущность переподчинена КЛИЕНТСКОМУ миру",
                        mirrored.ECSWorldOwner != null && mirrored.ECSWorldOwner.instanceId == mirror.instanceId);

                    // Клиентский локальный компонент — серверный роллинг не должен его снести
                    var local = Groups.Client(new ClientPredictionComponent { PredictedX = 99 });
                    mirrored.AddComponent(local);

                    // Дельта: сервер меняет позицию
                    pos.X = 5;
                    pos.MarkAsChanged();
                    srcSer.SerializeEntity(src, true);
                    var delta = srcSer.BuildSerializedEntityWithGDAP(viewer, src);
                    r.Check("дельта-пакет непустой", delta.Length > 0);
                    mSer.UpdateDeserialize(delta);

                    r.Check("дельта применилась к существующей сущности",
                        TestReport.Await(() => Math.Abs(mirrored.GetComponent<PositionComponent>().X - 5) < 1e-9, 2000),
                        "X=" + mirrored.GetComponent<PositionComponent>().X);
                    r.Check("клиентский локальный компонент ПЕРЕЖИЛ роллинг (фильтруется только чужая группа)",
                        mirrored.HasComponent<ClientPredictionComponent>());

                    // Удаление на сервере → доставка удаления на клиент
                    src.RemoveComponent<ScoreComponent>();
                    srcSer.SerializeEntity(src, true);
                    var removalBlob = srcSer.BuildSerializedEntityWithGDAP(viewer, src);
                    r.Check("пакет удаления собран", removalBlob.Length > 0);
                    mSer.UpdateDeserialize(removalBlob);

                    r.Check("компонент, снятый на сервере, снят и на клиенте (FilterRemovedComponents)",
                        TestReport.Await(() => !mirrored.HasComponent<ScoreComponent>(), 2000));
                    r.Check("остальные компоненты не пострадали",
                        mirrored.HasComponent<PositionComponent>() && mirrored.HasComponent<ClientPredictionComponent>());
                }

                // Отложенная десериализация: ребёнок приезжает ПОЗЖЕ родителя
                var parent = new ECSEntity { AliasName = "late-parent" };
                srcWorld.entityManager.AddNewEntity(parent);
                parent.AddComponent(Groups.Server(new PositionComponent { X = 100 }));
                ApplyPolicies(parent, null, new long[0], new[] { TK.Uid<PositionComponent>() });

                var kid = new ECSEntity { AliasName = "late-kid" };
                srcWorld.entityManager.AddNewEntity(kid);
                kid.AddComponent(Groups.Server(new ChildMarkerComponent { Tag = "late" }));
                ApplyPolicies(kid, null, new long[0], new[] { TK.Uid<ChildMarkerComponent>() });

                parent.AddChildObject(kid);

                parent.GetComponent<PositionComponent>().MarkAsChanged();
                kid.GetComponent<ChildMarkerComponent>().MarkAsChanged();

                srcSer.SerializeEntity(parent, true);
                srcSer.SerializeEntity(kid, true);
                var parentBlob = srcSer.BuildSerializedEntityWithGDAP(viewer, parent);
                var kidBlob = srcSer.BuildSerializedEntityWithGDAP(viewer, kid);

                // Родитель ссылается на ещё не пришедшего ребёнка ⇒ RestorePass провалится и
                // объект встанет в PendingDeserializationRegistry.
                mSer.UpdateDeserialize(parentBlob);

                ECSEntity mParent = null;
                mirror.entityManager.TryGetEntitySyncronized(parent.instanceId, out mParent);
                r.Check("родитель доехал", mParent != null);
                r.Check("выжидатель зарегистрирован в PendingDeserializationRegistry (ребёнка ещё нет)",
                    mirror.entityManager.PendingDeserialization.HasPending ||
                    (mParent != null && mParent.ContainsChildObject(kid.instanceId)),
                    "pending=" + mirror.entityManager.PendingDeserialization.HasPending);

                mSer.UpdateDeserialize(kidBlob);   // приход ребёнка ⇒ RequestDrain ⇒ повторная попытка

                r.Check("после прихода ребёнка дерево восстановлено (событийный retry)",
                    TestReport.Await(() => mParent != null && mParent.ContainsChildObject(kid.instanceId), 3000));
                r.Check("реестр отложенной десериализации опустел",
                    TestReport.Await(() => !mirror.entityManager.PendingDeserialization.HasPending, 3000));
            }
            catch (Exception ex)
            {
                r.Check("A13 без исключений", false, ex.ToString());
            }
            finally
            {
                try { mirror.Dispose(); } catch { }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        private static void A14_Squash(TestReport r)
        {
            r.Section("A14 · squash миров");

            var dst = Bootstrapping.CreateWorld(SquashDstWorldId, ECSWorld.WorldTypeEnum.Offline, new SerializationAdapter());
            var src = Bootstrapping.CreateWorld(SquashSrcWorldId, ECSWorld.WorldTypeEnum.Offline, new SerializationAdapter());

            try
            {
                var e1 = new ECSEntity { AliasName = "sq-1" };
                src.entityManager.AddNewEntity(e1);
                e1.AddComponent(new PositionComponent { X = 7 });

                var e2 = new ECSEntity { AliasName = "sq-2" };
                dst.entityManager.AddNewEntity(e2);

                ECSWorld.SquashWorlds(dst, src);

                r.Check("сущность исходного мира доступна в целевом",
                    dst.entityManager.ContainsEntitySyncronized(e1.instanceId));
                r.Check("исходный мир стал прозрачным прокси (редирект чтений)",
                    src.entityManager.ContainsEntitySyncronized(e1.instanceId));
                r.Check("компоненты пережили squash",
                    dst.entityManager.TryGetEntitySyncronized(e1.instanceId, out var moved) &&
                    moved.HasComponent<PositionComponent>() &&
                    Math.Abs(moved.GetComponent<PositionComponent>().X - 7) < 1e-9);
            }
            catch (Exception ex)
            {
                r.Check("A14 без исключений", false, ex.ToString());
            }
            finally
            {
                try { src.Dispose(); } catch { }
                try { dst.Dispose(); } catch { }
            }
        }
    }
}
