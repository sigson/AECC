using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AECC.Core;
using AECC.Core.Logging;
using AECC.ECS.DefaultObjects.ECSComponents;
using AECC.ECS.Events.ECSEvents;
using AECC.Extensions;
using AECC.Harness.Model;
using AECC.Harness.Services;
using AECC.LoadKit;
using AECC.Network;
using AECC.Serialization;
using AECC.TestKit;

namespace AECC.LoadServer
{
    /// <summary>
    /// Авторитарный нагрузочный сервер.
    ///
    /// Модель — как в базовом тесте, но с сессионной игрой:
    ///   • сервер единолично владеет состоянием и раскатывает его LoadRollEvent'ами
    ///     (GDAP-срезы: перезарядка/золото/уровни — только владельцу; ХП — только чужим);
    ///   • клиенты присылают только бизнес-события (выстрел, мина, покупка, вход/выход);
    ///   • сервер превращает события в данные с двойной проверкой (LK.VerifyMode).
    /// </summary>
    public static class LoadGameServer
    {
        public static ECSWorld World;
        public static EntityNetSerializer Ser;
        public static TestReport R;

        // ── Сессии ───────────────────────────────────────────────────────────
        public sealed class Session
        {
            public readonly object Gate = new object();
            public int Index;
            public ECSEntity Entity;
            public MinesDBComponent MinesDb;
            public SessionAccessPolicy MembershipPolicy;   // GDAP №2: членство в сессии
            public int State;               // 0 Running / 1 Restarting
            public int Kills;
            public int KillTarget;
            public int Round;
            public long RestartAtMs;
            public double DamageMultiplier = 1.0;
            public readonly HashSet<long> Members = new HashSet<long>();
            public readonly Dictionary<long, int> KillsRound = new Dictionary<long, int>();
        }

        public static readonly List<Session> Sessions = new List<Session>();

        // playerEntityId → session (быстрый обратный индекс)
        private static readonly ConcurrentDictionary<long, Session> PlayerSession =
            new ConcurrentDictionary<long, Session>();

        // playerEntityId → сущность (все залогиненные)
        private static readonly ConcurrentDictionary<long, ECSEntity> Players =
            new ConcurrentDictionary<long, ECSEntity>();

        // пер-игрок лок экономики (золото/уровни)
        private static readonly ConcurrentDictionary<long, object> PlayerGates =
            new ConcurrentDictionary<long, object>();

        // ── Метрики / verify-счётчики ────────────────────────────────────────
        public static long ShotsApplied, ShotsRejectedSoft, ShotsRejectedHard;
        public static long MinesPlaced, MinesExploded, MinesVanishedOnDeath;
        public static long Deaths, SessionRestarts, UpgradesApplied, UpgradesRejected;
        public static long RollsSent, RollBytes, FullSnapshots;
        public static long StateChecks, StateCheckMismatches, ClientPredictionMismatches;
        public static long LivenessQueries, LivenessDeadVerdicts;
        public static long VerifyViolations;      // нарушение серверных инвариантов
        public static long GoldMinted;            // всего выдано золота (сверка экономики)

        // ── Диагностика производительности тиков ────────────────────────────
        public static long RollTicks, RollTickMsTotal, RollTickMsMax, RollTicksSkipped;
        public static long GameTicks, GameTickMsTotal, GameTickMsMax;
        public static long EventsReceived;        // бизнес-события, дошедшие до хендлеров
        private static int _rollTickBusy;

        public static readonly List<string> ClientLines = new List<string>();
        public static readonly ManualResetEventSlim ClientFinished = new ManualResetEventSlim(false);
        public static int ClientPassed, ClientFailed;

        private static TimerCompat _rollTimer, _gameTimer;
        private static readonly Random Rng = new Random(12345);
        private static readonly object RngGate = new object();

        private static int RandomInt(int minInclusive, int maxExclusive)
        {
            lock (RngGate) return Rng.Next(minInclusive, maxExclusive);
        }

        // ─────────────────────────────────────────────────────────────────────
        public static void Start(ECSWorld world, TestReport report)
        {
            World = world;
            R = report;
            Ser = SerializationBootstrap.SerializerOf(world);

            WireAuth();
            WireHandlers();
            SpawnSessions();

            _rollTimer = new TimerCompat(LK.RollIntervalMs, (s, e) => RollTick(), loop: true, asyncRun: true);
            _rollTimer.Start();
            _gameTimer = new TimerCompat(LK.GameTickMs, (s, e) => GameTick(), loop: true, asyncRun: true);
            _gameTimer.Start();

            NLogger.LogSuccess("[LOAD-SERVER] мир поднят: " + Sessions.Count + " сессий, роллинг " +
                               LK.RollIntervalMs + " мс, verify=" + LK.VerifyMode);
        }

