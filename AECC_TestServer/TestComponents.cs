using AECC.Core;
using AECC.Core.BuiltInTypes.ComponentsGroup;
using System;

namespace TestShared.Components
{
    // =========================================================================
    //  Тестовые компоненты — небольшой набор для демонстрации фреймворка
    // =========================================================================

    [Serializable]
    [TypeUid(1000)]
    public class HealthComponent : ECSComponent
    {
        public static new long Id { get; set; } = 1000;

        public float CurrentHealth = 100f;
        public float MaxHealth = 100f;
        public bool IsDead => CurrentHealth <= 0;

        public HealthComponent()
        {
            // Компонент принадлежит серверной группе — клиент получает его через GDAP
            this.AddComponentGroup(new ServerComponentGroup());
        }

        public override string ToString() =>
            $"Health({CurrentHealth:F1}/{MaxHealth:F1})";
    }

    [Serializable]
    [TypeUid(1001)]
    public class PositionComponent : ECSComponent
    {
        public static new long Id { get; set; } = 1001;

        public float X = 0f;
        public float Y = 0f;
        public float Z = 0f;

        public PositionComponent()
        {
            this.AddComponentGroup(new ServerComponentGroup());
        }

        public override string ToString() =>
            $"Pos({X:F2}, {Y:F2}, {Z:F2})";
    }

    [Serializable]
    [TypeUid(1002)]
    public class VelocityComponent : ECSComponent
    {
        public static new long Id { get; set; } = 1002;

        public float VX = 0f;
        public float VY = 0f;
        public float VZ = 0f;

        public VelocityComponent()
        {
            this.AddComponentGroup(new ServerComponentGroup());
        }

        public override string ToString() =>
            $"Vel({VX:F2}, {VY:F2}, {VZ:F2})";
    }

    [Serializable]
    [TypeUid(1003)]
    public class ScoreComponent : ECSComponent
    {
        public static new long Id { get; set; } = 1003;

        public int Points = 0;
        public int KillCount = 0;

        public ScoreComponent()
        {
            this.AddComponentGroup(new ServerComponentGroup());
        }

        public override string ToString() =>
            $"Score(Pts={Points}, Kills={KillCount})";
    }

    // =========================================================================
    //  Серверный секретный компонент — НЕ передаётся клиенту через GDAP
    // =========================================================================

    [Serializable]
    [TypeUid(1004)]
    public class ServerSecretComponent : ECSComponent
    {
        public static new long Id { get; set; } = 1004;

        public string InternalState = "classified";
        public long LastTickProcessed = 0;

        public ServerSecretComponent()
        {
            // Группа Server — клиент его НЕ увидит в GDAP фильтре
            this.AddComponentGroup(new ServerComponentGroup());
        }
    }

    // =========================================================================
    //  GDAP политика — определяет какие компоненты доступны/ограничены
    // =========================================================================

    [Serializable]
    [TypeUid(1005)]
    public class TestGDAP : GroupDataAccessPolicy
    {
        public static new long Id = 1005;

        /// <summary>
        /// Создаёт GDAP: Available — компоненты которые отправятся при совпадении instanceId,
        /// Restricted — компоненты которые отправятся при совпадении GetId() (типа GDAP)
        /// </summary>
        public static TestGDAP CreateForPlayer()
        {
            var gdap = new TestGDAP();
            // Available — видимые компоненты для "своего" наблюдателя
            gdap.AvailableComponents.Add(HealthComponent.Id);
            gdap.AvailableComponents.Add(PositionComponent.Id);
            gdap.AvailableComponents.Add(VelocityComponent.Id);
            gdap.AvailableComponents.Add(ScoreComponent.Id);
            //gdap.AvailableComponents.Add(ECSEntity.Id); // сама сущность

            // Restricted — видимые для "чужого" наблюдателя (тот же тип GDAP но другой instanceId)
            gdap.RestrictedComponents.Add(PositionComponent.Id);
            gdap.RestrictedComponents.Add(HealthComponent.Id);
            //gdap.RestrictedComponents.Add(ECSEntity.Id);

            // ServerSecretComponent НЕ добавлен — он не будет сериализоваться клиенту
            return gdap;
        }
    }
}
