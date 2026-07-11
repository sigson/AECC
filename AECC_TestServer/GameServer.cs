using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AECC.Core;
using AECC.Core.BuiltInTypes.Components;
using AECC.Core.Logging;
using AECC.ECS.DefaultObjects.ECSComponents;
using AECC.ECS.DefaultObjects.Events.ECSEvents;
using AECC.ECS.Events.ECSEvents;
using AECC.Extensions;
using AECC.Extensions.ThreadingSync;
using AECC.Harness.Model;
using AECC.Harness.Services;
using AECC.Network;
using AECC.Serialization;
using AECC.TestKit;

namespace AECC.TestServer
{
    /// <summary>
    /// ФАЗА B — авторитарный сервер.
    ///
    /// Модель: сервер единолично владеет состоянием сущностей и РОЛЛИТ его клиентам
    /// (UpdateEntitiesEvent = системное событие с сериализованными сущностями/компонентами).
    /// Клиент состояние не роллит — присылает только бизнес-события (ClientCommandEvent).
    /// </summary>
    public static class GameServer
    {
        public static ECSWorld World;
        public static EntityNetSerializer Ser;
        public static TestReport R;

        public static ECSEntity Npc;
        public static ECSEntity Chest;
        public static ECSEntity ChestOwner;

        /// <summary>Сущности, которые сейчас раскатываются клиентам (кроме самих игроков).</summary>
        private static readonly ConcurrentDictionary<long, ECSEntity> Replicated =
            new ConcurrentDictionary<long, ECSEntity>();

        /// <summary>«Удостоверение» игрока: политика, instanceId которой служит ключом приватного доступа.</summary>
        private static readonly ConcurrentDictionary<long, ReplicationPolicy> PlayerIdentity =
            new ConcurrentDictionary<long, ReplicationPolicy>();

        private static readonly ConcurrentDictionary<string, int> CommandsSeen =
            new ConcurrentDictionary<string, int>();

        public static readonly List<string> ClientLines = new List<string>();
        public static readonly ManualResetEventSlim ClientFinished = new ManualResetEventSlim(false);
        public static int ClientPassed, ClientFailed;

        public static int SocketsConnected, SocketsDisconnected;
        public static long LastMaliciousScore;

        private static TimerCompat _rollTimer;
        private static volatile bool _spawned;

        // ─────────────────────────────────────────────────────────────────────
        public static void Start(ECSWorld world, TestReport report)
        {
            World = world;
            R = report;
            Ser = SerializationBootstrap.SerializerOf(world);

            WireAuth();
            WireNetworkHandlers();
            SpawnWorld();

            _rollTimer = new TimerCompat(TK.RollIntervalMs, (s, e) => RollTick(), loop: true, asyncRun: true);
            _rollTimer.Start();

            NLogger.LogSuccess("[SERVER] авторитарный мир поднят, роллинг запущен");
        }

