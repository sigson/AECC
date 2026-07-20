# AECC — карта кодовой базы (ECS-ядро + фреймворк)

> Составлено по срезу архива `AECC.zip` (177 .cs вне `AECC_Packages`, ~44k строк).
> Документ — рабочая карта: что где лежит, кто кем владеет, какие инварианты держатся,
> и **список найденных блокирующих дефектов** (раздел 10) с патчами (`patches/apply_patches.py`).

---

## 1. Состав решения

| Проект | TFM | Роль |
|---|---|---|
| `AECC_Core` | netstandard2.0 | Ядро ECS: мир, сущности, компоненты, контракты, локи, сериализационный пайплайн, query-индекс |
| `AECC_CoreFramework` | netstandard2.0 | Прослойка «движка»: `IService`/`SGT`/`ProxyBehaviour`, GameEngineAPI (Unity/Godot заглушки), атомарные типы |
| `AECC_Framework` | netstandard2.0 | Прикладной харнесс: сеть (NetCoreServer-адаптеры, `NetworkEvent`, identity, ping, RPC), NetSerializer, SQLite-провайдер, сервисы (Auth/DB/Constant/Malicious/GlobalProgramState) |
| `AECC_Packages` | netstandard2.0 | Вендор-код: **форк NetCoreServer** (API сохранён), YamlDotNet |
| *(в .sln, отсутствуют в архиве)* | | `AECC_TestConsolePerfomance`, `AECC_TestClient`, `AECC_TestServer`, `AECC.Locking` |

Зависимости: `Framework → CoreFramework → Core`, `Framework → Packages`.

---

## 2. Ядро ECS (`AECC_Core/Core`)

### 2.1 Иерархия объектов

```
IDObject                      (идентичность: instanceId, ECSWorldOwnerId, GetId() из [TypeUid])
 ├── IECSObject               (+ дерево детей, serialization shadow, SerialLocker)
 │    ├── ECSEntity   [TypeUid(2)]
 │    └── ECSComponent [TypeUid(3)]
 │         ├── TimerComponent [10]
 │         └── DBComponent    [11] ──► ComponentsDBComponent [12]
 ├── ECSComponentGroup [6]  ──► ServerComponentGroup [7] / ClientComponentGroup [8]
 ├── GroupDataAccessPolicy [17]   (GDAP)
 └── BaseCustomType ──► IECSObjectPathContainer [105], DeterministicContainer [106]
```

* **`[TypeUid(int)]` — единственный источник идентичности типа.** `TypeRegistry.Global`
  (`Core/TypeRegistry.cs`) при `InitSerialize` сканирует рефлексией всех наследников `IDObject`,
  прописывает `static Id` через backing-field и заполняет карты `id⇄Type⇄name`.
  `IDObject.GId<T>()` — статик-кэш горячего резолва.
* `instanceId` — `Guid → long` (уникален на объект, не на тип).

### 2.2 Мир (`ECSWorld`, `WorldProfile`, `WorldRegistry`)

Явный lifecycle: **Create → Configure → Start → [Squash] → Dispose**.

* `Configure()` — профиль, реестр (`WorldRegistry.Default`), создание `entityManager` /
  `componentManager` / `contractsManager` + `InitializeSystems()`.
* `Start()` — запуск таймера time-depend контрактов через `IScheduler`
  (`Profile.TimeDependContractsIntervalMs`, дефолт 5 мс).
* `InitWorldScope(filter)` — фасад Configure+Start.
* `ECSWorld.GetWorld` — статический `Func<long, ECSWorld>`, по умолчанию читает `WorldRegistry.Default`.
  **`ECSService.InitializeProcess()` подменяет его на create-on-miss-фабрику своего `WorldDB`** —
  это важная точка: мир, созданный вручную, после инициализации сервисов перестаёт резолвиться
  (см. §10.6).

`WorldProfile` (`Core/WorldProfile.cs`) — единственное место, где разведено поведение Server/Client/Offline:

