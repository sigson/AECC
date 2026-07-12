using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AECC.Core;
using AECC.Core.BuiltInTypes.Components;
using AECC.Core.Logging;
using AECC.ECS.DefaultObjects.ECSComponents;
using AECC.ECS.DefaultObjects.Events.ECSEvents;
using AECC.ECS.DefaultObjects.Events.LowLevelNetEvent.Auth;
using AECC.Extensions;
using AECC.Harness.Model;
using AECC.Harness.Serialization;
using AECC.Harness.Services;
using AECC.Network;
using AECC.Serialization;
using AECC.TestKit;

namespace AECC.TestClient
{
    /// <summary>
    /// AECC_TestClient — ФАЗА C: полный клиентский цикл поверх реальной сети.
    ///
    /// Клиент НИКОГДА не роллит сущности на сервер: он только
    ///   • применяет входящий роллинг (UpdateEntitiesEvent → UpdateDeserialize);
    ///   • считает свою локальную логику (свои компоненты в ClientComponentGroup);
    ///   • отправляет бизнес-события (ClientCommandEvent).
    ///
    /// Запускать ПОСЛЕ AECC_TestServer.
    /// </summary>
    public static class Program
    {
        private static readonly TestReport R = new TestReport("AECC · CLIENT (ФАЗА C: полный сетевой цикл)");
        private static ECSWorld World;

        // ── сигналы от входящих событий ──
        private static volatile UserLoggedEvent _logged;
        private static volatile AuthActionFailedEvent _authFailed;
        private static volatile IsUsernameAvailableEvent _usernameAnswer;
        private static readonly Dictionary<string, ServerNoticeEvent> Notices = new Dictionary<string, ServerNoticeEvent>();
        private static long _playerEntityId, _npcId, _chestId, _chestOwnerId;
        private static NetworkDestination _server;

        public static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            Bootstrapping.ConfigureKernel(multiThread: true);
            SerializationBootstrap.GetSerializationAdapter = () => new SerializationAdapter();
            PrepareFileSystem();

            // Клиентский мир — ДО сервисов: входящий роллинг обязан находить мир по
            // WorldOwnerId (это ОБЩАЯ константа TK.WorldId — ровно поэтому серверная и
            // клиентская сущности резолвят один и тот же ECSWorldOwnerId).
            World = Bootstrapping.CreateWorld(TK.WorldId, ECSWorld.WorldTypeEnum.Client, new SerializationAdapter());

            WireHandlers();

            bool servicesOk = StartServices();

            R.Section("C0 · сервисы и конфиг");
            R.Check("все IService инициализированы (в т.ч. обмен конфигом с сервером)", servicesOk);
            R.Check("ConstantService получил конфиг от сервера (ConfigCheckResultEvent)",
                ConstantService.instance.Loaded);
            R.Check("baseconfig доступен", ConstantService.instance.GetByConfigPath("baseconfig") != null);
            R.Check("на клиенте DBProvider отсутствует (БД — только серверная)",
                DBService.instance.DBProvider == null);

            Bootstrapping.RestoreWorldResolver();
            R.Check("клиентский мир имеет тот же instanceId, что и серверный",
                World.instanceId == TK.WorldId && ECSWorld.GetWorld(TK.WorldId) != null);
            R.Check("профиль мира — Client (retry-десериализация, identity-keyed lifecycle)",
                World.Profile.ClientRetryOnMissingRefs && World.Profile.IdentityKeyedLifecycleState);

            try
            {
                C1_Socket();
                C2_Auth();
                C3_Rolling();
                C4_ClientLogicAndCommands();
                C5_RemovalDelivery();
                C6_DbComponentAndPendingDeserialization();
                C7_MaliciousTraffic();
            }
            catch (Exception ex)
            {
                R.Check("сценарий клиента без исключений", false, ex.ToString());
            }

            SendReportToServer();

            R.PrintSummary();
            Thread.Sleep(500);
            try { World.Dispose(); } catch { }
            return R.Failed == 0 ? 0 : 1;
        }

