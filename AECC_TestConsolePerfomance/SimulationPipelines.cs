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

/// <summary>
/// Симуляция «20 параллельных конвейеров» — аналогов ECS-систем.
/// Каждый конвейер:
///   1. Через SearchGraph находит сущности по своей компонентной сигнатуре (With / Without).
///   2. На найденных сущностях параллельно запускает 3 вида операций:
///      a) Read-Lock: захватывает блокировки на «рабочие» компоненты + Task.Delay
///      b) Hold: блокирует добавление «запрещённых» компонентов + Task.Delay
///      c) Swap: удаляет часть компонентов и вставляет на их место другие
///   3. Все три операции внутри конвейера работают параллельно (Task.WhenAll).
/// </summary>
public class SimulationPipelines
{
    // ═══════════════════════════════════════════════════════
    //  Все типы компонентов
    // ═══════════════════════════════════════════════════════
    private static readonly Type[] AllComponentTypes = new Type[]
    {
        typeof(HealthComponent),        typeof(SpeedComponent),         typeof(RangeDamagerComponent),
        typeof(MeleeDamagerComponent),  typeof(InTimeoutComponent),     typeof(ManaComponent),
        typeof(StaminaComponent),       typeof(ArmorComponent),         typeof(MagicResistanceComponent),
        typeof(CritChanceComponent),    typeof(EvasionComponent),       typeof(PositionComponent),
        typeof(VelocityComponent),      typeof(AccelerationComponent),  typeof(GravityComponent),
        typeof(RotationComponent),      typeof(ScaleComponent),         typeof(PoisonedComponent),
        typeof(BurningComponent),       typeof(FrozenComponent),        typeof(StunnedComponent),
        typeof(RegenerationComponent),  typeof(TargetComponent),        typeof(PathfindingComponent),
        typeof(AggroComponent),         typeof(InventoryComponent),     typeof(ExperienceComponent),
        typeof(LevelComponent),         typeof(GoldComponent),          typeof(ColliderComponent),
        typeof(StealthComponent),       typeof(ShieldComponent),        typeof(TeamComponent),
        typeof(LifetimeComponent),      typeof(SoundEmitterComponent)
    };

    private static readonly Func<ECSComponent>[] ComponentFactories = new Func<ECSComponent>[]
    {
        () => new HealthComponent(),        () => new SpeedComponent(),         () => new RangeDamagerComponent(),
        () => new MeleeDamagerComponent(),  () => new InTimeoutComponent(),     () => new ManaComponent(),
        () => new StaminaComponent(),       () => new ArmorComponent(),         () => new MagicResistanceComponent(),
        () => new CritChanceComponent(),    () => new EvasionComponent(),       () => new PositionComponent(),
        () => new VelocityComponent(),      () => new AccelerationComponent(),  () => new GravityComponent(),
        () => new RotationComponent(),      () => new ScaleComponent(),         () => new PoisonedComponent(),
        () => new BurningComponent(),       () => new FrozenComponent(),        () => new StunnedComponent(),
        () => new RegenerationComponent(),  () => new TargetComponent(),        () => new PathfindingComponent(),
        () => new AggroComponent(),         () => new InventoryComponent(),     () => new ExperienceComponent(),
        () => new LevelComponent(),         () => new GoldComponent(),          () => new ColliderComponent(),
        () => new StealthComponent(),       () => new ShieldComponent(),        () => new TeamComponent(),
        () => new LifetimeComponent(),      () => new SoundEmitterComponent()
    };

    // ═══════════════════════════════════════════════════════
    //  Параметры симуляции
    // ═══════════════════════════════════════════════════════
    private const int MaxEntities = 100_000;
    private const int SimulationDurationMs = 30_000;
    private const int PipelineCount = 20;
    private const int LockDelayMinMs = 1;
    private const int LockDelayMaxMs = 4;
    private const int HoldDelayMinMs = 1;
    private const int HoldDelayMaxMs = 4;
    private const int InitComponentCountMin = 15;
    private const int InitComponentCountMax = 26;
    // Сколько сущностей обрабатывать за один «тик» конвейера (batch)
    private const int EntitiesPerBatch = 50;