| Флаг | Server | Client | Offline |
|---|---|---|---|
| `MaintainsSerializationMirrors` | ✔ (если не `Defines.CutClientServerCollections`) | ✔ | ✔ |
| `ClientRetryOnMissingRefs` (событийный retry десериализации) | ✘ | **✔** | ✘ |
| `IdentityKeyedLifecycleState` (lifecycle-состояние в `SharedFieldTable`, переживает подмену инстанса) | ✘ | **✔** | ✘ |
| `AlwaysUpdatePathCache` | ✘ | **✔** | ✘ |
| `ClientComponentGroups` | ✘ | **✔** | ✘ |
| `DbAuthoritativeChangeMarking` | ✔ | ✘ | ✔ |
| `ServerMarksChangedOnAdd` (`OnAdded` ⇒ `MarkAsChanged`) | **✔** | ✘ | ✘ |
| `RestoreFilterForeignGroupId` | Client(8) | Server(7) | Client(8) |

`ECSWorldSquash.SquashWorlds(target, sources…)` — слияние миров; исходные становятся прозрачными
прокси (редирект в `EntityRepository`/`RedirectingEntityRepository`).

### 2.3 Хранение сущностей и компонентов

* `ECSEntityManager` — фасад над `EntityRepository` (хранение + событие прихода/ухода) и
  `RedirectingEntityRepository` (вход squash-редиректа). Слушатель `IEntityRepositoryListener`.
  * `AddNewEntity(e, silent)` → `PrepareForThisWorld` (проставляет `manager`, `ECSWorldOwner`) →
    публикация → `AddNewEntityReaction`: `QueryIndex.OnEntityAdded`, асинхронно
    `RegisterAllComponents()` + `contractsManager.OnEntityCreated`, затем
    `PendingDeserialization.RequestDrain()`.
  * `RemoveEntity` → `EntityRemoved`: снятие узла из индекса, **переподчинение детей вверх**
    (`ReparentChildrenUpwards`), `entity.OnDelete()`, контрактная реакция (под гейтом
    `HasEntityReactions`).
* `EntityComponentStorage` (пер-сущностный оркестратор) поверх `ComponentStore` (чистое хранение +
  транзакционная матрица + absence-holds) поверх `ComponentBag<ECSComponent>` (массив ячеек,
  инлайн-RW-лок в ячейке; ключ — `[TypeUid]`, т.е. **один компонент данного типа на сущность**).
* Side-effects вынесены в `IComponentStoreListener` (реализует `EntityComponentStorage`):
  `ComponentAdded / ComponentChanged / ComponentMarkedChanged / ComponentRemoved` — вызываются
  **под write-локом ячейки, после мутации словаря**; пользовательские реакции (`AddedReaction` и т.п.)
  — уже вне лока.

### 2.4 Транзакционная матрица (то, что тестируется как «блокирующие операции»)

Уровень `EntityComponentStorage` / `ECSEntity`:

| API | Семантика |
|---|---|
| `AddComponentImmediately / AddOrChangeComponentImmediately / ChangeComponent / MarkComponentChanged / RemoveComponentImmediately` | атомарные операции над ячейкой |
| `ExecuteReadLockedComponent(type, act)` / `ExecuteWriteLockedComponent` | исполнение под read/write локом ячейки |
| `ExecuteReadLockedComponent<T1..T6>` / `ExecuteWriteLockedComponent<T1..T6>` | вложенный захват нескольких компонентов |
| `GetReadLockedComponent / GetWriteLockedComponent(out RWToken)` | «долгий» холд с ручным `Dispose` |
| `HoldComponentAddition(type, out token)` | **absence-hold**: держит слот ПУСТЫМ (add другого потока блокируется) |
| `ExecuteOnNotHasComponent(type, act)` / `ExecuteHoldComponent<T…>` | исполнение при гарантированном отсутствии |
| `GetWriteLockedComponentStorage()` / `EnterLockdown()` / `ExitLockdown()` | лок/локдаун всего хранилища (используется в `OnEntityDelete`) |
| `StabilizationGate` (RWLock уровня сущности) | write — мутации DB-агрегатора; read — сериализация среза сущности |

