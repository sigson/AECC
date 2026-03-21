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

namespace Threads
{
    
    public class SimulationThreads
    {
        // ─── Все типы компонентов для рандомного выбора ───
        private static readonly Type[] AllComponentTypes = new Type[]
        {
            typeof(HealthComponent), typeof(SpeedComponent), typeof(RangeDamagerComponent),
            typeof(MeleeDamagerComponent), typeof(InTimeoutComponent), typeof(ManaComponent),
            typeof(StaminaComponent), typeof(ArmorComponent), typeof(MagicResistanceComponent),
            typeof(CritChanceComponent), typeof(EvasionComponent), typeof(PositionComponent),
            typeof(VelocityComponent), typeof(AccelerationComponent), typeof(GravityComponent),
            typeof(RotationComponent), typeof(ScaleComponent), typeof(PoisonedComponent),
            typeof(BurningComponent), typeof(FrozenComponent), typeof(StunnedComponent),
            typeof(RegenerationComponent), typeof(TargetComponent), typeof(PathfindingComponent),
            typeof(AggroComponent), typeof(InventoryComponent), typeof(ExperienceComponent),
            typeof(LevelComponent), typeof(GoldComponent), typeof(ColliderComponent),
            typeof(StealthComponent), typeof(ShieldComponent), typeof(TeamComponent),
            typeof(LifetimeComponent), typeof(SoundEmitterComponent)
        };

        // ─── Фабрики компонентов (индексы совпадают с AllComponentTypes) ───
        private static readonly Func<ECSComponent>[] ComponentFactories = new Func<ECSComponent>[]
        {
            () => new HealthComponent(), () => new SpeedComponent(), () => new RangeDamagerComponent(),
            () => new MeleeDamagerComponent(), () => new InTimeoutComponent(), () => new ManaComponent(),
            () => new StaminaComponent(), () => new ArmorComponent(), () => new MagicResistanceComponent(),
            () => new CritChanceComponent(), () => new EvasionComponent(), () => new PositionComponent(),
            () => new VelocityComponent(), () => new AccelerationComponent(), () => new GravityComponent(),
            () => new RotationComponent(), () => new ScaleComponent(), () => new PoisonedComponent(),
            () => new BurningComponent(), () => new FrozenComponent(), () => new StunnedComponent(),
            () => new RegenerationComponent(), () => new TargetComponent(), () => new PathfindingComponent(),
            () => new AggroComponent(), () => new InventoryComponent(), () => new ExperienceComponent(),
            () => new LevelComponent(), () => new GoldComponent(), () => new ColliderComponent(),
            () => new StealthComponent(), () => new ShieldComponent(), () => new TeamComponent(),
            () => new LifetimeComponent(), () => new SoundEmitterComponent()
        };

        private const int MaxEntities = 100000;
        private const int SimulationDurationMs = 30_000; // 30 секунд симуляции
        private const int LockDelayMinMs = 1;
        private const int LockDelayMaxMs = 5;
        private const int HoldDelayMinMs = 1;
        private const int HoldDelayMaxMs = 5;
        private const int ComponentCountMin = 7;
        private const int ComponentCountMax = 15;
        private const int InitComponentCountMin = 15;
        private const int InitComponentCountMax = 26;
        private const int ParallelLockWorkers = 8;
        private const int ParallelSwapWorkers = 4;
        private const int ParallelHoldWorkers = 4;

        // ─── Счётчики операций (атомарные) ───
        private long _lockOpsCompleted = 0;
        private long _swapOpsCompleted = 0;
        private long _holdOpsCompleted = 0;
        private long _lockOpsFailed = 0;
        private long _swapOpsFailed = 0;
        private long _holdOpsFailed = 0;

        // ─── Накопленные задержки (в тиках Stopwatch) ───
        private long _lockDelayTicks = 0;
        private long _swapAllocTicks = 0;
        private long _holdDelayTicks = 0;

        // ─── Флаг остановки ───
        private volatile bool _stopSignal = false;

        // ─── Кэш сущностей для быстрого random access ───
        private ECSEntity[] _entityArray;

