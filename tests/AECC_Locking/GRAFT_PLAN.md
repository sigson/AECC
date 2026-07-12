# AECC — план графта ядра локера в полное ядро (AECC_Core)

Цель: заменить per-cell `ReaderWriterLockSlim` на упакованный CAS-движок, **сохранив идею
блокировки отдельного компонента отдельной сущности с разделением read/write**, и снять память,
которую съедают объекты локов и «жадные» поля. Бэк-совместимость не требуется.

Графт **поэтапный и компилируемый после каждой фазы** — не «большой взрыв».

---

## Фаза 0 — встроить модуль и ввести абстракцию токена

1. Добавить проект `AECC.Locking` (или влить файлы в неймспейс ядра).
2. Заменить тип токена в контракте на значимый `RWToken`.
   В `Collections/ILockedDictionary.cs` тип токена сейчас `RWLock.LockToken`. Меняем сигнатуры на
   `RWToken` (и `out RWToken` где возвращался токен).
3. Сделать **механическую замену типа** по дереву (везде, где фигурирует токен ячейки):

   | Было | Стало |
   |---|---|
   | `RWLock.LockToken` / `WriteLockToken` / `ReadLockToken` | `RWToken` |
   | `using (var t = dict.TryGetLockedElement(...))` | без изменений — `RWToken` тоже `IDisposable` |
   | `token?.Dispose()` | `token.Dispose()` (struct, не nullable; `default(RWToken)` сам no-op) |

   Места употребления (из анализа): `Core/EntityComponentStorage.cs`, `Core/Entity.cs`,
   `Core/IECSObject.cs`, и все вызовы `Get(Read|Write)LockedComponent*`.

> После Фазы 0 проект должен компилироваться, ещё на старом `LockedDictionary`, но уже с `RWToken`
> в сигнатурах (старый `RWLock` пока возвращает токен, обёрнутый в `RWToken` — временный шим,
> либо сразу переходим к Фазе 1).

---

## Фаза 1 — словари мира → `LockedDictionarySlim` (низкий риск, основной выигрыш по памяти)

Заменить тип у **словарей уровня мира**, где живут миллионы ячеек:

| Где (из анализа) | Поле | Замена |
|---|---|---|
| `Core/EntityManager.cs` | `EntityStorage : LockedDictionary` | `LockedDictionarySlim<long, ECSEntity>` |
| `Core/IECSObject.cs` | `childECSObjects : LockedDictionary` | `LockedDictionarySlim<long, IECSObject>` |
| Сериализация | `SerializationContainer`, `PreinitializedEntities` | `LockedDictionarySlim<...>` |

`LockedDictionarySlim` повторяет контракт построчно: `TryAddOrChange/TryRemove/
TryGetLockedElement/HoldKey/ExecuteOn*Locked/Unsafe*/LockStorage/EnterLockdown`. Семантика
`GlobalLocker.ReadLock` на время поиска + лок ячейки на удержание — сохранена.

**Удалить второй зеркальный async-словарь** `EntityStorageAsync : LockedDictionaryAsync` (Фаза 3
снимает async целиком; здесь — перестать его наполнять).

> Один объект `ReaderWriterLockSlim` на ячейку (~115 Б + дескриптор + реестр рекурсии) исчезает.
> ~140 Б/ячейку → ~32 Б/ячейку. Это и есть «локи на каждый компонент ~1.5 ГБ → сотни МБ».

---

## Фаза 2 — по-сущностное хранилище компонентов → `ComponentBag`

Самый горячий путь. В `Core/EntityComponentStorage.cs` поле
`components : LockedDictionary<Type, ECSComponent>` (с `HoldKeys=true`) заменяем на
`ComponentBag<ECSComponent>`, ключ — **стабильный int-id типа** (не `Type`).

1. Завести реестр `Type → int` (у вас уже есть `EntitySerializer.TypeIdStorage : long`; берём его и
   приводим к int, либо отдельная регистрация). Хранить id в самом `ECSComponent` (см. §4.3).
