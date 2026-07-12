using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AECC.Core;
using AECC.Core.Logging;
using AECC.ECS.DefaultObjects.Events.LowLevelNetEvent.Auth;
using AECC.ECS.DefaultObjects.Events.ECSEvents;
using AECC.ECS.Events.ECSEvents;
using AECC.Harness.Services;
using AECC.LoadKit;
using AECC.Network;
using AECC.Serialization;
using AECC.TestKit;

namespace AECC.LoadClient
{
    /// <summary>
    /// Мультиклиент-хост: держит внутри себя N виртуальных клиентов, каждый — со СВОИМ
    /// сетевым соединением (отдельный NetworkingInstance). Ёмкость хоста ограничена
    /// LK.MulticlientCapacity.
    ///
    /// Про общий клиентский мир: конверт роллинга и IECSObjectPathContainer несут
    /// сериализуемый ECSWorldOwnerId, который обязан резолвиться в ЛОКАЛЬНЫЙ мир по
    /// общей клиент-серверной константе LK.WorldId. Один процесс не может держать N миров
    /// с одним id (WorldRegistry ключуется по id), поэтому все виртуальные клиенты хоста
    /// разделяют ОДНУ клиентскую реплику: их GDAP-срезы сливаются в неё (это же даёт
    /// конкурентный стресс StabilizationGate — N приёмных потоков на одни сущности).
    /// Следствие: приватность GDAP проверяется НА ПРОВОДЕ — инспекцией blob'ов каждого
    /// LoadRollEvent per-получатель (verify mode), а не по содержимому общего мира.
    /// </summary>
    public static class Multiclient
    {
        public static ECSWorld World;
        public static TestReport R;
        public static NetworkDestination ServerDest;

        public static readonly List<VirtualClient> Clients = new List<VirtualClient>();
        private static readonly ConcurrentDictionary<long, VirtualClient> ByEntity = new ConcurrentDictionary<long, VirtualClient>();
        private static readonly ConcurrentDictionary<string, VirtualClient> ByName = new ConcurrentDictionary<string, VirtualClient>();

        /// <summary>Сущности сессий (порядок = SessionIndex), из SERVER_READY.</summary>
        public static long[] SessionEntityIds = new long[0];

        // ── агрегированные метрики/verify-счётчики хоста ──
        public static long RollsApplied, RollBytes, ShotsSent, MinesSent, UpgradesDone;
        public static long WireGdapViolations, WirePrivateOwnBlobs, WireHpForeignBlobs;
        public static long HardRejects, SoftRejects, StateChecksSent, StateCheckFails;
        public static long UpgradeEconMismatches, SessionInfoChangesSeen;
        public static long MinesSeen, MinesGone, LivenessQueriesSent, PurgedMines, PurgedEntities;
        public static long MemberLeftEvents, EntitiesRemovedByEvent;

        private static ISerializationAdapter _inspector;
        private static volatile bool _running;
        private static Thread _tickThread, _sweepThread;
        private static readonly Random HostRng = new Random();
        private static readonly object HostRngGate = new object();

        private static double NextRandom() { lock (HostRngGate) return HostRng.NextDouble(); }
        private static int NextRandom(int maxExclusive) { lock (HostRngGate) return HostRng.Next(maxExclusive); }

        // ── реестр живости (по IECSObjectPath-идентичности объектов) ──
        private sealed class Suspect
        {
            public long FirstSeenMs;
            public bool Queried;
        }
        private static readonly ConcurrentDictionary<long, Suspect> MineRegistry = new ConcurrentDictionary<long, Suspect>();
        private static readonly ConcurrentDictionary<long, Suspect> EntityRegistry = new ConcurrentDictionary<long, Suspect>();
        private static readonly Dictionary<int, (int kills, int members, int round)> LastSessionSnapshot =
            new Dictionary<int, (int, int, int)>();
        private static int _livenessSeq;

