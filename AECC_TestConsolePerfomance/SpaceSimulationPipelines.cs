using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AECC.Core;
using AECC.Extensions.ThreadingSync;
using AECC.Collections;
using AECC.Core.Logging;
using Async;

// ═══════════════════════════════════════════════════════════════════════════
//  КОМПОНЕНТЫ КОСМИЧЕСКОГО СИМУЛЯТОРА
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>Маркер фракции. Содержит индекс фракции и кэшированный процент бонуса.</summary>
[System.Serializable]
[TypeUid(100)]
public class FactionComponent : ECSComponent
{
    public static new long Id { get; set; } = 100;
    public int FactionIndex;
    public int StationCount;
    public int InitialStationCount;
    public double BonusPercent => StationCount > InitialStationCount
        ? (double)(StationCount - InitialStationCount) / InitialStationCount
        : 0.0;
}

/// <summary>Компонент орбитальной станции.</summary>
[System.Serializable]
[TypeUid(101)]
public class StationComponent : ECSComponent
{
    public static new long Id { get; set; } = 101;
    public int FactionIndex;
    public double Budget;
    public int ShipCount;
    public const double BaseBudget = 1000.0;
    public const int TargetShipCount = 100;
    public double FreeMoney => Math.Max(0, Budget - BaseBudget);
}

/// <summary>Маркер корабля.</summary>
[System.Serializable]
[TypeUid(102)]
public class ShipComponent : ECSComponent
{
    public static new long Id { get; set; } = 102;
    public long OwnerStationInstanceId;
    public int FactionIndex;
}

/// <summary>Маркер: корабль повреждён.</summary>
[System.Serializable]
[TypeUid(103)]
public class DamagedComponent : ECSComponent
{
    public static new long Id { get; set; } = 103;
}

/// <summary>Маркер: корабль в рейде.</summary>
[System.Serializable]
[TypeUid(104)]
public class OnRaidComponent : ECSComponent
{
    public static new long Id { get; set; } = 104;
}

/// <summary>Маркер: корабль в генеральном сражении.</summary>
[System.Serializable]
[TypeUid(110)]
public class InBattleComponent : ECSComponent
{
    public static new long Id { get; set; } = 105;
}

/// <summary>HP корабля для сражений.</summary>
[System.Serializable]
[TypeUid(111)]
public class ShipHPComponent : ECSComponent
{
    public static new long Id { get; set; } = 106;
    public double HP = 100.0;
    public double MaxHP = 100.0;
}

/// <summary>Маркер: станция уничтожена.</summary>
[System.Serializable]
[TypeUid(112)]
public class StationDestroyedComponent : ECSComponent
{
    public static new long Id { get; set; } = 107;
}

/// <summary>Маркер: корабль уничтожен в бою.</summary>
[System.Serializable]
[TypeUid(113)]
public class BattleDestroyedComponent : ECSComponent
{
    public static new long Id { get; set; } = 108;
}


/// <summary>
/// ═══════════════════════════════════════════════════════════════════════════
///  КОСМИЧЕСКИЙ СИМУЛЯТОР — ECS STRESS-TEST (v2 — Fixed ThreadPool starvation)
/// ═══════════════════════════════════════════════════════════════════════════
/// 
///  Ключевые изменения v2:
///    • Корабли обрабатываются ПОСЛЕДОВАТЕЛЬНО внутри станции (Task.Run per station, не per ship)
///    • Это уменьшает давление на ThreadPool с ~100 000 задач/тик до ~1 000 задач/тик
///    • Сражения: дуэли «станция vs станция» — параллельные Task-и по ~60 кораблей
///    • Критичные таймеры (Battle, GlobalEvent, Stats) используют Thread.Sleep 
///      вместо Task.Delay — не зависят от доступности ThreadPool
///    • ThreadPool.SetMinThreads(200) для гарантии минимума потоков
/// </summary>
public class SpaceSimulationPipelines
{
    // ═══════════════════════════════════════════════════════
    //  Параметры симуляции
    // ═══════════════════════════════════════════════════════
    private const int FactionCount = 10;
    private const int StationsPerFaction = 70;
    private const int ShipsPerStation = 100;
    private const int SimulationDurationMs = 60_000;
    private const int RaidIntervalMs = 20;
    private const int GlobalEventIntervalMs = 1_000;
    private const int BattleIntervalMs = 10_000;
    private const int StatsIntervalMs = 10_000;
 
    // Экономика
    private const double StartBudget = 1000.0;
    private const double RepairCost = 40.0;
    private const double RecruitCost = 200.0;
    private const double ShipSellPrice = 100.0;
    private const double HighCargo = 100.0;
    private const double LowCargo = 20.0;
    private const int NewStationShipCount = 50;
    private const double NewStationCost = NewStationShipCount * RecruitCost + StartBudget; // 11000
 
    // Вероятности рейда (суммарно 90%, остаток 10% — пустой возврат)
    private const int ChanceDamaged = 20;       // 20%
    private const int ChanceDestroyed = 10;     // 10%  cumul: 30%
    private const int ChanceHighCargo = 25;     // 25%  cumul: 55%
    private const int ChanceLowCargo = 35;      // 35%  cumul: 90% (было 50 → сумма 105!)
    // остаток 10% — корабль вернулся пустой
 
    // Сражение (v4: 10 раундов, 8-15% урона, 30% шанс попадания)
    // Матожидание: 10 раундов × 30% × 11.5% = ~35% HP потерь. Кто-то выживет, кто-то нет.
    private const double BattleDamageMinPct = 0.08;   // 8%
    private const double BattleDamageMaxPct = 0.15;   // 15%
    private const int BattleHitChance = 30;            // 30% шанс за раунд
 
    // Глобальное событие
    private const int GlobalWinChance = 6;
    private const int GlobalLoseChance = 10;
 
    private volatile bool _stopSignal = false;
 
    // ═══════════════════════════════════════════════════════
    //  Хранилище сущностей
    // ═══════════════════════════════════════════════════════
    private ECSEntity[] _factionEntities;
    private ConcurrentDictionary<int, List<ECSEntity>> _stationsByFaction = new ConcurrentDictionary<int, List<ECSEntity>>();
    private ConcurrentDictionary<long, List<ECSEntity>> _shipsByStation = new ConcurrentDictionary<long, List<ECSEntity>>();
    private ConcurrentBag<ECSEntity> _allShips = new ConcurrentBag<ECSEntity>();
    private ConcurrentDictionary<long, ECSEntity> _stationLookup = new ConcurrentDictionary<long, ECSEntity>();
 
    // ═══════════════════════════════════════════════════════
    //  Статистика
    // ═══════════════════════════════════════════════════════
    private class SimStats
    {
        public long RaidsSent;
        public long RaidsDamaged;
        public long RaidsDestroyed;
        public long RaidsHighCargo;
        public long RaidsLowCargo;
        public long RaidsEmpty;
        public long RepairsCompleted;
        public long ShipsSold;
        public long ShipsRecruited;
        public long StationsBought;
        public long StationsLost;
        public long BattlesHeld;
        public long BattleShipsParticipated;
        public long BattleShipsDestroyed;
        public long GlobalEventsRun;
        public long GlobalWins;
        public long GlobalLosses;
        public long AddComponentOps;
        public long RemoveComponentOps;
        public long SearchGraphOps;
        public long LockOps;
        public long LockFailed;
        public long RaidProcessingTicks;
        public long RepairProcessingTicks;
        public long BattleProcessingTicks;
    }
 