**Важно:** режим конкуренции фиксируется при конструировании хранилища из
`KernelRuntime.DefaultMode`. Дефолт — `SingleThread` (`Defines.OneThreadMode == true`), в котором
локи — `MockReaderWriterLockSlim` (no-op). Для реальных транзакций нужно выставить
`Defines.OneThreadMode = false` **до** создания миров/сущностей.

### 2.5 Контракты (системы)

`ECSExecutableContractContainer` — и «система», и «одноразовый контракт»:

* `Spec` (декларация): `ContractConditions` (entityId → предикаты), `EntityComponentPresenceSign`
  (entityId → {componentTypeId → должен ли присутствовать}), `ContractExecutable` /
  `ContractExecutableSingle`, `ErrorExecution`, `WorldFilter`, `TimeDependExecution`,
  `RemoveAfterExecution`, `MaxTries`, `AsyncExecution`, `DelayRunMilliseconds`,
  `PartialEntityFiltering`, `NotAllIncludedEntitiesPresenceSign`, `ManualExitFromWorkingState`.
* `Runtime`: `NowTried`, `InWork`, `InProgress`, `ContractExecuted`, `LastEndExecutionTimestamp`.
* `TryExecuteContract` → `AcquireContractTargets`:
  * **MT-режим**: присутствие ⇒ `GetReadLockedComponent` (read-token), отсутствие ⇒
    `HoldComponentAddition` + re-check ⇒ **исполнение тела идёт под удержанными токенами**
    (транзакционность), при провале — откат всех токенов.
  * **ST-режим**: только сверка `HasComponent`, без токенов.
  * Строгая финализация: если хоть одна сущность не прошла и это не time-depend —
    контракт не исполняется вовсе.
* `ECSContractsManager`:
  * `InitializeSystems()` — рефлексией поднимает **все** наследники `ECSExecutableContractContainer`
    (кроме отфильтрованных `staticContractFiltering` и `WorldFilter`), для каждого мира.
  * `AwaitingContractDatabase` (entityId → контракты) — реакция на Created/ComponentAdded/Removed.
  * `TimeDependContractEntityDatabase` — тикается `RunTimeDependContracts()` из таймера мира.
  * `MaxTries` исчерпан ⇒ dead-letter (`RemoveContract` + лог со стеком рождения, если включён
    `CaptureGenerationStackTrace`).

### 2.6 Query (`AECC_Core/Query`)

`EntityQueryIndex` (герметизированный DefaultEcs) + `MetricIndex` (MVCC) + `GraphNodeStore`.
Монтаж — `QueryBootstrap.Attach(world)` (или `AECC.Runtime.Bootstrap.AttachRuntime`).
API: `world.Query.Search(scope, with[], without[])` — AND/AND-NOT + сужение до потомков `scope`;
`FilterEntitiesForComponents`, `ComponentOwnersView` (обратный индекс).

### 2.7 Прочее ядро

* `ECSSharedField<T>` + `SharedFieldTable` — identity-keyed (worldId, instanceId) поля,
  переживают подмену инстанса компонента при `UpdateDeserialize` (клиент).
* `ComponentLifecycleDispatcher` — очередь Add→Change→Remove; для «листовых» компонентов без
  переопределённых хуков реакции идут инлайн (fast-path, без планировщика).
* `Kernel.Locking` — `RWLock`, `RWCell`, `RWToken`, `ComponentBag`, `LockedDictionarySlim`,
  `SharedLock`, `KernelRuntime` (`ConcurrencyMode`), `EscapeDiagnostics`.
