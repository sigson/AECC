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

namespace Async
{
    public class Simulation
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

        private const int MaxEntities = 10000;
        private const int SimulationDurationMs = 30_000; // 30 секунд симуляции
        private const int LockDelayMinMs = 1;
        private const int LockDelayMaxMs = 1;
        private const int HoldDelayMinMs = 1;
        private const int HoldDelayMaxMs = 1;
        private const int ComponentCountMin = 7;
        private const int ComponentCountMax = 7;
        private const int InitComponentCountMin = 15;
        private const int InitComponentCountMax = 26;
        private const int ParallelLockWorkers = 8;
        private const int ParallelSwapWorkers = 4;
        private const int ParallelHoldWorkers = 4;

        // ─── Счётчики операций ───
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
            NLogger.Log("║     ASYNC ECS STRESS-TEST SIMULATION                        ║");
            NLogger.Log("╚══════════════════════════════════════════════════════════════╝");
            NLogger.Log($"  Entities: {MaxEntities}  |  Duration: {SimulationDurationMs / 1000}s");
            NLogger.Log($"  Lock workers: {ParallelLockWorkers}  |  Swap workers: {ParallelSwapWorkers}  |  Hold workers: {ParallelHoldWorkers}");
            NLogger.Log("");

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

                    // Выбираем случайные компоненты
                    var indices = Enumerable.Range(0, AllComponentTypes.Length)
                        .OrderBy(_ => rng.Next())
                        .Take(componentCount - 1);

                    foreach (var idx in indices)
                    {
                        entityComponents.Add(ComponentFactories[idx]());
                    }