    private SimStats _stats = new SimStats();
 
    // ═══════════════════════════════════════════════════════
    //  УТИЛИТА: корректное удаление корабля из мира
    //  ship.Alive = false НЕ УДАЛЯЕТ сущность из SearchGraph!
    //  Нужно вызвать entityManager.DeleteEntity для очистки
    //  графа метрик, иначе SearchGraph будет находить трупы.
    // ═══════════════════════════════════════════════════════
    private void DestroyShipAsync(ECSEntity ship, ECSWorld world)
    {
        try
        {
            // 1. Помечаем как мёртвого
            ship.Alive = false;
 
            // 2. КРИТИЧНО: RemoveEntityAsync очищает SearchGraph метрики (InternalGraphRemoval)
            //    Без этого SearchGraph будет продолжать находить "трупы"!
            world.entityManager.RemoveEntityAsync(ship);
        }
        catch { }
    }
 
    // ═══════════════════════════════════════════════════════
    //  ТОЧКА ВХОДА
    // ═══════════════════════════════════════════════════════
 
    public void Start()
    {
        var world = ECSWorld.GetWorld(0);
 
        // ═══ КРИТИЧНО: поднимаем минимум потоков ThreadPool ═══
        ThreadPool.SetMinThreads(200, 200);
 
        NLogger.Log("╔══════════════════════════════════════════════════════════════════╗");
        NLogger.Log("║  SPACE SIMULATOR — ECS ASYNC STRESS-TEST (v2)                   ║");
        NLogger.Log("║  10 Factions × 100 Stations × 100 Ships = 100,000+ entities     ║");
        NLogger.Log("║  Fixed: sequential ship processing, no TP starvation            ║");
        NLogger.Log("╚══════════════════════════════════════════════════════════════════╝");
        NLogger.Log($"  Duration: {SimulationDurationMs / 1000}s  |  Factions: {FactionCount}  |  Stations: {FactionCount * StationsPerFaction}  |  Ships: {FactionCount * StationsPerFaction * ShipsPerStation}");
        NLogger.Log("");
 
        NLogger.Log("▸ Phase 1: Creating space entities (async mode)...");
        var createSw = Stopwatch.StartNew();
 
        _factionEntities = new ECSEntity[FactionCount];
        var allCreatedEntities = new ConcurrentBag<ECSEntity>();
 
        var createTasks = new Task[FactionCount];
        for (int f = 0; f < FactionCount; f++)
        {
            int factionIdx = f;
            createTasks[f] = Task.Run(() =>
            {
                var factionComp = new FactionComponent
                {
                    FactionIndex = factionIdx,
                    StationCount = StationsPerFaction,
                    InitialStationCount = StationsPerFaction
                };
                var factionEntity = new ECSEntity(world, new ECSComponent[] { factionComp }, asyncMode: true);
                _factionEntities[factionIdx] = factionEntity;
                allCreatedEntities.Add(factionEntity);
 
                var stationList = new List<ECSEntity>();
                _stationsByFaction[factionIdx] = stationList;
 
                for (int s = 0; s < StationsPerFaction; s++)
                {
                    var stationComp = new StationComponent
                    {
                        FactionIndex = factionIdx,
                        Budget = StartBudget,
                        ShipCount = ShipsPerStation
                    };
                    var stationEntity = new ECSEntity(world, new ECSComponent[] { stationComp }, asyncMode: true);
                    stationList.Add(stationEntity);
                    allCreatedEntities.Add(stationEntity);
                    _stationLookup[stationEntity.instanceId] = stationEntity;
 
                    var shipList = new List<ECSEntity>();
                    _shipsByStation[stationEntity.instanceId] = shipList;
 
                    for (int sh = 0; sh < ShipsPerStation; sh++)
                    {
                        var shipComp = new ShipComponent
                        {
                            OwnerStationInstanceId = stationEntity.instanceId,
                            FactionIndex = factionIdx
                        };
                        var hpComp = new ShipHPComponent { HP = 100.0, MaxHP = 100.0 };
                        var shipEntity = new ECSEntity(world, new ECSComponent[] { shipComp, hpComp }, asyncMode: true);
                        shipList.Add(shipEntity);
                        allCreatedEntities.Add(shipEntity);
                        _allShips.Add(shipEntity);
                    }
                }
            });
        }
 
        bool allCreated = false;
        TaskEx.RunAsync(() => { Task.WhenAll(createTasks).Wait(); allCreated = true; });
 
        var predicate = new PredicateExecutor("space_sim_init",
            new List<Func<bool>>() { () => allCreated }, () =>
        {
            createSw.Stop();
            NLogger.Log($"  Created {allCreatedEntities.Count} entities in {createSw.Elapsed.TotalMilliseconds:F0}ms");
 
            NLogger.Log("▸ Phase 2: Registering entities in world (async)...");
            var regSw = Stopwatch.StartNew();
 
            var entityList = allCreatedEntities.ToList();
            var regTasks = entityList.Select(e => world.entityManager.AddNewEntityAsync(e));
            Task.WhenAll(regTasks).Wait();
 
            regSw.Stop();
            NLogger.Log($"  Registered {entityList.Count} entities in {regSw.Elapsed.TotalMilliseconds:F0}ms");
 
            RunSimulationAsync(world).Wait();
 
        }, 1000, 60000).Start();
    }
 
    // ═══════════════════════════════════════════════════════
    //  ГЛАВНЫЙ ЦИКЛ СИМУЛЯЦИИ
    // ═══════════════════════════════════════════════════════
 
    private async Task RunSimulationAsync(ECSWorld world)
    {
        NLogger.Log("");
        NLogger.Log("▸ Phase 3: Running space simulation (v2 — fixed concurrency)...");
        NLogger.Log($"  Raid interval: {RaidIntervalMs}ms | Global event: {GlobalEventIntervalMs}ms | Battle: {BattleIntervalMs}ms");
        NLogger.Log($"  ThreadPool min threads: 200  |  Battle mode: station-vs-station duels");
        NLogger.Log("");
 
        _stopSignal = false;
        var globalSw = Stopwatch.StartNew();
 
        // Рейд: 10 конвейеров
        var raidTasks = new Task[FactionCount];
        for (int f = 0; f < FactionCount; f++)
        {
            int fIdx = f;
            raidTasks[f] = Task.Run(() => RaidPipelineAsync(world, fIdx));
        }
 
        // Ремонт: 10 конвейеров
        var repairTasks = new Task[FactionCount];
        for (int f = 0; f < FactionCount; f++)
        {
            int fIdx = f;
            repairTasks[f] = Task.Run(() => RepairPipelineAsync(world, fIdx));
        }
 
        // Рекрутинг: 10 конвейеров
        var recruitTasks = new Task[FactionCount];
        for (int f = 0; f < FactionCount; f++)
        {
            int fIdx = f;
            recruitTasks[f] = Task.Run(() => RecruitPipelineAsync(world, fIdx));
        }
 
        var stationPurchaseTask = Task.Run(() => StationPurchasePipelineAsync(world));
        var globalEventTask = Task.Run(() => GlobalEventPipelineAsync(world));
        var battleTask = Task.Run(() => BattlePipelineAsync(world));
        var statsTask = Task.Run(() => StatsPipelineAsync(world, globalSw));
 
        // ═══ Ждём окончания — dedicated Thread для точности ═══
        await Task.Run(() => Thread.Sleep(SimulationDurationMs));
        _stopSignal = true;
 
        var allTasks = raidTasks
            .Concat(repairTasks)
            .Concat(recruitTasks)
            .Concat(new[] { stationPurchaseTask, globalEventTask, battleTask, statsTask });
 
        await Task.WhenAll(allTasks);
        globalSw.Stop();
 
        PrintFinalReport(globalSw.Elapsed);
    }
 
