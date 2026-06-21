# AECC.Locking — ядро локера (§3) + компактная сумка (§4.1)

Собираемый модуль на **C# 7.3** (`<LangVersion>7.3</LangVersion>` принудительно), без внешних
зависимостей, только API уровня **netstandard2.0** (Unity IL2CPP / Godot — совместимо).

> ⚠️ В окружении, где это писалось, **нет компилятора C#**: код выверен вручную по спецификации
> 7.3 и проверен грепом на конструкции C# 8+, но **не компилировался**. Первый шаг у вас —
> `dotnet build`. Ниже — что делать с предупреждениями/ошибками, если всплывут.

## Что внутри

| Файл | Роль |
|---|---|
| `Locking/RWCell.cs` | Ядро (§3): состояние лока — один `long` через `Interlocked.CompareExchange`; parking lot из 1024 monitor-гейтов; thread-static учёт режима для reentry (dummy/throw). Writer-favoring. |
| `Locking/RWToken.cs` | Значимый тип `struct RWToken : IDisposable` — нулевые аллокации на горячем пути; `default(RWToken)` = no-op. `LockHost` — база контейнеров. |
| `Collections/LockedDictionarySlim.cs` | Drop-in замена `LockedDictionary<TKey,TValue>` с тем же контрактом (lockdown, HoldKey, ExecuteOn*Locked, Unsafe*), но **без объекта-лока на ячейку**: ~140 Б → ~32 Б. Рекомендуется для словарей мира. |
| `Collections/ComponentBag.cs` | §4.1: компактная по-сущностная сумка. Схлопывает `LockedValue + RWLock + ReaderWriterLockSlim` в один ~40-байтный узел с inline-локом; убирает вложенный `KeysHoldingStorage` (Hold = слот в состоянии HOLD). |
| `Benchmark/LegacyModel.cs` | Точный аналог текущего подхода (RWLS на ячейку) для сравнения «было/стало». |
| `Benchmark/Harness.cs` | Память + чистая стоимость лока + пропускная способность (профиль Simulation.cs) + инварианты. |
| `Program.cs` | Точка входа. |

## Сборка и запуск

```bash
cd AECC.Locking
dotnet build -c Release
# entities components durationMs threads
dotnet run -c Release -- 100000 12 5000 16
```

Аргументы по умолчанию: `100000 12 5000 16`. Чтобы приблизиться к целевой нагрузке — поднимайте
`entities` (следите за RAM на legacy-бэкенде: именно его рост и измеряется). Для проверки
no-op быстрого пути в одном потоке — `Defines.OneThreadMode = true` в `Program.Main`.

## Что покажет харнесс

1. **MEMORY** — байт/ячейку для трёх бэкендов и экстраполяция на 1M×12.
2. **PURE LOCK COST** — нс/оп и **B/op аллокаций** (новый путь — 0 B/op за счёт `struct`-токена).
3. **INVARIANTS** — PASS/FAIL: эксклюзивность писателя, конкурентность читателей, reentry,
   cross-mode dummy, hold блокирует add, независимость ключей.
4. **THROUGHPUT** — ops/sec, профиль Simulation (8 lock / 4 swap / 4 hold, 7 компонентов на
   операцию, удержание 1 мс), для legacy и нового; плюс вариант «без сна» (чистая стоимость лока
   под контеншеном).

## Если компилятор ругнётся

- Всё писалось под 7.3; вероятные места — `out _` (7.0, ок), `default(T)` (везде явный), `bool?`
  (значимый Nullable, ок). Если ваш проект на более старом C# — `<LangVersion>` подскажет точную
  строку.
- `GC.GetAllocatedBytesForCurrentThread()` вызывается через рефлексию (нет на старых таргетах →
  покажет `n/a`, не падает).