        public static void Stop()
        {
            try { if (_rollTimer != null) { _rollTimer.Stop(); _rollTimer.Dispose(); } } catch { }
            try { if (_gameTimer != null) { _gameTimer.Stop(); _gameTimer.Dispose(); } } catch { }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Мир: генерация всех сессий на старте сервера
        // ─────────────────────────────────────────────────────────────────────
        private static void SpawnSessions()
        {
            for (int i = 0; i < LK.MaxSessionsOnServer; i++)
            {
                var s = new Session
                {
                    Index = i,
                    KillTarget = LK.SessionKillTargetMin + RandomInt(0, LK.SessionKillTargetSpan + 1),
                    DamageMultiplier = LK.SessionDamageModifiers[RandomInt(0, LK.SessionDamageModifiers.Length)],
                    Round = 1,
                };

                var e = new ECSEntity { AliasName = "session:" + i };
                World.entityManager.AddNewEntity(e);

                var info = GroupsL.Server(new SessionInfoComponent
                {
                    SessionIndex = i,
                    State = 0,
                    MemberCount = 0,
                    MaxMembers = LK.MaxUsersPerSession,
                    Kills = 0,
                    KillTarget = s.KillTarget,
                    RoundNumber = s.Round,
                });
                e.AddComponent(info);
                e.AddComponent(GroupsL.Server(new SessionModifierComponent { DamageMultiplier = s.DamageMultiplier }));

                var mines = GroupsL.Server(new MinesDBComponent());
                e.AddComponent(mines);

                // GDAP №1: «карта сессий» — Info + Modifier публичны каждому носителю
                // политики этого типа. База мин сюда НЕ входит.
                var policy = new LoadReplicationPolicy();
                policy.RestrictedComponents = new List<long>
                {
                    LK.Uid<SessionInfoComponent>(),
                    LK.Uid<SessionModifierComponent>(),
                };
                e.dataAccessPolicies.Add(policy);

                // GDAP №2 (членство): совпадение по instanceId (клон выдаётся при JOIN,
                // снимается при LEAVE) ⇒ участник видит всё, включая базу мин.
                var membership = new SessionAccessPolicy();
                membership.AvailableComponents = new List<long>
                {
                    LK.Uid<SessionInfoComponent>(),
                    LK.Uid<SessionModifierComponent>(),
                    LK.Uid<MinesDBComponent>(),
                };
                e.dataAccessPolicies.Add(membership);

                s.Entity = e;
                s.MinesDb = mines;
                s.MembershipPolicy = membership;
                Sessions.Add(s);
            }

            R.Section("LS1 · серверный мир");
            R.CheckEq("сессии сгенерированы на старте (MaxSessionsOnServer)",
                LK.MaxSessionsOnServer, Sessions.Count);
            R.Check("каждая сессия несёт Info + Modifier + MinesDB",
                Sessions.All(x => x.Entity.HasComponent<SessionInfoComponent>() &&
                                  x.Entity.HasComponent<SessionModifierComponent>() &&
                                  x.Entity.HasComponent<MinesDBComponent>()));
            R.Check("килл-каунт каждой сессии ≥ " + LK.SessionKillTargetMin,
                Sessions.All(x => x.KillTarget >= LK.SessionKillTargetMin));
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Авторизация: сущность игрока + GDAP-политика видимости
        // ─────────────────────────────────────────────────────────────────────
        private static void WireAuth()
        {
            AuthService.instance.SetupAuthorizationRealization = (regEvent) => new UserDataRowBase
            {
                Username = regEvent.Username,
                Password = HashExtension.MD5(regEvent.Password),
                Email = regEvent.Email,
                HardwareId = regEvent.HardwareId,
                RegistrationDate = DateTime.UtcNow.ToString("O"),
                UserPrivilegesGroup = "user",
                LastIp = "127.0.0.1",
                UserLocation = "en",
            };

            AuthService.instance.AuthorizationRealization = (userData) =>
            {
                var player = new ECSEntity { AliasName = "player:" + userData.Username };
                player.ECSWorldOwner = World;

                player.AddComponentSilent(new UsernameComponent { Username = userData.Username });
                player.AddComponentSilent(GroupsL.Server(new PlayerPublicComponent { Name = userData.Username, SessionIndex = -1 }));
                player.AddComponentSilent(GroupsL.Server(new HpComponent { Hp = LK.PlayerMaxHp }));
                player.AddComponentSilent(GroupsL.Server(GunsComponent.CreateDefault()));
                player.AddComponentSilent(GroupsL.Server(GunReloadComponent.CreateDefault()));
                player.AddComponentSilent(GroupsL.Server(new GoldComponent()));
                player.AddComponentSilent(GroupsL.Server(new MineAbilityComponent()));

                // Одна политика на игрока:
                //   Available (только владелец):  публичная витрина + ПЕРЕЗАРЯДКИ + золото + уровни;
                //   Restricted (только остальные): публичная витрина + ХП.
                // ⇒ «перезарядку видит только сам», «ХП видят все, кроме самого».
                var policy = new LoadReplicationPolicy();
                policy.AvailableComponents = new List<long>
                {
                    LK.Uid<PlayerPublicComponent>(),
                    LK.Uid<GunsComponent>(),
                    LK.Uid<GunReloadComponent>(),
                    LK.Uid<GoldComponent>(),
                    LK.Uid<MineAbilityComponent>(),
                };
                policy.RestrictedComponents = new List<long>
                {
                    LK.Uid<PlayerPublicComponent>(),
                    LK.Uid<HpComponent>(),
                };
                player.dataAccessPolicies.Add(policy);

                Players[player.instanceId] = player;
                PlayerGates[player.instanceId] = new object();

                NLogger.LogSuccess("[LOAD-SERVER] игрок авторизован: " + userData.Username +
                                   " → entity " + player.instanceId);
                return player;
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Обработчики бизнес-событий клиентов
        // ─────────────────────────────────────────────────────────────────────
        private static void WireHandlers()
        {
            HelloLoadEvent.Handler = HandleHello;
            JoinSessionEvent.Handler = HandleJoin;
            LeaveSessionEvent.Handler = (e) => HandleLeave(e, "client");
            ShootEvent.Handler = HandleShoot;
            PlaceMineEvent.Handler = HandlePlaceMine;
            BuyUpgradeEvent.Handler = HandleBuyUpgrade;
            StateCheckEvent.Handler = HandleStateCheck;
            LivenessQueryEvent.Handler = HandleLiveness;

            LoadReportEvent.Handler = (rep) =>
            {
                lock (ClientLines) ClientLines.Add((rep.Ok ? "[ OK ] " : "[FAIL] ") + rep.Line);
                if (rep.Final)
                {
                    ClientPassed = rep.Passed;
                    ClientFailed = rep.Failed;
                    ClientFinished.Set();
                }
            };
        }

        private static ECSEntity PlayerOf(NetworkEvent evt)
        {
            var socket = evt.SocketSource;
            if (socket == null) return null;
            ECSEntity player;
            AuthService.instance.SocketToEntity.TryGetValue(socket, out player);
            return player;
        }

        private static void Send(NetworkEvent evt, ECSEntity recipient)
        {
            ISocketAdapter socket;
            if (!AuthService.instance.EntityToSocket.TryGetValue(recipient, out socket) ||
                socket == null || !socket.IsConnected) return;
            evt.WorldOwnerId = LK.WorldId;
            evt.EntityOwnerId = recipient.instanceId;      // роутинг внутри мультиклиента
            evt.Destination = socket.CachedDestination;
            NetworkService.instance.EventManager.Dispatch(evt);
        }

        private static void Notice(ECSEntity recipient, string kind, string payload = "", long a = 0, long b = 0)
        {
            Send(new LoadNoticeEvent { Kind = kind, Payload = payload, A = a, B = b }, recipient);
        }

        // ── HELLO: карта сессий + лимит пользователей на сервер ──
        private static void HandleHello(HelloLoadEvent evt)
        {
            var player = PlayerOf(evt);
            if (player == null) return;

            if (Players.Count > LK.MaxUsersOnServer)
            {
                Notice(player, LK.N_ServerFull, "", Players.Count, LK.MaxUsersOnServer);
                return;
            }

            // Полный снапшот раскаткой: своя сущность + все сессии
            // (информация о сессиях далее ПОСТОЯННО обновляется инкрементальным роллингом).
            var sources = new List<ECSEntity> { player };
            sources.AddRange(Sessions.Select(s => s.Entity));
            SendSnapshot(player, sources);

            Notice(player, LK.N_ServerReady,
                string.Join(",", Sessions.Select(s => s.Entity.instanceId)),
                Sessions.Count, LK.MaxUsersPerSession);
        }

        // ── JOIN: клиент сам выбрал сессию; сервер двойной проверкой валидирует ёмкость ──
        private static void HandleJoin(JoinSessionEvent evt)
        {
            var player = PlayerOf(evt);
            if (player == null) return;
            if (evt.SessionIndex < 0 || evt.SessionIndex >= Sessions.Count)
            {
                Notice(player, LK.N_JoinRejected, "no-such-session", evt.SessionIndex);
                return;
            }
            var s = Sessions[evt.SessionIndex];
            List<ECSEntity> memberEntities = null;

            lock (s.Gate)
            {
                if (PlayerSession.ContainsKey(player.instanceId))
                {
                    Notice(player, LK.N_JoinRejected, "already-in-session", evt.SessionIndex);
                    return;
                }
                if (s.Members.Count >= LK.MaxUsersPerSession)
                {
                    Notice(player, LK.N_JoinRejected, "full", evt.SessionIndex);
                    return;
                }
                s.Members.Add(player.instanceId);
                s.KillsRound[player.instanceId] = 0;
                PlayerSession[player.instanceId] = s;
                PublishSessionInfo(s);
                memberEntities = s.Members
                    .Select(id => { ECSEntity p; Players.TryGetValue(id, out p); return p; })
                    .Where(p => p != null).ToList();
            }

            player.ExecuteWriteLockedComponent<PlayerPublicComponent>(pp =>
            {
                pp.SessionIndex = s.Index;
            });
            player.GetComponent<PlayerPublicComponent>().MarkAsChanged();

            // GDAP №2: игрок получает клон политики членства (тот же instanceId ⇒
            // Available сессии, включая базу мин). Клон, а не ссылка: Bin-буферы политики
            // перестраиваются сериализацией каждой сущности-носителя.
            player.dataAccessPolicies.Add((GroupDataAccessPolicy)s.MembershipPolicy.Clone());

            // Догоняющий снапшот: сущности всех текущих участников (включая себя) + сессия.
            var sources = new List<ECSEntity>(memberEntities) { s.Entity };
            SendSnapshot(player, sources);

            Notice(player, LK.N_JoinOk, "", s.Index);
        }

        private static void HandleLeave(LeaveSessionEvent evt, string reason)
        {
            var player = PlayerOf(evt);
            if (player == null) return;
            LeaveInternal(player, reason);
        }

        private static void LeaveInternal(ECSEntity player, string reason)
        {
            Session s;
            if (!PlayerSession.TryRemove(player.instanceId, out s)) return;

            List<ECSEntity> remaining;
            lock (s.Gate)
            {
                s.Members.Remove(player.instanceId);
                s.KillsRound.Remove(player.instanceId);
                PublishSessionInfo(s);
                remaining = s.Members
                    .Select(id => { ECSEntity p; Players.TryGetValue(id, out p); return p; })
                    .Where(p => p != null).ToList();
            }

            player.ExecuteWriteLockedComponent<PlayerPublicComponent>(pp => { pp.SessionIndex = -1; });
            player.GetComponent<PlayerPublicComponent>().MarkAsChanged();

            // GDAP №2: снять политику членства — база мин сессии перестаёт доезжать,
            // уже приехавшие строки зависают у клиента (материал реестра живости).
            var carried = player.dataAccessPolicies
                .FirstOrDefault(p => p.instanceId == s.MembershipPolicy.instanceId);
            if (carried != null)
                player.dataAccessPolicies.Remove(carried);

            // Мины вышедшего НАМЕРЕННО остаются тикать (взорвутся сами) — вместе с его
            // сущностью, зависшей у оставшихся, это и есть материал для клиентского
            // реестра живости. Роллинг удаление «из видимости» не выражает ⇒ событие:
            foreach (var other in remaining)
                Send(new SessionMemberLeftEvent
                {
                    SessionIndex = s.Index,
                    LeftEntityId = player.instanceId,
                    Reason = reason
                }, other);

            Notice(player, LK.N_LeaveOk, reason, s.Index);
        }

        // ── SHOOT: событие → данные (урон/откат/киллы) с двойной проверкой ──
        private static void HandleShoot(ShootEvent evt)
        {
            Interlocked.Increment(ref EventsReceived);
            var shooter = PlayerOf(evt);
            if (shooter == null) return;

            Session s;
            if (!PlayerSession.TryGetValue(shooter.instanceId, out s))
            {
                Reject(shooter, evt.GunIndex, "not-in-session", hard: true);
                return;
            }
            if (evt.GunIndex >= LK.GunsPerPlayer)
            {
                Reject(shooter, evt.GunIndex, "gun-index", hard: true);
                return;
            }

            ECSEntity target;
            bool targetInSession;
            lock (s.Gate)
            {
                if (s.State != 0) return; // рестарт: выстрелы молча игнорируются
                targetInSession = s.Members.Contains(evt.TargetEntityId);
            }
            if (!targetInSession || !Players.TryGetValue(evt.TargetEntityId, out target))
            {
                // Цель успела выйти/умереть между тиками клиента — мягкий отказ.
                Reject(shooter, evt.GunIndex, "target-gone", hard: false);
                return;
            }

            // Откат пушки: авторитетная проверка + фиксация новой метки готовности.
            long now = LK.NowMs;
            bool cooldownOk = false;
            var reload = shooter.TryGetComponent<GunReloadComponent>();
            if (reload == null) return;
            lock (reload.SerialLocker)
            {
                if (reload.ReadyAtMs[evt.GunIndex] <= now + LK.CooldownToleranceMs)
                {
                    reload.ReadyAtMs[evt.GunIndex] = now + (long)LK.PerGunCooldownMs;
                    cooldownOk = true;
                }
            }
            if (!cooldownOk)
            {
                Reject(shooter, evt.GunIndex, "cooldown", hard: false);
                return;
            }
            reload.MarkAsChanged();   // приватный компонент ⇒ уедет только владельцу

            // Урон: уровень пушки × модификатор сессии.
            int level;
            var guns = shooter.GetComponent<GunsComponent>();
            lock (guns.SerialLocker) level = guns.Levels[evt.GunIndex];
            int damage = (int)Math.Round(LK.GunDamage(level) * s.DamageMultiplier);

            ApplyDamage(target, damage, shooter.instanceId, s);
            Interlocked.Increment(ref ShotsApplied);
        }

        private static void Reject(ECSEntity shooter, int gunIndex, string reason, bool hard)
        {
            if (hard) Interlocked.Increment(ref ShotsRejectedHard);
            else Interlocked.Increment(ref ShotsRejectedSoft);
            if (LK.VerifyMode)
                Send(new ShootRejectedEvent { GunIndex = gunIndex, Reason = reason, Hard = hard }, shooter);
        }

        /// <summary>Атомарное применение урона + обработка смерти.
        /// killerId == 0 ⇒ урон «от среды» (не должен случиться в этом тесте).</summary>
        private static void ApplyDamage(ECSEntity victim, int damage, long killerId, Session s)
        {
            bool died = false;
            var hp = victim.TryGetComponent<HpComponent>();
            if (hp == null) return;
            lock (hp.SerialLocker)
            {
                if (hp.Hp <= 0) return;                       // уже обрабатывается смерть
                hp.Hp -= Math.Max(1, damage);
                if (hp.Hp <= 0)
                {
                    died = true;
                    hp.Hp = LK.PlayerMaxHp;                   // мгновенный респаун
                }
                if (LK.VerifyMode && (hp.Hp < 0 || hp.Hp > LK.PlayerMaxHp))
                {
                    Interlocked.Increment(ref VerifyViolations);
                    NLogger.LogError("[VERIFY] hp вне диапазона: " + hp.Hp);
                }
            }
            hp.MarkAsChanged();   // Restricted ⇒ уедет всем, КРОМЕ владельца

            if (died) HandleDeath(victim, killerId, s);
        }

        private static void HandleDeath(ECSEntity victim, long killerId, Session s)
        {
            Interlocked.Increment(ref Deaths);

            // «В момент смерти игрока все его неразорвавшиеся мины исчезают не взрываясь»:
            // помечаем строки Removed ⇒ следующий срез доставит удаления, AfterSnapshot
            // вычистит их из серверной DB.
            int before = s.MinesDb.GetComponentsByType<MineComponent>(victim).Count;
            if (before > 0)
            {
                s.MinesDb.RemoveComponentsByOwner(victim.instanceId);
                s.MinesDb.MarkAsChanged();
                Interlocked.Add(ref MinesVanishedOnDeath, before);
            }

            var victimGold = victim.TryGetComponent<GoldComponent>();
            if (victimGold != null)
            {
                lock (victimGold.SerialLocker) victimGold.TotalDeaths++;
                victimGold.MarkAsChanged();
            }

            bool reachedTarget = false;
            if (killerId != 0 && killerId != victim.instanceId)
            {
                ECSEntity killer;
                if (Players.TryGetValue(killerId, out killer))
                {
                    var kGold = killer.TryGetComponent<GoldComponent>();
                    if (kGold != null)
                    {
                        lock (kGold.SerialLocker) kGold.TotalKills++;
                        kGold.MarkAsChanged();
                    }
                    var kPub = killer.TryGetComponent<PlayerPublicComponent>();
                    if (kPub != null)
                    {
                        lock (kPub.SerialLocker) kPub.KillsRound++;
                        kPub.MarkAsChanged();
                    }
                }

                lock (s.Gate)
                {
                    if (s.State == 0)
                    {
                        s.Kills++;
                        int kr;
                        s.KillsRound.TryGetValue(killerId, out kr);
                        s.KillsRound[killerId] = kr + 1;
                        if (s.Kills >= s.KillTarget) reachedTarget = true;
                        PublishSessionInfo(s);
                    }
                }
            }

            if (reachedTarget) BeginRestart(s);
        }

        // ── Мины ──
        private static void HandlePlaceMine(PlaceMineEvent evt)
        {
            Interlocked.Increment(ref EventsReceived);
            var player = PlayerOf(evt);
            if (player == null) return;

            Session s;
            if (!PlayerSession.TryGetValue(player.instanceId, out s)) return;
            lock (s.Gate) { if (s.State != 0) return; }

            long now = LK.NowMs;
            var ability = player.TryGetComponent<MineAbilityComponent>();
            if (ability == null) return;
            bool ok = false;
            lock (ability.SerialLocker)
            {
                if (ability.NextReadyAtMs <= now + LK.CooldownToleranceMs)
                {
                    ability.NextReadyAtMs = now + (long)LK.MineCooldownMs;
                    ability.MinesPlaced++;
                    ok = true;
                }
            }
            if (!ok)
            {
                Interlocked.Increment(ref ShotsRejectedSoft);
                return;
            }
            ability.MarkAsChanged();   // приватный откат — только владельцу

            var mine = new MineComponent
            {
                PlacedAtMs = now,
                DetonateAtMs = now + RandomInt(LK.MineFuseMinMs, LK.MineFuseMaxMs + 1),
                Damage = LK.MineDamage,
            };
            // Строка DB принадлежит СУЩНОСТИ ИГРОКА (owner-path "ent") — то, что клиенты
            // резолвят через IECSObjectPathContainer и за чем следит реестр живости.
            s.MinesDb.AddComponent(player, mine);
            s.MinesDb.MarkAsChanged();
            Interlocked.Increment(ref MinesPlaced);
        }

        // ── Экономика ──
        private static void HandleBuyUpgrade(BuyUpgradeEvent evt)
        {
            var player = PlayerOf(evt);
            if (player == null) return;
            if (evt.GunIndex >= LK.GunsPerPlayer)
            {
                Send(new UpgradeResultEvent { Ok = false, GunIndex = evt.GunIndex, Reason = "gun-index" }, player);
                Interlocked.Increment(ref UpgradesRejected);
                return;
            }

            object gate;
            PlayerGates.TryGetValue(player.instanceId, out gate);
            var gold = player.GetComponent<GoldComponent>();
            var guns = player.GetComponent<GunsComponent>();

            long cost, goldAfter; int newLevel; bool ok;
            lock (gate ?? new object())
            {
                int level;
                lock (guns.SerialLocker) level = guns.Levels[evt.GunIndex];
                cost = LK.UpgradeCost(level);          // Base * 1.1^level — бесконечная прогрессия
                lock (gold.SerialLocker)
                {
                    ok = gold.Gold >= cost;
                    if (ok) gold.Gold -= cost;
                    goldAfter = gold.Gold;
                }
                newLevel = level;
                if (ok)
                {
                    lock (guns.SerialLocker) guns.Levels[evt.GunIndex] = ++newLevel;
                }
            }

            if (ok)
            {
                gold.MarkAsChanged();
                guns.MarkAsChanged();
                Interlocked.Increment(ref UpgradesApplied);

                // Двойная проверка: клиентский прогноз против авторитета.
                if (LK.VerifyMode && evt.ExpectedCost >= 0 &&
                    (evt.ExpectedCost != cost || evt.ExpectedGoldAfter != goldAfter))
                {
                    Interlocked.Increment(ref ClientPredictionMismatches);
                    NLogger.LogError("[VERIFY] прогноз клиента разошёлся: cost " + evt.ExpectedCost +
                                     "≠" + cost + " или gold " + evt.ExpectedGoldAfter + "≠" + goldAfter);
                }
            }
            else Interlocked.Increment(ref UpgradesRejected);

            Send(new UpgradeResultEvent
            {
                Ok = ok,
                GunIndex = evt.GunIndex,
                NewLevel = newLevel,
                NewGold = goldAfter,
                PaidCost = ok ? cost : 0,
                Reason = ok ? "" : "not-enough-gold",
            }, player);
        }

        // ── Verify: сверка состояний по запросу клиента ──
        private static void HandleStateCheck(StateCheckEvent evt)
        {
            var player = PlayerOf(evt);
            if (player == null || !LK.VerifyMode) return;
            Interlocked.Increment(ref StateChecks);

            var mismatches = new List<string>();

            var gold = player.GetComponent<GoldComponent>();
            long srvGold; int srvKills;
            lock (gold.SerialLocker) { srvGold = gold.Gold; srvKills = gold.TotalKills; }
            // -1 — сентинел «не сверять» (волатильные поля во время боя клиент не шлёт)
            if (evt.Gold >= 0 && srvGold != evt.Gold) mismatches.Add("gold srv=" + srvGold + " cli=" + evt.Gold);
            if (evt.TotalKills >= 0 && srvKills != evt.TotalKills) mismatches.Add("kills srv=" + srvKills + " cli=" + evt.TotalKills);

            var guns = player.GetComponent<GunsComponent>();
            lock (guns.SerialLocker)
            {
                for (int i = 0; i < LK.GunsPerPlayer && i < evt.GunLevels.Count; i++)
                    if (guns.Levels[i] != evt.GunLevels[i])
                        mismatches.Add("gun" + i + " srv=" + guns.Levels[i] + " cli=" + evt.GunLevels[i]);
            }

            Session s;
            int srvSession = PlayerSession.TryGetValue(player.instanceId, out s) ? s.Index : -1;
            if (srvSession != evt.SessionIndex)
                mismatches.Add("session srv=" + srvSession + " cli=" + evt.SessionIndex);

            bool ok = mismatches.Count == 0;
            if (!ok) Interlocked.Increment(ref StateCheckMismatches);
            Send(new StateCheckResultEvent
            {
                Seq = evt.Seq,
                Ok = ok,
                Detail = ok ? "" : string.Join("; ", mismatches),
            }, player);
        }

        // ── Реестр живости: авторитетные вердикты по подозрительным объектам ──
        private static void HandleLiveness(LivenessQueryEvent evt)
        {
            var player = PlayerOf(evt);
            if (player == null) return;
            Interlocked.Increment(ref LivenessQueries);

            var verdict = new LivenessVerdictEvent { Seq = evt.Seq };

            foreach (var id in evt.EntityIds)
                if (!World.entityManager.ContainsEntitySyncronized(id))
                    verdict.DeadEntityIds.Add(id);

            foreach (var mineId in evt.MineRowIds)
            {
                bool alive = false;
                foreach (var s in Sessions)
                {
                    if (s.MinesDb.ComponentOwners.ContainsKey(mineId)) { alive = true; break; }
                }
                if (!alive) verdict.DeadMineRowIds.Add(mineId);
            }

            Interlocked.Add(ref LivenessDeadVerdicts,
                verdict.DeadEntityIds.Count + verdict.DeadMineRowIds.Count);
            Send(verdict, player);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Игровой тик: взрывы мин, завершение рестартов
        // ─────────────────────────────────────────────────────────────────────
        private static void GameTick()
        {
            long now = LK.NowMs;
            var swTick = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                foreach (var s in Sessions)
                {
                    // рестарт завершился — новый раунд
                    bool restarted = false;
                    lock (s.Gate)
                    {
                        if (s.State == 1 && now >= s.RestartAtMs)
                        {
                            s.State = 0;
                            s.Kills = 0;
                            s.Round++;
                            s.KillTarget = LK.SessionKillTargetMin + RandomInt(0, LK.SessionKillTargetSpan + 1);
                            s.DamageMultiplier = LK.SessionDamageModifiers[RandomInt(0, LK.SessionDamageModifiers.Length)];
                            foreach (var m in s.Members.ToList()) s.KillsRound[m] = 0;
                            PublishSessionInfo(s);
                            restarted = true;
                        }
                    }
                    if (restarted)
                    {
                        s.Entity.ExecuteWriteLockedComponent<SessionModifierComponent>(m =>
                        {
                            m.DamageMultiplier = s.DamageMultiplier;
                        });
                        s.Entity.GetComponent<SessionModifierComponent>().MarkAsChanged();
                    }

                    // взрывы мин
                    var due = new List<(MineComponent mine, long owner)>();
                    foreach (var row in s.MinesDb.GetComponentsByType<MineComponent>())
                    {
                        var mine = (MineComponent)row.Item1;
                        if (mine.DetonateAtMs <= now)
                        {
                            long owner;
                            s.MinesDb.ComponentOwners.TryGetValue(mine.instanceId, out owner);
                            due.Add((mine, owner));
                        }
                    }
                    if (due.Count == 0) continue;

                    List<ECSEntity> members;
                    double mult;
                    lock (s.Gate)
                    {
                        members = s.Members
                            .Select(id => { ECSEntity p; Players.TryGetValue(id, out p); return p; })
                            .Where(p => p != null).ToList();
                        mult = s.DamageMultiplier;
                    }

                    foreach (var (mine, owner) in due)
                    {
                        // удалить из DB (строка уедет клиентам со state=Removed), затем урон всем
                        s.MinesDb.RemoveComponent(mine.instanceId);
                        Interlocked.Increment(ref MinesExploded);

                        int dmg = (int)Math.Round(mine.Damage * mult);
                        foreach (var member in members)
                            ApplyDamage(member, dmg, owner, s);
                    }
                    s.MinesDb.MarkAsChanged();
                }
            }
            catch (Exception ex)
            {
                NLogger.LogError("[LOAD-SERVER] GameTick: " + ex);
            }
            swTick.Stop();
            Interlocked.Increment(ref GameTicks);
            Interlocked.Add(ref GameTickMsTotal, swTick.ElapsedMilliseconds);
            if (swTick.ElapsedMilliseconds > Interlocked.Read(ref GameTickMsMax))
                Interlocked.Exchange(ref GameTickMsMax, swTick.ElapsedMilliseconds);
        }

        private static void BeginRestart(Session s)
        {
            List<(ECSEntity player, long delta)> rewards = new List<(ECSEntity, long)>();
            long topKiller = 0;
            long restartAt;
            int round;

            lock (s.Gate)
            {
                if (s.State == 1) return;
                s.State = 1;
                Interlocked.Increment(ref SessionRestarts);
                restartAt = s.RestartAtMs = LK.NowMs + LK.SessionRestartMs;
                round = s.Round;

                // «Кто больше убил за сессию — получает больше золота».
                int best = -1;
                foreach (var kv in s.KillsRound)
                    if (kv.Value > best) { best = kv.Value; topKiller = kv.Key; }

                foreach (var kv in s.KillsRound)
                {
                    ECSEntity p;
                    if (!Players.TryGetValue(kv.Key, out p)) continue;
                    long delta = kv.Value * LK.GoldPerKill + (kv.Key == topKiller ? LK.TopKillerBonus : 0);
                    rewards.Add((p, delta));
                }
                PublishSessionInfo(s);
            }

            // мины раунда сгорают (Removed ⇒ доедет клиентам срезом)
            s.MinesDb.ClearDB();
            s.MinesDb.MarkAsChanged();

            long minted = 0;
            foreach (var (p, delta) in rewards)
            {
                if (delta > 0)
                {
                    var gold = p.GetComponent<GoldComponent>();
                    lock (gold.SerialLocker) gold.Gold += delta;
                    gold.MarkAsChanged();
                    minted += delta;
                }
                var pub = p.TryGetComponent<PlayerPublicComponent>();
                if (pub != null)
                {
                    lock (pub.SerialLocker) pub.KillsRound = 0;
                    pub.MarkAsChanged();
                }
                Send(new SessionRestartingEvent
                {
                    SessionIndex = s.Index,
                    TopKillerEntityId = topKiller,
                    YourGoldDelta = delta,
                    RestartAtMs = restartAt,
                    RoundNumber = round,
                }, p);
            }
            Interlocked.Add(ref GoldMinted, minted);
        }

        /// <summary>Синхронизирует SessionInfoComponent с серверным состоянием
        /// (вызывать под s.Gate) и помечает изменённым ⇒ клиенты видят наполнение.</summary>
        private static void PublishSessionInfo(Session s)
        {
            var info = s.Entity.GetComponent<SessionInfoComponent>();
            lock (info.SerialLocker)
            {
                info.State = s.State;
                info.MemberCount = s.Members.Count;
                info.Kills = s.Kills;
                info.KillTarget = s.KillTarget;
                info.RoundNumber = s.Round;
                info.RestartAtMs = s.RestartAtMs;
                info.MemberIds = new List<long>(s.Members);
            }
            info.MarkAsChanged();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Роллинг: интерес-множества получателей
        // ─────────────────────────────────────────────────────────────────────
        /// <summary>
        /// СЕРИАЛИЗАЦИОННЫЙ ГЕЙТ. SerializeDB/SerializeEntity мутируют разделяемые
        /// serializedDB/GDAP-бины сущности и рассчитаны на ОДИН сериализующий поток
        /// (см. комментарий в DbSerialization.SerializeDB). Полные снапшоты (HELLO/JOIN,
        /// сетевые потоки) и роллинг-тик исполняются под общим локом.
        /// </summary>
        private static readonly object SerGate = new object();

        private static void SendSnapshot(ECSEntity player, List<ECSEntity> sources)
        {
            ISocketAdapter socket;
            if (!AuthService.instance.EntityToSocket.TryGetValue(player, out socket) ||
                socket == null || !socket.IsConnected) return;

            var blobs = new List<byte[]>();
            lock (SerGate)
            {
                foreach (var src in sources.Distinct())
                {
                    try
                    {
                        var b = Ser.BuildFullSerializedEntityWithGDAP(player, src);
                        if (b != null && b.Length > 0) blobs.Add(b);
                    }
                    catch (Exception ex)
                    {
                        NLogger.LogError("[LOAD-SERVER] snapshot " + src.AliasName + ": " + ex.Message);
                    }
                }
            }
            if (blobs.Count == 0) return;
            Interlocked.Increment(ref FullSnapshots);

            NetworkService.instance.EventManager.Dispatch(new LoadRollEvent
            {
                EntityIdRecipient = player.instanceId,
                EntityOwnerId = player.instanceId,
                WorldOwnerId = LK.WorldId,
                Entities = blobs,
                ServerTimeMs = LK.NowMs,
                FullSnapshot = true,
                Destination = socket.CachedDestination,
            });
        }

        private static void RollTick()
        {
            // Под нагрузкой тик может длиться дольше интервала таймера; перекрывающиеся
            // тики бессмысленны (сериализуют то же состояние) и лишь копят потоки на
            // SerGate — пропускаем, пока предыдущий не закончился.
            if (Interlocked.Exchange(ref _rollTickBusy, 1) != 0)
            {
                Interlocked.Increment(ref RollTicksSkipped);
                return;
            }
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                lock (SerGate) RollTickLocked();
                sw.Stop();
                Interlocked.Increment(ref RollTicks);
                Interlocked.Add(ref RollTickMsTotal, sw.ElapsedMilliseconds);
                if (sw.ElapsedMilliseconds > Interlocked.Read(ref RollTickMsMax))
                    Interlocked.Exchange(ref RollTickMsMax, sw.ElapsedMilliseconds);
                if (RollTicks % 100 == 0)
                    NLogger.Log($"[LOAD-SERVER] roll-tick #{RollTicks}: avg={RollTickMsTotal / Math.Max(1, RollTicks)}ms max={RollTickMsMax}ms skipped={RollTicksSkipped}; game-tick #{GameTicks}: avg={GameTickMsTotal / Math.Max(1, GameTicks)}ms max={GameTickMsMax}ms; eventsReceived={EventsReceived}");
            }
            catch (Exception ex)
            {
                NLogger.LogError("[LOAD-SERVER] RollTick: " + ex);
            }
            finally
            {
                Interlocked.Exchange(ref _rollTickBusy, 0);
            }
        }

        private static void RollTickLocked()
        {
                var players = AuthService.instance.EntityToSocket.ToArray();
                if (players.Length == 0) return;

                // 1) один срез на сущность за тик (SerializeEntity чистит dirty и наполняет GDAP-бины)
                var allSources = new HashSet<ECSEntity>();
                foreach (var kv in players) allSources.Add(kv.Key);
                foreach (var s in Sessions) allSources.Add(s.Entity);

                foreach (var src in allSources)
                {
                    try { Ser.SerializeEntity(src, true); }
                    catch (Exception ex)
                    {
                        NLogger.LogError("[LOAD-SERVER] SerializeEntity " + src.AliasName + ": " + ex.Message);
                    }
                }

                // 2) на получателя — интерес-множество: своя сущность + ВСЕ сессии
                //    (постоянное обновление «карты сессий») + участники СВОЕЙ сессии.
                foreach (var kv in players)
                {
                    var player = kv.Key;
                    var socket = kv.Value;
                    if (socket == null || !socket.IsConnected) continue;

                    var interest = new HashSet<ECSEntity> { player };
                    foreach (var s in Sessions) interest.Add(s.Entity);

                    Session mySession;
                    if (PlayerSession.TryGetValue(player.instanceId, out mySession))
                    {
                        lock (mySession.Gate)
                        {
                            foreach (var mid in mySession.Members)
                            {
                                ECSEntity m;
                                if (Players.TryGetValue(mid, out m)) interest.Add(m);
                            }
                        }
                    }

                    var blobs = new List<byte[]>();
                    long bytes = 0;
                    foreach (var src in interest)
                    {
                        try
                        {
                            var b = Ser.BuildSerializedEntityWithGDAP(player, src);
                            if (b != null && b.Length > 0) { blobs.Add(b); bytes += b.Length; }
                        }
                        catch (Exception ex)
                        {
                            NLogger.LogError("[LOAD-SERVER] GDAP " + src.AliasName + ": " + ex.Message);
                        }
                    }
                    if (blobs.Count == 0) continue;

                    Interlocked.Increment(ref RollsSent);
                    Interlocked.Add(ref RollBytes, bytes);

                    NetworkService.instance.EventManager.Dispatch(new LoadRollEvent
                    {
                        EntityIdRecipient = player.instanceId,
                        EntityOwnerId = player.instanceId,
                        WorldOwnerId = LK.WorldId,
                        Entities = blobs,
                        ServerTimeMs = LK.NowMs,
                        FullSnapshot = false,
                        Destination = socket.CachedDestination,
                    });
                }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Финальная серверная верификация
        // ─────────────────────────────────────────────────────────────────────
        public static void Verify()
        {
            R.Section("LS2 · нагрузка прошла через сервер");
            R.Check("клиенты авторизовались (" + Players.Count + ")", Players.Count > 0);
            R.Check("выстрелы превращались в данные: applied=" + ShotsApplied, ShotsApplied > 0);
            R.Check("мины ставились: placed=" + MinesPlaced, MinesPlaced > 0);
            R.Check("мины взрывались (DBComponent-цикл): exploded=" + MinesExploded, MinesExploded > 0);
            R.Check("смерти случались: deaths=" + Deaths, Deaths > 0);
            R.Check("сессии перезапускались: restarts=" + SessionRestarts, SessionRestarts > 0,
                "если тест короткий/пустой — увеличьте длительность");
            R.Check("апгрейды покупались: applied=" + UpgradesApplied, UpgradesApplied > 0);
            R.Check("роллинг шёл: rolls=" + RollsSent + " (" + (RollBytes / 1024) + " KiB)", RollsSent > 0);

            R.Section("LS3 · согласованность (двойные проверки)");
            if (LK.VerifyMode)
            {
                R.CheckEq("нарушений серверных инвариантов нет", 0L, Interlocked.Read(ref VerifyViolations));
                R.CheckEq("прогнозы клиента по экономике сходились", 0L, Interlocked.Read(ref ClientPredictionMismatches));
                R.Check("StateCheck-циклы шли: " + StateChecks, StateChecks > 0);
                R.CheckEq("StateCheck-расхождений нет", 0L, Interlocked.Read(ref StateCheckMismatches));
                R.CheckEq("жёстких отказов по выстрелам нет (мягкие по откату: " + ShotsRejectedSoft + ")",
                    0L, Interlocked.Read(ref ShotsRejectedHard));
            }
            else
            {
                R.Check("verify-режим отключён (чистая нагрузка)", true);
            }

            // Инвариант экономики: всё выданное золото = у игроков на руках + потрачено.
            long inHand = 0;
            foreach (var p in Players.Values)
            {
                var g = p.TryGetComponent<GoldComponent>();
                if (g == null) continue;
                lock (g.SerialLocker) inHand += g.Gold;
            }
            long spent = 0;
            foreach (var p in Players.Values)
            {
                var guns = p.TryGetComponent<GunsComponent>();
                if (guns == null) continue;
                lock (guns.SerialLocker)
                {
                    for (int i = 0; i < LK.GunsPerPlayer; i++)
                        for (int lvl = 0; lvl < guns.Levels[i]; lvl++)
                            spent += LK.UpgradeCost(lvl);
                }
            }
            R.CheckEq("экономика сходится: выдано == на руках + потрачено (" +
                      GoldMinted + " == " + inHand + " + " + spent + ")",
                GoldMinted, inHand + spent);

            R.Section("LS4 · реестр живости");
            R.Check("клиенты сверялись с реестром: queries=" + LivenessQueries, LivenessQueries > 0);
            long leftoverMines = Sessions.Sum(s => (long)s.MinesDb.GetComponentsByType<MineComponent>().Count);
            R.Check("на сервере нет протухших мин (осталось " + leftoverMines + " — валидные свежие)",
                leftoverMines <= LK.MaxUsersOnServer * 4);

            R.Section("LS5 · отчёт мультиклиента");
            lock (ClientLines)
                foreach (var l in ClientLines) Console.WriteLine("   client │ " + l);
            R.Check("мультиклиент прислал финальный отчёт", ClientFinished.IsSet);
            R.CheckEq("у мультиклиента нет провалов", 0, ClientFailed);
        }
    }
}