* `Kernel.Collections` — `DictionaryWrapper`, `ConcurrentHashSet`, `SynchronizedList`, RoaringBitmap.
* `Legacy/` — не используется ядром (исторические словари/локи).

---

## 3. Сериализация (`AECC_Core/Serialization` + `Core/Serialization`)

### 3.1 Слои

```
EntitySerializer (abstract)          ← статические карты типов, InitSerialize, SerializedEntity
  └── EntityNetSerializer            ← реальная реализация
        ↕ ISerializationAdapter      ← AECC_Framework/Harness/Serialization/SerializationAdapter
                                        (NetSerializer или JSON при Defines.AOTMode)
```

Монтаж: `SerializationBootstrap.Attach(world, adapter)` → создаёт `EntityNetSerializer`,
зовёт `InitSerialize` (рефлексия + `TypeRegistry`), кладёт в `world.EntityWorldSerializer`.
Фабрика адаптера по умолчанию задаётся в `GlobalProgramState.InitializeProcess()`.

### 3.2 Пер-объектное состояние

* `SerializationShadow` (opaque-слот `IECSObject.serializationShadow`) — автомат
  **NoData → Changed → Freezed**, счётчик retry, `SnapshotPass` (материализация зеркала детей
  `childECSObjectsId`), `RestorePass` (восстановление дерева), `AfterRestore` (профильная ветка
  с событийным retry).
* `EntitySerializationState` (opaque-слот `ECSEntity.serializationState`) — dirty-set
  (`ChangedComponents`), `RemovedComponents`, `BinSerializedEntity`, `EmptySerialized`.
* `PendingDeserializationRegistry` — событийная замена ретрай-таймеров: объект, которому не хватило
  ещё не пришедшей сущности, регистрирует повтор; слив — при `AddNewEntityReaction`/`OnAddComponent`
  (`RequestDrain`, коалесинг через CAS, протокол двойных проверок «register-then-recheck»).

### 3.3 Хуки

* Пользовательские: `EnterToSerializationImpl` / `AfterSerializationImpl` / `AfterDeserializationImpl`.
* `ISerializationParticipant` (реализует `DBComponent`): `BeforeSnapshot` / `AfterSnapshot` /
  `AfterRestore(clientRetry)`.

### 3.4 GDAP — кто что видит

`GroupDataAccessPolicy` (на сущности — `entity.dataAccessPolicies`, **[NonSerialized]**, т.е. чисто
серверная сторона):

* `AvailableComponents` — «приватный» набор (для получателя, у которого есть политика **с тем же
  `instanceId`**);
* `RestrictedComponents` — «публичный» набор (для получателя с политикой **того же типа**, но другим
  `instanceId`);
* `Bin*Components` — заполняются в `SerializeEntity()` бинарями свежего среза;
* `IncludeRemoved*` — флаги доставки удалений.

Пайплайн раскатки (сервер):

```
serializer.SerializeEntity(entity, serializeOnlyChanged: true)   // срез + заполнение GDAP-бинов, чистка dirty
byte[] blob = serializer.BuildSerializedEntityWithGDAP(toEntity: recipient, fromEntity: entity)
// blob.Length == 0 ⇒ получателю ничего не положено
→ UpdateEntitiesEvent { Entities = [blob…], WorldOwnerId = worldId, Destination = socket.CachedDestination }
```

Приём (клиент): `UpdateEntitiesEvent.Execute()` → `EntityNetSerializer.UpdateDeserialize(blob)`:

1. распаковка конверта, `DeserializeECSEntity`;
2. нет такой сущности ⇒ добавляем кандидата (`AddNewEntity(silent:true)`) под её
   `StabilizationGate.WriteLock`, восстанавливаем компоненты, `AddNewEntityReaction`;
3. есть ⇒ под write-гейтом: **`FilterRemovedComponents`** (по пришедшему
   `fastEntityComponentsId` и «чужой» группе из профиля) → `AddOrChangeComponentWithOwnerRestoring`
   для каждого пришедшего компонента → `AfterRestore` участников → `AfterDeserialization`.