        // ─────────────────────────────────────────────────────────────────────
        public static void Start(ECSWorld world, TestReport report, NetworkDestination serverDest,
                                 int clientCount, string prefix)
        {
            World = world;
            R = report;
            ServerDest = serverDest;
            _inspector = SerializationBootstrap.SerializerOf(world).serializationAdapter;

            WireHandlers();

            clientCount = Math.Min(clientCount, LK.MulticlientCapacity);
            for (int i = 0; i < clientCount; i++)
            {
                var vc = new VirtualClient(i, prefix + "_" + i);
                Clients.Add(vc);
                ByName[vc.Name] = vc;
            }

            // vc0 живёт на default-инстансе NetworkService (он уже подключён и провёл
            // обмен конфигом); остальные поднимают собственные NetworkingInstance.
            Clients[0].AttachInstance(NetworkService.instance.Instance);
            for (int i = 1; i < Clients.Count; i++)
            {
                Clients[i].SpawnOwnInstance();
                Thread.Sleep(LK.ClientSpawnDelayMs);
            }

            _running = true;
            _tickThread = new Thread(TickLoop) { IsBackground = true, Name = "mc-tick" };
            _tickThread.Start();
            _sweepThread = new Thread(SweepLoop) { IsBackground = true, Name = "mc-sweep" };
            _sweepThread.Start();
        }

        public static void StopActivity()
        {
            foreach (var vc in Clients) vc.BeginShutdown();
        }

        public static void Shutdown()
        {
            _running = false;
            try { _tickThread?.Join(2000); } catch { }
            try { _sweepThread?.Join(2000); } catch { }
            foreach (var vc in Clients) vc.DisposeInstance();
        }

        public static void RegisterEntity(long entityId, VirtualClient vc) { ByEntity[entityId] = vc; }
        public static bool IsHostedEntity(long entityId) { return ByEntity.ContainsKey(entityId); }