    private volatile bool _stopSignal = false;
    private ECSEntity[] _entityArray;

    // ═══════════════════════════════════════════════════════
    //  Описание конвейера (ECS-«системы»)
    // ═══════════════════════════════════════════════════════
    private class PipelineConfig
    {
        public string Name;
        /// <summary>Компоненты, которые ДОЛЖНЫ быть у сущности (для SearchGraph + ReadLock)</summary>
        public Type[] WithComponents;
        /// <summary>Компоненты, которых НЕ ДОЛЖНО быть (для SearchGraph + Hold)</summary>
        public Type[] WithoutComponents;
        /// <summary>Компоненты, которые конвейер будет удалять у найденных сущностей</summary>
        public Type[] RemoveComponents;
        /// <summary>Компоненты, которые конвейер будет добавлять взамен удалённых</summary>
        public Type[] AddComponents;
    }

    // ═══════════════════════════════════════════════════════
    //  Статистика по каждому конвейеру
    // ═══════════════════════════════════════════════════════
    private class PipelineStats
    {
        public long SearchOps;
        public long SearchEntitiesFound;
        public long LockOps;
        public long LockFailed;
        public long HoldOps;
        public long HoldFailed;
        public long SwapOps;
        public long SwapFailed;
        public long LockDelayTicks;
        public long HoldDelayTicks;
        public long SwapAllocTicks;
        public long TotalCycleCount;
    }

    private PipelineConfig[] _pipelines;
    private PipelineStats[] _pipelineStats;

    public void Start()
    {
        var world = ECSWorld.GetWorld(0);

        NLogger.Log("╔══════════════════════════════════════════════════════════════════╗");
        NLogger.Log("║  PIPELINE (ECS-System Analogue) STRESS-TEST SIMULATION          ║");
        NLogger.Log("║  20 parallel conveyors × SearchGraph × Async locks               ║");
        NLogger.Log("╚══════════════════════════════════════════════════════════════════╝");
        NLogger.Log($"  Entities: {MaxEntities}  |  Duration: {SimulationDurationMs / 1000}s  |  Pipelines: {PipelineCount}");
        NLogger.Log("");

        // ─── Генерация конфигураций конвейеров ───
        GeneratePipelineConfigs();

        // ─── Фаза 1: Создание сущностей (async mode) ───
        NLogger.Log("▸ Phase 1: Creating entities (async mode)...");
        var createSw = Stopwatch.StartNew();

        var entities = new ConcurrentBag<ECSEntity>();
        int halfMax = MaxEntities / 2;
        bool end1 = false, end2 = false;

        Action fillAction = () =>
        {
            var rng = new Random(Guid.NewGuid().GetHashCode());
            for (int i = 0; i < halfMax; i++)
            {
                int componentCount = rng.Next(InitComponentCountMin, InitComponentCountMax + 1);
                var entityComponents = new List<ECSComponent> { new ViewComponent() };

                var indices = Enumerable.Range(0, AllComponentTypes.Length)
                    .OrderBy(_ => rng.Next())
                    .Take(componentCount - 1);

                foreach (var idx in indices)
                    entityComponents.Add(ComponentFactories[idx]());

                var entity = new ECSEntity(world, entityComponents.ToArray(), asyncMode: true);
                entities.Add(entity);
            }
        };

        TaskEx.RunAsync(() => { fillAction(); end1 = true; });
        TaskEx.RunAsync(() => { fillAction(); end2 = true; });

        var predicate = new PredicateExecutor("flush_pipelines",
            new List<Func<bool>>() { () => end1 && end2 }, () =>
        {
            createSw.Stop();
            NLogger.Log($"  Created {entities.Count} entities in {createSw.Elapsed.TotalMilliseconds:F0}ms");

            // ─── Фаза 2: Регистрация (async) ───
            NLogger.Log("▸ Phase 2: Registering entities in world...");
            var regSw = Stopwatch.StartNew();

            var entityList = entities.ToList();
            var regTasks = entityList.Select(e => world.entityManager.AddNewEntityAsync(e));
            Task.WhenAll(regTasks).Wait();

            regSw.Stop();
            NLogger.Log($"  Registered {entityList.Count} entities in {regSw.Elapsed.TotalMilliseconds:F0}ms");

            _entityArray = entityList.ToArray();

            // ─── Фаза 3: Запуск конвейеров ───
            RunPipelinesAsync(world).Wait();

        }, 1000, 60000).Start();
    }