        // ─────────────────────────────────────────────────────────────────────
        private static void PrepareFileSystem()
        {
            var gameData = Path.Combine(AppContext.BaseDirectory, "GameData");
            Directory.CreateDirectory(Path.Combine(gameData, "GameConfig"));
        }

        private static bool StartServices()
        {
            var allDone = new ManualResetEventSlim(false);
            IService.SyncManager.OnAllServicesCompleted += () => allDone.Set();

            IService.RegisterAllServices();

            GlobalProgramState.instance.ProgramType = GlobalProgramState.ProgramTypeEnum.Client;

            _server = NetworkDestination.ForHost(TK.Host, TK.Port, NetworkProtocol.TCP);
            GlobalProgramState.instance.ClientNetworkGameDestination = _server;

            NetworkService.instance.EndpointConfigs.Add(new NetworkDestination
            {
                Host = TK.Host,
                Port = TK.Port,
                Protocol = NetworkProtocol.TCP,
                IsListener = false,
                BufferSize = 65536,
            });

            // Предзасев конфига: DBService.InitializeProcess читает baseconfig БЕЗУСЛОВНО
            // (даже на клиенте), а ConstantService на клиенте наполняется только после
            // ответа сервера ⇒ без предзасева гарантированный NRE на гонке шагов.
            ConstantService.instance.SetupConfigs(GlobalProgramState.instance.GameConfigDir);

            IService.InitializeAllServices();

            // ConstantService на клиенте ЗАМОРАЖИВАЕТСЯ до прихода ConfigCheckResultEvent —
            // значит этот Wait заодно проверяет живой сетевой обмен с сервером.
            bool ok = allDone.Wait(TimeSpan.FromSeconds(60));
            if (!ok)
            {
                NLogger.Error("[CLIENT] сервисы не инициализировались за 60 c — размораживаем ConstantService принудительно");
                try { ConstantService.instance.UnfreezeConstantService(); } catch { }
                allDone.Wait(TimeSpan.FromSeconds(10));
            }
            return ok;
        }

        private static void WireHandlers()
        {
            UserLoggedEvent.actionAfterLoggin = (e) => { _logged = e; };
            AuthActionFailedEvent.action = (e) => { _authFailed = e; };
            IsUsernameAvailableEvent.action = (e) => { _usernameAnswer = e; };

            ServerNoticeEvent.Handler = (n) =>
            {
                lock (Notices) Notices[n.Kind] = n;
                NLogger.LogNetwork("[CLIENT] нотис сервера: " + n.Kind + " " + n.Payload);
            };
        }

        private static bool GotNotice(string kind, out ServerNoticeEvent n)
        {
            lock (Notices) return Notices.TryGetValue(kind, out n);
        }

        private static void Send(NetworkEvent evt)
        {
            evt.WorldOwnerId = TK.WorldId;
            evt.Destination = _server;
            NetworkService.instance.EventManager.Dispatch(evt);
        }

        private static void Cmd(string cmd, double x = 0, double y = 0, int amount = 0, long target = 0)
        {
            Send(new ClientCommandEvent { Cmd = cmd, X = x, Y = y, Amount = amount, TargetEntityId = target });
        }

        private static ECSEntity Ent(long id)
        {
            ECSEntity e;
            return World.entityManager.TryGetEntitySyncronized(id, out e) ? e : null;
        }

        // ─────────────────────────────────────────────────────────────────────
        private static void C1_Socket()
        {
            R.Section("C1 · сокет, identity-handshake, ping");

            R.AwaitCheck("клиентский сокет подключился", () =>
                NetworkService.instance.ClientSockets.Values.Any(s => s.IsConnected), 15000);

            var socket = NetworkService.instance.ClientSockets.Values.FirstOrDefault();
            R.AwaitCheck("сервер выдал identity (AssignId → ConfirmId), socket.Id != 0",
                () => socket != null && socket.Id != 0, 15000);
            R.Check("сокет попал в SocketsById", NetworkService.instance.SocketsById.Count >= 1);
            R.Check("CachedDestination построен (zero-alloc reply routing)",
                socket != null && socket.CachedDestination != null);

            R.AwaitCheck("PingService измерил RTT (LatencyMs >= 0)",
                () => socket != null && socket.LatencyMs >= 0, 12000);
        }