> Следствие: **удаление компонента доезжает до клиента только если компонент помечен «чужой»
> группой** (на сервере — `ServerComponentGroup`, т.е. `component.SetGlobalComponentGroup()`).
> Компоненты без группы клиент никогда не вычищает — на этом держится клиентская локальная логика
> (client-only компоненты не сносятся серверным роллингом).

### 3.5 NetSerializer (форк, `AECC_Framework/Extensions/NetSerializer`)

* Идентификатор типа на проводе = **CRC32 от `Type.ToString()` с вырезанными namespace’ами**
  корневых типов. ⇒ Клиент и сервер обязаны иметь **одинаковый набор `IDObject`-типов и
  namespace’ов**, иначе id разъедутся. (Отсюда требование к тестовым проектам: общие типы —
  в общих файлах с общими namespace’ами.)
* Экземпляр строится в `SerializationAdapter.InitializeAdapterCache(types)` →
  `NetSerializer.Serializer.Default = new Serializer(types)`.
* Десериализация: `FormatterServices.GetUninitializedObject` + пофайловая простановка полей,
  затем `ReflectionCopy.MakeReverseShallowCopy` — **переинициализирует только те поля, у которых
  стоят ОБА атрибута `[NonSerialized]` и `[IgnoreDataMember]`** (см. §10.1 — тут баг).

---

## 4. Харнесс сервисов (`IService` / `SGT` / `ProxyBehaviour`)

* `SGT` — типизированный синглтон (`instances` по имени типа), `InitalizeSingleton`, `Get<T>()`,
  `DestroySGT`.
* `IService : SGT` — сервис с **шаговой инициализацией** и кросс-сервисными барьерами:
  * `GetInitializationSteps()` → `Action<int>[]` (шаги);
  * `SetupCallbacks(allServices)` → `RegisterCallback(targetServiceId, targetStep, condition, cb, authorBlockingStep)`
    — «не пускать сервис X на шаг N, пока мой callback не завершён»;
  * Freeze/Unfreeze (`FreezeCurrentService` / `UnfreezeCurrentService`) — заморозка шага до внешнего
    события (используется `ConstantService` на клиенте: ждёт конфиг с сервера);
  * `ServiceSynchronizationManager` — event-loop (очередь событий + монитор), сигналы
    `OnServiceStepChanged/Failed/Completed/AllServicesCompleted/Frozen/Unfrozen`.
* Порядок запуска: `IService.RegisterAllServices(exclude)` (рефлексия по всем наследникам) →
  настройка полей сервисов → `IService.InitializeAllServices()`.

### Готовые сервисы

| Сервис | Что делает |
|---|---|
| `GlobalProgramState` | `ProgramType` (Server/Client/Offline), пути (`GameDataDir`, `GameConfigDir`, `TechConfigDir`), дефолтные конфиги, `PlayerEntityId`, `ClientNetworkGameDestination`; **регистрирует фабрику `SerializationBootstrap.GetSerializationAdapter`** |
| `ECSService` | Реестр миров (`WorldDB`), `GetWorld(id)` (create-on-miss), `GetWorldAndEntity`; **подменяет `ECSWorld.GetWorld`** |
| `ConstantService` | Загрузка json/yaml-конфигов в `ConstantDB`, сервер — зип конфигов + хеш; клиент — `ConfigCheckEvent` → `ConfigCheckResultEvent` → распаковка, **freeze до получения конфига** |
| `DBService` | Читает `DataBase/DBPath`, `DataBase/DBType` из `baseconfig`, поднимает `IDBProvider` (**только под `#if NET && !GODOT`** — см. §10.3) |
| `AuthService` | `AuthProcess(ClientAuthEvent)` / `RegistrationProcess(ClientRegistrationEvent)`; делегаты `AuthorizationRealization` (UserDataRow → ECSEntity) и `SetupAuthorizationRealization`; карты `SocketToEntity`/`EntityToSocket`; шлёт `UserLoggedEvent` / `AuthActionFailedEvent` |
| `NetworkService` | Обёртка `IService` над `NetworkingInstance` (+ мульти-инстансы) |
| `NetworkMaliciousEventCounteractionService` | Периодическое затухание malicious-score |
| `LogDumpService` | Дамп логов |