    /// <summary>
    /// Генерирует 20 уникальных конфигураций конвейеров.
    /// Каждый конвейер выбирает:
    ///   - 2-5 компонентов для With (поиск + read-lock)
    ///   - 1-3 компонента для Without (поиск + hold)
    ///   - 1-3 компонента из With для удаления
    ///   - 1-3 компонента для добавления (из оставшихся)
    /// </summary>
    private void GeneratePipelineConfigs()
    {
        var rng = new Random(42); // фиксированный seed для воспроизводимости
        _pipelines = new PipelineConfig[PipelineCount];
        _pipelineStats = new PipelineStats[PipelineCount];

        var usedSignatures = new HashSet<string>();

        for (int p = 0; p < PipelineCount; p++)
        {
            _pipelineStats[p] = new PipelineStats();

            PipelineConfig cfg;
            string signature;

            do
            {
                // Перемешиваем все типы
                var shuffled = AllComponentTypes.OrderBy(_ => rng.Next()).ToArray();
                int cursor = 0;

                int withCount = rng.Next(2, 6); // 2-5
                var withTypes = shuffled.Skip(cursor).Take(withCount).ToArray();
                cursor += withCount;

                int withoutCount = rng.Next(1, 4); // 1-3
                var withoutTypes = shuffled.Skip(cursor).Take(withoutCount).ToArray();
                cursor += withoutCount;

                // Удаляем часть With-компонентов (1-3)
                int removeCount = rng.Next(1, Math.Min(4, withCount + 1));
                var removeTypes = withTypes.OrderBy(_ => rng.Next()).Take(removeCount).ToArray();

                // Добавляем компоненты из тех, что не в With и не в Without
                var excluded = new HashSet<Type>(withTypes.Concat(withoutTypes));
                int addCount = removeCount; // добавляем столько же, сколько удалили
                var addTypes = shuffled.Skip(cursor).Where(t => !excluded.Contains(t)).Take(addCount).ToArray();

                cfg = new PipelineConfig
                {
                    Name = $"Pipeline_{p:D2}",
                    WithComponents = withTypes,
                    WithoutComponents = withoutTypes,
                    RemoveComponents = removeTypes,
                    AddComponents = addTypes
                };

                signature = string.Join(",", withTypes.Select(t => t.Name).OrderBy(x => x))
                    + "|" + string.Join(",", withoutTypes.Select(t => t.Name).OrderBy(x => x));

            } while (usedSignatures.Contains(signature));

            usedSignatures.Add(signature);
            _pipelines[p] = cfg;
        }
    }

    private async Task RunPipelinesAsync(ECSWorld world)
    {
        // Выводим конфигурации конвейеров
        NLogger.Log("");
        NLogger.Log("▸ Pipeline configurations:");
        for (int p = 0; p < PipelineCount; p++)
        {
            var cfg = _pipelines[p];
            NLogger.Log($"  [{cfg.Name}]  With=[{string.Join(",", cfg.WithComponents.Select(t => ShortName(t)))}]  " +
                        $"Without=[{string.Join(",", cfg.WithoutComponents.Select(t => ShortName(t)))}]  " +
                        $"Remove=[{string.Join(",", cfg.RemoveComponents.Select(t => ShortName(t)))}]  " +
                        $"Add=[{string.Join(",", cfg.AddComponents.Select(t => ShortName(t)))}]");
        }

        NLogger.Log("");
        NLogger.Log("▸ Phase 3: Running 20 parallel pipelines...");
        NLogger.Log($"  Duration: {SimulationDurationMs / 1000}s  |  Batch size: {EntitiesPerBatch}");
        NLogger.Log("");

        _stopSignal = false;
        var globalSw = Stopwatch.StartNew();

        // Запускаем все 20 конвейеров параллельно
        var pipelineTasks = new Task[PipelineCount];
        for (int p = 0; p < PipelineCount; p++)
        {
            int pipelineIndex = p;
            pipelineTasks[p] = Task.Run(() => RunSinglePipelineAsync(world, pipelineIndex));
        }

        // Таймер остановки
        await Task.Delay(SimulationDurationMs);
        _stopSignal = true;

        await Task.WhenAll(pipelineTasks);
        globalSw.Stop();

        PrintReport(globalSw.Elapsed);
    }

