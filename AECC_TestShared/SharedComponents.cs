using System;
using System.Threading;
using AECC.Core;
using AECC.Core.BuiltInTypes.Components;

namespace AECC.TestKit
{
    // ─────────────────────────────────────────────────────────────────────────
    //  ВАЖНО: эти типы компилируются в ОБА проекта (сервер и клиент) из общей папки.
    //  NetSerializer вычисляет id типа на проводе как CRC32(Type.ToString()) с вырезанными
    //  namespace'ами корневых типов ⇒ имена типов и namespace обязаны совпадать буквально.
    //  Поэтому НИКАКИХ IDObject-наследников в самих проектах Server/Client быть не должно —
    //  только здесь.
    //
    //  Диапазон [TypeUid]: 5001..5199 (не пересекается с фреймворком: 0..28, 105/106, 9001..9003).
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>ПУБЛИЧНЫЙ компонент: реплицируется всем (Restricted).</summary>
    [Serializable]
    [TypeUid(5001)]
    public class PositionComponent : ECSComponent
    {
        public static new long Id { get; set; } = 5001;
        public double X;
        public double Y;
    }

    /// <summary>Серверная симуляция. Клиенту не реплицируется вообще (нет ни в одном списке GDAP).</summary>
    [Serializable]
    [TypeUid(5002)]
    public class VelocityComponent : ECSComponent
    {
        public static new long Id { get; set; } = 5002;
        public double VX;
        public double VY;
    }

    /// <summary>ПРИВАТНЫЙ компонент: виден только «владельцу» (Available, матч по instanceId политики).</summary>
    [Serializable]
    [TypeUid(5003)]
    public class HealthComponent : ECSComponent
    {
        public static new long Id { get; set; } = 5003;
        public int Hp = 100;
        public int MaxHp = 100;
    }

    /// <summary>Реплицируемый компонент, который сервер потом СНИМЕТ — проверка доставки удалений.</summary>
    [Serializable]
    [TypeUid(5004)]
    public class ScoreComponent : ECSComponent
    {
        public static new long Id { get; set; } = 5004;
        public int Score;
    }

    [Serializable]
    [TypeUid(5005)]
    public class PlayerTagComponent : ECSComponent
    {
        public static new long Id { get; set; } = 5005;
        public string Login = "";
    }

    /// <summary>DB-агрегатор (ComponentsDBComponent): «компоненты внутри компонента».</summary>
    [Serializable]
    [TypeUid(5006)]
    public class InventoryDBComponent : ComponentsDBComponent
    {
        public static new long Id { get; set; } = 5006;
    }

    /// <summary>Живёт ВНУТРИ InventoryDBComponent (не в EntityComponentStorage).</summary>
    [Serializable]
    [TypeUid(5007)]
    public class ItemComponent : ECSComponent
    {
        public static new long Id { get; set; } = 5007;
        public string ItemName = "";
        public int Count;
    }

    /// <summary>
    /// Клиентский локальный компонент (client-side prediction). Помечается ClientComponentGroup,
    /// поэтому серверный роллинг (FilterRemovedComponents фильтрует только «чужую» группу = Server)
    /// его НЕ сносит.
    /// </summary>
    [Serializable]
    [TypeUid(5008)]
    public class ClientPredictionComponent : ECSComponent
    {
        public static new long Id { get; set; } = 5008;
        public double PredictedX;
        public double PredictedY;
        public int AppliedRolls;
    }

    /// <summary>
    /// Компонент с ПЕРЕОПРЕДЕЛЁННЫМИ lifecycle-хуками ⇒ уходит с fast-path на
    /// ComponentLifecycleDispatcher (очередь Add→Change→Remove через IScheduler).
    /// Счётчики статические — сериализацией не трогаются.
    /// </summary>
    [Serializable]
    [TypeUid(5009)]
    public class LifecycleProbeComponent : ECSComponent
    {
        public static new long Id { get; set; } = 5009;

        public static int Added;
        public static int Changed;
        public static int Removed;
        public static string Order = "";
        private static readonly object OrderLock = new object();

        public int Payload;

        public static void Reset()
        {
            Interlocked.Exchange(ref Added, 0);
            Interlocked.Exchange(ref Changed, 0);
            Interlocked.Exchange(ref Removed, 0);
            lock (OrderLock) { Order = ""; }
        }

        private static void Trace(string s) { lock (OrderLock) { Order += s; } }

        protected override void OnAdded(ECSEntity entity)
        {
            base.OnAdded(entity);           // на Server-профиле здесь же идёт MarkAsChanged()
            Interlocked.Increment(ref Added);
            Trace("A");
        }

        protected override void OnChanged(ECSEntity entity)
        {
            Interlocked.Increment(ref Changed);
            Trace("C");
        }

        protected override void OnRemoved(ECSEntity entity)
        {
            Interlocked.Increment(ref Removed);
            Trace("R");
        }
    }

    /// <summary>Маркер сущности-ребёнка (проверка дерева IECSObject + отложенной десериализации).</summary>
    [Serializable]
    [TypeUid(5010)]
    public class ChildMarkerComponent : ECSComponent
    {
        public static new long Id { get; set; } = 5010;
        public string Tag = "";
    }

    /// <summary>Компонент для проверки транзакционных absence-hold'ов контракта.</summary>
    [Serializable]
    [TypeUid(5011)]
    public class BlockerComponent : ECSComponent
    {
        public static new long Id { get; set; } = 5011;
    }

    // ── GDAP ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Политика доступа. Механика:
    ///   • получатель имеет политику того же ТИПА и того же instanceId ⇒ ему уходит AvailableComponents;
    ///   • получатель имеет политику того же ТИПА, но другой instanceId ⇒ RestrictedComponents.
    /// «Приватный» доступ игрока к своей сущности реализуется тем, что на сущность кладётся
    /// Clone() политики игрока (Clone сохраняет instanceId и обнуляет бины).
    /// </summary>
    [Serializable]
    [TypeUid(5101)]
    public class ReplicationPolicy : GroupDataAccessPolicy
    {
        public static new long Id { get; set; } = 5101;
    }

    /// <summary>
    /// Пометка компонентов группами. НЕ используем ECSComponent.SetGlobalComponentGroup():
    /// он читает ECSComponentManager.GlobalProgramComponentGroup — СТАТИК, который
    /// перезаписывается конструктором последнего созданного мира (см. FRAMEWORK_MAP §9.2).
    /// В процессе, где создаётся несколько миров разного профиля, это ведёт себя недетерминированно.
    /// </summary>
    public static class Groups
    {
        /// <summary>Компонент, которым владеет СЕРВЕР: только такие компоненты клиент
        /// вычищает по FilterRemovedComponents (доставка удалений).</summary>
        public static T Server<T>(T component) where T : ECSComponent
        {
            component.AddComponentGroup(new AECC.Core.BuiltInTypes.ComponentsGroup.ServerComponentGroup());
            return component;
        }

        /// <summary>Компонент, которым владеет КЛИЕНТ: серверный роллинг его не трогает.</summary>
        public static T Client<T>(T component) where T : ECSComponent
        {
            component.AddComponentGroup(new AECC.Core.BuiltInTypes.ComponentsGroup.ClientComponentGroup());
            return component;
        }
    }
}
