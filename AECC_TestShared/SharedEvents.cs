using System;
using AECC.Core;
using AECC.ECS.Core;
using AECC.Network;
using MessagePack;

namespace AECC.TestKit
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Сетевые события теста. Правила фреймворка:
    //    • [MessagePackObject] + [TypeUid(N)] (N — дискриминатор в конверте)
    //    • [Key(0..2)] заняты базой (InstanceId / EntityOwnerId / WorldOwnerId) ⇒ свои поля с 10
    //    • Destination задан ⇒ событие уходит в сеть, Execute() локально НЕ вызывается
    //    • Destination не задан ⇒ Execute() вызывается сразу (этот же путь у входящего пакета)
    //  Диапазон [TypeUid]: 5201..5299
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>КЛИЕНТ → СЕРВЕР. Единственный легальный способ клиента влиять на мир:
    /// бизнес-логическое событие. Никакого роллинга сущностей от клиента.</summary>
    [MessagePackObject]
    [NetworkScore(1)]
    [Serializable]
    [TypeUid(5201)]
    public class ClientCommandEvent : NetworkEvent
    {
        [Key(10)] public string Cmd = "";
        [Key(11)] public long TargetEntityId;
        [Key(12)] public double X;
        [Key(13)] public double Y;
        [Key(14)] public int Amount;

        [IgnoreMember] public static Action<ClientCommandEvent> Handler = _ => { };

        public override void Execute() { Handler(this); }

        public override bool CheckPacket()
        {
            // Валидация на приёме: пустая команда — мусор.
            return !string.IsNullOrEmpty(Cmd) && Cmd.Length <= 64;
        }

        public override int NetworkScoreBooster()
        {
            return Cmd == TK.C_Damage ? 5 : 0;
        }
    }

    /// <summary>СЕРВЕР → КЛИЕНТ. Служебный нотис (синхронизация фаз сценария).</summary>
    [MessagePackObject]
    [NetworkScore(0)]
    [Serializable]
    [TypeUid(5202)]
    public class ServerNoticeEvent : NetworkEvent
    {
        [Key(10)] public string Kind = "";
        [Key(11)] public string Payload = "";
        [Key(12)] public long EntityId;

        [IgnoreMember] public static Action<ServerNoticeEvent> Handler = _ => { };

        public override void Execute() { Handler(this); }
    }

    /// <summary>КЛИЕНТ → СЕРВЕР. Клиент отдаёт свой отчёт, сервер сводит общий итог.</summary>
    [MessagePackObject]
    [NetworkScore(0)]
    [Serializable]
    [TypeUid(5203)]
    public class ClientReportEvent : NetworkEvent
    {
        [Key(10)] public string Line = "";
        [Key(11)] public bool Ok;
        [Key(12)] public bool Final;
        [Key(13)] public int Passed;
        [Key(14)] public int Failed;

        [IgnoreMember] public static Action<ClientReportEvent> Handler = _ => { };

        public override void Execute() { Handler(this); }
    }

    /// <summary>
    /// Заведомо «злое» событие: высокий NetworkScore + CheckPacket() == false.
    /// Проверяем, что EventManager его отбрасывает (Execute не вызывается) и копит score.
    /// </summary>
    [MessagePackObject]
    [NetworkScore(250)]
    [Serializable]
    [TypeUid(5204)]
    public class MaliciousProbeEvent : NetworkEvent
    {
        [Key(10)] public bool Poison = true;

        [IgnoreMember] public static int ExecutedCount;

        public override bool CheckPacket() { return !Poison; }

        public override void Execute()
        {
            System.Threading.Interlocked.Increment(ref ExecutedCount);
        }
    }
}