2. Маппинг методов:

   | Было (`LockedDictionary`) | Стало (`ComponentBag`) |
   |---|---|
   | `TryGetLockedElement(type, …, override=false)` | `TryGetReadLocked(typeId, out comp, out tok)` |
   | `TryGetLockedElement(type, …, override=true)` | `TryGetWriteLocked(typeId, out comp, out tok)` |
   | `ExecuteReadLocked` / `ExecuteWriteLocked` | одноимённые `(typeId, action)` |
   | `ExecuteOnAddLocked` | `ExecuteOnAddLocked(typeId, comp, action)` |
   | `TryAddOrChange` (add) | `TryAdd(typeId, comp)` |
   | `TryRemove` | `Remove(typeId, out comp)` |
   | `HoldKey` / `HoldComponentAddition` (shared) | `TryHoldShared(typeId, out tok)` / `ExecuteHoldRead` |
   | exclusive absence hold | `TryHoldExclusive(typeId, out tok)` / `ExecuteHoldWrite` |
   | `Keys/Values/foreach` | `Snapshot()` / `Count` / `ContainsKey` |

3. **Комбинаторы на N компонентов** (`Entity.ExecuteWriteLockedComponent<T1..T6>` и read/hold-версии)
   остаются как есть по структуре — вложенные лямбды, просто каждый внутренний лок теперь
   `bag.TryGet(Read|Write)Locked(typeId, …)`. Идея «залочить компонент C сущности E с R/W» — цела.

4. **Сериализация без `lock(SerialLocker)`.** В `EntityComponentStorage.SlicedSerializeStorage`
   сейчас: `ExecuteReadLocked(component)` → затем `lock(component.SerialLocker)` +
   `EnterToSerialization` и мутация. Переводим на **write-лок слота**:
   `bag.ExecuteWriteLocked(typeId, (k, comp) => { comp.EnterToSerialization(); … })`.
   Это снимает отдельный `SerialLocker` и убирает cross-mode переход (read→затем мутация под
   другим локом) — см. аудит ниже.

---

## Фаза 3 — снять async и гигиена

1. Удалить async-ветку целиком: `LockedDictionaryAsync`, `EntityStorageAsync`,
   `*Async`-методы (`AddNewEntityAsync`, `GetReadLockedComponentAsync`, `RemoveComponentAsync`,
   `HoldComponentAdditionAsync`, `AddOrChangeComponentAsync`, `HasComponentAsync`), `.Result`
   sync-over-async (≈20 мест в `Core/`). Оставить синхронные эквиваленты на `ComponentBag`/
   `LockedDictionarySlim`.
   > Токен лока имеет привязку к потоку (как у `ReaderWriterLockSlim`): захват и `Dispose`
   > обязаны быть на одном потоке. `await` это ломает — поэтому async и снимается. Симуляция
   > `Simulation.cs` должна быть переписана на синхронные воркеры (как `Benchmark/Harness.cs`).