    // ═══════════════════════════════════════════════════════
    //  КОНВЕЙЕР 1: РЕЙДЫ (v2 — ИСПРАВЛЕНО)
    //  Task.Run per STATION, корабли — ПОСЛЕДОВАТЕЛЬНО
    //  ~1000 задач/тик вместо ~100 000
    // ═══════════════════════════════════════════════════════
 
    private async Task RaidPipelineAsync(ECSWorld world, int factionIndex)
    {
        while (!_stopSignal)
        {
            var sw = Stopwatch.StartNew();
 
            List<ECSEntity> stations;
            if (!_stationsByFaction.TryGetValue(factionIndex, out stations))
            {
                await Task.Delay(100);
                continue;
            }
 
            // Бонус фракции — один раз за тик
            double factionBonus = 0;
            try
            {
                var factionEntity = _factionEntities[factionIndex];
                if (factionEntity != null && factionEntity.Alive)
                {
                    var fc = await factionEntity.TryGetComponentAsync<FactionComponent>();
                    if (fc != null) factionBonus = fc.BonusPercent;
                }
            }
            catch { }
 
            // ═══ Task.Run PER STATION, корабли — await последовательно ═══
            var stationTasks = new List<Task>();
            var stationSnapshot = stations.ToList();
 
            foreach (var station in stationSnapshot)
            {
                if (!station.Alive) continue;
 
                var capturedStation = station;
                var capturedBonus = factionBonus;
 
                stationTasks.Add(Task.Run(async () =>
                {
                    var rng = new Random(Guid.NewGuid().GetHashCode());
 
                    try
                    {
                        if (await capturedStation.HasComponentAsync<StationDestroyedComponent>()) return;
 
                        List<ECSEntity> ships;
                        if (!_shipsByStation.TryGetValue(capturedStation.instanceId, out ships)) return;
 
                        // ═══ ПОСЛЕДОВАТЕЛЬНАЯ обработка кораблей ═══
                        foreach (var ship in ships.ToList())
                        {
                            if (!ship.Alive) continue;
 
                            try
                            {
                                bool isDamaged = await ship.HasComponentAsync<DamagedComponent>();
                                bool isOnRaid = await ship.HasComponentAsync<OnRaidComponent>();
                                bool isInBattle = await ship.HasComponentAsync<InBattleComponent>();
                                Interlocked.Add(ref _stats.LockOps, 3);
 
                                if (isDamaged || isOnRaid || isInBattle) continue;
 
                                await ship.AddOrChangeComponentAsync(new OnRaidComponent());
                                Interlocked.Increment(ref _stats.AddComponentOps);
                                Interlocked.Increment(ref _stats.RaidsSent);
 
                                int roll = rng.Next(100);
 
                                if (roll < ChanceDamaged)
                                {
                                    await ship.AddOrChangeComponentAsync(new DamagedComponent());
                                    Interlocked.Increment(ref _stats.AddComponentOps);
                                    Interlocked.Increment(ref _stats.RaidsDamaged);
                                }
                                else if (roll < ChanceDamaged + ChanceDestroyed)
                                {
                                    await ship.RemoveComponentIfPresentAsync<OnRaidComponent>();
                                    Interlocked.Increment(ref _stats.RemoveComponentOps);
                                    Interlocked.Increment(ref _stats.RaidsDestroyed);
 
                                    await capturedStation.ExecuteReadLockedComponentAsync<StationComponent>(async (t, c) =>
                                    {
                                        ((StationComponent)c).ShipCount = Math.Max(0, ((StationComponent)c).ShipCount - 1);
                                        await Task.CompletedTask;
                                    });
                                    Interlocked.Increment(ref _stats.LockOps);
 
                                    DestroyShipAsync(ship, world);
                                    continue;
                                }
                                else if (roll < ChanceDamaged + ChanceDestroyed + ChanceHighCargo)
                                {
                                    double income = HighCargo * (1.0 + capturedBonus);
                                    await capturedStation.ExecuteReadLockedComponentAsync<StationComponent>(async (t, c) =>
                                    {
                                        ((StationComponent)c).Budget += income;
                                        await Task.CompletedTask;
                                    });
                                    Interlocked.Increment(ref _stats.LockOps);
                                    Interlocked.Increment(ref _stats.RaidsHighCargo);
                                }
                                else if (roll < ChanceDamaged + ChanceDestroyed + ChanceHighCargo + ChanceLowCargo)
                                {
                                    double income = LowCargo * (1.0 + capturedBonus);
                                    await capturedStation.ExecuteReadLockedComponentAsync<StationComponent>(async (t, c) =>
                                    {
                                        ((StationComponent)c).Budget += income;
                                        await Task.CompletedTask;
                                    });
                                    Interlocked.Increment(ref _stats.LockOps);
                                    Interlocked.Increment(ref _stats.RaidsLowCargo);
                                }
                                else
                                {
                                    Interlocked.Increment(ref _stats.RaidsEmpty);
                                }
 
                                await ship.RemoveComponentIfPresentAsync<OnRaidComponent>();
                                Interlocked.Increment(ref _stats.RemoveComponentOps);
                            }
                            catch
                            {
                                Interlocked.Increment(ref _stats.LockFailed);
                            }
                        }
                    }
                    catch
                    {
                        Interlocked.Increment(ref _stats.LockFailed);
                    }
                }));
            }
 
            await Task.WhenAll(stationTasks);
 
            sw.Stop();
            Interlocked.Add(ref _stats.RaidProcessingTicks, sw.ElapsedTicks);
 
            int elapsed = (int)sw.ElapsedMilliseconds;
            if (elapsed < RaidIntervalMs)
                await Task.Delay(RaidIntervalMs - elapsed);
        }
    }
 
    // ═══════════════════════════════════════════════════════
    //  КОНВЕЙЕР 2: РЕМОНТ (v2 — последовательно)
    // ═══════════════════════════════════════════════════════
 