`IDBProvider` (`Harness/Model/IDBProvider.cs`) + `UserDataRowBase` (Username/Password/Email/
EmailVerified/HardwareId/…/`GameDataPacked`). Реализация — `SQLiteDefaultDBProvider`
(Microsoft.Data.Sqlite; таблицы Users/News/Logs/Invites/Friends).

---

## 5. Сеть (`AECC_Framework/Network`)

```
NetworkingInstance  (самодостаточный «узел»: сокеты, буферы, identity, ping, RPC, EventManager)
  ├── IServerAdapter / ISocketAdapter          ← NetCoreServerAdapters.cs (TCP/UDP/WS/WSS/HTTP/HTTPS + Godot WS)
  ├── SocketIdentityManager                    ← AssignId/ConfirmId/RestoreId/RestoreAccepted, очередь пакетов до Ready
  ├── OutboundBufferHub                        ← hot(level0)/batch(level1), автоконнект, флаш по возрасту/объёму
  ├── PingService                              ← Ping/Pong, LatencyMs, OnPingTimeout
  ├── RpcBridge                                ← StreamJsonRpc поверх кадра 0x20
  └── EventManager                             ← malicious-score, CheckPacket, роутинг: сеть vs Execute()
```

* **Кадрирование** (`MessageFraming.cs`): TCP — `[len:int32][type:byte][payload]`
  (`StreamFrameAccumulator`); WS/UDP — `[type:byte][payload]` (`DatagramFrame`).
  Типы: `0x01 Event`, `0x10..0x13` identity, `0x14/0x15` ping/pong, `0x20` RPC.
* **`NetworkEvent`** — база всех событий:
  * поля-конверта `InstanceId/EntityOwnerId/WorldOwnerId` (`[Key(0..2)]`);
  * `Destination` / `Destinations` (если заданы ⇒ **уходит в сеть, `Execute()` локально НЕ зовётся**);
    если не заданы ⇒ `Execute()` немедленно (это же и путь входящего пакета);
  * `BufferLevel` (0 hot / 1 batched), `CachedSerializedData`, `CheckPacket()`, `NetworkScoreBooster()`.
  * Идентификация на проводе: `NetworkEventEnvelope{TypeId, InstanceId, EntityOwnerId, WorldOwnerId, Payload}`,
    `TypeId` — из `[TypeUid(N)]`; реестр — `NetworkEventRegistry` (скан сборок).
  * Сериализация — **MessagePack** (`NetworkSerialization`, `StandardResolver + UntrustedData`).
* Системные события сокетов: `SocketConnectedEvent(9001)` / `SocketReconnectedEvent(9002)` /
  `SocketDisconnectedEvent(9003)` + статические хуки.
* Прикладные события фреймворка: `UpdateEntitiesEvent(15)`, `ClientDisconnectedEvent(17)`,
  `ConfigCheckEvent(19)` / `ConfigCheckResultEvent(20)`, `ClientAuthEvent(21)`,
  `ClientRegistrationEvent(22)`, `UserLoggedEvent(25)`, `AuthActionFailedEvent(26)`,
  `IsUsernameAvailableEvent(27)`.
* Компоненты фреймворка: `SocketComponent(23)`, `UsernameComponent(24)`, `UserEmailComponent(28)`,
  `TimerSelfDestructionComponent(13)`.

---

## 6. Итоговая модель клиент-сервер (как задумано)