    /// <summary>
    /// Один конвейер (аналог ECS-системы).
    /// Цикл: SearchGraph → для каждого batch сущностей параллельно запускает Lock/Hold/Swap.
    /// </summary>
    private async Task RunSinglePipelineAsync(ECSWorld world, int pipelineIndex)
    {
        var cfg = _pipelines[pipelineIndex];
        var stats = _pipelineStats[pipelineIndex];
        var rng = new Random(Guid.NewGuid().GetHashCode());

        while (!_stopSignal)
        {
            // ═══ Шаг 1: Поиск сущностей через SearchGraph ═══
            IEnumerable<ECSEntity> foundEntities;
            try
            {
                foundEntities = world.entityManager.SearchGraph(
                    parentScope: null,
                    withComponentTypes: cfg.WithComponents,
                    withoutComponentTypes: cfg.WithoutComponents
                );

                Interlocked.Increment(ref stats.SearchOps);
            }
            catch
            {
                continue;
            }

            // Материализуем и считаем
            var entityBatch = foundEntities.ToList();
            Interlocked.Add(ref stats.SearchEntitiesFound, entityBatch.Count);

            if (entityBatch.Count == 0)
            {
                // Нет подходящих сущностей — короткий yield чтобы не спинить
                await Task.Yield();
                continue;
            }

            Interlocked.Increment(ref stats.TotalCycleCount);

            // Берём batch — не больше EntitiesPerBatch случайных из найденных
            if (entityBatch.Count > EntitiesPerBatch)
            {
                // Fisher-Yates partial shuffle
                for (int i = 0; i < EntitiesPerBatch && i < entityBatch.Count; i++)
                {
                    int j = rng.Next(i, entityBatch.Count);
                    var tmp = entityBatch[i];
                    entityBatch[i] = entityBatch[j];
                    entityBatch[j] = tmp;
                }
                entityBatch = entityBatch.Take(EntitiesPerBatch).ToList();
            }

            // ═══ Шаг 2: Параллельный запуск трёх операций на batch'е ═══
            var lockTask = RunLockOperationsAsync(entityBatch, cfg, stats, rng);
            var holdTask = RunHoldOperationsAsync(entityBatch, cfg, stats, rng);
            var swapTask = RunSwapOperationsAsync(entityBatch, cfg, stats, rng);

            await Task.WhenAll(lockTask, holdTask, swapTask);
        }
    }