        public static void Stop()
        {
            try { if (_rollTimer != null) { _rollTimer.Stop(); _rollTimer.Dispose(); } } catch { }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Мир
        // ─────────────────────────────────────────────────────────────────────
        private static void SpawnWorld()
        {
            // NPC: публично видны Position и Score; Health — приватный и НИКОМУ не отдаётся
            // (проверка GDAP-приватности «по проводу»).
            Npc = new ECSEntity { AliasName = "npc" };
            World.entityManager.AddNewEntity(Npc);
            Npc.AddComponent(Groups.Server(new PositionComponent { X = 0, Y = 0 }));
            Npc.AddComponent(Groups.Server(new ScoreComponent { Score = 100 }));
            Npc.AddComponent(Groups.Server(new HealthComponent { Hp = 500, MaxHp = 500 }));
            Npc.AddComponent(new VelocityComponent { VX = 1.0, VY = 0.5 });   // не реплицируется вовсе
            PublicPolicy(Npc, TK.Uid<PositionComponent>(), TK.Uid<ScoreComponent>());

            // Сундук с DB-агрегатором. Строки DB принадлежат ДРУГОЙ сущности (ChestOwner),
            // которую мы отправим клиенту ПОЗЖЕ — так проверяется отложенная десериализация
            // (PendingDeserializationRegistry + serializedDBNonEO + событийный retry).
            ChestOwner = new ECSEntity { AliasName = "chest-owner" };
            World.entityManager.AddNewEntity(ChestOwner);
            ChestOwner.AddComponent(Groups.Server(new PositionComponent { X = 50, Y = 50 }));
            ChestOwner.AddComponent(Groups.Server(new ChildMarkerComponent { Tag = "owner" }));
            PublicPolicy(ChestOwner, TK.Uid<PositionComponent>(), TK.Uid<ChildMarkerComponent>());

            Chest = new ECSEntity { AliasName = "chest" };
            World.entityManager.AddNewEntity(Chest);
            Chest.AddComponent(Groups.Server(new PositionComponent { X = 20, Y = 20 }));
            var inv = Groups.Server(new InventoryDBComponent());
            Chest.AddComponent(inv);
            inv.AddComponent(ChestOwner, new ItemComponent { ItemName = "sword", Count = 1 });
            inv.AddComponent(ChestOwner, new ItemComponent { ItemName = "potion", Count = 3 });
            PublicPolicy(Chest, TK.Uid<PositionComponent>(), TK.Uid<InventoryDBComponent>());

            Replicated[Npc.instanceId] = Npc;      // сундук и его владелец — по команде клиента

            _spawned = true;

            R.Section("S1 · серверный мир");
            R.Check("NPC создан", World.entityManager.ContainsEntitySyncronized(Npc.instanceId));
            R.Check("сундук с DB-агрегатором создан",
                Chest.HasComponent<InventoryDBComponent>() &&
                Chest.GetComponent<InventoryDBComponent>().GetComponentsByType<ItemComponent>().Count == 2);
            R.Check("MovementSystem поднята в Server-мире (WorldFilter)",
                World.contractsManager.AllSystems.Any(s => s is MovementSystem));
        }

        private static void PublicPolicy(ECSEntity e, params long[] publicComponents)
        {
            var pub = new ReplicationPolicy();
            pub.RestrictedComponents = new List<long>(publicComponents);
            e.dataAccessPolicies.Add(pub);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Авторизация: создание сущности игрока
        // ─────────────────────────────────────────────────────────────────────
        private static void WireAuth()
        {
            AuthService.instance.SetupAuthorizationRealization = (regEvent) =>
            {
                return new UserDataRowBase
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
            };

            AuthService.instance.AuthorizationRealization = (userData) =>
            {
                var player = new ECSEntity { AliasName = "player:" + userData.Username };
                player.ECSWorldOwner = World;    // AuthService падает, если мир не проставлен

                player.AddComponentSilent(new UsernameComponent { Username = userData.Username });
                player.AddComponentSilent(Groups.Server(new PlayerTagComponent { Login = userData.Username }));
                player.AddComponentSilent(Groups.Server(new PositionComponent { X = 10, Y = 10 }));
                player.AddComponentSilent(Groups.Server(new HealthComponent { Hp = 100, MaxHp = 100 }));
                player.AddComponentSilent(new VelocityComponent());   // серверная симуляция, не реплицируется

                // Приватная политика — она же «удостоверение» игрока-получателя:
                //   • на этой сущности она даёт доступ к AvailableComponents (Health);
                //   • её Clone() на другой сущности дал бы этому игроку приватный доступ и там.
                var identity = new ReplicationPolicy();
                identity.AvailableComponents = new List<long> { TK.Uid<HealthComponent>() };
                player.dataAccessPolicies.Add(identity);
                PublicPolicy(player, TK.Uid<PositionComponent>(), TK.Uid<PlayerTagComponent>());

                PlayerIdentity[player.instanceId] = identity;

                NLogger.LogSuccess("[SERVER] игрок авторизован: " + userData.Username + " → entity " + player.instanceId);
                return player;
            };

            UserLoggedEvent.actionAfterLoggin = (evt) =>
            {
                // на сервере это событие ИСХОДЯЩЕЕ (Destination задан) ⇒ Execute локально не зовётся,
                // хук оставлен для симметрии с клиентом
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Сетевые обработчики
        // ─────────────────────────────────────────────────────────────────────
        private static void WireNetworkHandlers()
        {
            SocketConnectedEvent.OnSocketConnected += (e) =>
            {
                Interlocked.Increment(ref SocketsConnected);
                NLogger.LogNetwork("[SERVER] сокет подключён: id=" + e.SocketId + " " + e.Address + ":" + e.Port);
            };

            SocketDisconnectedEvent.OnSocketDisconnected += (e) =>
            {
                Interlocked.Increment(ref SocketsDisconnected);
                NLogger.LogNetwork("[SERVER] сокет отвалился: id=" + e.SocketId);
            };

            ClientCommandEvent.Handler = HandleCommand;

            ClientReportEvent.Handler = (rep) =>
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

        private static void HandleCommand(ClientCommandEvent cmd)
        {
            CommandsSeen.AddOrUpdate(cmd.Cmd, 1, (k, v) => v + 1);
            NLogger.LogNetwork("[SERVER] команда клиента: " + cmd.Cmd);

            var socket = cmd.SocketSource;
            ECSEntity player = null;
            if (socket != null) AuthService.instance.SocketToEntity.TryGetValue(socket, out player);

            switch (cmd.Cmd)
            {
                case TK.C_Hello:
                {
                    // ждём окончания спавна и отдаём клиенту «карту мира»
                    TaskEx.RunAsync(() =>
                    {
                        TestReport.Await(() => _spawned && player != null, 10000);
                        if (player == null) return;

                        SendFullSnapshot(player, socket);

                        Dispatch(new ServerNoticeEvent
                        {
                            Kind = TK.N_WorldSpawned,
                            Payload = Npc.instanceId + ";" + Chest.instanceId + ";" + ChestOwner.instanceId,
                            EntityId = player.instanceId,
                            Destination = socket.CachedDestination
                        });
                    });
                    break;
                }

                case TK.C_Move:
                {
                    if (player == null) return;
                    var vel = player.TryGetComponent<VelocityComponent>();
                    if (vel == null) return;
                    lock (vel.SerialLocker) { vel.VX = cmd.X; vel.VY = cmd.Y; }
                    vel.MarkAsChanged();          // не реплицируется, но участвует в симуляции
                    Notice(socket, TK.N_MoveApplied, cmd.X + ";" + cmd.Y, player.instanceId);
                    break;
                }

                case TK.C_Damage:
                {
                    if (player == null) return;
                    var hp = player.TryGetComponent<HealthComponent>();
                    if (hp == null) return;
                    lock (hp.SerialLocker) { hp.Hp -= Math.Max(1, cmd.Amount); }
                    hp.MarkAsChanged();           // приватный компонент → уедет только владельцу
                    break;
                }

                case TK.C_RemoveScore:
                {
                    Npc.RemoveComponentIfPresent<ScoreComponent>();
                    Notice(socket, TK.N_ScoreRemoved, "", Npc.instanceId);
                    break;
                }

                case TK.C_SendChest:
                {
                    Replicated[Chest.instanceId] = Chest;
                    ForceFullResend(Chest);
                    Notice(socket, TK.N_ChestSent, "", Chest.instanceId);
                    break;
                }

                case TK.C_SendChestOwner:
                {
                    Replicated[ChestOwner.instanceId] = ChestOwner;
                    ForceFullResend(ChestOwner);
                    Notice(socket, TK.N_ChestOwnerSent, "", ChestOwner.instanceId);
                    break;
                }

                case TK.C_Finish:
                {
                    if (socket != null)
                    {
                        ScoreObject so;
                        if (NetworkService.instance.EventManager.MaliciousScoringStorage.TryGetValue(socket.Id, out so))
                            Interlocked.Exchange(ref LastMaliciousScore, so.Score);
                    }
                    break;
                }
            }
        }

        private static void Notice(ISocketAdapter socket, string kind, string payload, long entityId)
        {
            if (socket == null) return;
            Dispatch(new ServerNoticeEvent
            {
                Kind = kind,
                Payload = payload,
                EntityId = entityId,
                Destination = socket.CachedDestination
            });
        }

        private static void Dispatch(NetworkEvent evt)
        {
            evt.WorldOwnerId = TK.WorldId;
            NetworkService.instance.EventManager.Dispatch(evt);
        }

        /// <summary>Помечает все компоненты изменёнными, чтобы сущность целиком уехала следующим срезом.</summary>
        private static void ForceFullResend(ECSEntity e)
        {
            foreach (var c in e.entityComponents.Components)
                c.MarkAsChanged(false, true);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Роллинг
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Полный снапшот для только что вошедшего игрока (догоняющий клиент).</summary>
        private static void SendFullSnapshot(ECSEntity player, ISocketAdapter socket)
        {
            var blobs = new List<byte[]>();
            foreach (var src in SourcesFor(player))
            {
                try
                {
                    var b = Ser.BuildFullSerializedEntityWithGDAP(player, src);
                    if (b != null && b.Length > 0) blobs.Add(b);
                }
                catch (Exception ex) { NLogger.LogError("[SERVER] snapshot " + src.AliasName + ": " + ex.Message); }
            }
            if (blobs.Count == 0) return;

            Dispatch(new UpdateEntitiesEvent
            {
                EntityIdRecipient = player.instanceId,
                Entities = blobs,
                Destination = socket.CachedDestination
            });
            NLogger.LogNetwork("[SERVER] полный снапшот игроку " + player.instanceId + ": " + blobs.Count + " сущностей");
        }

        private static IEnumerable<ECSEntity> SourcesFor(ECSEntity player)
        {
            yield return player;                                  // своя сущность (в т.ч. приватные компоненты)
            foreach (var kv in Replicated) yield return kv.Value; // мировые сущности
        }

        private static void RollTick()
        {
            try
            {
                var players = AuthService.instance.EntityToSocket.ToArray();
                if (players.Length == 0) return;

                // 1) один срез на сущность за тик (SerializeEntity чистит dirty-set и заполняет GDAP-бины)
                var sources = new List<ECSEntity>();
                foreach (var kv in players) sources.Add(kv.Key);
                foreach (var kv in Replicated) sources.Add(kv.Value);

                foreach (var src in sources.Distinct())
                {
                    try { Ser.SerializeEntity(src, true); }
                    catch (Exception ex) { NLogger.LogError("[SERVER] SerializeEntity " + src.AliasName + ": " + ex.Message); }
                }

                // 2) на каждого получателя — GDAP-фильтрация уже готового среза
                foreach (var kv in players)
                {
                    var player = kv.Key;
                    var socket = kv.Value;
                    if (socket == null || !socket.IsConnected) continue;

                    var blobs = new List<byte[]>();
                    foreach (var src in sources.Distinct())
                    {
                        try
                        {
                            var b = Ser.BuildSerializedEntityWithGDAP(player, src);
                            if (b != null && b.Length > 0) blobs.Add(b);
                        }
                        catch (Exception ex)
                        {
                            NLogger.LogError("[SERVER] GDAP " + src.AliasName + ": " + ex.Message);
                        }
                    }

                    if (blobs.Count == 0) continue;

                    Dispatch(new UpdateEntitiesEvent
                    {
                        EntityIdRecipient = player.instanceId,
                        Entities = blobs,
                        Destination = socket.CachedDestination
                    });
                }
            }
            catch (Exception ex)
            {
                NLogger.LogError("[SERVER] RollTick: " + ex);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Серверные проверки (после завершения сценария клиентом)
        // ─────────────────────────────────────────────────────────────────────
        public static void Verify(SqliteDbProvider db)
        {
            R.Section("S2 · БД и авторизация (серверная сторона)");

            var row = db.GetUserViaCallsign<UserDataRowBase>(TK.User);
            R.Check("пользователь создан в SQLite", row != null && row.Username.Equals(TK.User, StringComparison.OrdinalIgnoreCase));
            if (row != null)
            {
                R.CheckEq("пароль сохранён как MD5-хеш", HashExtension.MD5(TK.Password), row.Password);
                R.CheckEq("email сохранён", TK.Email, row.Email);
                R.Check("id автоинкремента проставлен", row.Id > 0);
            }
            R.Check("LoginCheck принимает верный пароль", db.LoginCheck(TK.User, HashExtension.MD5(TK.Password)));
            R.Check("LoginCheck отвергает неверный пароль", !db.LoginCheck(TK.User, HashExtension.MD5("wrong")));
            R.Check("UsernameAvailable == false после регистрации", !db.UsernameAvailable(TK.User));

            R.Check("AuthService связал сокет и сущность игрока",
                AuthService.instance.SocketToEntity.Count >= 1 && AuthService.instance.EntityToSocket.Count >= 1);

            var player = AuthService.instance.EntityToSocket.Keys.FirstOrDefault();
            R.Check("сущность игрока живёт в мире",
                player != null && World.entityManager.ContainsEntitySyncronized(player.instanceId));
            R.Check("на сущности игрока есть SocketComponent",
                player != null && player.HasComponent<SocketComponent>());
            R.Check("на сущности игрока есть UsernameComponent",
                player != null && player.HasComponent<UsernameComponent>() &&
                player.GetComponent<UsernameComponent>().Username == TK.User);

            R.Section("S3 · сеть и авторитарность");
            R.Check("были подключения сокетов", SocketsConnected >= 1);
            R.Check("команды клиента дошли (HELLO/MOVE/DAMAGE/…)",
                CommandsSeen.ContainsKey(TK.C_Hello) && CommandsSeen.ContainsKey(TK.C_Move) &&
                CommandsSeen.ContainsKey(TK.C_Damage),
                "получено: " + string.Join(",", CommandsSeen.Select(x => x.Key + "×" + x.Value)));
            R.Check("MovementSystem реально тикала (авторитарная симуляция)", MovementSystem.Ticks > 0,
                "ticks=" + MovementSystem.Ticks);
            R.Check("NPC сдвинулся серверной системой",
                Math.Abs(Npc.GetComponent<PositionComponent>().X) > 0.5,
                "npc.X=" + Npc.GetComponent<PositionComponent>().X);

            R.Section("S4 · защита от мусорного трафика");
            R.CheckEq("MaliciousProbeEvent.CheckPacket() отбросил пакет (Execute не вызван)",
                0, MaliciousProbeEvent.ExecutedCount);
            R.Check("malicious-score накопился на сокете", Interlocked.Read(ref LastMaliciousScore) >= 250,
                "score=" + Interlocked.Read(ref LastMaliciousScore));

            R.Section("S5 · отчёт клиента");
            lock (ClientLines)
                foreach (var l in ClientLines) Console.WriteLine("   client │ " + l);
            R.Check("клиент прислал финальный отчёт", ClientFinished.IsSet);
            R.CheckEq("у клиента нет провалов", 0, ClientFailed);
        }
    }
}