```
СЕРВЕР (авторитет)                                  КЛИЕНТ
──────────────────                                  ──────
world(Server profile)                               world(Client profile)
 ├ системы (контракты) считают состояние             ├ UpdateEntitiesEvent.Execute()
 ├ MarkAsChanged на изменённых компонентах            │   → UpdateDeserialize(blob)
 ├ tick: SerializeEntity(only changed)                │   → сущности/компоненты применяются
 ├ BuildSerializedEntityWithGDAP(recipient, entity)   ├ клиентская логика (свои компоненты,
 └ UpdateEntitiesEvent ──────► (роллинг) ───────────► │   ClientComponentGroup — не сносятся фильтром)
                                                      └ бизнес-события ──► (только события!) ──► сервер
◄──────────────── ClientCommandEvent / ClientAuthEvent / ...
```

Клиент **никогда** не роллит сущности на сервер — только `NetworkEvent`'ы.

---

## 7. Точки расширения

* Новый компонент: `[System.Serializable] [TypeUid(N)] class X : ECSComponent` + parameterless ctor.
* Новая система: `class S : ECSExecutableContractContainer` + `Initialize()` (задать
  `ContractConditions`, `EntityComponentPresenceSign`, `WorldFilter`, `TimeDependExecution`).
* Новое сетевое событие: `[MessagePackObject] [TypeUid(N)] class E : NetworkEvent` + `[Key(10+)]`
  на полях + `Execute()`.
* Новый сервис: `class S : IService` (авто-подхват в `RegisterAllServices`).
* Новая БД: наследник `IDBProvider`.

---

## 8. Глобальные флаги (`Defines`)

`OneThreadMode` (→ `KernelRuntime.DefaultMode`, **дефолт SingleThread**), `ThreadsMode`,
`AOTMode` (JSON вместо NetSerializer), `CutClientServerCollections`, `TrackRemovedComponents`,
`TimerMinimumMSTick`, набор логирующих флагов, `IgnoreNonDangerousExceptions`.

---

## 9. Инварианты, которые легко сломать

1. **Режим конкуренции фиксируется при конструировании** структур (`ComponentBag`, `RWLock`).
   Менять `Defines.OneThreadMode` после создания миров бесполезно.
2. `ECSComponentManager.GlobalProgramComponentGroup` — **статик**, перезаписывается конструктором
   последнего созданного мира. Server- и Client-мир в одном процессе конфликтуют.
3. Порядок локов: `StabilizationGate` → `lock(serializedDB)` → `SerialLocker`.
4. `RequestDrain()` обязан вызываться **после** публикации сущности в хранилище.
5. Один компонент данного типа на сущность (ключ ячейки — type-uid).
6. GDAP-политику нельзя шарить объектом между сущностями (бины перетираются) — только `Clone()`
   (сохраняет `instanceId`, обнуляет бины).

---

## 10. НАЙДЕННЫЕ ДЕФЕКТЫ (блокируют сетевой цикл)

> Все правятся скриптом `patches/apply_patches.py` (идемпотентно, с отчётом).

### 10.1 [BLOCKER] `ReflectionCopy.MakeReverseShallowCopy` не восстанавливает половину `[NonSerialized]`-полей
`ReflectionExtensions.cs` требует **одновременно** `[NonSerialized]` **и** `[IgnoreDataMember]`.
Поля, у которых стоит только `[NonSerialized]`, после десериализации остаются `null`:

| Поле | Последствие |
|---|---|
| `IECSObject.SerialLocker` | `SharedLock.Scope(null)` в `SerializationShadow.AfterRestore` ⇒ NRE на каждом входящем пакете (в MultiThread) |
| `ECSEntity.entityComponents` | NRE в `EntityNetSerializer.Deserialize/UpdateDeserialize` (`bufEntity.desEntity.entityComponents`) |
| `ECSEntity.dataAccessPolicies` | NRE в `ECSEntity.OnDelete()` |
| `TimerComponent.componentTimer` | NRE при любом обращении к таймеру пришедшего компонента |