    private async Task RepairPipelineAsync(ECSWorld world, int factionIndex)
    {
        while (!_stopSignal)
        {
            var sw = Stopwatch.StartNew();
 
            IEnumerable<ECSEntity> damagedShips;
            try
            {
                damagedShips = world.entityManager.SearchGraph(
                    parentScope: null,
                    withComponentTypes: new Type[] { typeof(ShipComponent), typeof(DamagedComponent) },
                    withoutComponentTypes: new Type[] { typeof(OnRaidComponent), typeof(InBattleComponent) }
                );
                Interlocked.Increment(ref _stats.SearchGraphOps);
            }
            catch
            {
                await Task.Yield();
                continue;
            }
 
            var batch = damagedShips.ToList();
            if (batch.Count == 0)
            {
                await Task.Delay(50);
                continue;
            }
 
            var factionShips = batch.Where(s =>
            {
                try
                {
                    var sc = s.TryGetComponentAsync<ShipComponent>().Result;
                    return sc != null && sc.FactionIndex == factionIndex;
                }
                catch { return false; }
            }).ToList();
 
            double factionBonus = 0;
            try
            {
                var fc = await _factionEntities[factionIndex].TryGetComponentAsync<FactionComponent>();
                if (fc != null) factionBonus = fc.BonusPercent;
            }
            catch { }
 
            double adjustedRepairCost = RepairCost * (1.0 - factionBonus * 0.5);
            if (adjustedRepairCost < 10) adjustedRepairCost = 10;
 
            // ═══ ПОСЛЕДОВАТЕЛЬНО, без Task.Run per ship ═══
            foreach (var ship in factionShips)
            {
                if (!ship.Alive || _stopSignal) continue;
 
                try
                {
                    var shipComp = await ship.TryGetComponentAsync<ShipComponent>();
                    if (shipComp == null) continue;
 
                    ECSEntity station;
                    if (!_stationLookup.TryGetValue(shipComp.OwnerStationInstanceId, out station)) continue;
                    if (!station.Alive) continue;
 
                    bool repaired = false;
                    bool sold = false;
 
                    await station.ExecuteReadLockedComponentAsync<StationComponent>(async (t, c) =>
                    {
                        var sc = (StationComponent)c;
                        if (sc.Budget >= adjustedRepairCost)
                        {
                            sc.Budget -= adjustedRepairCost;
                            repaired = true;
                        }
                        else
                        {
                            sc.Budget += ShipSellPrice;
                            sc.ShipCount = Math.Max(0, sc.ShipCount - 1);
                            sold = true;
                        }
                        await Task.CompletedTask;
                    });
                    Interlocked.Increment(ref _stats.LockOps);
 
                    if (repaired)
                    {
                        await ship.RemoveComponentIfPresentAsync<DamagedComponent>();
                        Interlocked.Increment(ref _stats.RemoveComponentOps);
                        Interlocked.Increment(ref _stats.RepairsCompleted);
 
                        await ship.ExecuteReadLockedComponentAsync<ShipHPComponent>(async (t, c) =>
                        {
                            ((ShipHPComponent)c).HP = ((ShipHPComponent)c).MaxHP;
                            await Task.CompletedTask;
                        });
                        Interlocked.Increment(ref _stats.LockOps);
                    }
                    else if (sold)
                    {
                        DestroyShipAsync(ship, world);
                        Interlocked.Increment(ref _stats.ShipsSold);
                    }
                }
                catch
                {
                    Interlocked.Increment(ref _stats.LockFailed);
                }
            }
 
            sw.Stop();
            Interlocked.Add(ref _stats.RepairProcessingTicks, sw.ElapsedTicks);
            await Task.Delay(50);
        }
    }
 
    // ═══════════════════════════════════════════════════════
    //  КОНВЕЙЕР 3: РЕКРУТИНГ
    // ═══════════════════════════════════════════════════════
 
    private async Task RecruitPipelineAsync(ECSWorld world, int factionIndex)
    {
        while (!_stopSignal)
        {
            List<ECSEntity> stations;
            if (!_stationsByFaction.TryGetValue(factionIndex, out stations))
            {
                await Task.Delay(200);
                continue;
            }
 
            double factionBonus = 0;
            try
            {
                var fc = await _factionEntities[factionIndex].TryGetComponentAsync<FactionComponent>();
                if (fc != null) factionBonus = fc.BonusPercent;
            }
            catch { }
 
            double adjustedRecruitCost = RecruitCost * (1.0 - factionBonus * 0.5);
            if (adjustedRecruitCost < 50) adjustedRecruitCost = 50;
 
            foreach (var station in stations.ToList())
            {
                if (_stopSignal) break;
                if (!station.Alive) continue;
 
                try
                {
                    if (await station.HasComponentAsync<StationDestroyedComponent>()) continue;
 
                    var sc = await station.TryGetComponentAsync<StationComponent>();
                    if (sc == null) continue;
 
                    if (sc.ShipCount >= StationComponent.TargetShipCount) continue;
                    if (sc.Budget < adjustedRecruitCost + StationComponent.BaseBudget) continue;
 
                    bool bought = false;
                    await station.ExecuteReadLockedComponentAsync<StationComponent>(async (t, c) =>
                    {
                        var stc = (StationComponent)c;
                        if (stc.Budget >= adjustedRecruitCost + StationComponent.BaseBudget && stc.ShipCount < StationComponent.TargetShipCount)
                        {
                            stc.Budget -= adjustedRecruitCost;
                            stc.ShipCount++;
                            bought = true;
                        }
                        await Task.CompletedTask;
                    });
                    Interlocked.Increment(ref _stats.LockOps);
 
                    if (bought)
                    {
                        var shipComp = new ShipComponent
                        {
                            OwnerStationInstanceId = station.instanceId,
                            FactionIndex = factionIndex
                        };
                        var hpComp = new ShipHPComponent { HP = 100.0, MaxHP = 100.0 };
                        var newShip = new ECSEntity(world, new ECSComponent[] { shipComp, hpComp }, asyncMode: true);
                        await world.entityManager.AddNewEntityAsync(newShip);
 
                        List<ECSEntity> shipList;
                        if (_shipsByStation.TryGetValue(station.instanceId, out shipList))
                        {
                            lock (shipList) { shipList.Add(newShip); }
                        }
                        _allShips.Add(newShip);
 
                        Interlocked.Increment(ref _stats.ShipsRecruited);
                        Interlocked.Increment(ref _stats.AddComponentOps);
                    }
 
                    if (sc.ShipCount <= 0 && sc.Budget < adjustedRecruitCost)
                    {
                        if (!await station.HasComponentAsync<StationDestroyedComponent>())
                        {
                            await station.AddOrChangeComponentAsync(new StationDestroyedComponent());
                            Interlocked.Increment(ref _stats.AddComponentOps);
                            Interlocked.Increment(ref _stats.StationsLost);
 
                            try
                            {
                                await _factionEntities[factionIndex].ExecuteReadLockedComponentAsync<FactionComponent>(async (t, c) =>
                                {
                                    ((FactionComponent)c).StationCount = Math.Max(0, ((FactionComponent)c).StationCount - 1);
                                    await Task.CompletedTask;
                                });
                            }
                            catch { }
                        }
                    }
                }
                catch
                {
                    Interlocked.Increment(ref _stats.LockFailed);
                }
            }
 
            await Task.Delay(200);
        }
    }
 
    // ═══════════════════════════════════════════════════════
    //  КОНВЕЙЕР 4: ПОКУПКА СТАНЦИЙ (v3 — быстрая массовая покупка)
    //  Считает свободные деньги ОДИН раз, покупает ВСЕ доступные станции
    //  разом, 10 фракций параллельно, цикл каждые 500мс
    // ═══════════════════════════════════════════════════════
 