        public void Start()
        {
            var world = ECSWorld.GetWorld(0);

            NLogger.Log("╔══════════════════════════════════════════════════════════════╗");
            NLogger.Log("║     THREADED (TaskEx.RunAsync) ECS STRESS-TEST SIMULATION   ║");
            NLogger.Log("╚══════════════════════════════════════════════════════════════╝");
            NLogger.Log($"  Entities: {MaxEntities}  |  Duration: {SimulationDurationMs / 1000}s");
            NLogger.Log($"  Lock workers: {ParallelLockWorkers}  |  Swap workers: {ParallelSwapWorkers}  |  Hold workers: {ParallelHoldWorkers}");
            NLogger.Log("");

            // ─── Фаза 1: Создание сущностей (sync mode — LockedDictionary) ───
            NLogger.Log("▸ Phase 1: Creating entities (sync mode)...");
            var createSw = Stopwatch.StartNew();

            var entities = new ConcurrentBag<ECSEntity>();
            int halfMax = MaxEntities / 2;
            var fillDone = new CountdownEvent(2);
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
                    {
                        entityComponents.Add(ComponentFactories[idx]());
                    }

                    // asyncMode = false (default) — используется синхронный LockedDictionary
                    var entity = new ECSEntity(world, entityComponents.ToArray());
                    entities.Add(entity);
                }
                //fillDone.Signal();
            };

            // Запускаем через пул потоков (TaskEx.RunAsync)
            TaskEx.RunAsync(() => {fillAction();end1 = true;});
            TaskEx.RunAsync(() => {fillAction();end2 = true;});
            //TaskEx.RunAsync(fillAction);