                    // asyncMode = true для использования async storage
                    var entity = new ECSEntity(world, entityComponents.ToArray(), asyncMode: true);
                    entities.Add(entity);
                }
            };

            TaskEx.RunAsync(() => { fillAction(); end1 = true; });
            TaskEx.RunAsync(() => { fillAction(); end2 = true; });

            // Ждём завершения заполнения
            var predicate = new PredicateExecutor("flush", new List<Func<bool>>() { () => end1 && end2 }, () =>
            {
                createSw.Stop();
                NLogger.Log($"  Created {entities.Count} entities in {createSw.Elapsed.TotalMilliseconds:F0}ms");

                // ─── Фаза 2: Регистрация в мире (async) ───
                NLogger.Log("▸ Phase 2: Registering entities in world (async)...");
                var regSw = Stopwatch.StartNew();

                var entityList = entities.ToList();
                var regTasks = new List<Task>();
                foreach (var e in entityList)
                {
                    // regTasks.Add(world.entityManager.AddNewEntityAsync(e));
                }
                Task.WhenAll(regTasks).Wait();

                regSw.Stop();
                NLogger.Log($"  Registered {entityList.Count} entities in {regSw.Elapsed.TotalMilliseconds:F0}ms");

                // Кэшируем массив для быстрого random-access
                _entityArray = entityList.ToArray();

                // ─── Фаза 3: Запуск нагрузочных тестов параллельно ───
                RunStressTestAsync(world).Wait();

            }, 1000, 30000).Start();
        }

        private async Task RunStressTestAsync(ECSWorld world)
        {
            NLogger.Log("");
            NLogger.Log("▸ Phase 3: Running parallel stress tests...");
            NLogger.Log($"  Duration: {SimulationDurationMs / 1000}s");
            NLogger.Log("");

            _stopSignal = false;
            var globalSw = Stopwatch.StartNew();

            // Запуск всех воркеров параллельно
            var tasks = new List<Task>();

            // Workload 1: Массовые блокировки (GetReadLockedComponentAsync + Task.Delay)
            for (int w = 0; w < ParallelLockWorkers; w++)
            {
                tasks.Add(Task.Run(() => LockWorkloadAsync()));
            }

            // Workload 2: Удаление/добавление компонентов (swap)
            for (int w = 0; w < ParallelSwapWorkers; w++)
            {
                tasks.Add(Task.Run(() => SwapWorkloadAsync()));
            }

            // Workload 3: Hold отсутствия компонентов
            for (int w = 0; w < ParallelHoldWorkers; w++)
            {
                tasks.Add(Task.Run(() => HoldWorkloadAsync()));
            }

            // Таймер остановки
            await Task.Delay(SimulationDurationMs);
            _stopSignal = true;

            // Ждём завершения всех воркеров
            await Task.WhenAll(tasks);
            globalSw.Stop();

            // ─── Фаза 4: Отчёт ───
            PrintReport(globalSw.Elapsed);
        }

        /// <summary>
        /// Workload 1: Массовые взаимные блокировки.
        /// Обходит все сущности, каждая берёт 2 случайные другие сущности,
        /// блокирует 7-15 случайных компонентов на каждой, удерживает короткое время.
        /// </summary>
        private async Task LockWorkloadAsync()
        {
            var rng = new Random(Guid.NewGuid().GetHashCode());
            int entityCount = _entityArray.Length;

            while (!_stopSignal)
            {
                // Выбираем текущую сущность и 2 другие случайные
                int selfIdx = rng.Next(entityCount);
                int otherIdx1 = rng.Next(entityCount);
                int otherIdx2 = rng.Next(entityCount);

                // Избегаем совпадений
                while (otherIdx1 == selfIdx) otherIdx1 = rng.Next(entityCount);
                while (otherIdx2 == selfIdx || otherIdx2 == otherIdx1) otherIdx2 = rng.Next(entityCount);

                var targets = new ECSEntity[] { _entityArray[otherIdx1], _entityArray[otherIdx2] };

                foreach (var targetEntity in targets)
                {
                    int lockCount = rng.Next(ComponentCountMin, ComponentCountMax + 1);
                    var selectedTypes = GetRandomComponentTypes(rng, lockCount);
                    var acquiredTokens = new List<IDisposable>();

                    try
                    {
                        bool allAcquired = true;

                        // Последовательно берём read-lock на каждый компонент
                        foreach (var compType in selectedTypes)
                        {
                            try
                            {
                                // var result = await targetEntity.entityComponents.GetReadLockedComponentAsync(compType);
                                // if (result.Success)
                                // {
                                //     acquiredTokens.Add(result.Token);
                                // }
                                // Компонент может отсутствовать — это нормально, пропускаем
                            }
                            catch
                            {
                                // Ошибка блокировки — продолжаем со следующим
                            }
                        }

                        if (acquiredTokens.Count > 0)
                        {
                            // Замеряем и вычитаем время задержки
                            int delayMs = rng.Next(LockDelayMinMs, LockDelayMaxMs + 1);
                            var delaySw = Stopwatch.StartNew();
                            await Task.Delay(delayMs);
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
                        // Освобождаем все захваченные блокировки
                        foreach (var token in acquiredTokens)
                        {
                            try { token?.Dispose(); } catch { }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Workload 2: Замена компонентов.
        /// Выбирает случайную сущность, удаляет 7-15 случайных компонентов,
        /// добавляет вместо них другие случайные компоненты.
        /// </summary>
        private async Task SwapWorkloadAsync()
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
                            // if (await entity.HasComponentAsync(compType))
                            // {
                            //     await entity.RemoveComponentAsync(compType);
                            //     removedTypes.Add(compType);
                            // }
                        }
                        catch { } // Конкурентное удаление — нормальная ситуация
                    }

                    // Выбираем типы для добавления (те, которых нет у сущности)
                    var typesToAdd = AllComponentTypes
                        .Where(t => !removedTypes.Contains(t))
                        .OrderBy(_ => rng.Next())
                        .Take(swapCount)
                        .ToList();

                    // Замеряем время аллокации компонентов (вычтем из общего)
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

                    // Добавляем новые компоненты
                    foreach (var comp in newComponents)
                    {
                        try
                        {
                            //await entity.AddOrChangeComponentAsync(comp);
                        }
                        catch { } // Конкурентный конфликт — нормально
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
        /// Workload 3: Hold-блокировка отсутствия компонентов.
        /// Выбирает случайную сущность и случайные компоненты,
        /// удерживает отсутствие этих компонентов (HoldComponentAdditionAsync) короткое время.
        /// </summary>
        private async Task HoldWorkloadAsync()
        {
            var rng = new Random(Guid.NewGuid().GetHashCode());
            int entityCount = _entityArray.Length;

            while (!_stopSignal)
            {
                int entityIdx = rng.Next(entityCount);
                var entity = _entityArray[entityIdx];
                int holdCount = rng.Next(ComponentCountMin, ComponentCountMax + 1);

                var selectedTypes = GetRandomComponentTypes(rng, holdCount);
                var acquiredTokens = new List<IDisposable>();

                try
                {
                    // Пытаемся захватить hold на отсутствие каждого компонента
                    foreach (var compType in selectedTypes)
                    {
                        try
                        {
                            // var holdResult = await entity.entityComponents.HoldComponentAdditionAsync(compType);
                            // if (holdResult.Success)
                            // {
                            //     acquiredTokens.Add(holdResult.LockToken);
                            // }
                        }
                        catch { }
                    }

                    if (acquiredTokens.Count > 0)
                    {
                        // Задержка удержания
                        int delayMs = rng.Next(HoldDelayMinMs, HoldDelayMaxMs + 1);
                        var delaySw = Stopwatch.StartNew();
                        await Task.Delay(delayMs);
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
        /// Формирует случайный набор типов компонентов заданного размера.
        /// </summary>
        private Type[] GetRandomComponentTypes(Random rng, int count)
        {
            // Fisher-Yates partial shuffle для быстрого выбора без аллокаций LINQ
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

        /// <summary>
        /// Формирует и выводит финальный отчёт.
        /// </summary>
        private void PrintReport(TimeSpan totalElapsed)
        {
            double totalMs = totalElapsed.TotalMilliseconds;
            double ticksPerMs = Stopwatch.Frequency / 1000.0;

            // Конвертация накопленных задержек из тиков в миллисекунды
            double lockDelayMs = _lockDelayTicks / ticksPerMs;
            double swapAllocMs = _swapAllocTicks / ticksPerMs;
            double holdDelayMs = _holdDelayTicks / ticksPerMs;

            // «Чистое» время (всё стеночное время минус суммарные задержки по каждому воркеру)
            // Поскольку воркеры параллельны, задержки складываются по воркерам, а не по стеночному времени.
            // Для нормировки: эффективное стеночное время per-workload = totalMs * workerCount
            double lockWallMs = totalMs * ParallelLockWorkers;
            double swapWallMs = totalMs * ParallelSwapWorkers;
            double holdWallMs = totalMs * ParallelHoldWorkers;

            double lockNetMs = Math.Max(lockWallMs - lockDelayMs, 1);
            double swapNetMs = Math.Max(swapWallMs - swapAllocMs, 1);
            double holdNetMs = Math.Max(holdWallMs - holdDelayMs, 1);

            // Операции / мс (чистая производительность)
            double lockOpsPerMs = _lockOpsCompleted / lockNetMs;
            double swapOpsPerMs = _swapOpsCompleted / swapNetMs;
            double holdOpsPerMs = _holdOpsCompleted / holdNetMs;

            // Суммарная пропускная способность
            double totalOps = _lockOpsCompleted + _swapOpsCompleted + _holdOpsCompleted;
            double totalNetMs = lockNetMs + swapNetMs + holdNetMs;
            double combinedOpsPerMs = totalOps / totalNetMs;

            // Грубая (raw) пропускная способность без вычитания задержек
            double lockRawOpsPerMs = _lockOpsCompleted / lockWallMs;
            double swapRawOpsPerMs = _swapOpsCompleted / swapWallMs;
            double holdRawOpsPerMs = _holdOpsCompleted / holdWallMs;

            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("╔══════════════════════════════════════════════════════════════════════════╗");
            sb.AppendLine("║                    STRESS-TEST RESULTS REPORT                           ║");
            sb.AppendLine("╠══════════════════════════════════════════════════════════════════════════╣");
            sb.AppendLine($"║  Total wall-clock time:  {totalMs:F0} ms ({totalElapsed.TotalSeconds:F1}s)");
            sb.AppendLine($"║  Entities:               {_entityArray.Length}");
            sb.AppendLine($"║  Workers:                Lock={ParallelLockWorkers}  Swap={ParallelSwapWorkers}  Hold={ParallelHoldWorkers}");
            sb.AppendLine("╠══════════════════════════════════════════════════════════════════════════╣");

            // ── Workload 1: Lock ──
            sb.AppendLine("║");
            sb.AppendLine("║  ▸ WORKLOAD 1: Read-Lock (GetReadLockedComponentAsync + Delay)");
            sb.AppendLine($"║    Operations completed:  {_lockOpsCompleted:N0}");
            sb.AppendLine($"║    Operations failed:     {_lockOpsFailed:N0}");
            sb.AppendLine($"║    Cumulative delay:      {lockDelayMs:F1} ms (Task.Delay inside locks)");
            sb.AppendLine($"║    Wall time (all workers): {lockWallMs:F0} ms");
            sb.AppendLine($"║    Net time (- delays):   {lockNetMs:F0} ms");
            sb.AppendLine($"║    ── RAW throughput:     {lockRawOpsPerMs:F4} ops/ms");
            sb.AppendLine($"║    ── NET throughput:     {lockOpsPerMs:F4} ops/ms");

            // ── Workload 2: Swap ──
            sb.AppendLine("║");
            sb.AppendLine("║  ▸ WORKLOAD 2: Component Swap (Remove + Add)");
            sb.AppendLine($"║    Operations completed:  {_swapOpsCompleted:N0}");
            sb.AppendLine($"║    Operations failed:     {_swapOpsFailed:N0}");
            sb.AppendLine($"║    Cumulative alloc time: {swapAllocMs:F1} ms (new component instances)");
            sb.AppendLine($"║    Wall time (all workers): {swapWallMs:F0} ms");
            sb.AppendLine($"║    Net time (- alloc):    {swapNetMs:F0} ms");
            sb.AppendLine($"║    ── RAW throughput:     {swapRawOpsPerMs:F4} ops/ms");
            sb.AppendLine($"║    ── NET throughput:     {swapOpsPerMs:F4} ops/ms");

            // ── Workload 3: Hold ──
            sb.AppendLine("║");
            sb.AppendLine("║  ▸ WORKLOAD 3: Hold (absence lock via HoldComponentAdditionAsync)");
            sb.AppendLine($"║    Operations completed:  {_holdOpsCompleted:N0}");
            sb.AppendLine($"║    Operations failed:     {_holdOpsFailed:N0}");
            sb.AppendLine($"║    Cumulative delay:      {holdDelayMs:F1} ms (Task.Delay inside holds)");
            sb.AppendLine($"║    Wall time (all workers): {holdWallMs:F0} ms");
            sb.AppendLine($"║    Net time (- delays):   {holdNetMs:F0} ms");
            sb.AppendLine($"║    ── RAW throughput:     {holdRawOpsPerMs:F4} ops/ms");
            sb.AppendLine($"║    ── NET throughput:     {holdOpsPerMs:F4} ops/ms");

            // ── Итого ──
            sb.AppendLine("║");
            sb.AppendLine("╠══════════════════════════════════════════════════════════════════════════╣");
            sb.AppendLine("║  COMBINED SUMMARY");
            sb.AppendLine($"║    Total operations:      {totalOps:N0}");
            sb.AppendLine($"║    Total net time:        {totalNetMs:F0} ms");
            sb.AppendLine($"║    Combined throughput:   {combinedOpsPerMs:F4} ops/ms");
            sb.AppendLine($"║    Estimated ops/sec:     {combinedOpsPerMs * 1000:F0}");
            sb.AppendLine("║");

            // ── Breakdown per-entity ──
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

    // ═══════════════════════════════════════════════════════
    //  КОМПОНЕНТЫ (не изменены)
    // ═══════════════════════════════════════════════════════

    [TypeUid(1000)]
    public class ViewComponent : ECSComponent
    {
        static new public long Id { get; set; } = 1000;
        protected override void OnAdded(ECSEntity entity) { base.OnAdded(entity); }
    }

    [TypeUid(1001)]
    public class HealthComponent : ECSComponent
    {
        static new public long Id { get; set; } = 1001;
        public float Health = 1000;
    }

    [TypeUid(1002)]
    public class SpeedComponent : ECSComponent
    {
        static new public long Id { get; set; } = 1002;
        public float Speed = 50;
    }

    [TypeUid(1003)]
    public class RangeDamagerComponent : ECSComponent
    {
        static new public long Id { get; set; } = 1003;
        public float Damage = 10;
        public float RangeDistance = 100;
        public float DamageTimeoutSec = 1.5f;
    }

    [TypeUid(1004)]
    public class MeleeDamagerComponent : ECSComponent
    {
        static new public long Id { get; set; } = 1004;
        public float Damage = 30;
        public float RangeDistance = 10;
        public float DamageTimeoutSec = 0.5f;
    }

    [TypeUid(1005)]
    public class InTimeoutComponent : ECSComponent
    {
        static new public long Id { get; set; } = 1005;
        public float Timeout = 0f;
        public float RemainTimeout = 0f;
    }

    [TypeUid(1006)]
    public class ManaComponent : ECSComponent
    {
        static new public long Id { get; set; } = 1006;
        public float MaxMana = 100f;
        public float CurrentMana = 100f;
    }

    [TypeUid(1007)]
    public class StaminaComponent : ECSComponent
    {
        static new public long Id { get; set; } = 1007;
        public float MaxStamina = 100f;
        public float CurrentStamina = 100f;
    }

    [TypeUid(1008)]
    public class ArmorComponent : ECSComponent
    {
        static new public long Id { get; set; } = 1008;
        public float ArmorValue = 15f;
    }

    [TypeUid(1009)]
    public class MagicResistanceComponent : ECSComponent
    {
        static new public long Id { get; set; } = 1009;
        public float ResistanceValue = 10f;
    }

    [TypeUid(1010)]
    public class CritChanceComponent : ECSComponent
    {
        static new public long Id { get; set; } = 1010;
        public float Probability = 0.05f;
        public float Multiplier = 2.0f;
    }

    [TypeUid(1011)]
    public class EvasionComponent : ECSComponent
    {
        static new public long Id { get; set; } = 1011;
        public float DodgeChance = 0.1f;
    }

    [TypeUid(1012)]
    public class PositionComponent : ECSComponent
    {
        static new public long Id { get; set; } = 1012;
        public float X = 0f;
        public float Y = 0f;
        public float Z = 0f;
    }

    [TypeUid(1013)]
    public class VelocityComponent : ECSComponent
    {
        static new public long Id { get; set; } = 1013;
        public float Vx = 0f;
        public float Vy = 0f;
    }

    [TypeUid(1014)]
    public class AccelerationComponent : ECSComponent
    {
        static new public long Id { get; set; } = 1014;
        public float Ax = 0f;
        public float Ay = 0f;
    }

    [TypeUid(1015)]
    public class GravityComponent : ECSComponent
    {
        static new public long Id { get; set; } = 1015;
        public float GravityScale = 1.0f;
    }

    [TypeUid(1016)]
    public class RotationComponent : ECSComponent
    {
        static new public long Id { get; set; } = 1016;
        public float AngleDegrees = 0f;
    }

    [TypeUid(1017)]
    public class ScaleComponent : ECSComponent
    {
        static new public long Id { get; set; } = 1017;
        public float ScaleX = 1.0f;
        public float ScaleY = 1.0f;
    }

    [TypeUid(1018)]
    public class PoisonedComponent : ECSComponent
    {
        static new public long Id { get; set; } = 1018;
        public float DamagePerTick = 5f;
        public float Duration = 10f;
    }

    [TypeUid(1019)]
    public class BurningComponent : ECSComponent
    {
        static new public long Id { get; set; } = 1019;
        public float DamagePerTick = 8f;
        public float Duration = 5f;
    }

    [TypeUid(1020)]
    public class FrozenComponent : ECSComponent
    {
        static new public long Id { get; set; } = 1020;
        public float SpeedDebuff = 0.5f;
        public float Duration = 3f;
    }

    [TypeUid(1021)]
    public class StunnedComponent : ECSComponent
    {
        static new public long Id { get; set; } = 1021;
        public float Duration = 2f;
    }

    [TypeUid(1022)]
    public class RegenerationComponent : ECSComponent
    {
        static new public long Id { get; set; } = 1022;
        public float HealPerTick = 2f;
        public float TickRateSec = 1f;
    }

    [TypeUid(1023)]
    public class TargetComponent : ECSComponent
    {
        static new public long Id { get; set; } = 1023;
        public long TargetEntityId = -1;
    }

    [TypeUid(1024)]
    public class PathfindingComponent : ECSComponent
    {
        static new public long Id { get; set; } = 1024;
        public float DestX = 0f;
        public float DestY = 0f;
    }

    [TypeUid(1025)]
    public class AggroComponent : ECSComponent
    {
        static new public long Id { get; set; } = 1025;
        public float AggroRadius = 250f;
    }

    [TypeUid(1026)]
    public class InventoryComponent : ECSComponent
    {
        static new public long Id { get; set; } = 1026;
        public int SlotsCount = 20;
    }

    [TypeUid(1027)]
    public class ExperienceComponent : ECSComponent
    {
        static new public long Id { get; set; } = 1027;
        public float CurrentExp = 0f;
        public float ExpToNextLevel = 1000f;
    }

    [TypeUid(1028)]
    public class LevelComponent : ECSComponent
    {
        static new public long Id { get; set; } = 1028;
        public int CurrentLevel = 1;
    }

    [TypeUid(1029)]
    public class GoldComponent : ECSComponent
    {
        static new public long Id { get; set; } = 1029;
        public int Coins = 0;
    }

    [TypeUid(1030)]
    public class ColliderComponent : ECSComponent
    {
        static new public long Id { get; set; } = 1030;
        public float Radius = 15f;
        public bool IsTrigger = false;
    }

    [TypeUid(1031)]
    public class StealthComponent : ECSComponent
    {
        static new public long Id { get; set; } = 1031;
        public bool IsInvisible = true;
        public float VisibilityRange = 50f;
    }

    [TypeUid(1032)]
    public class ShieldComponent : ECSComponent
    {
        static new public long Id { get; set; } = 1032;
        public float ShieldHealth = 500f;
    }

    [TypeUid(1033)]
    public class TeamComponent : ECSComponent
    {
        static new public long Id { get; set; } = 1033;
        public int TeamId = 0;
    }

    [TypeUid(1034)]
    public class LifetimeComponent : ECSComponent
    {
        static new public long Id { get; set; } = 1034;
        public float RemainingLifeSec = 60f;
    }

    [TypeUid(1035)]
    public class SoundEmitterComponent : ECSComponent
    {
        static new public long Id { get; set; } = 1035;
        public float Volume = 1.0f;
        public float Pitch = 1.0f;
    }
}