    private async Task StationPurchasePipelineAsync(ECSWorld world)
    {
        while (!_stopSignal)
        {
            // ═══ Все фракции параллельно ═══
            var factionTasks = new List<Task>();
            for (int f = 0; f < FactionCount; f++)
            {
                int fIdx = f;
                factionTasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        List<ECSEntity> stations;
                        if (!_stationsByFaction.TryGetValue(fIdx, out stations)) return;
 
                        // Шаг 1: Считаем свободные деньги ОДИН раз
                        var activeStations = new List<ECSEntity>();
                        double totalFreeMoney = 0;
 
                        foreach (var station in stations.ToList())
                        {
                            if (!station.Alive) continue;
                            try
                            {
                                if (await station.HasComponentAsync<StationDestroyedComponent>()) continue;
                                var sc = await station.TryGetComponentAsync<StationComponent>();
                                if (sc != null)
                                {
                                    totalFreeMoney += sc.FreeMoney;
                                    activeStations.Add(station);
                                }
                            }
                            catch { }
                        }
                        Interlocked.Add(ref _stats.LockOps, activeStations.Count);
 
                        // Шаг 2: Сколько станций можно купить? (макс 5 за цикл, чтобы не блокировать)
                        int canBuy = Math.Min((int)(totalFreeMoney / NewStationCost), 5);
                        if (canBuy <= 0) return;
 
                        // Шаг 3: Собираем деньги на canBuy станций
                        double toCollect = canBuy * NewStationCost;
                        foreach (var station in activeStations)
                        {
                            if (toCollect <= 0) break;
                            await station.ExecuteReadLockedComponentAsync<StationComponent>(async (t, c) =>
                            {
                                var sc = (StationComponent)c;
                                double canGive = sc.FreeMoney;
                                if (canGive > 0)
                                {
                                    double give = Math.Min(canGive, toCollect);
                                    sc.Budget -= give;
                                    toCollect -= give;
                                }
                                await Task.CompletedTask;
                            });
                            Interlocked.Increment(ref _stats.LockOps);
                        }
 
                        double collected = canBuy * NewStationCost - toCollect;
                        int actualBuy = (int)(collected / NewStationCost);
                        if (actualBuy <= 0) return;
 
                        NLogger.Log($"  🏗 Faction_{fIdx} buying {actualBuy} stations (had {totalFreeMoney:F0} free)");
 
                        // Шаг 4: Создаём станции — StationCount обновляется ЗА КАЖДУЮ станцию
                        for (int b = 0; b < actualBuy && !_stopSignal; b++)
                        {
                            var newStationComp = new StationComponent { FactionIndex = fIdx, Budget = StartBudget, ShipCount = NewStationShipCount };
                            var newStation = new ECSEntity(world, new ECSComponent[] { newStationComp }, asyncMode: true);
                            await world.entityManager.AddNewEntityAsync(newStation);
                            _stationLookup[newStation.instanceId] = newStation;
                            lock (stations) { stations.Add(newStation); }
 
                            var newShipList = new List<ECSEntity>();
                            _shipsByStation[newStation.instanceId] = newShipList;
 
                            for (int sh = 0; sh < NewStationShipCount; sh++)
                            {
                                var shipComp = new ShipComponent { OwnerStationInstanceId = newStation.instanceId, FactionIndex = fIdx };
                                var hpComp = new ShipHPComponent { HP = 100.0, MaxHP = 100.0 };
                                var newShip = new ECSEntity(world, new ECSComponent[] { shipComp, hpComp }, asyncMode: true);
                                await world.entityManager.AddNewEntityAsync(newShip);
                                newShipList.Add(newShip);
                                _allShips.Add(newShip);
                            }
 
                            // ═══ КРИТИЧНО: обновляем StationCount СРАЗУ после создания каждой станции ═══
                            await _factionEntities[fIdx].ExecuteReadLockedComponentAsync<FactionComponent>(async (t, c) =>
                            {
                                ((FactionComponent)c).StationCount++;
                                await Task.CompletedTask;
                            });
                            Interlocked.Increment(ref _stats.LockOps);
                            Interlocked.Increment(ref _stats.StationsBought);
                            Interlocked.Add(ref _stats.AddComponentOps, 1 + NewStationShipCount);
                        }
                    }
                    catch
                    {
                        Interlocked.Increment(ref _stats.LockFailed);
                    }
                }));
            }
 
            await Task.WhenAll(factionTasks);
 
            await Task.Delay(500); // 500мс вместо 2000мс
        }
    }
 
    // ═══════════════════════════════════════════════════════
    //  КОНВЕЙЕР 5: ГЛОБАЛЬНЫЕ СОБЫТИЯ
    //  Thread.Sleep для точности таймера
    // ═══════════════════════════════════════════════════════
 
    private async Task GlobalEventPipelineAsync(ECSWorld world)
    {
        var rng = new Random(Guid.NewGuid().GetHashCode());
 
        while (!_stopSignal)
        {
            // ═══ Thread.Sleep — не зависит от ThreadPool ═══
            await Task.Run(() => Thread.Sleep(GlobalEventIntervalMs));
            if (_stopSignal) break;
 
            Interlocked.Increment(ref _stats.GlobalEventsRun);
 
            var eventTasks = new List<Task>();
            for (int f = 0; f < FactionCount; f++)
            {
                int fIdx = f;
                int roll = rng.Next(100);
 
                eventTasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        List<ECSEntity> stations;
                        if (!_stationsByFaction.TryGetValue(fIdx, out stations)) return;
 
                        foreach (var station in stations.ToList())
                        {
                            if (!station.Alive) continue;
                            try
                            {
                                if (await station.HasComponentAsync<StationDestroyedComponent>()) continue;
 
                                await station.ExecuteReadLockedComponentAsync<StationComponent>(async (t, c) =>
                                {
                                    var sc = (StationComponent)c;
                                    double free = sc.FreeMoney;
                                    if (free <= 0) { await Task.CompletedTask; return; }
 
                                    if (roll < GlobalWinChance)
                                        sc.Budget += free * 0.30;
                                    else if (roll < GlobalWinChance + GlobalLoseChance)
                                    {
                                        sc.Budget -= free * 0.20;
                                        if (sc.Budget < StationComponent.BaseBudget)
                                            sc.Budget = StationComponent.BaseBudget;
                                    }
                                    await Task.CompletedTask;
                                });
                                Interlocked.Increment(ref _stats.LockOps);
                            }
                            catch { }
                        }
 
                        if (roll < GlobalWinChance)
                            Interlocked.Increment(ref _stats.GlobalWins);
                        else if (roll < GlobalWinChance + GlobalLoseChance)
                            Interlocked.Increment(ref _stats.GlobalLosses);
                    }
                    catch { Interlocked.Increment(ref _stats.LockFailed); }
                }));
            }
 
            await Task.WhenAll(eventTasks);
        }
    }
 
    // ═══════════════════════════════════════════════════════
    //  КОНВЕЙЕР 6: ГЕНЕРАЛЬНОЕ СРАЖЕНИЕ (v3 — дуэли станция-на-станцию)
    //  Группирует корабли по станциям, формирует пары/тройки,
    //  каждая дуэль в своём Task — параллельно ~500 дуэлей
    // ═══════════════════════════════════════════════════════
 
    private async Task BattlePipelineAsync(ECSWorld world)
    {
        var rng = new Random(Guid.NewGuid().GetHashCode());
 
        while (!_stopSignal)
        {
            await Task.Run(() => Thread.Sleep(BattleIntervalMs));
            if (_stopSignal) break;
 
            var battleSw = Stopwatch.StartNew();
            Interlocked.Increment(ref _stats.BattlesHeld);
 
            NLogger.Log($"  ⚔ GENERAL BATTLE started! Grouping combatants by station...");
 
            // ═══ Шаг 1: Группируем корабли по станциям ═══
            // Собираем свободных кораблей (не в рейде, не повреждены)
            List<ECSEntity> allCombatants;
            try
            {
                var found = world.entityManager.SearchGraph(
                    parentScope: null,
                    withComponentTypes: new Type[] { typeof(ShipComponent), typeof(ShipHPComponent) },
                    withoutComponentTypes: new Type[] { typeof(DamagedComponent), typeof(OnRaidComponent) }
                );
                allCombatants = found.Where(c => c.Alive).ToList();
                Interlocked.Increment(ref _stats.SearchGraphOps);
            }
            catch
            {
                NLogger.Log($"  ⚔ Battle SearchGraph failed");
                continue;
            }
 
            if (allCombatants.Count < 20)
            {
                NLogger.Log($"  ⚔ Not enough combatants ({allCombatants.Count}), skipping");
                continue;
            }
 
            // Группировка: stationId → список кораблей
            var stationGroups = new Dictionary<long, List<ECSEntity>>();
            foreach (var ship in allCombatants)
            {
                try
                {
                    var sc = await ship.TryGetComponentAsync<ShipComponent>();
                    if (sc == null) continue;
                    if (!stationGroups.ContainsKey(sc.OwnerStationInstanceId))
                        stationGroups[sc.OwnerStationInstanceId] = new List<ECSEntity>();
                    stationGroups[sc.OwnerStationInstanceId].Add(ship);
                }
                catch { }
            }
 
            var stationIds = stationGroups.Keys.ToList();
            if (stationIds.Count < 2)
            {
                NLogger.Log($"  ⚔ Not enough station groups ({stationIds.Count}), skipping");
                continue;
            }
 
            // Перемешиваем станции для случайного матчмейкинга
            for (int i = stationIds.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                var tmp = stationIds[i]; stationIds[i] = stationIds[j]; stationIds[j] = tmp;
            }
 
            // ═══ Шаг 2: Формируем дуэли «станция vs станция» (иногда 3-way) ═══
            var duels = new List<List<long>>(); // каждая дуэль — список из 2-3 stationId
            int idx = 0;
            while (idx < stationIds.Count - 1)
            {
                // 15% шанс 3-way боя (если есть 3+ станции)
                if (idx < stationIds.Count - 2 && rng.Next(100) < 15)
                {
                    duels.Add(new List<long> { stationIds[idx], stationIds[idx + 1], stationIds[idx + 2] });
                    idx += 3;
                }
                else
                {
                    duels.Add(new List<long> { stationIds[idx], stationIds[idx + 1] });
                    idx += 2;
                }
            }
 
            int totalParticipants = allCombatants.Count;
            NLogger.Log($"  ⚔ Battle begins: {totalParticipants} ships in {duels.Count} duels,  rounds");
            Interlocked.Add(ref _stats.BattleShipsParticipated, totalParticipants);
 
            // ═══ Шаг 3: Маркируем InBattle — параллельно по станциям ═══
            var markTasks = stationGroups.Values.Select(group => Task.Run(async () =>
            {
                foreach (var ship in group)
                {
                    if (!ship.Alive) continue;
                    try
                    {
                        await ship.AddOrChangeComponentAsync(new InBattleComponent());
                        Interlocked.Increment(ref _stats.AddComponentOps);
                    }
                    catch { }
                }
            }));
            await Task.WhenAll(markTasks);
 
            // ═══ Шаг 4: Запускаем все дуэли ПАРАЛЛЕЛЬНО — 10 раундов каждая ═══
            const int BattleRounds = 10;
            long destroyedBefore = Interlocked.Read(ref _stats.BattleShipsDestroyed);
 
            var duelTasks = new List<Task>();
            foreach (var duel in duels)
            {
                var capturedDuel = duel;
                duelTasks.Add(Task.Run(async () =>
                {
                    var duelRng = new Random(Guid.NewGuid().GetHashCode());
 
                    var sides = new List<List<ECSEntity>>();
                    foreach (var stationId in capturedDuel)
                    {
                        if (stationGroups.TryGetValue(stationId, out var group))
                            sides.Add(new List<ECSEntity>(group.Where(s => s.Alive)));
                    }
                    if (sides.Count < 2) return;
 
                    // ═══ 10 РАУНДОВ (вместо 100 тиков) ═══
                    for (int round = 0; round < BattleRounds && !_stopSignal; round++)
                    {
                        // Обновляем живых
                        for (int s = 0; s < sides.Count; s++)
                            sides[s] = sides[s].Where(sh => sh.Alive).ToList();
 
                        // Считаем живых с каждой стороны
                        int totalAlive = sides.Sum(s => s.Count);
                        if (totalAlive < 2) break;
 
                        // Формируем атаки: каждый корабль бьёт случайного врага
                        var attacks = new List<(ECSEntity attacker, ECSEntity defender)>();
                        var activeSides = sides.Where(s => s.Count > 0).ToList();
                        if (activeSides.Count < 2) break;
 
                        foreach (var side in activeSides)
                        {
                            var enemySides = activeSides.Where(es => es != side && es.Count > 0).ToList();
                            if (enemySides.Count == 0) continue;
 
                            foreach (var attacker in side)
                            {
                                if (!attacker.Alive) continue;
                                var enemySide = enemySides[duelRng.Next(enemySides.Count)];
                                var defender = enemySide[duelRng.Next(enemySide.Count)];
                                attacks.Add((attacker, defender));
                            }
                        }
 
                        // ═══ Применяем урон ═══
                        foreach (var (attacker, defender) in attacks)
                        {
                            if (!attacker.Alive || !defender.Alive) continue;
                            if (duelRng.Next(100) >= BattleHitChance) continue;
 
                            double dmgPct = BattleDamageMinPct + duelRng.NextDouble() * (BattleDamageMaxPct - BattleDamageMinPct);
                            bool destroyed = false;
 
                            try
                            {
                                await defender.ExecuteReadLockedComponentAsync<ShipHPComponent>(async (t, c) =>
                                {
                                    var hp = (ShipHPComponent)c;
                                    hp.HP -= hp.MaxHP * dmgPct;
                                    if (hp.HP <= 0) { hp.HP = 0; destroyed = true; }
                                    await Task.CompletedTask;
                                });
                                Interlocked.Increment(ref _stats.LockOps);
                            }
                            catch { continue; }
 
                            if (destroyed)
                            {
                                // Уменьшаем ShipCount станции
                                try
                                {
                                    var sc = await defender.TryGetComponentAsync<ShipComponent>();
                                    if (sc != null)
                                    {
                                        ECSEntity station;
                                        if (_stationLookup.TryGetValue(sc.OwnerStationInstanceId, out station) && station.Alive)
                                        {
                                            await station.ExecuteReadLockedComponentAsync<StationComponent>(async (t, c) =>
                                            {
                                                ((StationComponent)c).ShipCount = Math.Max(0, ((StationComponent)c).ShipCount - 1);
                                                await Task.CompletedTask;
                                            });
                                        }
                                    }
                                }
                                catch { }
 
                                DestroyShipAsync(defender, world);
                                Interlocked.Increment(ref _stats.BattleShipsDestroyed);
                            }
                        }
 
                        // Пауза между тиками — но только Thread.Sleep(1) чтобы не блокировать
                        await Task.Yield(); // yield между раундами
                    }
                }));
            }
 
            await Task.WhenAll(duelTasks);
 
            // ═══ Шаг 5: Выжившие — бесплатная починка, параллельно ═══
            int survived = 0;
            var cleanupTasks = stationGroups.Values.Select(group => Task.Run(async () =>
            {
                foreach (var ship in group)
                {
                    if (!ship.Alive) continue;
                    Interlocked.Increment(ref survived);
                    try
                    {
                        await ship.RemoveComponentIfPresentAsync<InBattleComponent>();
                        Interlocked.Increment(ref _stats.RemoveComponentOps);
 
                        await ship.ExecuteReadLockedComponentAsync<ShipHPComponent>(async (t, c) =>
                        {
                            ((ShipHPComponent)c).HP = ((ShipHPComponent)c).MaxHP;
                            await Task.CompletedTask;
                        });
                        Interlocked.Increment(ref _stats.LockOps);
                    }
                    catch { }
                }
            }));
            await Task.WhenAll(cleanupTasks);
 
            battleSw.Stop();
            Interlocked.Add(ref _stats.BattleProcessingTicks, battleSw.ElapsedTicks);
            long destroyedInBattle = Interlocked.Read(ref _stats.BattleShipsDestroyed) - destroyedBefore;
            NLogger.Log($"  ⚔ Battle ended: {survived} survived, {destroyedInBattle} destroyed in {duels.Count} duels ({battleSw.ElapsedMilliseconds}ms)");
        }
    }
 
    // ═══════════════════════════════════════════════════════
    //  КОНВЕЙЕР 7: СТАТИСТИКА
    // ═══════════════════════════════════════════════════════
 
    private async Task StatsPipelineAsync(ECSWorld world, Stopwatch globalSw)
    {
        int reportNumber = 0;
        while (!_stopSignal)
        {
            await Task.Run(() => Thread.Sleep(StatsIntervalMs));
            if (_stopSignal) break;
            reportNumber++;
            PrintPeriodicStats(reportNumber, globalSw.Elapsed);
        }
    }
 
    // ═══════════════════════════════════════════════════════
    //  ПЕРИОДИЧЕСКИЙ ОТЧЁТ
    // ═══════════════════════════════════════════════════════
 
    private void PrintPeriodicStats(int reportNum, TimeSpan elapsed)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"╔══════════════════════════════════════════════════════════════════════════════════════════════════════╗");
        sb.AppendLine($"║  PERIODIC REPORT #{reportNum}  |  Elapsed: {elapsed.TotalSeconds:F1}s                                                      ║");
        sb.AppendLine($"╠══════════════════════════════════════════════════════════════════════════════════════════════════════╣");
        sb.AppendLine();
 
        sb.AppendLine(string.Format("  {0,-12} | {1,8} | {2,8} | {3,12} | {4,12} | {5,8} | {6,8} | {7,6}",
            "Faction", "Stations", "Ships", "TotalBudget", "FreeMoney", "Damaged", "OnRaid", "Bonus%"));
        sb.AppendLine("  " + new string('─', 95));
 
        long totalShipsAlive = 0, totalStationsAlive = 0;
        double totalBudget = 0, totalFree = 0;
 
        for (int f = 0; f < FactionCount; f++)
        {
            int stationsAlive = 0, shipsAlive = 0, shipsDamaged = 0, shipsOnRaid = 0;
            double factionBudget = 0, factionFree = 0, bonus = 0;
 
            try { var fc = _factionEntities[f]?.TryGetComponentAsync<FactionComponent>().Result; if (fc != null) bonus = fc.BonusPercent * 100; } catch { }
 
            List<ECSEntity> stations;
            if (_stationsByFaction.TryGetValue(f, out stations))
            {
                foreach (var station in stations.ToList())
                {
                    if (!station.Alive) continue;
                    try
                    {
                        if (station.HasComponentAsync<StationDestroyedComponent>().Result) continue;
                        var sc = station.TryGetComponentAsync<StationComponent>().Result;
                        if (sc == null) continue;
                        stationsAlive++;
                        factionBudget += sc.Budget;
                        factionFree += sc.FreeMoney;
 
                        List<ECSEntity> ships;
                        if (_shipsByStation.TryGetValue(station.instanceId, out ships))
                        {
                            foreach (var ship in ships.ToList())
                            {
                                if (!ship.Alive) continue;
                                shipsAlive++;
                                try
                                {
                                    if (ship.HasComponentAsync<DamagedComponent>().Result) shipsDamaged++;
                                    if (ship.HasComponentAsync<OnRaidComponent>().Result) shipsOnRaid++;
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }
            }
 
            totalShipsAlive += shipsAlive; totalStationsAlive += stationsAlive;
            totalBudget += factionBudget; totalFree += factionFree;
 
            sb.AppendLine(string.Format("  {0,-12} | {1,8} | {2,8} | {3,12:F0} | {4,12:F0} | {5,8} | {6,8} | {7,5:F1}%",
                $"Faction_{f}", stationsAlive, shipsAlive, factionBudget, factionFree, shipsDamaged, shipsOnRaid, bonus));
        }
 
        sb.AppendLine("  " + new string('─', 95));
        sb.AppendLine(string.Format("  {0,-12} | {1,8} | {2,8} | {3,12:F0} | {4,12:F0}", "TOTAL", totalStationsAlive, totalShipsAlive, totalBudget, totalFree));
        sb.AppendLine();
        sb.AppendLine($"  Raids: {_stats.RaidsSent:N0}  |  Dmg: {_stats.RaidsDamaged:N0}  |  Dest: {_stats.RaidsDestroyed:N0}  |  Hi$: {_stats.RaidsHighCargo:N0}  |  Lo$: {_stats.RaidsLowCargo:N0}  |  Empty: {_stats.RaidsEmpty:N0}");
        sb.AppendLine($"  Repairs: {_stats.RepairsCompleted:N0}  |  Sold: {_stats.ShipsSold:N0}  |  Recruited: {_stats.ShipsRecruited:N0}  |  StBuy: {_stats.StationsBought:N0}  |  StLost: {_stats.StationsLost:N0}");
        sb.AppendLine($"  Battles: {_stats.BattlesHeld:N0}  |  Participants: {_stats.BattleShipsParticipated:N0}  |  BattleDest: {_stats.BattleShipsDestroyed:N0}");
        sb.AppendLine($"  GlobalEvents: {_stats.GlobalEventsRun:N0}  |  Wins: {_stats.GlobalWins:N0}  |  Losses: {_stats.GlobalLosses:N0}");
 
        long totalOps = _stats.AddComponentOps + _stats.RemoveComponentOps + _stats.SearchGraphOps + _stats.LockOps;
        double opsPerSec = totalOps / Math.Max(elapsed.TotalSeconds, 0.001);
        sb.AppendLine();
        sb.AppendLine($"  ECS ops: Add={_stats.AddComponentOps:N0} Rem={_stats.RemoveComponentOps:N0} Search={_stats.SearchGraphOps:N0} Lock={_stats.LockOps:N0} Fail={_stats.LockFailed:N0}");
        sb.AppendLine($"  Total: {totalOps:N0}  |  {opsPerSec:F0} ops/sec");
        sb.AppendLine($"╚══════════════════════════════════════════════════════════════════════════════════════════════════════╝");
 
        NLogger.Log(sb.ToString());
    }
 
    // ═══════════════════════════════════════════════════════
    //  ФИНАЛЬНЫЙ ОТЧЁТ
    // ═══════════════════════════════════════════════════════
 
    private void PrintFinalReport(TimeSpan totalElapsed)
    {
        double totalMs = totalElapsed.TotalMilliseconds;
        double totalSec = totalElapsed.TotalSeconds;
        double ticksPerMs = Stopwatch.Frequency / 1000.0;
 
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("╔══════════════════════════════════════════════════════════════════════════════════════════════════════╗");
        sb.AppendLine("║                    SPACE SIMULATOR — FINAL STRESS-TEST REPORT (v2)                                 ║");
        sb.AppendLine("║                    10 Factions × Async ECS × Fixed ThreadPool Starvation                           ║");
        sb.AppendLine("╠══════════════════════════════════════════════════════════════════════════════════════════════════════╣");
        sb.AppendLine($"║  Wall-clock time:           {totalMs:F0} ms ({totalSec:F1}s)");
        sb.AppendLine($"║  Initial entities:          {FactionCount + FactionCount * StationsPerFaction + FactionCount * StationsPerFaction * ShipsPerStation}");
        sb.AppendLine($"║  Concurrent pipelines:      {FactionCount * 3 + 4}");
        sb.AppendLine($"║  ThreadPool min threads:    200");
        sb.AppendLine($"║  Battle mode:               station-vs-station duels");
        sb.AppendLine("╠══════════════════════════════════════════════════════════════════════════════════════════════════════╣");
        sb.AppendLine();
 
        sb.AppendLine("║  ▸ RAID OPERATIONS");
        sb.AppendLine($"║    Total raids:        {_stats.RaidsSent:N0}");
        sb.AppendLine($"║    Outcomes:           Dmg={_stats.RaidsDamaged:N0}  Dest={_stats.RaidsDestroyed:N0}  Hi$={_stats.RaidsHighCargo:N0}  Lo$={_stats.RaidsLowCargo:N0}  Empty={_stats.RaidsEmpty:N0}");
        double raidMs = _stats.RaidProcessingTicks / ticksPerMs;
        sb.AppendLine($"║    Processing time:    {raidMs:F0} ms");
        sb.AppendLine($"║    Raid throughput:    {(_stats.RaidsSent / Math.Max(totalSec, 0.001)):F0} raids/sec");
        sb.AppendLine("║");
 
        sb.AppendLine("║  ▸ MAINTENANCE");
        sb.AppendLine($"║    Repairs completed:  {_stats.RepairsCompleted:N0}");
        sb.AppendLine($"║    Ships sold:         {_stats.ShipsSold:N0}");
        sb.AppendLine($"║    Ships recruited:    {_stats.ShipsRecruited:N0}");
        double repairMs = _stats.RepairProcessingTicks / ticksPerMs;
        sb.AppendLine($"║    Repair proc. time:  {repairMs:F0} ms");
        sb.AppendLine("║");
 
        sb.AppendLine("║  ▸ STATIONS");
        sb.AppendLine($"║    Stations bought:    {_stats.StationsBought:N0}");
        sb.AppendLine($"║    Stations lost:      {_stats.StationsLost:N0}");
        sb.AppendLine("║");
 
        sb.AppendLine("║  ▸ BATTLES");
        sb.AppendLine($"║    Battles held:       {_stats.BattlesHeld:N0}");
        sb.AppendLine($"║    Total participants: {_stats.BattleShipsParticipated:N0}");
        sb.AppendLine($"║    Ships destroyed:    {_stats.BattleShipsDestroyed:N0}");
        double battleMs = _stats.BattleProcessingTicks / ticksPerMs;
        sb.AppendLine($"║    Battle proc. time:  {battleMs:F0} ms");
        sb.AppendLine("║");
 
        sb.AppendLine("║  ▸ GLOBAL EVENTS");
        sb.AppendLine($"║    Events triggered:   {_stats.GlobalEventsRun:N0}");
        sb.AppendLine($"║    Wins (+30%):        {_stats.GlobalWins:N0}");
        sb.AppendLine($"║    Losses (-20%):      {_stats.GlobalLosses:N0}");
        sb.AppendLine("║");
 
        sb.AppendLine("╠══════════════════════════════════════════════════════════════════════════════════════════════════════╣");
        sb.AppendLine("║  ECS ASYNC THROUGHPUT ANALYSIS");
        sb.AppendLine("╠══════════════════════════════════════════════════════════════════════════════════════════════════════╣");
 
        long totalOps = _stats.AddComponentOps + _stats.RemoveComponentOps + _stats.SearchGraphOps + _stats.LockOps;
        double opsPerMs = totalOps / Math.Max(totalMs, 1);
        double opsPerSec = opsPerMs * 1000;
 
        sb.AppendLine($"║  AddComponent ops:     {_stats.AddComponentOps:N0}");
        sb.AppendLine($"║  RemoveComponent ops:  {_stats.RemoveComponentOps:N0}");
        sb.AppendLine($"║  SearchGraph ops:      {_stats.SearchGraphOps:N0}");
        sb.AppendLine($"║  WriteLock ops:        {_stats.LockOps:N0}");
        sb.AppendLine($"║  Failed operations:    {_stats.LockFailed:N0}");
        sb.AppendLine("║");
        sb.AppendLine($"║  Total ECS operations: {totalOps:N0}");
        sb.AppendLine($"║  RAW throughput:       {opsPerMs:F4} ops/ms  =  {opsPerSec:F0} ops/sec");
        sb.AppendLine("║");
 
        int initialEntities = FactionCount + FactionCount * StationsPerFaction + FactionCount * StationsPerFaction * ShipsPerStation;
        sb.AppendLine($"║  Avg add ops per entity:     {(double)_stats.AddComponentOps / initialEntities:F2}");
        sb.AppendLine($"║  Avg remove ops per entity:  {(double)_stats.RemoveComponentOps / initialEntities:F2}");
        sb.AppendLine($"║  Avg lock ops per entity:    {(double)_stats.LockOps / initialEntities:F2}");
        sb.AppendLine("║");
 
        sb.AppendLine("╠══════════════════════════════════════════════════════════════════════════════════════════════════════╣");
        sb.AppendLine("║  FINAL FACTION STATE");
        sb.AppendLine("╠══════════════════════════════════════════════════════════════════════════════════════════════════════╣");
        sb.AppendLine();
 
        sb.AppendLine(string.Format("  {0,-12} | {1,8} | {2,8} | {3,12} | {4,12} | {5,6}",
            "Faction", "Stations", "Ships", "TotalBudget", "FreeMoney", "Bonus%"));
        sb.AppendLine("  " + new string('─', 70));
 
        for (int f = 0; f < FactionCount; f++)
        {
            int stationsAlive = 0, shipsAlive = 0;
            double factionBudget = 0, factionFree = 0, bonus = 0;
 
            try { var fc = _factionEntities[f]?.TryGetComponentAsync<FactionComponent>().Result; if (fc != null) bonus = fc.BonusPercent * 100; } catch { }
 
            List<ECSEntity> stations;
            if (_stationsByFaction.TryGetValue(f, out stations))
            {
                foreach (var station in stations.ToList())
                {
                    if (!station.Alive) continue;
                    try
                    {
                        if (station.HasComponentAsync<StationDestroyedComponent>().Result) continue;
                        var sc = station.TryGetComponentAsync<StationComponent>().Result;
                        if (sc == null) continue;
                        stationsAlive++;
                        factionBudget += sc.Budget;
                        factionFree += sc.FreeMoney;
                        shipsAlive += sc.ShipCount;
                    }
                    catch { }
                }
            }
 
            sb.AppendLine(string.Format("  {0,-12} | {1,8} | {2,8} | {3,12:F0} | {4,12:F0} | {5,5:F1}%",
                $"Faction_{f}", stationsAlive, shipsAlive, factionBudget, factionFree, bonus));
        }
 
        sb.AppendLine("  " + new string('─', 70));
        sb.AppendLine();
        sb.AppendLine("╚══════════════════════════════════════════════════════════════════════════════════════════════════════╝");
 
        NLogger.Log(sb.ToString());
    }
}