            // Ожидаем завершения через PredicateExecutor (как в оригинале)
            var predicate = new PredicateExecutor("flush_threads", 
                new List<Func<bool>>() { () => end1 && end2 }, () =>
            {
                createSw.Stop();
                NLogger.Log($"  Created {entities.Count} entities in {createSw.Elapsed.TotalMilliseconds:F0}ms");

                // ─── Фаза 2: Синхронная регистрация в мире ───
                NLogger.Log("▸ Phase 2: Registering entities in world (sync)...");
                var regSw = Stopwatch.StartNew();

                var entityList = entities.ToList();
                foreach (var e in entityList)
                {
                    world.entityManager.AddNewEntity(e);
                }

                regSw.Stop();
                NLogger.Log($"  Registered {entityList.Count} entities in {regSw.Elapsed.TotalMilliseconds:F0}ms");

                _entityArray = entityList.ToArray();

                // ─── Фаза 3: Запуск нагрузочных тестов через потоки ───
                RunStressTestThreaded(world);

            }, 1000, 30000).Start();
        }

        private void RunStressTestThreaded(ECSWorld world)
        {
            NLogger.Log("");
            NLogger.Log("▸ Phase 3: Running parallel stress tests (thread pool)...");
            NLogger.Log($"  Duration: {SimulationDurationMs / 1000}s");
            NLogger.Log("");

            _stopSignal = false;
            var globalSw = Stopwatch.StartNew();

            // Общее количество воркеров
            int totalWorkers = ParallelLockWorkers + ParallelSwapWorkers + ParallelHoldWorkers;
            var allDone = new CountdownEvent(totalWorkers);

            // Workload 1: Массовые блокировки через GetReadLockedComponent + Thread.Sleep
            for (int w = 0; w < ParallelLockWorkers; w++)
            {
                TaskEx.RunAsync(() =>
                {
                    try { LockWorkloadThreaded(); }
                    finally { allDone.Signal(); }
                });
            }

            // Workload 2: Удаление/добавление компонентов (swap)
            for (int w = 0; w < ParallelSwapWorkers; w++)
            {
                TaskEx.RunAsync(() =>
                {
                    try { SwapWorkloadThreaded(); }
                    finally { allDone.Signal(); }
                });
            }

            // Workload 3: Hold отсутствия компонентов
            for (int w = 0; w < ParallelHoldWorkers; w++)
            {
                TaskEx.RunAsync(() =>
                {
                    try { HoldWorkloadThreaded(); }
                    finally { allDone.Signal(); }
                });
            }

            // Таймер остановки — тоже в потоке, чтобы не блокировать вызывающий
            TaskEx.RunAsync(() =>
            {
                Thread.Sleep(SimulationDurationMs);
                _stopSignal = true;

                // Ждём завершения всех воркеров
                allDone.Wait(TimeSpan.FromSeconds(60));
                globalSw.Stop();

                // ─── Фаза 4: Отчёт ───
                PrintReport(globalSw.Elapsed);
            });
        }

        /// <summary>
        /// Workload 1: Массовые взаимные блокировки (синхронный API).
        /// Обходит случайные сущности, каждая берёт 2 другие случайные,
        /// блокирует 7-15 компонентов через GetReadLockedComponent, удерживает Thread.Sleep.
        /// </summary>
        private void LockWorkloadThreaded()
        {
            var rng = new Random(Guid.NewGuid().GetHashCode());
            int entityCount = _entityArray.Length;

            while (!_stopSignal)
            {
                int selfIdx = rng.Next(entityCount);
                int otherIdx1 = rng.Next(entityCount);
                int otherIdx2 = rng.Next(entityCount);

                while (otherIdx1 == selfIdx) otherIdx1 = rng.Next(entityCount);
                while (otherIdx2 == selfIdx || otherIdx2 == otherIdx1) otherIdx2 = rng.Next(entityCount);

                var targets = new ECSEntity[] { _entityArray[otherIdx1], _entityArray[otherIdx2] };

                foreach (var targetEntity in targets)
                {
                    int lockCount = rng.Next(ComponentCountMin, ComponentCountMax + 1);
                    var selectedTypes = GetRandomComponentTypes(rng, lockCount);
                    var acquiredTokens = new List<RWLock.LockToken>();

                    try
                    {
                        // Последовательно берём read-lock на каждый компонент (синхронно)
                        foreach (var compType in selectedTypes)
                        {
                            try
                            {
                                ECSComponent comp;
                                RWLock.LockToken token;
                                if (targetEntity.entityComponents.GetReadLockedComponent(compType, out comp, out token))
                                {
                                    acquiredTokens.Add(token);
                                }
                                // Компонент может отсутствовать — нормально
                            }
                            catch
                            {
                                // Ошибка блокировки — продолжаем
                            }
                        }

                        if (acquiredTokens.Count > 0)
                        {
                            // Замеряем и вычитаем время задержки (Thread.Sleep вместо Task.Delay)
                            int delayMs = rng.Next(LockDelayMinMs, LockDelayMaxMs + 1);
                            var delaySw = Stopwatch.StartNew();
                            Thread.Sleep(delayMs);
                            delaySw.Stop();
                            Interlocked.Add(ref _lockDelayTicks, delaySw.ElapsedTicks);

                            Interlocked.Increment(ref _lockOpsCompleted);
                        }
                        else
                        {
                            Interlocked.Increment(ref _lockOpsFailed);
                        }
                    }
                    catch
                    {
                        Interlocked.Increment(ref _lockOpsFailed);
                    }
                    finally
                    {
                        foreach (var token in acquiredTokens)
                        {
                            try { token?.Dispose(); } catch { }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Workload 2: Замена компонентов (синхронный API).
        /// Выбирает случайную сущность, удаляет 7-15 компонентов, добавляет другие.
        /// </summary>
        private void SwapWorkloadThreaded()
        {
            var rng = new Random(Guid.NewGuid().GetHashCode());
            int entityCount = _entityArray.Length;

            while (!_stopSignal)
            {
                int entityIdx = rng.Next(entityCount);
                var entity = _entityArray[entityIdx];
                int swapCount = rng.Next(ComponentCountMin, ComponentCountMax + 1);

                try
                {
                    // Выбираем случайные типы для удаления
                    var typesToRemove = GetRandomComponentTypes(rng, swapCount);

                    // Удаляем те, которые присутствуют
                    var removedTypes = new HashSet<Type>();
                    foreach (var compType in typesToRemove)
                    {
                        try
                        {
                            if (entity.HasComponent(compType))
                            {
                                entity.RemoveComponent(compType);
                                removedTypes.Add(compType);
                            }
                        }
                        catch { } // Конкурентное удаление — нормальная ситуация
                    }

                    // Выбираем типы для добавления (те, которых нет)
                    var typesToAdd = AllComponentTypes
                        .Where(t => !removedTypes.Contains(t))
                        .OrderBy(_ => rng.Next())
                        .Take(swapCount)
                        .ToList();

                    // Замеряем время аллокации компонентов (вычтем из итога)
                    var allocSw = Stopwatch.StartNew();
                    var newComponents = new List<ECSComponent>();
                    foreach (var compType in typesToAdd)
                    {
                        int typeIndex = Array.IndexOf(AllComponentTypes, compType);
                        if (typeIndex >= 0)
                        {
                            newComponents.Add(ComponentFactories[typeIndex]());
                        }
                    }
                    allocSw.Stop();
                    Interlocked.Add(ref _swapAllocTicks, allocSw.ElapsedTicks);

                    // Добавляем через синхронный AddOrChangeComponent
                    foreach (var comp in newComponents)
                    {
                        try
                        {
                            entity.AddOrChangeComponent(comp);
                        }
                        catch { }
                    }

                    Interlocked.Increment(ref _swapOpsCompleted);
                }
                catch
                {
                    Interlocked.Increment(ref _swapOpsFailed);
                }
            }
        }

        /// <summary>
        /// Workload 3: Hold-блокировка отсутствия компонентов (синхронный API).
        /// HoldComponentAddition возвращает RWLock.LockToken через out-параметр.
        /// Удерживает Thread.Sleep, затем Dispose.
        /// </summary>
        private void HoldWorkloadThreaded()
        {
            var rng = new Random(Guid.NewGuid().GetHashCode());
            int entityCount = _entityArray.Length;

            while (!_stopSignal)
            {
                int entityIdx = rng.Next(entityCount);
                var entity = _entityArray[entityIdx];
                int holdCount = rng.Next(ComponentCountMin, ComponentCountMax + 1);

                var selectedTypes = GetRandomComponentTypes(rng, holdCount);
                var acquiredTokens = new List<RWLock.LockToken>();

                try
                {
                    foreach (var compType in selectedTypes)
                    {
                        try
                        {
                            RWLock.LockToken token;
                            if (entity.entityComponents.HoldComponentAddition(compType, out token))
                            {
                                acquiredTokens.Add(token);
                            }
                        }
                        catch { }
                    }

                    if (acquiredTokens.Count > 0)
                    {
                        int delayMs = rng.Next(HoldDelayMinMs, HoldDelayMaxMs + 1);
                        var delaySw = Stopwatch.StartNew();
                        Thread.Sleep(delayMs);
                        delaySw.Stop();
                        Interlocked.Add(ref _holdDelayTicks, delaySw.ElapsedTicks);

                        Interlocked.Increment(ref _holdOpsCompleted);
                    }
                    else
                    {
                        Interlocked.Increment(ref _holdOpsFailed);
                    }
                }
                catch
                {
                    Interlocked.Increment(ref _holdOpsFailed);
                }
                finally
                {
                    foreach (var token in acquiredTokens)
                    {
                        try { token?.Dispose(); } catch { }
                    }
                }
            }
        }

        /// <summary>
        /// Fisher-Yates partial shuffle для быстрого выбора без LINQ-аллокаций.
        /// </summary>
        private Type[] GetRandomComponentTypes(Random rng, int count)
        {
            var indices = new int[AllComponentTypes.Length];
            for (int i = 0; i < indices.Length; i++) indices[i] = i;

            int take = Math.Min(count, indices.Length);
            for (int i = 0; i < take; i++)
            {
                int j = rng.Next(i, indices.Length);
                int tmp = indices[i];
                indices[i] = indices[j];
                indices[j] = tmp;
            }

            var result = new Type[take];
            for (int i = 0; i < take; i++)
            {
                result[i] = AllComponentTypes[indices[i]];
            }
            return result;
        }

        private void PrintReport(TimeSpan totalElapsed)
        {
            double totalMs = totalElapsed.TotalMilliseconds;
            double ticksPerMs = Stopwatch.Frequency / 1000.0;

            double lockDelayMs = _lockDelayTicks / ticksPerMs;
            double swapAllocMs = _swapAllocTicks / ticksPerMs;
            double holdDelayMs = _holdDelayTicks / ticksPerMs;

            double lockWallMs = totalMs * ParallelLockWorkers;
            double swapWallMs = totalMs * ParallelSwapWorkers;
            double holdWallMs = totalMs * ParallelHoldWorkers;

            double lockNetMs = Math.Max(lockWallMs - lockDelayMs, 1);
            double swapNetMs = Math.Max(swapWallMs - swapAllocMs, 1);
            double holdNetMs = Math.Max(holdWallMs - holdDelayMs, 1);

            double lockOpsPerMs = _lockOpsCompleted / lockNetMs;
            double swapOpsPerMs = _swapOpsCompleted / swapNetMs;
            double holdOpsPerMs = _holdOpsCompleted / holdNetMs;

            double totalOps = _lockOpsCompleted + _swapOpsCompleted + _holdOpsCompleted;
            double totalNetMs = lockNetMs + swapNetMs + holdNetMs;
            double combinedOpsPerMs = totalOps / totalNetMs;

            double lockRawOpsPerMs = _lockOpsCompleted / lockWallMs;
            double swapRawOpsPerMs = _swapOpsCompleted / swapWallMs;
            double holdRawOpsPerMs = _holdOpsCompleted / holdWallMs;

            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("╔══════════════════════════════════════════════════════════════════════════╗");
            sb.AppendLine("║              THREADED STRESS-TEST RESULTS REPORT                        ║");
            sb.AppendLine("║              (TaskEx.RunAsync / Thread Pool / RWLock)                    ║");
            sb.AppendLine("╠══════════════════════════════════════════════════════════════════════════╣");
            sb.AppendLine($"║  Total wall-clock time:  {totalMs:F0} ms ({totalElapsed.TotalSeconds:F1}s)");
            sb.AppendLine($"║  Entities:               {_entityArray.Length}");
            sb.AppendLine($"║  Workers:                Lock={ParallelLockWorkers}  Swap={ParallelSwapWorkers}  Hold={ParallelHoldWorkers}");
            sb.AppendLine("╠══════════════════════════════════════════════════════════════════════════╣");

            sb.AppendLine("║");
            sb.AppendLine("║  ▸ WORKLOAD 1: Read-Lock (GetReadLockedComponent + Thread.Sleep)");
            sb.AppendLine($"║    Operations completed:  {_lockOpsCompleted:N0}");
            sb.AppendLine($"║    Operations failed:     {_lockOpsFailed:N0}");
            sb.AppendLine($"║    Cumulative delay:      {lockDelayMs:F1} ms (Thread.Sleep inside locks)");
            sb.AppendLine($"║    Wall time (all workers): {lockWallMs:F0} ms");
            sb.AppendLine($"║    Net time (- delays):   {lockNetMs:F0} ms");
            sb.AppendLine($"║    ── RAW throughput:     {lockRawOpsPerMs:F4} ops/ms");
            sb.AppendLine($"║    ── NET throughput:     {lockOpsPerMs:F4} ops/ms");

            sb.AppendLine("║");
            sb.AppendLine("║  ▸ WORKLOAD 2: Component Swap (Remove + Add, sync)");
            sb.AppendLine($"║    Operations completed:  {_swapOpsCompleted:N0}");
            sb.AppendLine($"║    Operations failed:     {_swapOpsFailed:N0}");
            sb.AppendLine($"║    Cumulative alloc time: {swapAllocMs:F1} ms (new component instances)");
            sb.AppendLine($"║    Wall time (all workers): {swapWallMs:F0} ms");
            sb.AppendLine($"║    Net time (- alloc):    {swapNetMs:F0} ms");
            sb.AppendLine($"║    ── RAW throughput:     {swapRawOpsPerMs:F4} ops/ms");
            sb.AppendLine($"║    ── NET throughput:     {swapOpsPerMs:F4} ops/ms");

            sb.AppendLine("║");
            sb.AppendLine("║  ▸ WORKLOAD 3: Hold (absence lock via HoldComponentAddition, sync)");
            sb.AppendLine($"║    Operations completed:  {_holdOpsCompleted:N0}");
            sb.AppendLine($"║    Operations failed:     {_holdOpsFailed:N0}");
            sb.AppendLine($"║    Cumulative delay:      {holdDelayMs:F1} ms (Thread.Sleep inside holds)");
            sb.AppendLine($"║    Wall time (all workers): {holdWallMs:F0} ms");
            sb.AppendLine($"║    Net time (- delays):   {holdNetMs:F0} ms");
            sb.AppendLine($"║    ── RAW throughput:     {holdRawOpsPerMs:F4} ops/ms");
            sb.AppendLine($"║    ── NET throughput:     {holdOpsPerMs:F4} ops/ms");

            sb.AppendLine("║");
            sb.AppendLine("╠══════════════════════════════════════════════════════════════════════════╣");
            sb.AppendLine("║  COMBINED SUMMARY");
            sb.AppendLine($"║    Total operations:      {totalOps:N0}");
            sb.AppendLine($"║    Total net time:        {totalNetMs:F0} ms");
            sb.AppendLine($"║    Combined throughput:   {combinedOpsPerMs:F4} ops/ms");
            sb.AppendLine($"║    Estimated ops/sec:     {combinedOpsPerMs * 1000:F0}");
            sb.AppendLine("║");

            double lockPerEntity = (double)_lockOpsCompleted / _entityArray.Length;
            double swapPerEntity = (double)_swapOpsCompleted / _entityArray.Length;
            double holdPerEntity = (double)_holdOpsCompleted / _entityArray.Length;
            sb.AppendLine($"║  Avg lock ops per entity:   {lockPerEntity:F2}");
            sb.AppendLine($"║  Avg swap ops per entity:   {swapPerEntity:F2}");
            sb.AppendLine($"║  Avg hold ops per entity:   {holdPerEntity:F2}");

            sb.AppendLine("║");
            sb.AppendLine("╚══════════════════════════════════════════════════════════════════════════╝");

            NLogger.Log(sb.ToString());
        }
    }
}