        public static ECSEntity Ent(long id)
        {
            ECSEntity e;
            return World.entityManager.TryGetEntitySyncronized(id, out e) ? e : null;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Статические обработчики входящих событий: роутинг по EntityOwnerId
        //  (сервер проставляет его = entityId игрока-получателя).
        // ─────────────────────────────────────────────────────────────────────
        private static VirtualClient Route(NetworkEvent evt)
        {
            VirtualClient vc;
            return ByEntity.TryGetValue(evt.EntityOwnerId, out vc) ? vc : null;
        }

        private static void WireHandlers()
        {
            UserLoggedEvent.actionAfterLoggin = (e) =>
            {
                VirtualClient vc;
                if (e.Username != null && ByName.TryGetValue(e.Username, out vc)) vc.OnLogged(e);
            };

            LoadRollEvent.PreApply = OnRollPreApply;
            LoadNoticeEvent.Handler = (n) => Route(n)?.OnNotice(n);
            SessionRestartingEvent.Handler = (e) => Route(e)?.OnSessionRestarting(e);
            SessionMemberLeftEvent.Handler = OnMemberLeft;
            UpgradeResultEvent.Handler = (e) => Route(e)?.OnUpgradeResult(e);
            StateCheckResultEvent.Handler = (e) =>
            {
                if (!e.Ok)
                {
                    Interlocked.Increment(ref StateCheckFails);
                    NLogger.LogError("[MC] StateCheck FAIL: " + e.Detail);
                }
            };
            ShootRejectedEvent.Handler = (e) =>
            {
                if (e.Hard)
                {
                    Interlocked.Increment(ref HardRejects);
                    NLogger.LogError("[MC] ЖЁСТКИЙ отказ по выстрелу: " + e.Reason);
                }
                else Interlocked.Increment(ref SoftRejects);
            };
            LivenessVerdictEvent.Handler = OnLivenessVerdict;
        }

        // ── wire-инспекция GDAP: до применения blob'ов в мир ──
        private static void OnRollPreApply(LoadRollEvent evt)
        {
            Interlocked.Increment(ref RollsApplied);
            long bytes = 0;
            if (evt.Entities != null) foreach (var b in evt.Entities) bytes += b?.Length ?? 0;
            Interlocked.Add(ref RollBytes, bytes);

            if (!LK.VerifyMode || evt.Entities == null) return;

            VirtualClient vc;
            if (!ByEntity.TryGetValue(evt.EntityIdRecipient, out vc)) return; // ранняя гонка — пропуск

            long hpId = LK.Uid<HpComponent>();
            var privateIds = new[]
            {
                LK.Uid<GunsComponent>(), LK.Uid<GunReloadComponent>(),
                LK.Uid<GoldComponent>(), LK.Uid<MineAbilityComponent>(),
            };

            foreach (var blob in evt.Entities)
            {
                try
                {
                    var se = _inspector.DeserializeAdapterEntity(blob);
                    if (se == null) continue;
                    se.DeserializeEntity();
                    long entityId = se.desEntity != null ? se.desEntity.instanceId : 0;
                    bool own = entityId == vc.PlayerEntityId;

                    bool hasPrivate = false;
                    foreach (var pid in privateIds)
                        if (se.Components.ContainsKey(pid)) { hasPrivate = true; break; }
                    bool hasHp = se.Components.ContainsKey(hpId);

                    // «Только пользователь видит свою информацию по перезарядке»:
                    if (hasPrivate && !own)
                    {
                        Interlocked.Increment(ref WireGdapViolations);
                        NLogger.LogError("[MC][GDAP] приватные компоненты чужой сущности " + entityId +
                                         " утекли получателю " + vc.Name);
                    }
                    // «Остальные видят его хп, а сам пользователь — своих хп не видит»:
                    if (hasHp && own)
                    {
                        Interlocked.Increment(ref WireGdapViolations);
                        NLogger.LogError("[MC][GDAP] собственный Hp приехал владельцу " + vc.Name);
                    }

                    if (hasPrivate && own) Interlocked.Increment(ref WirePrivateOwnBlobs);
                    if (hasHp && !own) Interlocked.Increment(ref WireHpForeignBlobs);
                }
                catch (Exception ex)
                {
                    NLogger.LogError("[MC] инспекция blob: " + ex.Message);
                }
            }
        }

        // ── событие «участник вышел из сессии»: единственный канал удаления из видимости ──
        private static void OnMemberLeft(SessionMemberLeftEvent evt)
        {
            Interlocked.Increment(ref MemberLeftEvents);
            var vc = Route(evt);
            vc?.OnMemberLeft(evt);

            long id = evt.LeftEntityId;
            if (IsHostedEntity(id)) return;                    // сущность нашего клиента — оставляем
            if (AnyHostedClientInterested(id)) return;         // нужна другому виртуальному клиенту

            var e = Ent(id);
            if (e != null)
            {
                try
                {
                    World.entityManager.RemoveEntity(e);
                    Interlocked.Increment(ref EntitiesRemovedByEvent);
                }
                catch (Exception ex) { NLogger.LogError("[MC] RemoveEntity: " + ex.Message); }
            }
        }

        /// <summary>Интерес хоста к сущности: она — участник сессии, где сидит любой из
        /// наших виртуальных клиентов (по свежераскатанному SessionInfo).</summary>
        private static bool AnyHostedClientInterested(long entityId)
        {
            foreach (var vc in Clients)
            {
                int si = vc.SessionIndex;
                if (si < 0 || si >= SessionEntityIds.Length) continue;
                var sess = Ent(SessionEntityIds[si]);
                var info = sess?.TryGetComponent<SessionInfoComponent>();
                if (info == null) continue;
                lock (info.SerialLocker)
                    if (info.MemberIds != null && info.MemberIds.Contains(entityId)) return true;
            }
            return false;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Тик виртуальных клиентов
        // ─────────────────────────────────────────────────────────────────────
        private static void TickLoop()
        {
            while (_running)
            {
                long now = LK.NowMs;
                foreach (var vc in Clients)
                {
                    try { vc.Tick(now); }
                    catch (Exception ex) { NLogger.LogError("[MC] tick " + vc.Name + ": " + ex.Message); }
                }
                Thread.Sleep(LK.ClientTickMs);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Свип хоста: наблюдение за миром, реестр живости, метрики сессий
        // ─────────────────────────────────────────────────────────────────────
        private static void SweepLoop()
        {
            long lastPrint = LK.NowMs;
            while (_running)
            {
                try { Sweep(); }
                catch (Exception ex) { NLogger.LogError("[MC] sweep: " + ex.Message); }

                if (LK.NowMs - lastPrint >= 5000)
                {
                    lastPrint = LK.NowMs;
                    NLogger.Log(string.Format(
                        "[MC] rolls={0} ({1} KiB) shots={2} mines={3} rollsGdapOk viol={4} " +
                        "minesSeen={5}/gone={6} liveQ={7} purged m/e={8}/{9} soft={10}",
                        RollsApplied, RollBytes / 1024, ShotsSent, MinesSent, WireGdapViolations,
                        MinesSeen, MinesGone, LivenessQueriesSent, PurgedMines, PurgedEntities, SoftRejects));
                }
                Thread.Sleep(LK.LivenessSweepMs);
            }
        }

        private static void Sweep()
        {
            long now = LK.NowMs;

            // 1) сессии: фиксируем «карта сессий живёт» + собираем известные строки мин
            var liveMineIds = new HashSet<long>();
            for (int i = 0; i < SessionEntityIds.Length; i++)
            {
                var sess = Ent(SessionEntityIds[i]);
                if (sess == null) continue;

                var info = sess.TryGetComponent<SessionInfoComponent>();
                if (info != null)
                {
                    int kills, members, round;
                    lock (info.SerialLocker) { kills = info.Kills; members = info.MemberCount; round = info.RoundNumber; }
                    (int, int, int) prev;
                    if (LastSessionSnapshot.TryGetValue(i, out prev) && !prev.Equals((kills, members, round)))
                        Interlocked.Increment(ref SessionInfoChangesSeen);
                    LastSessionSnapshot[i] = (kills, members, round);
                }

                var db = sess.TryGetComponent<MinesDBComponent>();
                if (db == null) continue;
                foreach (var row in db.GetComponentsByType<MineComponent>())
                {
                    long id = row.Item1.instanceId;
                    liveMineIds.Add(id);
                    if (MineRegistry.TryAdd(id, new Suspect { FirstSeenMs = now }))
                        Interlocked.Increment(ref MinesSeen);
                }
            }

            // мины, исчезнувшие из DB (взрыв / смерть владельца / рестарт) — снять с учёта
            foreach (var kv in MineRegistry)
                if (!liveMineIds.Contains(kv.Key))
                {
                    Suspect _;
                    if (MineRegistry.TryRemove(kv.Key, out _)) Interlocked.Increment(ref MinesGone);
                }

            // 2) чужие сущности-игроки: кандидаты на зависание
            foreach (var vc in Clients)
            {
                // регистрируем участников чужих взаимодействий по мере появления в мире
                int si = vc.SessionIndex;
                if (si < 0 || si >= SessionEntityIds.Length) continue;
                var sess = Ent(SessionEntityIds[si]);
                var info = sess?.TryGetComponent<SessionInfoComponent>();
                if (info == null) continue;
                List<long> members;
                lock (info.SerialLocker) members = info.MemberIds != null ? new List<long>(info.MemberIds) : new List<long>();
                foreach (var m in members)
                    if (!IsHostedEntity(m))
                        EntityRegistry.TryAdd(m, new Suspect { FirstSeenMs = now });
            }

            // 3) сверка с сервером: (а) ПОДОЗРИТЕЛЬНЫЕ — живут дольше обычного;
            //    (б) ПЛАНОВЫЙ АУДИТ — небольшая выборка самых старых учтённых объектов,
            //    чтобы механизм сверки работал постоянно, а не только по инцидентам.
            var mineSuspects = new List<long>();
            foreach (var kv in MineRegistry)
                if (!kv.Value.Queried && now - kv.Value.FirstSeenMs > LK.LivenessSuspectAgeMs)
                {
                    kv.Value.Queried = true;
                    mineSuspects.Add(kv.Key);
                }
            // плановый аудит мин: до 4 самых старых живых строк
            foreach (var kv in MineRegistry.OrderBy(x => x.Value.FirstSeenMs).Take(4))
                if (!mineSuspects.Contains(kv.Key)) mineSuspects.Add(kv.Key);

            var entSuspects = new List<long>();
            foreach (var kv in EntityRegistry)
            {
                if (kv.Value.Queried || now - kv.Value.FirstSeenMs <= LK.LivenessSuspectAgeMs) continue;
                if (IsHostedEntity(kv.Key) || AnyHostedClientInterested(kv.Key))
                {
                    kv.Value.FirstSeenMs = now;       // интерес жив — обнуляем возраст
                    continue;
                }
                if (Ent(kv.Key) == null)
                {
                    Suspect _;
                    EntityRegistry.TryRemove(kv.Key, out _);   // уже убрана событием
                    continue;
                }
                kv.Value.Queried = true;
                entSuspects.Add(kv.Key);
            }
            // плановый аудит сущностей: до 2 самых старых учтённых не-наших
            foreach (var kv in EntityRegistry.OrderBy(x => x.Value.FirstSeenMs).Take(2))
                if (!entSuspects.Contains(kv.Key) && !IsHostedEntity(kv.Key) && Ent(kv.Key) != null)
                    entSuspects.Add(kv.Key);

            if ((mineSuspects.Count > 0 || entSuspects.Count > 0) &&
                Clients.Count > 0 && Clients[0].PlayerEntityId != 0)
            {
                var q = new LivenessQueryEvent
                {
                    Seq = Interlocked.Increment(ref _livenessSeq),
                    EntityIds = entSuspects,
                    MineRowIds = mineSuspects,
                };
                Clients[0].Send(q);
                Interlocked.Increment(ref LivenessQueriesSent);
            }
        }

        private static void OnLivenessVerdict(LivenessVerdictEvent evt)
        {
            // мёртвые строки мин — вычистить из локальных DB сессий
            foreach (var mineId in evt.DeadMineRowIds)
            {
                for (int i = 0; i < SessionEntityIds.Length; i++)
                {
                    var db = Ent(SessionEntityIds[i])?.TryGetComponent<MinesDBComponent>();
                    if (db == null || !db.ComponentOwners.ContainsKey(mineId)) continue;
                    try
                    {
                        db.RemoveComponent(mineId);
                        Interlocked.Increment(ref PurgedMines);
                    }
                    catch (Exception ex) { NLogger.LogError("[MC] purge mine: " + ex.Message); }
                    break;
                }
                Suspect _;
                MineRegistry.TryRemove(mineId, out _);
            }

            // мёртвые сущности — убрать из общего мира (если никому из наших не нужны)
            foreach (var id in evt.DeadEntityIds)
            {
                Suspect _;
                EntityRegistry.TryRemove(id, out _);
                if (IsHostedEntity(id) || AnyHostedClientInterested(id)) continue;
                var e = Ent(id);
                if (e == null) continue;
                try
                {
                    World.entityManager.RemoveEntity(e);
                    Interlocked.Increment(ref PurgedEntities);
                }
                catch (Exception ex) { NLogger.LogError("[MC] purge entity: " + ex.Message); }
            }
        }

        /// <summary>Нерешённые древние подозреваемые на конец прогона (для финальной проверки).</summary>
        public static int UnresolvedAncientSuspects()
        {
            long now = LK.NowMs;
            int n = 0;
            foreach (var kv in MineRegistry)
                if (now - kv.Value.FirstSeenMs > LK.LivenessSuspectAgeMs * 3) n++;
            foreach (var kv in EntityRegistry)
                if (now - kv.Value.FirstSeenMs > LK.LivenessSuspectAgeMs * 3 &&
                    !IsHostedEntity(kv.Key) && !AnyHostedClientInterested(kv.Key) && Ent(kv.Key) != null) n++;
            return n;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  ВИРТУАЛЬНЫЙ КЛИЕНТ
        // ═════════════════════════════════════════════════════════════════════
        public sealed class VirtualClient
        {
            public enum VCState
            {
                Boot, Connecting, Registering, WaitingReady,
                ChoosingSession, JoinPending, Playing,
                LeavePending, UpgradeSettle, Upgrading,
                Parked, ShuttingDown
            }

            public readonly int Index;
            public readonly string Name;
            public NetworkingInstance Instance;
            private bool _ownsInstance;
            private ISocketAdapter _socket;

            public volatile VCState State = VCState.Boot;
            public long PlayerEntityId;
            public volatile int SessionIndex = -1;

            private readonly Random _rng;
            private readonly object _rngGate = new object();
            private double RngDouble() { lock (_rngGate) return _rng.NextDouble(); }
            private int RngNext(int maxExclusive) { lock (_rngGate) return _rng.Next(maxExclusive); }
            private long _stateSince, _nextShotAt, _nextMineAt, _nextCheckAt, _authSentAt;
            private int _gunCursor;
            private bool _triedLoginFallback;
            private volatile bool _restartPending;
            private volatile bool _awaitingUpgradeResult;
            private long _upgradeGold; private List<int> _upgradeLevels;
            private long _predCost, _predGoldAfter; private int _predGun;
            private int _checkSeq;

            public long MyShots, MyMines, MyJoins, MyUpgrades;
            public bool ReloadWireSeen
            {
                get
                {
                    var me = Ent(PlayerEntityId);
                    var r = me?.TryGetComponent<GunReloadComponent>();
                    if (r == null) return false;
                    lock (r.SerialLocker) return r.ReadyAtMs.Any(x => x > 0);
                }
            }

            public VirtualClient(int index, string name)
            {
                Index = index;
                Name = name;
                _rng = new Random(unchecked(name.GetHashCode() * 31 + index));
            }

            // ── сеть ──
            public void AttachInstance(NetworkingInstance inst)
            {
                Instance = inst;
                _ownsInstance = false;
                State = VCState.Connecting;
                _stateSince = LK.NowMs;
            }

            public void SpawnOwnInstance()
            {
                Instance = new NetworkingInstance();
                Instance.EndpointConfigs.Add(new NetworkDestination
                {
                    Host = LK.Host,
                    Port = LK.Port,
                    Protocol = NetworkProtocol.TCP,
                    IsListener = false,
                    BufferSize = 65536,
                });
                Instance.Start();
                _ownsInstance = true;
                State = VCState.Connecting;
                _stateSince = LK.NowMs;
            }

            public void DisposeInstance()
            {
                if (_ownsInstance) { try { Instance?.Dispose(); } catch { } }
            }

            public void Send(NetworkEvent evt)
            {
                evt.WorldOwnerId = LK.WorldId;
                evt.EntityOwnerId = PlayerEntityId;
                evt.Destination = ServerDest;
                Instance.EventManager.Dispatch(evt);
            }

            // ── входящие ──
            public void OnLogged(UserLoggedEvent e)
            {
                PlayerEntityId = e.userEntityId;
                RegisterEntity(PlayerEntityId, this);
                Send(new HelloLoadEvent());
                State = VCState.WaitingReady;
                _stateSince = LK.NowMs;
            }

            public void OnNotice(LoadNoticeEvent n)
            {
                switch (n.Kind)
                {
                    case LK.N_ServerReady:
                        if (SessionEntityIds.Length == 0 && !string.IsNullOrEmpty(n.Payload))
                            SessionEntityIds = n.Payload.Split(',').Select(long.Parse).ToArray();
                        State = VCState.ChoosingSession;
                        _stateSince = LK.NowMs;
                        break;
                    case LK.N_ServerFull:
                        NLogger.LogError("[MC] " + Name + ": SERVER_FULL — паркуюсь");
                        State = VCState.Parked;
                        break;
                    case LK.N_JoinOk:
                        SessionIndex = (int)n.A;
                        MyJoins++;
                        _restartPending = false;
                        ResetSchedules(LK.NowMs);
                        State = VCState.Playing;
                        _stateSince = LK.NowMs;
                        break;
                    case LK.N_JoinRejected:
                        // двойная проверка сервера сработала (гонка на ёмкость) — выбираем заново
                        State = VCState.ChoosingSession;
                        _stateSince = LK.NowMs;
                        break;
                    case LK.N_LeaveOk:
                        SessionIndex = -1;
                        State = VCState.UpgradeSettle;
                        _stateSince = LK.NowMs;
                        break;
                }
            }

            public void OnSessionRestarting(SessionRestartingEvent e)
            {
                if (State != VCState.Playing || e.SessionIndex != SessionIndex) return;
                // «игрок случайно решает — выйти прокачаться или остаться играть по новой»
                if (RngDouble() < 0.6)
                {
                    Send(new LeaveSessionEvent());
                    State = VCState.LeavePending;
                    _stateSince = LK.NowMs;
                }
                else
                {
                    _restartPending = true; // остаёмся: стрельба возобновится с Running
                }
            }

            public void OnMemberLeft(SessionMemberLeftEvent e) { /* бухгалтерия — на хосте */ }

            public void OnUpgradeResult(UpgradeResultEvent e)
            {
                if (!_awaitingUpgradeResult) return;
                _awaitingUpgradeResult = false;

                if (e.Ok)
                {
                    MyUpgrades++;
                    Interlocked.Increment(ref UpgradesDone);
                    if (LK.VerifyMode &&
                        (e.PaidCost != _predCost || e.NewGold != _predGoldAfter ||
                         e.GunIndex != _predGun || e.NewLevel != _upgradeLevels[_predGun] + 1))
                    {
                        Interlocked.Increment(ref UpgradeEconMismatches);
                        NLogger.LogError("[MC] " + Name + " экономика разошлась: paid=" + e.PaidCost +
                                         "≠" + _predCost + " gold=" + e.NewGold + "≠" + _predGoldAfter);
                    }
                    _upgradeGold = e.NewGold;
                    _upgradeLevels[e.GunIndex] = e.NewLevel;
                }
                else
                {
                    // прогноз говорил «хватает», сервер отказал — рассинхрон
                    if (LK.VerifyMode) Interlocked.Increment(ref UpgradeEconMismatches);
                    _upgradeGold = 0;   // прекращаем цикл покупок
                }
            }

            public void BeginShutdown()
            {
                if (State == VCState.Playing || State == VCState.JoinPending)
                {
                    try { Send(new LeaveSessionEvent()); } catch { }
                }
                State = VCState.ShuttingDown;
            }

            // ── тик ──
            private void ResetSchedules(long now)
            {
                _nextShotAt = now + (long)(RngDouble() * LK.ShotIntervalMs);
                _nextMineAt = now + (long)(RngDouble() * LK.MineCooldownMs);
                _nextCheckAt = now + LK.StateCheckIntervalMs + RngNext(400);
            }

            public void Tick(long now)
            {
                switch (State)
                {
                    case VCState.Connecting: TickConnecting(now); break;
                    case VCState.Registering: TickRegistering(now); break;
                    case VCState.WaitingReady:
                        if (now - _stateSince > 10000) { Send(new HelloLoadEvent()); _stateSince = now; }
                        break;
                    case VCState.ChoosingSession: TickChoose(now); break;
                    case VCState.JoinPending:
                        if (now - _stateSince > 4000) { State = VCState.ChoosingSession; _stateSince = now; }
                        break;
                    case VCState.Playing: TickPlaying(now); break;
                    case VCState.LeavePending:
                        if (now - _stateSince > 4000) { State = VCState.ChoosingSession; _stateSince = now; }
                        break;
                    case VCState.UpgradeSettle:
                        // ждём, пока роллинг довезёт золото/уровни после награды раунда
                        if (now - _stateSince > 700) EnterUpgrading(now);
                        break;
                    case VCState.Upgrading: TickUpgrading(now); break;
                }
            }

            private void TickConnecting(long now)
            {
                if (_socket == null)
                    _socket = Instance.ClientSockets.Values.FirstOrDefault();
                if (_socket != null && _socket.IsConnected && _socket.Id != 0)
                {
                    Send(new ClientRegistrationEvent
                    {
                        Username = Name,
                        Password = LK.Password,
                        Email = Name + LK.EmailDomain,
                        HardwareId = "LOADHW" + Index,
                    });
                    _authSentAt = now;
                    State = VCState.Registering;
                    _stateSince = now;
                }
                else if (now - _stateSince > 30000)
                {
                    NLogger.LogError("[MC] " + Name + ": сокет не поднялся за 30 c");
                    State = VCState.Parked;
                }
            }

            private void TickRegistering(long now)
            {
                if (now - _authSentAt <= 8000) return;
                if (!_triedLoginFallback)
                {
                    // повторный прогон против живого сервера: имя занято ⇒ обычный логин
                    _triedLoginFallback = true;
                    Send(new ClientAuthEvent { Username = Name, Password = LK.Password });
                    _authSentAt = now;
                }
                else
                {
                    NLogger.LogError("[MC] " + Name + ": авторизация не удалась");
                    State = VCState.Parked;
                }
            }

            /// <summary>Выбор сессии: стараемся зайти К ДРУГИМ (в заполняющиеся), иначе —
            /// случайная пустующая; всё занято — ждём.</summary>
            private void TickChoose(long now)
            {
                if (now - _stateSince < 250) return;

                int bestIdx = -1, bestCount = -1;
                var empties = new List<int>();
                for (int i = 0; i < SessionEntityIds.Length; i++)
                {
                    var info = Ent(SessionEntityIds[i])?.TryGetComponent<SessionInfoComponent>();
                    if (info == null) continue;
                    int mc; lock (info.SerialLocker) mc = info.MemberCount;
                    if (mc > 0 && mc < LK.MaxUsersPerSession)
                    {
                        if (mc > bestCount) { bestCount = mc; bestIdx = i; }
                    }
                    else if (mc == 0) empties.Add(i);
                }

                int target = bestIdx >= 0
                    ? bestIdx
                    : (empties.Count > 0 ? empties[RngNext(empties.Count)] : -1);
                if (target < 0) { _stateSince = now; return; }   // всё занято — подождать

                Send(new JoinSessionEvent { SessionIndex = target });
                State = VCState.JoinPending;
                _stateSince = now;
            }

            private void TickPlaying(long now)
            {
                // во время рестарта сессии активность бессмысленна — сервер её игнорирует
                var sess = SessionIndex >= 0 && SessionIndex < SessionEntityIds.Length
                    ? Ent(SessionEntityIds[SessionIndex]) : null;
                var info = sess?.TryGetComponent<SessionInfoComponent>();
                if (info == null) return;
                int state; List<long> members;
                lock (info.SerialLocker)
                {
                    state = info.State;
                    members = info.MemberIds != null ? new List<long>(info.MemberIds) : new List<long>();
                }
                if (state != 0)
                {
                    _nextShotAt = Math.Max(_nextShotAt, now);
                    _nextMineAt = Math.Max(_nextMineAt, now);
                    return;
                }
                if (_restartPending) _restartPending = false;

                // стрельба: частота на игрока, round-robin по пушкам (задел до 32)
                if (now >= _nextShotAt)
                {
                    _nextShotAt = now + (long)LK.ShotIntervalMs;
                    var targets = members.Where(m => m != PlayerEntityId).ToList();
                    if (targets.Count > 0)
                    {
                        int gun = _gunCursor++ % LK.GunsPerPlayer;
                        Send(new ShootEvent
                        {
                            GunIndex = gun,
                            TargetEntityId = targets[RngNext(targets.Count)],
                            ClientFiredAtMs = now,
                        });
                        MyShots++;
                        Interlocked.Increment(ref ShotsSent);
                    }
                }

                // мины: откатываемая способность
                if (now >= _nextMineAt)
                {
                    _nextMineAt = now + (long)LK.MineCooldownMs;
                    Send(new PlaceMineEvent());
                    MyMines++;
                    Interlocked.Increment(ref MinesSent);
                }

                // сверка состояний (только стабильные поля: уровни/сессия; золото и киллы
                // меняются на лету и проверяются в тихой точке апгрейда)
                if (LK.VerifyMode && now >= _nextCheckAt)
                {
                    _nextCheckAt = now + LK.StateCheckIntervalMs + RngNext(400);
                    var me = Ent(PlayerEntityId);
                    var guns = me?.TryGetComponent<GunsComponent>();
                    if (guns != null)
                    {
                        List<int> levels;
                        lock (guns.SerialLocker) levels = guns.Levels.Take(LK.GunsPerPlayer).ToList();
                        Send(new StateCheckEvent
                        {
                            Seq = ++_checkSeq,
                            Gold = -1,          // волатильное — не сверяем в бою
                            TotalKills = -1,
                            GunLevels = levels,
                            SessionIndex = SessionIndex,
                        });
                        Interlocked.Increment(ref StateChecksSent);
                    }
                }
            }

            private void EnterUpgrading(long now)
            {
                // тихая точка: читаем СВОЁ приватное состояние из реплики (GDAP Available)
                var me = Ent(PlayerEntityId);
                var gold = me?.TryGetComponent<GoldComponent>();
                var guns = me?.TryGetComponent<GunsComponent>();
                if (gold == null || guns == null) { State = VCState.ChoosingSession; _stateSince = now; return; }

                lock (gold.SerialLocker) _upgradeGold = gold.Gold;
                lock (guns.SerialLocker) _upgradeLevels = new List<int>(guns.Levels);

                // полная сверка в тихой точке (золото и киллы включительно)
                if (LK.VerifyMode)
                {
                    int kills; lock (gold.SerialLocker) kills = gold.TotalKills;
                    Send(new StateCheckEvent
                    {
                        Seq = ++_checkSeq,
                        Gold = _upgradeGold,
                        TotalKills = kills,
                        GunLevels = _upgradeLevels.Take(LK.GunsPerPlayer).ToList(),
                        SessionIndex = -1,
                    });
                    Interlocked.Increment(ref StateChecksSent);
                }

                _awaitingUpgradeResult = false;
                State = VCState.Upgrading;
                _stateSince = now;
            }

            private void TickUpgrading(long now)
            {
                if (_awaitingUpgradeResult)
                {
                    if (now - _stateSince > 5000) _awaitingUpgradeResult = false; // потерянный ответ
                    return;
                }

                // «на золото он может улучшить параметры пушек, бесконечно, цена ×1.1»
                int gun = RngNext(LK.GunsPerPlayer);
                long cost = LK.UpgradeCost(_upgradeLevels[gun]);
                if (_upgradeGold >= cost)
                {
                    _predGun = gun;
                    _predCost = cost;
                    _predGoldAfter = _upgradeGold - cost;
                    Send(new BuyUpgradeEvent
                    {
                        GunIndex = gun,
                        ExpectedCost = LK.VerifyMode ? cost : -1,
                        ExpectedGoldAfter = LK.VerifyMode ? _predGoldAfter : -1,
                    });
                    _awaitingUpgradeResult = true;
                    _stateSince = now;
                }
                else
                {
                    // прокачались — «с целью затем вернуться в сессию»
                    State = VCState.ChoosingSession;
                    _stateSince = now;
                }
            }
        }
    }
}
