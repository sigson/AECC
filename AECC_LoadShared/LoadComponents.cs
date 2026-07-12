using System;
using System.Collections.Generic;
using AECC.Core;
using AECC.Core.BuiltInTypes.Components;

namespace AECC.LoadKit
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Компоненты нагрузочного теста. Компилируются В ОБА проекта из общей папки
    //  (совпадение имён/namespace обязательно для CRC-идентификаторов NetSerializer).
    //
    //  Диапазон [TypeUid]: 6001..6199 — не пересекается ни с фреймворком
    //  (0..28, 105/106, 9001..9003), ни с базовым тест-китом (5001..5299).
    //
    //  Схема видимости (GDAP, одна политика LoadReplicationPolicy на сущность):
    //    Available  → уезжает ТОЛЬКО владельцу политики (матч по instanceId);
    //    Restricted → уезжает ВСЕМ ОСТАЛЬНЫМ носителям политики того же типа.
    //  Именно этой асимметрией реализовано требование:
    //    • перезарядку (GunReload / MineAbility) видит только сам игрок;
    //    • ХП игрока видят все ОСТАЛЬНЫЕ, а сам игрок — НЕ видит.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Публичная витрина игрока: видна и владельцу, и остальным
    /// (тип включён и в Available, и в Restricted).</summary>
    [Serializable]
    [TypeUid(6001)]
    public class PlayerPublicComponent : ECSComponent
    {
        public static new long Id { get; set; } = 6001;
        public string Name = "";
        /// <summary>Индекс сессии, где сейчас игрок; -1 — лобби.</summary>
        public int SessionIndex = -1;
        /// <summary>Киллы текущего раунда (лидерборд сессии).</summary>
        public int KillsRound;
    }

    /// <summary>ХП игрока. ТОЛЬКО Restricted: остальные видят, владелец — нет.</summary>
    [Serializable]
    [TypeUid(6002)]
    public class HpComponent : ECSComponent
    {
        public static new long Id { get; set; } = 6002;
        public int Hp;
    }

    /// <summary>Уровни пушек (операционные компоненты). Приватно (Available):
    /// прогресс игрока чужим не показывается. Список создаётся сразу на
    /// LK.MaxOperationalComponents (32) слота — задел на 32 одновременные пушки.</summary>
    [Serializable]
    [TypeUid(6003)]
    public class GunsComponent : ECSComponent
    {
        public static new long Id { get; set; } = 6003;
        public List<int> Levels = new List<int>();

        public static GunsComponent CreateDefault()
        {
            var c = new GunsComponent();
            for (int i = 0; i < LK.MaxOperationalComponents; i++) c.Levels.Add(0);
            return c;
        }
    }

    /// <summary>Перезарядка пушек: серверные метки готовности (server clock, мс).
    /// СТРОГО приватно (Available) — «только пользователь видит свою информацию по перезарядке».</summary>
    [Serializable]
    [TypeUid(6004)]
    public class GunReloadComponent : ECSComponent
    {
        public static new long Id { get; set; } = 6004;
        public List<long> ReadyAtMs = new List<long>();

        public static GunReloadComponent CreateDefault()
        {
            var c = new GunReloadComponent();
            for (int i = 0; i < LK.MaxOperationalComponents; i++) c.ReadyAtMs.Add(0);
            return c;
        }
    }

    /// <summary>Кошелёк и счётчики. Приватно (Available).</summary>
    [Serializable]
    [TypeUid(6005)]
    public class GoldComponent : ECSComponent
    {
        public static new long Id { get; set; } = 6005;
        public long Gold;
        public int TotalKills;
        public int TotalDeaths;
    }

    /// <summary>Откатываемая способность постановки мин (LK.MinePlacesPerSecond раз/с).
    /// Приватно (Available) — откат видит только владелец.</summary>
    [Serializable]
    [TypeUid(6006)]
    public class MineAbilityComponent : ECSComponent
    {
        public static new long Id { get; set; } = 6006;
        public long NextReadyAtMs;
        public int MinesPlaced;
    }

    /// <summary>Состояние сессии. Публично всем (Restricted) — это и есть постоянно
    /// обновляемая «карта сессий», по которой клиенты выбирают, куда зайти.</summary>
    [Serializable]
    [TypeUid(6007)]
    public class SessionInfoComponent : ECSComponent
    {
        public static new long Id { get; set; } = 6007;
        public int SessionIndex;
        /// <summary>0 = Running, 1 = Restarting.</summary>
        public int State;
        public int MemberCount;
        public int MaxMembers;
        public int Kills;
        public int KillTarget;
        public int RoundNumber;
        public long RestartAtMs;
        public List<long> MemberIds = new List<long>();
    }

    /// <summary>Модификатор сессии (например, повышенный урон). Публично (Restricted).</summary>
    [Serializable]
    [TypeUid(6008)]
    public class SessionModifierComponent : ECSComponent
    {
        public static new long Id { get; set; } = 6008;
        public double DamageMultiplier = 1.0;
    }

    /// <summary>DB-агрегатор мин сессии: строки (MineComponent) принадлежат сущностям
    /// игроков через IECSObjectPathContainer. Публично (Restricted): мины видят все
    /// участники — это и даёт клиенту материал для «реестра живости» (строки, чей
    /// владелец вышел из сессии, паркуются в serializedDBNonEO / зависают).</summary>
    [Serializable]
    [TypeUid(6009)]
    public class MinesDBComponent : ComponentsDBComponent
    {
        public static new long Id { get; set; } = 6009;
    }

    /// <summary>Мина (строка MinesDBComponent). Взводится на 1..2 c, затем сервер
    /// взрывает её (урон всем участникам сессии) и удаляет из DB. При смерти
    /// владельца все его неразорвавшиеся мины исчезают без взрыва.</summary>
    [Serializable]
    [TypeUid(6010)]
    public class MineComponent : ECSComponent
    {
        public static new long Id { get; set; } = 6010;
        public long PlacedAtMs;
        public long DetonateAtMs;
        public int Damage;
    }

    // ── GDAP ────────────────────────────────────────────────────────────────

    /// <summary>Политика доступа нагрузочного теста (см. шапку файла).</summary>
    [Serializable]
    [TypeUid(6101)]
    public class LoadReplicationPolicy : GroupDataAccessPolicy
    {
        public static new long Id { get; set; } = 6101;
    }

    /// <summary>
    /// Пометка компонентов серверной группой. Как и в базовом тест-ките, НЕ используем
    /// ECSComponent.SetGlobalComponentGroup(): он читает статик, перезаписываемый
    /// конструктором последнего созданного мира. Серверная группа обязательна для
    /// компонентов, чьи УДАЛЕНИЯ должны доезжать до клиента (FilterRemovedComponents
    /// вычищает только «чужую» для профиля группу).
    /// </summary>
    public static class GroupsL
    {
        public static T Server<T>(T component) where T : ECSComponent
        {
            component.AddComponentGroup(new AECC.Core.BuiltInTypes.ComponentsGroup.ServerComponentGroup());
            return component;
        }
    }
}