        // ─────────────────────────────────────────────────────────────────────
        private static void C2_Auth()
        {
            R.Section("C2 · регистрация / логин / relogin");

            // Проверка доступности логина. ВНИМАНИЕ: IsUsernameAvailableEvent во фреймворке
            // обрабатывается сервером, но ОТВЕТ КЛИЕНТУ НЕ ОТПРАВЛЯЕТСЯ (см. FRAMEWORK_MAP §10.14).
            _usernameAnswer = null;
            Send(new IsUsernameAvailableEvent { Username = TK.User });
            Thread.Sleep(1200);
            R.Check("IsUsernameAvailableEvent: сервер не присылает ответ (известный пробел фреймворка)",
                _usernameAnswer == null,
                "если ответ пришёл — фреймворк починен, поправьте ожидание теста");

            // Регистрация
            _logged = null; _authFailed = null;
            Send(new ClientRegistrationEvent
            {
                Username = TK.User,
                Password = TK.Password,
                Email = TK.Email,
                HardwareId = "TESTHW",
            });

            R.AwaitCheck("регистрация → UserLoggedEvent", () => _logged != null, 15000);
            if (_logged != null)
            {
                R.Check("это первичный вход (userRelogin == false)", !_logged.userRelogin);
                R.CheckEq("username в ответе", TK.User, _logged.Username);
                R.Check("userEntityId проставлен", _logged.userEntityId != 0);
                _playerEntityId = _logged.userEntityId;
            }
            R.CheckEq("GlobalProgramState.PlayerEntityId выставлен из UserLoggedEvent.Execute",
                _playerEntityId, GlobalProgramState.instance.PlayerEntityId);
            R.CheckEq("GlobalProgramState.Username выставлен", TK.User, GlobalProgramState.instance.Username);

            // Неверный пароль
            _authFailed = null;
            Send(new ClientAuthEvent { Username = TK.User, Password = "wrongpass" });
            R.AwaitCheck("неверный пароль → AuthActionFailedEvent", () => _authFailed != null, 10000);
            if (_authFailed != null)
                R.Check("в ответе указана причина", !string.IsNullOrEmpty(_authFailed.Reason), _authFailed.Reason);

            // Верный пароль ⇒ relogin на ту же сущность
            _logged = null;
            Send(new ClientAuthEvent { Username = TK.User, Password = TK.Password });
            R.AwaitCheck("верный пароль → UserLoggedEvent", () => _logged != null, 10000);
            if (_logged != null)
            {
                R.Check("это повторный вход (userRelogin == true)", _logged.userRelogin);
                R.CheckEq("сущность игрока та же", _playerEntityId, _logged.userEntityId);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        private static void C3_Rolling()
        {
            R.Section("C3 · авторитарный роллинг сервера → клиент");

            Cmd(TK.C_Hello);
            ServerNoticeEvent spawn = null;
            R.AwaitCheck("сервер прислал карту мира (ServerNoticeEvent)",
                () => GotNotice(TK.N_WorldSpawned, out spawn), 20000);

            if (spawn == null) return;
            var ids = spawn.Payload.Split(';');
            _npcId = long.Parse(ids[0]);
            _chestId = long.Parse(ids[1]);
            _chestOwnerId = long.Parse(ids[2]);
            if (_playerEntityId == 0) _playerEntityId = spawn.EntityId;

            // ── своя сущность ──
            R.AwaitCheck("сущность игрока приехала роллингом (UpdateEntitiesEvent → UpdateDeserialize)",
                () => Ent(_playerEntityId) != null, 15000);

            var me = Ent(_playerEntityId);
            if (me != null)
            {
                R.AwaitCheck("у себя виден Position (public)", () => me.HasComponent<PositionComponent>(), 8000);
                R.AwaitCheck("у себя виден PlayerTag (public)", () => me.HasComponent<PlayerTagComponent>(), 8000);
                R.AwaitCheck("у себя виден ПРИВАТНЫЙ Health (GDAP Available по instanceId политики)",
                    () => me.HasComponent<HealthComponent>(), 8000);
                R.Check("Velocity НЕ реплицируется (нет ни в одном списке политики)",
                    !me.HasComponent<VelocityComponent>());
                R.Check("сущность принадлежит клиентскому миру",
                    me.ECSWorldOwner != null && me.ECSWorldOwner.instanceId == TK.WorldId);
                R.Check("реплицированные компоненты помечены серверной группой",
                    me.GetComponent<PositionComponent>().ComponentGroups != null &&
                    me.GetComponent<PositionComponent>().ComponentGroups.ContainsKey(
                        AECC.Core.BuiltInTypes.ComponentsGroup.ServerComponentGroup.Id));
            }

            // ── NPC: проверка GDAP-приватности ПО ПРОВОДУ ──
            R.AwaitCheck("NPC приехал", () => Ent(_npcId) != null, 15000);
            var npc = Ent(_npcId);
            if (npc != null)
            {
                R.AwaitCheck("у NPC виден Position (Restricted)", () => npc.HasComponent<PositionComponent>(), 8000);
                R.AwaitCheck("у NPC виден Score (Restricted)", () => npc.HasComponent<ScoreComponent>(), 8000);
                R.Check("у NPC НЕ виден Health — приватный компонент чужой сущности не утёк",
                    !npc.HasComponent<HealthComponent>());
                R.Check("у NPC НЕ виден Velocity", !npc.HasComponent<VelocityComponent>());

                // Авторитарная симуляция: сервер двигает NPC своей системой (MovementSystem),
                // клиент видит только результат.
                double x0 = npc.GetComponent<PositionComponent>().X;
                R.AwaitCheck("позиция NPC меняется роллингом (серверная симуляция)",
                    () => Math.Abs(npc.GetComponent<PositionComponent>().X - x0) > 0.9, 8000);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        private static void C4_ClientLogicAndCommands()
        {
            R.Section("C4 · клиентская логика и бизнес-события клиент → сервер");

            var me = Ent(_playerEntityId);
            if (me == null) { R.Check("сущность игрока доступна", false); return; }

            // 1) Клиентский локальный компонент (ClientComponentGroup) — сервер о нём не знает,
            //    роллинг обязан его СОХРАНИТЬ (FilterRemovedComponents трогает только чужую группу).
            var local = Groups.Client(new ClientPredictionComponent { PredictedX = 42, PredictedY = 24 });
            me.AddComponent(local);
            R.Check("клиентский компонент добавлен локально", me.HasComponent<ClientPredictionComponent>());

            // 2) MOVE: клиент НЕ двигает себя сам — просит сервер. Сервер меняет Velocity,
            //    его MovementSystem интегрирует Position, результат приезжает роллингом.
            double x0 = me.GetComponent<PositionComponent>().X;
            Cmd(TK.C_Move, x: 2.0, y: 0.0);

            ServerNoticeEvent _;
            R.AwaitCheck("сервер подтвердил приём команды MOVE", () => GotNotice(TK.N_MoveApplied, out _), 8000);
            R.AwaitCheck("позиция игрока изменилась ПОСЛЕ серверной симуляции (авторитарность)",
                () => me.GetComponent<PositionComponent>().X > x0 + 1.5, 8000,
                "x0=" + x0);

            R.Check("клиентский компонент пережил несколько раундов роллинга",
                me.HasComponent<ClientPredictionComponent>() &&
                Math.Abs(me.GetComponent<ClientPredictionComponent>().PredictedX - 42) < 1e-9);

            // 3) DAMAGE: приватный компонент меняется на сервере и доезжает только владельцу
            int hp0 = me.GetComponent<HealthComponent>().Hp;
            Cmd(TK.C_Damage, amount: 7);
            R.AwaitCheck("приватный Health обновился роллингом (Hp уменьшился на 7)",
                () => me.GetComponent<HealthComponent>().Hp == hp0 - 7, 8000,
                "hp0=" + hp0 + " now=" + me.GetComponent<HealthComponent>().Hp);
        }

        // ─────────────────────────────────────────────────────────────────────
        private static void C5_RemovalDelivery()
        {
            R.Section("C5 · доставка удалений компонентов");

            var npc = Ent(_npcId);
            if (npc == null) { R.Check("NPC доступен", false); return; }

            R.Check("до удаления Score на месте", npc.HasComponent<ScoreComponent>());

            Cmd(TK.C_RemoveScore);
            ServerNoticeEvent _;
            R.AwaitCheck("сервер подтвердил снятие Score", () => GotNotice(TK.N_ScoreRemoved, out _), 8000);

            R.AwaitCheck("компонент, снятый на сервере, снят и у клиента (FilterRemovedComponents по серверной группе)",
                () => !npc.HasComponent<ScoreComponent>(), 8000);
            R.Check("остальные компоненты NPC не пострадали", npc.HasComponent<PositionComponent>());
        }

        // ─────────────────────────────────────────────────────────────────────
        private static void C6_DbComponentAndPendingDeserialization()
        {
            R.Section("C6 · DBComponent по сети + отложенная десериализация");

            // Сундук приезжает БЕЗ сущности-владельца строк DB ⇒ UnserializeDB не может
            // разрешить IECSObjectPathContainer и паркует строки в serializedDBNonEO,
            // регистрируясь в PendingDeserializationRegistry.
            Cmd(TK.C_SendChest);
            R.AwaitCheck("сундук приехал", () => Ent(_chestId) != null, 15000);

            var chest = Ent(_chestId);
            if (chest == null) return;

            R.AwaitCheck("на сундуке есть DB-агрегатор", () => chest.HasComponent<InventoryDBComponent>(), 8000);
            var inv = chest.TryGetComponent<InventoryDBComponent>();
            if (inv == null) { R.Check("InventoryDBComponent доступен", false); return; }

            Thread.Sleep(400);
            bool parked = inv.serializedDBNonEO.Count > 0 || inv.GetComponentsByType<ItemComponent>().Count == 0;
            R.Check("строки DB припаркованы: владелец строк ещё не приехал",
                parked,
                "NonEO=" + inv.serializedDBNonEO.Count + " items=" + inv.GetComponentsByType<ItemComponent>().Count);

            // Присылаем владельца ⇒ приход сущности дёргает RequestDrain ⇒ событийный retry
            Cmd(TK.C_SendChestOwner);
            R.AwaitCheck("владелец строк приехал", () => Ent(_chestOwnerId) != null, 15000);

            bool restored = TestReport.Await(() => inv.GetComponentsByType<ItemComponent>().Count == 2, 15000);
            R.Check("после прихода владельца DB-строки восстановились (событийный retry)",
                restored,
                restored ? "" : "items=" + inv.GetComponentsByType<ItemComponent>().Count);

            // ── ДИАГНОСТИКА (печатается только если событийный retry не сработал) ──
            // Разделяет два сценария: (а) retry вообще не был вызван / вызван на другом
            // инстансе; (б) retry вызывался, но IECSObjectPathContainer не резолвит владельца.
            if (!restored)
            {
                Console.WriteLine();
                Console.WriteLine("  ┌─ ДИАГНОСТИКА C6 ─────────────────────────────────────────");
                Console.WriteLine("  │ serializedDB.Count = " + inv.serializedDB.Count);
                Console.WriteLine("  │ serializedDBNonEO.Count = " + inv.serializedDBNonEO.Count);
                Console.WriteLine("  │ PendingDeserialization.HasPending = " +
                                  World.entityManager.PendingDeserialization.HasPending);
                Console.WriteLine("  │ chestOwnerId = " + _chestOwnerId +
                                  ", в мире = " + (Ent(_chestOwnerId) != null));
                Console.WriteLine("  │ inv.ECSWorldOwnerId = " + inv.ECSWorldOwnerId +
                                  ", резолвится = " + (inv.ECSWorldOwner != null));

                foreach (var kv in inv.serializedDBNonEO)
                {
                    var key = kv.Key;
                    Console.WriteLine("  │ NonEO key: path=[" + string.Join(" > ", key.pathToECSObject) + "]");
                    Console.WriteLine("  │           serializableInstanceId = " + key.serializableInstanceId);
                    Console.WriteLine("  │           ECSWorldOwnerId = " + key.ECSWorldOwnerId +
                                      ", мир резолвится = " + (key.ECSWorldOwner != null));
                    Console.WriteLine("  │           key.ECSObject != null = " + (key.ECSObject != null));
                    Console.WriteLine("  │           AlwaysUpdateCache = " + key.AlwaysUpdateCache);
                    Console.WriteLine("  │           строк = " + kv.Value.Item1.Count + ", попыток = " + kv.Value.Item2);
                }

                // Ручной ретрай тем же вызовом, что дёргает реестр.
                Console.WriteLine("  │ → ручной вызов inv.UnserializeDB(true)");
                try { inv.UnserializeDB(true); }
                catch (Exception ex) { Console.WriteLine("  │   ИСКЛЮЧЕНИЕ: " + ex); }
                Thread.Sleep(300);

                int manual = inv.GetComponentsByType<ItemComponent>().Count;
                Console.WriteLine("  │ items после ручного ретрая = " + manual);
                Console.WriteLine("  └──────────────────────────────────────────────────────────");
                Console.WriteLine();

                R.Check("ДИАГНОЗ: ручной UnserializeDB(true) восстановил строки " +
                        "(⇒ данные и путь целы, не отработал событийный retry)",
                        manual == 2, "items=" + manual);
            }

            var items = inv.GetComponentsByType<ItemComponent>()
                .Select(x => (ItemComponent)x.Item1).OrderBy(x => x.ItemName).ToList();
            if (items.Count == 2)
            {
                R.CheckEq("предмет #1", "potion", items[0].ItemName);
                R.CheckEq("количество #1", 3, items[0].Count);
                R.CheckEq("предмет #2", "sword", items[1].ItemName);
                R.Check("у вложенного компонента проставлен ownerDB", ReferenceEquals(inv, items[0].ownerDB));
            }

            // ВНИМАНИЕ: PendingDeserialization.HasPending — плохой индикатор: Drain() очищает
            // словарь ДО запуска попыток, поэтому «пусто» ловится и в момент неудачного слива.
            // Надёжный признак — что у DB-компонента не осталось припаркованных строк.
            R.AwaitCheck("припаркованных DB-строк не осталось (serializedDBNonEO пуст)",
                () => inv.serializedDBNonEO.Count == 0, 8000);
        }

        // ─────────────────────────────────────────────────────────────────────
        private static void C7_MaliciousTraffic()
        {
            R.Section("C7 · мусорный трафик");

            // CheckPacket() == false ⇒ сервер обязан отбросить пакет, но начислить score.
            for (int i = 0; i < 2; i++)
                Send(new MaliciousProbeEvent { Poison = true });

            Thread.Sleep(600);
            Cmd(TK.C_Finish);
            Thread.Sleep(400);
            R.Check("«злые» пакеты отправлены (проверка отбраковки — на серверной стороне)", true);
        }

        // ─────────────────────────────────────────────────────────────────────
        private static void SendReportToServer()
        {
            foreach (var res in R.Results.Where(x => !x.Ok))
            {
                Send(new ClientReportEvent
                {
                    Line = res.Section + " / " + res.Name + (string.IsNullOrEmpty(res.Detail) ? "" : " — " + res.Detail),
                    Ok = false,
                });
                Thread.Sleep(20);
            }

            Send(new ClientReportEvent
            {
                Line = R.ToCompactString(),
                Ok = R.Failed == 0,
                Final = true,
                Passed = R.Passed,
                Failed = R.Failed,
            });

            Thread.Sleep(800);   // дать outbound-буферу уйти в сокет
        }
    }
}