2. Удалить мёртвый код: `ConcurrentLockingDictionary` (#if false, ~2550 строк), вендоренные
   `AsyncLocker.zip` / `AsyncReaderWriterLockSlim.7z`, OBSOLETE `SerializeStorage`, пустой
   `Program.cs`, `RWLockLogging` и закомментированные блоки в `RWLock.cs`.
3. Удалить legacy `SerialLocker`-локинг (см. §4.3).

---

## §4.3 — облегчение объектов (патч `IECSObject` / `ECSComponent`)

Документированный diff (применять в Фазе 2–3). **Не** полный рерайт — точечные правки.

### `Core/IECSObject.cs`

```diff
- protected object SerialLocker = new object();                 // жадно, на каждый объект
- protected Dictionary<long, ...> childECSObjectsId = new ...;  // жадно
+ // SerialLocker удалён: внешняя сериализация идёт через write-лок слота в ComponentBag
+ // (Фаза 2.4). Точечная синхронизация полей — через тот же слот или через короткий CAS.
+ private Dictionary<long, ...> _childECSObjectsId;             // лениво
+ private Dictionary<long, ...> ChildECSObjectsId =>           // C# 7.3: обычное свойство, не =>
+     _childECSObjectsId ?? (_childECSObjectsId = new Dictionary<long, ...>());
```

```diff
- // в AfterDeserialization: lock (SerialLocker) { ... }
+ // заменить на: childECSObjects.ExecuteWriteLocked(thisId, (_, __) => { ... });
+ // (childECSObjects уже LockedDictionarySlim после Фазы 1)
```

Метаданные типа — на уровень типа, не экземпляра (статический кэш по `Type`):

```diff
- public long ObjectType;          // дублируется в каждом экземпляре
- public long ReflectionId;
+ // вынести в static-таблицу: TypeMeta[type] = { ObjectType, ReflectionId, TypeId }
+ // экземпляр хранит только short/int индекс в таблице, либо берёт по this.GetType().
```

### `Core/Component.cs` (`ECSComponent`)

```diff
- private ReaderWriterLockSlim lockerValue;   // лениво, но всё равно объект
- private SharedLock monoLockerValue;         // лениво
+ // Оба удалить: блокировка компонента теперь — inline-лок слота в ComponentBag.
+ // Никакого собственного лок-объекта у компонента больше нет.

- public Dictionary<...> ComponentGroups = new ...();   // жадно
- public List<...> OnChangeHandlers = new List<...>();  // жадно
+ private Dictionary<...> _componentGroups;              // лениво (создавать в геттере)
+ private List<...> _onChangeHandlers;                   // лениво
```

Упаковать флаги в один int (было 5 bool + enum = ~6 полей с выравниванием):

```diff
- public bool Unregistered;
- public bool AlreadyRemovedReaction;
- // ... ещё ~3 bool + ComponentLifecycleState enum
+ private int _flags;   // биты 0..4 — bool-флаги, биты 5..7 — ComponentLifecycleState
+ // аксессоры: get => (_flags & MASK) != 0; set => _flags = cond ? _flags|MASK : _flags&~MASK;
```

> Эффект §4.3: «жадные» поля (создаются у каждого из ~1M объектов, даже если не нужны) становятся
> ленивыми; per-объектные лок-объекты исчезают; bool-поля упаковываются. Это вторая половина
> экономии (~2.7 ГБ «жадных объектов» → сотни МБ).

---

## Аудит рисков (проверить вручную при графте)

1. **Cross-mode W→R / R→W.** Места, где удерживается read-лок и затем мутируется под другим
   механизмом. Найдено: `EntityComponentStorage.SlicedSerializeStorage` (read-лок компонента →
   `lock(SerialLocker)` → `EnterToSerialization` + мутация). При графте перевести на **write-лок
   слота** (Фаза 2.4). Если где-то останется «взять read, потом write на тот же слот» — по умолчанию
   движок вернёт **dummy** (как старый «DEADLOCK ESCAPE»); для отладки можно включить
   `RWCell.ThrowOnOrderViolation = true`, прогнать тесты и найти все такие точки.

2. **Привязка токена к потоку.** Захват и `Dispose` — на одном потоке. Синхронный API это
   гарантирует. Любой оставшийся `await` между захватом и `Dispose` — баг (Фаза 3 снимает async).

3. **`OneThreadMode`.** Первая строка `Enter`/`Exit` — no-op (как `MockReaderWriterLockSlim`).
   `Hold` в этом режиме — чистый no-op-success (слот не резервируется). Проверьте, что мост к
   главному циклу Unity/Godot действительно ставит флаг до старта мира.

4. **`ComponentBag._struct`.** Короткий `Monitor` держится только на время структурной мутации
   (поиск/выделение/освобождение слота), **никогда** во время блокировки на лок ячейки или на
   время удержания компонента. Конкурентные add разных ключей одной сущности сериализуются на
   микросекунды — это дешевле старого графа global+per-cell RWLock. Если профиль покажет
   контеншен на `_struct` — это сигнал к Phase-2b (см. ниже).

5. **Phase-2b (опционально, абсолютный минимум памяти).** Текущий `ComponentBag` оставляет один
   ~40-байтный узел на компонент. Полный value-slot вариант (lock прямо в `long`-поле массива,
   ноль узлов, ~8 Б/ячейку, пол к ~104 МБ на 12M) **не включён**: он требует протокола поколений
   против reuse слота во время удержания, который без компилятора я не могу выверить. Это
   отдельный, изолированный шаг после того, как текущая версия пройдёт ваш бенч и тесты.

6. **`HoldKeys` на словарях мира — выключить.** `LockedDictionarySlim` с `HoldKeys=true` оставляет
   вечную запись во вложенном `KeysHoldingStorage` на каждый `add` (как и оригинал) — это удваивает
   узлы. `EntityStorage` / `childECSObjects` в Hold по ключу не нуждаются → конструировать с
   `HoldKeys=false` (по умолчанию). Hold по компонентам делает `ComponentBag` инлайн, без вложенного
   словаря.

7. **Глобальный лок контейнера УБРАН (реализовано).** Изначально каждая операция брала storage
   read-лок. Это создавало инверсию иерархии: `HoldKey` и мультиключевой комбинатор берут лок
   ЯЧЕЙКИ, а затем (через `ContainsKey` / захват следующей ячейки) — глобальный лок, тогда как
   `add` берёт глобальный, затем ячейку. С writer-favoring `EnterLockdown`/`ClearSnapshot` это
   давало гарантированный дедлок по кругу. Лечение, которое теперь в коде: **глобального
   пер-операционного лока нет вообще** — `ConcurrentDictionary` потокобезопасен сам, а гранулярную
   гарантию даёт лок ячейки. Следствия: (а) снят один из двух локов на операцию (быстрее);
   (б) **`lockdown` стал «мягким»** — это volatile-флаг, который запрещает НОВЫЕ мутации
   (add/change/remove/hold), но не вытесняет операции «в полёте»; для строгой остановки вызывайте
   lockdown в момент, когда поток управления гарантирует отсутствие конкурентных операций (граница
   сериализации/тиров). `ClearSnapshot` больше не атомарен относительно конкурентных add — снимать
   снимок при квиесцентности.

8. **Канонический порядок локов в комбинаторах.** `Entity.Execute*LockedComponent<T1..T6>` захватывает
   несколько (сущность,компонент) сразу. Захват в произвольном порядке между потоками даёт классический
   deadlock по порядку локов — это свойство ЛЮБЫХ множественных локов, не баг движка. Комбинаторы
   обязаны брать локи в детерминированном порядке (например, по возрастанию typeId). В стресс-тесте
   `DictStress` это сделано (сортировка ключей перед мультизахватом); в графте — отсортировать
   `T1..Tn` по стабильному ключу типа перед вложенными лямбдами.

---

## Порядок действий (чек-лист)

- [ ] Фаза 0: `RWToken` в `ILockedDictionary` + механическая замена типа; собрать.
- [ ] Фаза 1: `LockedDictionarySlim` для `EntityStorage` / `childECSObjects` / сериализации; собрать; прогнать `Harness`.
- [ ] Фаза 2: `ComponentBag` в `EntityComponentStorage`; маппинг методов; комбинаторы N-компонентов; сериализация через write-лок слота; собрать; прогнать `Harness` + реальную симуляцию.
- [ ] Фаза 3: снять async + `.Result`; удалить мёртвый код; §4.3 slimming; собрать; финальный прогон на больших `entities`.
- [ ] Включить `RWCell.ThrowOnOrderViolation = true` в отладочном прогоне, устранить все cross-mode точки, выключить обратно.