**Патч:** добавить `[IgnoreDataMember]` к этим четырём полям (тогда `MakeReverseShallowCopy`
переинициализирует их значениями свежего инстанса).

### 10.2 [BLOCKER] 8 сетевых событий не помечены `[MessagePackObject]`
`ClientAuthEvent(21)`, `ClientRegistrationEvent(22)`, `UserLoggedEvent(25)`,
`AuthActionFailedEvent(26)`, `IsUsernameAvailableEvent(27)`, `ConfigCheckEvent(19)`,
`ConfigCheckResultEvent(20)`, `ClientDisconnectedEvent(17)` имеют `[Key(N)]`, но не имеют
`[MessagePackObject]` ⇒ `MessagePackSerializer.Serialize` бросает
`FormatterNotRegisteredException`. Ломается **весь** цикл регистрации/логина и обмен конфигом.
**Патч:** добавить `[MessagePackObject]`.

### 10.3 [BLOCKER] SQLite-провайдер вырезан препроцессором
`SQLiteDefaultDBProvider.cs` и тело `DBService.InitializeProcess`/`UserDataRowBase.DBUnpack`
обёрнуты в `#if NET && !GODOT`, а `AECC_Framework` таргетит **netstandard2.0**, где `NET` не
определён ⇒ провайдер не компилируется, `DBService.DBProvider == null`.
**Обход в тестах:** серверный проект (net8.0) поставляет свой `IDBProvider` и присваивает его
`DBService.instance.DBProvider` (см. `AECC_TestServer/SqliteDbProvider.cs`).
**Правильная правка:** мультитаргет `netstandard2.0;net8.0` либо `<DefineConstants>NET</DefineConstants>`.

### 10.4 SQL-инъекции
`SQLiteDefaultDBProvider`: `LoginCheck/UsernameAvailable/EmailAvailable/GetUserVia*/Set*` строят SQL
конкатенацией. Частично прикрыто `CheckPacket()` (только буквы/цифры), но `SetEmail`, `SetUsername`,
`GetUserViaEmail` вызываются и из кода без валидации. Параметризовать.

### 10.5 Прочее (не блокирует, но стоит знать)
* `ECSContractsManager.OnEntityDestroyed` пишет `NLogger.LogError("core system error")`, если у
  сущности не было time-depend-подписок — шумит на любом удалении.
* `EntityComponentStorage.FilterRemovedComponents` мутирует `Store` во время `foreach` по нему
  (спасает снапшот в `ComponentStore.GetEnumerator`, но это хрупко).
* `AuthService.AuthorizationProcess` делает `x.GetComponent<UsernameComponent>().Username` для
  **всех** значений `SocketToEntity` — NRE, если у какой-то сущности компонента нет.
* `NetworkEvent.GetSerializedPacket()` кэширует байты — переиспользование инстанса события после
  мутации полей отправит старые байты.
* `SerializedEntity.adapter` — публичное сериализуемое поле интерфейсного типа (работает только
  потому, что при отправке оно `null`).

### 10.6 Резолв мира после инициализации сервисов
`ECSService.InitializeProcess()` перекрывает `ECSWorld.GetWorld` на create-on-miss своей `WorldDB`.
Мир, созданный приложением напрямую, после этого **не резолвится по id** (вместо него создастся
пустой Offline-мир). В тестах после `InitializeAllServices()` мы возвращаем резолвер на
`WorldRegistry.Default`.

---

## 11. Что покрывает тест-кит

См. `TESTKIT_README.md`. Кратко: `AECC_TestServer` (фаза A — локальная батарея ECS-ядра, фаза B —
авторитарный сервер) + `AECC_TestClient` (фаза C — полный сетевой цикл), общие типы — в
`AECC_TestShared` (линкуются в оба проекта, чтобы совпали namespace’ы и CRC-идентификаторы
NetSerializer).
