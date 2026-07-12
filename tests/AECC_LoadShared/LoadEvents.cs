using System;
using System.Collections.Generic;
using AECC.Core;
using AECC.ECS.Core;
using AECC.ECS.Events.ECSEvents;
using AECC.Network;
using MessagePack;

namespace AECC.LoadKit
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Сетевые события нагрузочного теста. Правила фреймворка:
    //    • [MessagePackObject] + [TypeUid(N)] (N — дискриминатор конверта)
    //    • [Key(0..2)] заняты базой (InstanceId / EntityOwnerId / WorldOwnerId) ⇒ поля с 10
    //    • Destination задан ⇒ уходит в сеть, Execute() локально НЕ вызывается
    //    • Destination не задан ⇒ Execute() немедленно (тот же путь у входящего пакета)
    //
    //  Основное правило обмена: сервер раскатывает состояние сериализацией ECS-мира
    //  (LoadRollEvent : UpdateEntitiesEvent), изредка посылая события (выход из сессии,
    //  рестарт, вердикты живости) — то, что роллингом не выражается. Клиенты общаются
    //  ИСКЛЮЧИТЕЛЬНО бизнес-событиями, никакого роллинга сущностей на сервер.
    //
    //  Роутинг на мультиклиенте: сервер проставляет EntityOwnerId (Key(1) конверта) =
    //  entityId игрока-получателя; хост раздаёт события своим виртуальным клиентам по нему.
    //
    //  Диапазон [TypeUid]: 6201..6299.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// СЕРВЕР → КЛИЕНТ. Авторитарный роллинг: сериализованные GDAP-срезы сущностей.
    /// Наследует механику UpdateEntitiesEvent (blob'ы применяются UpdateDeserialize),
    /// добавляя серверное время и клиентский pre-hook: в verify-режиме мультиклиент
    /// инспектирует blob'ы ДО применения (wire-проверка GDAP) и снимает метрики.
    /// </summary>
    [MessagePackObject]
    [NetworkScore(0)]
    [Serializable]
    [TypeUid(6201)]
    public class LoadRollEvent : UpdateEntitiesEvent
    {
        [IgnoreMember] public static new long Id { get; set; } = 6201;

        [Key(12)] public long ServerTimeMs;
        /// <summary>true — полный снапшот (вход/джойн), false — инкрементальный срез.</summary>
        [Key(13)] public bool FullSnapshot;

        [IgnoreMember] public static Action<LoadRollEvent> PreApply = null;

        // ── Дедуп применения в ОБЩИЙ мир мультиклиента ──
        // Один и тот же срез приезжает на хост многократно (карточка сессии — по разу
        // на каждого из N клиентов, срез соседа — по разу на каждого участника сессии).
        // Идентичные байты идемпотентны — применяем один раз за короткое окно.
        // Окно малое (5 роллинг-тиков): цикл значений A→B→A с байтами как у A
        // не должен подавляться дольше окна.
        [IgnoreMember] private const long DedupWindowMs = 300;
        [IgnoreMember] private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, long> _appliedBlobs =
            new System.Collections.Concurrent.ConcurrentDictionary<ulong, long>();
        [IgnoreMember] private static long _lastPruneMs;

        private static ulong Fnv1a(byte[] data)
        {
            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < data.Length; i++)
                hash = (hash ^ data[i]) * 1099511628211UL;
            return hash;
        }

        public override void Execute()
        {
            var hook = PreApply;
            if (hook != null) hook(this);

            long now = LK.NowMs;
            var deduped = new List<byte[]>(Entities.Count);
            foreach (var blob in Entities)
            {
                var h = Fnv1a(blob);
                if (_appliedBlobs.TryGetValue(h, out var seenAt) && now - seenAt < DedupWindowMs)
                    continue;
                _appliedBlobs[h] = now;
                deduped.Add(blob);
            }

            if (now - System.Threading.Interlocked.Read(ref _lastPruneMs) > 2000)
            {
                System.Threading.Interlocked.Exchange(ref _lastPruneMs, now);
                foreach (var kv in _appliedBlobs)
                    if (now - kv.Value > DedupWindowMs)
                        _appliedBlobs.TryRemove(kv.Key, out _);
            }

            if (deduped.Count == 0) return;
            var originalList = Entities;
            Entities = deduped;
            try { base.Execute(); }
            finally { Entities = originalList; }
        }
    }

    /// <summary>КЛИЕНТ → СЕРВЕР. Вход в нагрузочный сценарий после логина:
    /// сервер отвечает полным снапшотом сессий + SERVER_READY (или SERVER_FULL).</summary>
    [MessagePackObject]
    [NetworkScore(1)]
    [Serializable]
    [TypeUid(6202)]
    public class HelloLoadEvent : NetworkEvent
    {
        [IgnoreMember] public static new long Id { get; set; } = 6202;
        [IgnoreMember] public static Action<HelloLoadEvent> Handler = _ => { };
        public override void Execute() { Handler(this); }
    }

    /// <summary>КЛИЕНТ → СЕРВЕР. Запрос на вход в выбранную КЛИЕНТОМ сессию.</summary>
    [MessagePackObject]
    [NetworkScore(1)]
    [Serializable]
    [TypeUid(6203)]
    public class JoinSessionEvent : NetworkEvent
    {
        [IgnoreMember] public static new long Id { get; set; } = 6203;
        [Key(10)] public int SessionIndex;
        [IgnoreMember] public static Action<JoinSessionEvent> Handler = _ => { };
        public override void Execute() { Handler(this); }
        public override bool CheckPacket() { return SessionIndex >= 0 && SessionIndex < 4096; }
    }

    /// <summary>КЛИЕНТ → СЕРВЕР. Выход из текущей сессии (например, на прокачку).</summary>
    [MessagePackObject]
    [NetworkScore(1)]
    [Serializable]
    [TypeUid(6204)]
    public class LeaveSessionEvent : NetworkEvent
    {
        [IgnoreMember] public static new long Id { get; set; } = 6204;
        [IgnoreMember] public static Action<LeaveSessionEvent> Handler = _ => { };
        public override void Execute() { Handler(this); }
    }

    /// <summary>КЛИЕНТ → СЕРВЕР. «Я выстрелил из пушки GunIndex в TargetEntityId».
    /// Клиент только сообщает о событии; в данные (урон, откат, киллы) его
    /// превращает СЕРВЕР — с двойной проверкой отката/принадлежности к сессии.</summary>
    [MessagePackObject]
    [NetworkScore(1)]
    [Serializable]
    [TypeUid(6205)]
    public class ShootEvent : NetworkEvent
    {
        [IgnoreMember] public static new long Id { get; set; } = 6205;
        [Key(10)] public int GunIndex;
        [Key(11)] public long TargetEntityId;
        /// <summary>Клиентская метка (для диагностики; сервер решает по своим часам).</summary>
        [Key(12)] public long ClientFiredAtMs;
        [IgnoreMember] public static Action<ShootEvent> Handler = _ => { };
        public override void Execute() { Handler(this); }
        public override bool CheckPacket()
        {
            return GunIndex >= 0 && GunIndex < LK.MaxOperationalComponents && TargetEntityId != 0;
        }
    }

    /// <summary>КЛИЕНТ → СЕРВЕР. Постановка мины (откат проверяет сервер).</summary>
    [MessagePackObject]
    [NetworkScore(1)]
    [Serializable]
    [TypeUid(6206)]
    public class PlaceMineEvent : NetworkEvent
    {
        [IgnoreMember] public static new long Id { get; set; } = 6206;
        [IgnoreMember] public static Action<PlaceMineEvent> Handler = _ => { };
        public override void Execute() { Handler(this); }
    }

    /// <summary>КЛИЕНТ → СЕРВЕР. Покупка улучшения пушки за золото.
    /// Expected* — клиентский прогноз для двойной проверки (verify mode; -1 = не задан).</summary>
    [MessagePackObject]
    [NetworkScore(1)]
    [Serializable]
    [TypeUid(6207)]
    public class BuyUpgradeEvent : NetworkEvent
    {
        [IgnoreMember] public static new long Id { get; set; } = 6207;
        [Key(10)] public int GunIndex;
        [Key(11)] public long ExpectedCost = -1;
        [Key(12)] public long ExpectedGoldAfter = -1;
        [IgnoreMember] public static Action<BuyUpgradeEvent> Handler = _ => { };
        public override void Execute() { Handler(this); }
        public override bool CheckPacket() { return GunIndex >= 0 && GunIndex < LK.MaxOperationalComponents; }
    }

    /// <summary>КЛИЕНТ → СЕРВЕР (verify mode). Клиент отдаёт свой взгляд на своё
    /// состояние; сервер сверяет с авторитетом и отвечает StateCheckResultEvent.</summary>
    [MessagePackObject]
    [NetworkScore(0)]
    [Serializable]
    [TypeUid(6208)]
    public class StateCheckEvent : NetworkEvent
    {
        [IgnoreMember] public static new long Id { get; set; } = 6208;
        [Key(10)] public int Seq;
        [Key(11)] public long Gold;
        [Key(12)] public List<int> GunLevels = new List<int>();
        [Key(13)] public int TotalKills;
        [Key(14)] public int SessionIndex;
        [IgnoreMember] public static Action<StateCheckEvent> Handler = _ => { };
        public override void Execute() { Handler(this); }
    }

    /// <summary>КЛИЕНТ → СЕРВЕР. Сверка реестра живости: клиент спрашивает про
    /// объекты, живущие у него дольше обычного (мины по строкам DB, чужие сущности).</summary>
    [MessagePackObject]
    [NetworkScore(0)]
    [Serializable]
    [TypeUid(6209)]
    public class LivenessQueryEvent : NetworkEvent
    {
        [IgnoreMember] public static new long Id { get; set; } = 6209;
        [Key(10)] public int Seq;
        [Key(11)] public List<long> EntityIds = new List<long>();
        [Key(12)] public List<long> MineRowIds = new List<long>();
        [IgnoreMember] public static Action<LivenessQueryEvent> Handler = _ => { };
        public override void Execute() { Handler(this); }
    }

    /// <summary>КЛИЕНТ(хост) → СЕРВЕР. Строки отчёта мультиклиента + финальный итог.</summary>
    [MessagePackObject]
    [NetworkScore(0)]
    [Serializable]
    [TypeUid(6210)]
    public class LoadReportEvent : NetworkEvent
    {
        [IgnoreMember] public static new long Id { get; set; } = 6210;
        [Key(10)] public string Line = "";
        [Key(11)] public bool Ok;
        [Key(12)] public bool Final;
        [Key(13)] public int Passed;
        [Key(14)] public int Failed;
        [IgnoreMember] public static Action<LoadReportEvent> Handler = _ => { };
        public override void Execute() { Handler(this); }
    }

    // ── СЕРВЕР → КЛИЕНТ ─────────────────────────────────────────────────────

    /// <summary>Служебный нотис (SERVER_READY / SERVER_FULL / JOIN_OK / JOIN_REJECTED / LEAVE_OK).</summary>
    [MessagePackObject]
    [NetworkScore(0)]
    [Serializable]
    [TypeUid(6220)]
    public class LoadNoticeEvent : NetworkEvent
    {
        [IgnoreMember] public static new long Id { get; set; } = 6220;
        [Key(10)] public string Kind = "";
        [Key(11)] public string Payload = "";
        [Key(12)] public long A;
        [Key(13)] public long B;
        [IgnoreMember] public static Action<LoadNoticeEvent> Handler = _ => { };
        public override void Execute() { Handler(this); }
    }

    /// <summary>Сессия достигла килл-каунта и перезапускается (LK.SessionRestartMs).
    /// Несёт раздачу золота получателю — в этот момент клиент случайно решает:
    /// выйти прокачаться или остаться играть по новой.</summary>
    [MessagePackObject]
    [NetworkScore(0)]
    [Serializable]
    [TypeUid(6221)]
    public class SessionRestartingEvent : NetworkEvent
    {
        [IgnoreMember] public static new long Id { get; set; } = 6221;
        [Key(10)] public int SessionIndex;
        [Key(11)] public long TopKillerEntityId;
        [Key(12)] public long YourGoldDelta;
        [Key(13)] public long RestartAtMs;
        [Key(14)] public int RoundNumber;
        [IgnoreMember] public static Action<SessionRestartingEvent> Handler = _ => { };
        public override void Execute() { Handler(this); }
    }

    /// <summary>Игрок покинул сессию получателя. Именно тот случай, когда роллинг
    /// бессилен (нет механизма отроллбечить удаление сущности, вышедшей из «видимости»),
    /// поэтому — событие. Хост убирает сущность из клиентского мира, если ни один из его
    /// виртуальных клиентов больше в ней не заинтересован.</summary>
    [MessagePackObject]
    [NetworkScore(0)]
    [Serializable]
    [TypeUid(6222)]
    public class SessionMemberLeftEvent : NetworkEvent
    {
        [IgnoreMember] public static new long Id { get; set; } = 6222;
        [Key(10)] public int SessionIndex;
        [Key(11)] public long LeftEntityId;
        [Key(12)] public string Reason = "";
        [IgnoreMember] public static Action<SessionMemberLeftEvent> Handler = _ => { };
        public override void Execute() { Handler(this); }
    }

    /// <summary>Ответ на BuyUpgradeEvent — прямая двойная проверка экономики.</summary>
    [MessagePackObject]
    [NetworkScore(0)]
    [Serializable]
    [TypeUid(6223)]
    public class UpgradeResultEvent : NetworkEvent
    {
        [IgnoreMember] public static new long Id { get; set; } = 6223;
        [Key(10)] public bool Ok;
        [Key(11)] public int GunIndex;
        [Key(12)] public int NewLevel;
        [Key(13)] public long NewGold;
        [Key(14)] public long PaidCost;
        [Key(15)] public string Reason = "";
        [IgnoreMember] public static Action<UpgradeResultEvent> Handler = _ => { };
        public override void Execute() { Handler(this); }
    }

    /// <summary>Ответ на StateCheckEvent (verify mode).</summary>
    [MessagePackObject]
    [NetworkScore(0)]
    [Serializable]
    [TypeUid(6224)]
    public class StateCheckResultEvent : NetworkEvent
    {
        [IgnoreMember] public static new long Id { get; set; } = 6224;
        [Key(10)] public int Seq;
        [Key(11)] public bool Ok;
        [Key(12)] public string Detail = "";
        [IgnoreMember] public static Action<StateCheckResultEvent> Handler = _ => { };
        public override void Execute() { Handler(this); }
    }

    /// <summary>Отказ по выстрелу/мине (verify mode): «мягкие» отказы (откат не готов
    /// из-за сетевой задержки) клиент считает, «жёсткие» (не в сессии, кривой индекс) —
    /// признак рассинхрона.</summary>
    [MessagePackObject]
    [NetworkScore(0)]
    [Serializable]
    [TypeUid(6225)]
    public class ShootRejectedEvent : NetworkEvent
    {
        [IgnoreMember] public static new long Id { get; set; } = 6225;
        [Key(10)] public int GunIndex;
        [Key(11)] public string Reason = "";
        [Key(12)] public bool Hard;
        [IgnoreMember] public static Action<ShootRejectedEvent> Handler = _ => { };
        public override void Execute() { Handler(this); }
    }

    /// <summary>Вердикт живости по запросу LivenessQueryEvent.</summary>
    [MessagePackObject]
    [NetworkScore(0)]
    [Serializable]
    [TypeUid(6226)]
    public class LivenessVerdictEvent : NetworkEvent
    {
        [IgnoreMember] public static new long Id { get; set; } = 6226;
        [Key(10)] public int Seq;
        [Key(11)] public List<long> DeadEntityIds = new List<long>();
        [Key(12)] public List<long> DeadMineRowIds = new List<long>();
        [IgnoreMember] public static Action<LivenessVerdictEvent> Handler = _ => { };
        public override void Execute() { Handler(this); }
    }
}