    /// <summary>
    /// Операция A: Read-Lock.
    /// Для каждой сущности из batch'а захватывает read-lock на все WithComponents,
    /// удерживает Task.Delay, затем отпускает.
    /// </summary>
    private async Task RunLockOperationsAsync(
        List<ECSEntity> batch, PipelineConfig cfg, PipelineStats stats, Random rng)
    {
        var tasks = new List<Task>();

        foreach (var entity in batch)
        {
            // Каждая сущность обрабатывается параллельно
            tasks.Add(Task.Run(async () =>
            {
                var tokens = new List<IDisposable>();
                try
                {
                    bool anyLocked = false;

                    // Захватываем read-lock на все With-компоненты
                    foreach (var compType in cfg.WithComponents)
                    {
                        try
                        {
                            var result = await entity.entityComponents.GetReadLockedComponentAsync(compType);
                            if (result.Success)
                            {
                                tokens.Add(result.Token);
                                anyLocked = true;
                            }
                        }
                        catch { }
                    }

                    if (anyLocked)
                    {
                        // Имитация работы системы — удерживаем блокировки
                        int delayMs = rng.Next(LockDelayMinMs, LockDelayMaxMs + 1);
                        var sw = Stopwatch.StartNew();
                        await Task.Delay(delayMs);
                        sw.Stop();
                        Interlocked.Add(ref stats.LockDelayTicks, sw.ElapsedTicks);
                        Interlocked.Increment(ref stats.LockOps);
                    }
                    else
                    {
                        Interlocked.Increment(ref stats.LockFailed);
                    }
                }
                catch
                {
                    Interlocked.Increment(ref stats.LockFailed);
                }
                finally
                {
                    foreach (var t in tokens)
                        try { t?.Dispose(); } catch { }
                }
            }));
        }

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Операция B: Hold — блокирует добавление Without-компонентов.
    /// Для каждой сущности захватывает HoldComponentAdditionAsync на все WithoutComponents,
    /// удерживает Task.Delay, затем отпускает.
    /// </summary>
    private async Task RunHoldOperationsAsync(
        List<ECSEntity> batch, PipelineConfig cfg, PipelineStats stats, Random rng)
    {
        var tasks = new List<Task>();

        foreach (var entity in batch)
        {
            tasks.Add(Task.Run(async () =>
            {
                var tokens = new List<IDisposable>();
                try
                {
                    bool anyHeld = false;

                    foreach (var compType in cfg.WithoutComponents)
                    {
                        try
                        {
                            var holdResult = await entity.entityComponents.HoldComponentAdditionAsync(compType);
                            if (holdResult.Success)
                            {
                                tokens.Add(holdResult.LockToken);
                                anyHeld = true;
                            }
                        }
                        catch { }
                    }

                    if (anyHeld)
                    {
                        int delayMs = rng.Next(HoldDelayMinMs, HoldDelayMaxMs + 1);
                        var sw = Stopwatch.StartNew();
                        await Task.Delay(delayMs);
                        sw.Stop();
                        Interlocked.Add(ref stats.HoldDelayTicks, sw.ElapsedTicks);
                        Interlocked.Increment(ref stats.HoldOps);
                    }
                    else
                    {
                        Interlocked.Increment(ref stats.HoldFailed);
                    }
                }
                catch
                {
                    Interlocked.Increment(ref stats.HoldFailed);
                }
                finally
                {
                    foreach (var t in tokens)
                        try { t?.Dispose(); } catch { }
                }
            }));
        }

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Операция C: Swap — удаляет RemoveComponents и добавляет AddComponents.
    /// Для каждой сущности из batch'а: удаляет указанные компоненты, аллоцирует
    /// и добавляет замещающие. Аллокация замеряется отдельно.
    /// </summary>
    private async Task RunSwapOperationsAsync(
        List<ECSEntity> batch, PipelineConfig cfg, PipelineStats stats, Random rng)
    {
        var tasks = new List<Task>();

        foreach (var entity in batch)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    // Удаляем компоненты
                    foreach (var removeType in cfg.RemoveComponents)
                    {
                        try
                        {
                            if (await entity.HasComponentAsync(removeType))
                            {
                                await entity.RemoveComponentAsync(removeType);
                            }
                        }
                        catch { }
                    }

                    // Аллоцируем новые (замеряем время аллокации)
                    var allocSw = Stopwatch.StartNew();
                    var newComponents = new List<ECSComponent>();
                    foreach (var addType in cfg.AddComponents)
                    {
                        int idx = Array.IndexOf(AllComponentTypes, addType);
                        if (idx >= 0)
                            newComponents.Add(ComponentFactories[idx]());
                    }
                    allocSw.Stop();
                    Interlocked.Add(ref stats.SwapAllocTicks, allocSw.ElapsedTicks);

                    // Добавляем
                    foreach (var comp in newComponents)
                    {
                        try
                        {
                            await entity.AddOrChangeComponentAsync(comp);
                        }
                        catch { }
                    }

                    Interlocked.Increment(ref stats.SwapOps);
                }
                catch
                {
                    Interlocked.Increment(ref stats.SwapFailed);
                }
            }));
        }

        await Task.WhenAll(tasks);
    }

    // ═══════════════════════════════════════════════════════
    //  Отчёт
    // ═══════════════════════════════════════════════════════

    private string ShortName(Type t)
    {
        var name = t.Name;
        return name.EndsWith("Component") ? name.Substring(0, name.Length - 9) : name;
    }

    private void PrintReport(TimeSpan totalElapsed)
    {
        double totalMs = totalElapsed.TotalMilliseconds;
        double ticksPerMs = Stopwatch.Frequency / 1000.0;

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("╔══════════════════════════════════════════════════════════════════════════════════════╗");
        sb.AppendLine("║                  PIPELINE STRESS-TEST RESULTS REPORT                                ║");
        sb.AppendLine("║                  20 Parallel ECS-System Conveyors (Async)                           ║");
        sb.AppendLine("╠══════════════════════════════════════════════════════════════════════════════════════╣");
        sb.AppendLine($"║  Wall-clock time:   {totalMs:F0} ms ({totalElapsed.TotalSeconds:F1}s)");
        sb.AppendLine($"║  Entities:          {_entityArray.Length}");
        sb.AppendLine($"║  Pipelines:         {PipelineCount}");
        sb.AppendLine($"║  Batch size:        {EntitiesPerBatch}");
        sb.AppendLine("╠══════════════════════════════════════════════════════════════════════════════════════╣");
        sb.AppendLine();

        // ── Таблица per-pipeline ──
        sb.AppendLine(string.Format("  {0,-14} | {1,6} | {2,8} | {3,9} | {4,8} | {5,9} | {6,8} | {7,9} | {8,7}",
            "Pipeline", "Search", "Avg Ent", "LockOps", "LockFail", "HoldOps", "HoldFail", "SwapOps", "Cycles"));
        sb.AppendLine("  " + new string('─', 110));

        long totalSearch = 0, totalFound = 0;
        long totalLock = 0, totalLockFail = 0;
        long totalHold = 0, totalHoldFail = 0;
        long totalSwap = 0, totalSwapFail = 0;
        long totalCycles = 0;
        long totalLockDelayTicks = 0, totalHoldDelayTicks = 0, totalSwapAllocTicks = 0;

        for (int p = 0; p < PipelineCount; p++)
        {
            var s = _pipelineStats[p];
            double avgEnt = s.SearchOps > 0 ? (double)s.SearchEntitiesFound / s.SearchOps : 0;

            sb.AppendLine(string.Format("  {0,-14} | {1,6} | {2,8:F1} | {3,9} | {4,8} | {5,9} | {6,8} | {7,9} | {8,7}",
                _pipelines[p].Name,
                s.SearchOps, avgEnt,
                s.LockOps, s.LockFailed,
                s.HoldOps, s.HoldFailed,
                s.SwapOps,
                s.TotalCycleCount));

            totalSearch += s.SearchOps;
            totalFound += s.SearchEntitiesFound;
            totalLock += s.LockOps;
            totalLockFail += s.LockFailed;
            totalHold += s.HoldOps;
            totalHoldFail += s.HoldFailed;
            totalSwap += s.SwapOps;
            totalSwapFail += s.SwapFailed;
            totalCycles += s.TotalCycleCount;
            totalLockDelayTicks += s.LockDelayTicks;
            totalHoldDelayTicks += s.HoldDelayTicks;
            totalSwapAllocTicks += s.SwapAllocTicks;
        }

        sb.AppendLine("  " + new string('─', 110));

        // ── Агрегированная статистика ──
        sb.AppendLine();
        sb.AppendLine("╠══════════════════════════════════════════════════════════════════════════════════════╣");
        sb.AppendLine("║  AGGREGATED TOTALS");
        sb.AppendLine("╠══════════════════════════════════════════════════════════════════════════════════════╣");

        sb.AppendLine($"║  SearchGraph calls:       {totalSearch:N0}");
        sb.AppendLine($"║  Total entities matched:  {totalFound:N0}  (avg {(totalSearch > 0 ? (double)totalFound / totalSearch : 0):F1} per search)");
        sb.AppendLine($"║  Total pipeline cycles:   {totalCycles:N0}");
        sb.AppendLine("║");

        // ── Lock throughput ──
        double lockDelayMs = totalLockDelayTicks / ticksPerMs;
        double lockWallMs = totalMs * PipelineCount; // каждый pipeline — отдельный поток
        double lockNetMs = Math.Max(lockWallMs - lockDelayMs, 1);
        double lockRaw = totalLock / lockWallMs;
        double lockNet = totalLock / lockNetMs;

        sb.AppendLine("║  ▸ READ-LOCK Operations");
        sb.AppendLine($"║    Completed:  {totalLock:N0}    Failed: {totalLockFail:N0}");
        sb.AppendLine($"║    Delay time: {lockDelayMs:F1} ms");
        sb.AppendLine($"║    RAW:        {lockRaw:F4} ops/ms    NET: {lockNet:F4} ops/ms");
        sb.AppendLine("║");

        // ── Hold throughput ──
        double holdDelayMs = totalHoldDelayTicks / ticksPerMs;
        double holdWallMs = lockWallMs;
        double holdNetMs = Math.Max(holdWallMs - holdDelayMs, 1);
        double holdRaw = totalHold / holdWallMs;
        double holdNet = totalHold / holdNetMs;

        sb.AppendLine("║  ▸ HOLD Operations");
        sb.AppendLine($"║    Completed:  {totalHold:N0}    Failed: {totalHoldFail:N0}");
        sb.AppendLine($"║    Delay time: {holdDelayMs:F1} ms");
        sb.AppendLine($"║    RAW:        {holdRaw:F4} ops/ms    NET: {holdNet:F4} ops/ms");
        sb.AppendLine("║");

        // ── Swap throughput ──
        double swapAllocMs = totalSwapAllocTicks / ticksPerMs;
        double swapWallMs = lockWallMs;
        double swapNetMs = Math.Max(swapWallMs - swapAllocMs, 1);
        double swapRaw = totalSwap / swapWallMs;
        double swapNet = totalSwap / swapNetMs;

        sb.AppendLine("║  ▸ SWAP Operations (Remove + Add)");
        sb.AppendLine($"║    Completed:  {totalSwap:N0}    Failed: {totalSwapFail:N0}");
        sb.AppendLine($"║    Alloc time: {swapAllocMs:F1} ms");
        sb.AppendLine($"║    RAW:        {swapRaw:F4} ops/ms    NET: {swapNet:F4} ops/ms");
        sb.AppendLine("║");

        // ── Combined ──
        double allOps = totalLock + totalHold + totalSwap + totalSearch;
        double allNetMs = lockNetMs + holdNetMs + swapNetMs;
        double combinedOpsPerMs = allOps / allNetMs;

        sb.AppendLine("╠══════════════════════════════════════════════════════════════════════════════════════╣");
        sb.AppendLine("║  COMBINED");
        sb.AppendLine($"║    Total operations (lock+hold+swap+search): {allOps:N0}");
        sb.AppendLine($"║    Combined NET throughput:                   {combinedOpsPerMs:F4} ops/ms");
        sb.AppendLine($"║    Estimated ops/sec:                        {combinedOpsPerMs * 1000:F0}");
        sb.AppendLine("║");

        // ── Per entity ──
        double lockPerEnt = (double)totalLock / _entityArray.Length;
        double holdPerEnt = (double)totalHold / _entityArray.Length;
        double swapPerEnt = (double)totalSwap / _entityArray.Length;
        sb.AppendLine($"║  Avg lock ops per entity:  {lockPerEnt:F2}");
        sb.AppendLine($"║  Avg hold ops per entity:  {holdPerEnt:F2}");
        sb.AppendLine($"║  Avg swap ops per entity:  {swapPerEnt:F2}");

        sb.AppendLine("║");
        sb.AppendLine("╚══════════════════════════════════════════════════════════════════════════════════════╝");

        NLogger.Log(sb.ToString());
    }
}