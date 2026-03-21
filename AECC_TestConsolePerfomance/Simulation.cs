using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Text;
using AECC.Core;
using AECC.Extensions.ThreadingSync;
using AECC.Collections;
using AECC.Core.Logging;

public class Simulation
{
    public void Start()
    {
        var world = ECSWorld.GetWorld(0);
        bool end1 = false;
        bool end2 = false;
        ConcurrentDictionary<ECSEntity, bool> entities = new ConcurrentDictionary<ECSEntity, bool>();
        int maxEntt = 100000;

        Action fillAction = () =>
        {
            var random = new Random(Guid.NewGuid().GetHashCode());

            var componentFactories = new List<Func<ECSComponent>>()
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

            for (int i = 0; i < maxEntt / 2; i++)
            {
                int componentCount = random.Next(15, 26); 
                var entityComponents = new List<ECSComponent>();

                entityComponents.Add(new ViewComponent() {  });

                var selectedFactories = componentFactories.OrderBy(x => random.Next()).Take(componentCount - 1);
                foreach (var factory in selectedFactories)
                {
                    entityComponents.Add(factory());
                }

                var entity = new ECSEntity(world, entityComponents.ToArray());
                
                //if (entities.Count > 0)
                //    entity.AddChildObject(entities.TakeRandomOptimized(1).First().Key);
                
                entities.TryAdd(entity, false);
            }
        };

        TaskEx.RunAsync(() => { fillAction(); end1 = true; });
        TaskEx.RunAsync(() => { fillAction(); end2 = true; });
        
        var predicate = new PredicateExecutor("flush", new List<Func<bool>>() { () => end1 && end2 }, () =>
        {
            entities.ForEach(e => world.entityManager.AddNewEntity(e.Key));
            RunPerformanceDiagnostics(world); // Вызываем диагностику вместо BuildSystems
        }, 1000, 10000).Start();
    }

    // Структура для хранения тестовых конфигураций
    private struct QueryConfig
    {
        public string Name;
        public Type[] With;
        public Type[] Without;

        public QueryConfig(string name, Type[] with, Type[] without = null)
        {
            Name = name;
            With = with;
            Without = without;
        }
    }

    public void RunPerformanceDiagnostics(ECSWorld world)
    {
        // 1. Формируем пул различных запросов
        var queryConfigs = new List<QueryConfig>
        {
            new QueryConfig("Simple: 1 With", 
                new[] { typeof(HealthComponent) }),
                
            new QueryConfig("Simple: 2 With", 
                new[] { typeof(PositionComponent), typeof(VelocityComponent) }),
                
            new QueryConfig("Base Combat", 
                new[] { typeof(HealthComponent), typeof(TeamComponent) }),
                
            new QueryConfig("Melee Attackers", 
                new[] { typeof(MeleeDamagerComponent), typeof(HealthComponent) }, 
                new[] { typeof(SpeedComponent), typeof(RangeDamagerComponent) }),
                
            new QueryConfig("Moving Ranged", 
                new[] { typeof(RangeDamagerComponent), typeof(PositionComponent), typeof(VelocityComponent) },
                new[] { typeof(StunnedComponent), typeof(FrozenComponent) }),
                
            new QueryConfig("Status Effects (Debuffs)", 
                new[] { typeof(HealthComponent) }, 
                new[] { typeof(PoisonedComponent), typeof(BurningComponent), typeof(FrozenComponent) }),
                
            new QueryConfig("Mages with Mana", 
                new[] { typeof(ManaComponent), typeof(MagicResistanceComponent), typeof(ExperienceComponent) },
                new[] { typeof(MeleeDamagerComponent), typeof(StealthComponent) }),
                
            new QueryConfig("Heavy Physics", 
                new[] { typeof(PositionComponent), typeof(VelocityComponent), typeof(AccelerationComponent), typeof(GravityComponent), typeof(ColliderComponent) }),
                
            new QueryConfig("Complex: Stealth Units", 
                new[] { typeof(StealthComponent), typeof(PositionComponent), typeof(TeamComponent), typeof(AggroComponent) },
                new[] { typeof(SoundEmitterComponent), typeof(BurningComponent) }),
                
            new QueryConfig("Complex: Dying Units", 
                new[] { typeof(HealthComponent), typeof(PoisonedComponent) },
                new[] { typeof(RegenerationComponent), typeof(ShieldComponent), typeof(ArmorComponent) }),

            new QueryConfig("Huge Query (6 With, 4 Without)", 
                new[] { typeof(HealthComponent), typeof(PositionComponent), typeof(LevelComponent), typeof(InventoryComponent), typeof(GoldComponent), typeof(ExperienceComponent) },
                new[] { typeof(StealthComponent), typeof(StunnedComponent), typeof(FrozenComponent), typeof(InTimeoutComponent) }),
                
            new QueryConfig("View Only (Almost All)", 
                new[] { typeof(ViewComponent) })
        };

        int iterationsPerQuery = 1; // Количество повторений для вычисления среднего времени
        StringBuilder report = new StringBuilder();

        report.AppendLine($"\n=== ECS SEARCH PERFORMANCE REPORT (Entities: {world.entityManager.EntityStorage.Count}, Iters/Query: {iterationsPerQuery}) ===");
        report.AppendLine(string.Format("{0,-35} | {1,-10} | {2,-15} | {3,-15}", "Query Name", "Found Ents", "Total Time(ms)", "Avg Time(ms)"));
        report.AppendLine(new string('-', 84));

        Stopwatch globalSw = Stopwatch.StartNew();

        // 2. Выполняем бенчмарк для каждой конфигурации
        foreach (var config in queryConfigs)
        {
            // Прогрев (Warmup) JIT-компилятора: выполняем 1 раз без замеров
            var warmup = world.entityManager.SearchGraph(null, config.With, config.Without);
            int entitiesFound = warmup?.Count() ?? 0;

            // Замер времени
            Stopwatch sw = Stopwatch.StartNew();
            for (int i = 0; i < iterationsPerQuery; i++)
            {
                // Запускаем сам поиск. Если ленивые вычисления (LINQ), нужно вызывать Count() или ToList(), 
                // чтобы форсировать выполнение поиска в графе.
                var result = world.entityManager.SearchGraph(null, config.With, config.Without);
                
                // Чтобы компилятор не вырезал "мертвый код", считаем элементы
                int count = result?.Count() ?? 0; 
            }
            sw.Stop();

            // 3. Сбор статистики
            double totalMs = sw.Elapsed.TotalMilliseconds;
            double avgMs = totalMs / iterationsPerQuery;

            report.AppendLine(string.Format("{0,-35} | {1,-10} | {2,-15:F4} | {3,-15:F6}", 
                config.Name, 
                entitiesFound, 
                totalMs, 
                avgMs));
        }

        globalSw.Stop();
        report.AppendLine(new string('-', 84));
        report.AppendLine($"Total Benchmark Time: {globalSw.Elapsed.TotalMilliseconds:F2} ms");
        report.AppendLine("====================================================================================\n");

        // 4. Вывод в логи Godot
        NLogger.Log(report.ToString());
    }
}

// --- СУЩЕСТВУЮЩИЕ КОМПОНЕНТЫ ---

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

// --- НОВЫЕ КОМПОНЕНТЫ (30 шт.) ---

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