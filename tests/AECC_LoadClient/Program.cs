using System;
using System.IO;
using System.Linq;
using System.Threading;
using AECC.Core;
using AECC.Core.Logging;
using AECC.Harness.Model;
using AECC.Harness.Serialization;
using AECC.Harness.Services;
using AECC.LoadKit;
using AECC.Network;
using AECC.Serialization;
using AECC.TestKit;

namespace AECC.LoadClient
{
    /// <summary>
    /// AECC_LoadClient — мультиклиент-хост нагрузочного сессионного теста.
    ///
    /// Запуск (ПОСЛЕ AECC_LoadServer):
    ///   dotnet run -c Release --project tests/AECC_LoadClient [clients=8] [durationSec=75] [prefix=load&lt;rnd&gt;]
    ///
    /// Хост держит внутри себя N виртуальных клиентов (N ≤ LK.MulticlientCapacity),
    /// каждый — со своим TCP-соединением, регистрацией и поведением. Двойные проверки
    /// клиент⇄сервер отключаются AECC_LOAD_VERIFYMODE=false (чистая нагрузка).
    ///
    /// Exit code 0 — все проверки мультиклиента прошли.
    /// </summary>
    public static class Program
    {
        private static readonly TestReport R = new TestReport("AECC · MULTICLIENT (нагрузочный сессионный тест)");
        private static ECSWorld World;
        private static NetworkDestination _server;

        public static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            int clients = args.Length > 0 && int.TryParse(args[0], out var c) ? c : 8;
            int durationSec = args.Length > 1 && int.TryParse(args[1], out var d) ? d : 75;
            string prefix = args.Length > 2 ? args[2] : "load" + DateTime.UtcNow.ToString("HHmmss");

            LK.ApplyEnvOverrides();
            clients = Math.Min(clients, LK.MulticlientCapacity);

            Bootstrapping.ConfigureKernel(multiThread: true);
            SerializationBootstrap.GetSerializationAdapter = () => new SerializationAdapter();
            PrepareFileSystem();

            // ОБЩИЙ клиентский мир хоста — ДО сервисов; instanceId == серверному (LK.WorldId):
            // конверт роллинга и path-контейнеры DB резолвят мир по этому id.
            World = Bootstrapping.CreateWorld(LK.WorldId, ECSWorld.WorldTypeEnum.Client, new SerializationAdapter());

            bool servicesOk = StartServices();

            R.Section("MC0 · сервисы и мир");
            R.Check("все IService инициализированы (в т.ч. обмен конфигом)", servicesOk);
            Bootstrapping.RestoreWorldResolver();
            R.Check("общий клиентский мир резолвится по LK.WorldId",
                ECSWorld.GetWorld(LK.WorldId) != null && World.instanceId == LK.WorldId);
            R.Check("профиль мира — Client (retry-десериализация, identity-keyed lifecycle)",
                World.Profile.ClientRetryOnMissingRefs && World.Profile.IdentityKeyedLifecycleState);
            R.Check("мультиклиент в пределах ёмкости: " + clients + " ≤ " + LK.MulticlientCapacity,
                clients <= LK.MulticlientCapacity);

            // На сотнях клиентов Network-категория (connecting/confirmed на каждый сокет)
            // забивает 90% вывода — глушим; ошибки и Success идут другими типами.
            if (clients > 32)
                NLogger.MutedLogTypes.Add("Network");

            Multiclient.Start(World, R, _server, clients, prefix);

            NLogger.LogSuccess("[MC] " + clients + " виртуальных клиентов запущены; нагрузка " +
                               durationSec + " c, verify=" + LK.VerifyMode);

            // ── фаза разгона: все должны войти в игру ──
            // Спавн идёт в фоне с темпом ClientSpawnDelayMs — таймауты проверок входа
            // должны вмещать всё окно спавна (при 1000 клиентах это ~2 минуты).
            int spawnWindowMs = clients * LK.ClientSpawnDelayMs;
            R.Section("MC1 · подключение и вход");
            R.AwaitCheck("все клиенты авторизовались (UserLoggedEvent)",
                () => Multiclient.Clients.All(v => v.PlayerEntityId != 0), 60000 + spawnWindowMs);
            R.AwaitCheck("карта сессий получена (SERVER_READY, " + LK.MaxSessionsOnServer + " сессий)",
                () => Multiclient.SessionEntityIds.Length == LK.MaxSessionsOnServer, 30000);
            // GDAP №2: не вошедшему видна только «карточка» (Info + Modifier);
            // база мин доезжает лишь участнику сессии после JOIN.
            R.AwaitCheck("все сессии приехали раскаткой и несут SessionInfo",
                () => Multiclient.SessionEntityIds.Length > 0 && Multiclient.SessionEntityIds.All(id =>
                {
                    var e = Multiclient.Ent(id);
                    return e != null && e.HasComponent<SessionInfoComponent>() &&
                           e.HasComponent<SessionModifierComponent>();
                }), 30000);
            R.AwaitCheck("каждый клиент хотя бы раз вошёл в сессию (выбор — на клиенте)",
                () => Multiclient.Clients.All(v => v.MyJoins > 0), 60000 + spawnWindowMs);

            // ── основная нагрузка ──
            Thread.Sleep(TimeSpan.FromSeconds(durationSec));

            // ── останов активности и финальная сверка ──
            Multiclient.StopActivity();
            Thread.Sleep(2500); // дать долететь хвостам роллинга/вердиктов

            RunFinalChecks(clients);

            SendReportToServer();
            R.PrintSummary();

            Multiclient.Shutdown();
            Thread.Sleep(400);
            try { World.Dispose(); } catch { }
            return R.Failed == 0 ? 0 : 1;
        }

        // ─────────────────────────────────────────────────────────────────────
        private static void RunFinalChecks(int clients)
        {
            R.Section("MC2 · роллинг и «карта сессий»");
            R.Check("роллинг применялся: rolls=" + Multiclient.RollsApplied +
                    " (" + Multiclient.RollBytes / 1024 + " KiB)", Multiclient.RollsApplied > 0);
            R.Check("информация о сессиях постоянно обновлялась: изменений=" +
                    Multiclient.SessionInfoChangesSeen, Multiclient.SessionInfoChangesSeen > 0);

            R.Section("MC3 · игровая нагрузка (события клиент → сервер)");
            R.Check("выстрелы отправлялись: " + Multiclient.ShotsSent, Multiclient.ShotsSent > 0);
            R.Check("мины ставились: " + Multiclient.MinesSent, Multiclient.MinesSent > 0);
            R.Check("апгрейды покупались (цикл выйти-прокачаться-вернуться): " + Multiclient.UpgradesDone,
                Multiclient.UpgradesDone > 0);
            R.CheckEq("жёстких отказов сервера нет (мягких по откату: " + Multiclient.SoftRejects + ")",
                0L, Interlocked.Read(ref Multiclient.HardRejects));

            R.Section("MC4 · GDAP по проводу");
            if (LK.VerifyMode)
            {
                R.CheckEq("нарушений GDAP в blob'ах НЕТ (перезарядка — только владельцу, " +
                          "свой Hp — никогда владельцу)", 0L,
                    Interlocked.Read(ref Multiclient.WireGdapViolations));
                R.Check("приватные срезы (перезарядка/золото) доезжали владельцам: " +
                        Multiclient.WirePrivateOwnBlobs, Multiclient.WirePrivateOwnBlobs > 0);
                R.Check("Hp чужих игроков доезжал наблюдателям: " + Multiclient.WireHpForeignBlobs,
                    Multiclient.WireHpForeignBlobs > 0);
                R.Check("перезарядка владельца материализовалась в реплике (GunReload > 0)",
                    Multiclient.Clients.Any(v => v.ReloadWireSeen));
            }
            else R.Check("verify-режим отключён (чистая нагрузка)", true);

            R.Section("MC5 · DBComponent: мины");
            R.Check("строки мин наблюдались в DB сессий: " + Multiclient.MinesSeen, Multiclient.MinesSeen > 0);
            R.Check("мины исчезали из DB (взрыв / смерть владельца / рестарт): " + Multiclient.MinesGone,
                Multiclient.MinesGone > 0);

            R.Section("MC6 · согласованность состояний");
            if (LK.VerifyMode)
            {
                R.Check("StateCheck-циклы шли: " + Multiclient.StateChecksSent, Multiclient.StateChecksSent > 0);
                R.CheckEq("StateCheck-провалов нет", 0L, Interlocked.Read(ref Multiclient.StateCheckFails));
                R.CheckEq("экономика клиента сходится с сервером (прогноз цены/золота)",
                    0L, Interlocked.Read(ref Multiclient.UpgradeEconMismatches));
            }
            else R.Check("verify-режим отключён", true);

            R.Section("MC7 · реестр живости и удаление из видимости");
            R.Check("события о выходе участников приходили: " + Multiclient.MemberLeftEvents,
                Multiclient.MemberLeftEvents > 0);
            R.Check("реестр сверялся с сервером: запросов=" + Multiclient.LivenessQueriesSent +
                    " (purged мин=" + Multiclient.PurgedMines + ", сущностей=" + Multiclient.PurgedEntities + ")",
                Multiclient.LivenessQueriesSent > 0,
                "подозрительных объектов не возникло — удлините прогон");
            R.CheckEq("древних неразрешённых подозреваемых на конец прогона нет",
                0, Multiclient.UnresolvedAncientSuspects());

            R.Section("MC8 · итог по клиентам");
            foreach (var v in Multiclient.Clients)
                Console.WriteLine(string.Format(
                    "   {0,-12} joins={1,-3} shots={2,-6} mines={3,-5} upgrades={4,-3} state={5}",
                    v.Name, v.MyJoins, v.MyShots, v.MyMines, v.MyUpgrades, v.State));
            R.Check("все " + clients + " клиентов активно играли (shots > 0)",
                Multiclient.Clients.All(v => v.MyShots > 0));
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

            _server = NetworkDestination.ForHost(LK.Host, LK.Port, NetworkProtocol.TCP);
            GlobalProgramState.instance.ClientNetworkGameDestination = _server;

            NetworkService.instance.EndpointConfigs.Add(new NetworkDestination
            {
                Host = LK.Host,
                Port = LK.Port,
                Protocol = NetworkProtocol.TCP,
                IsListener = false,
                BufferSize = 65536,
            });

            // Предзасев конфига (см. базовый тест-кит: DBService читает baseconfig безусловно).
            ConstantService.instance.SetupConfigs(GlobalProgramState.instance.GameConfigDir);

            IService.InitializeAllServices();

            // ConstantService клиента заморожен до ConfigCheckResultEvent от сервера ⇒
            // ожидание заодно валидирует живой сетевой обмен default-инстанса (vc0).
            bool ok = allDone.Wait(TimeSpan.FromSeconds(60));
            if (!ok)
            {
                NLogger.Error("[MC] сервисы не инициализировались за 60 c — принудительная разморозка");
                try { ConstantService.instance.UnfreezeConstantService(); } catch { }
                allDone.Wait(TimeSpan.FromSeconds(10));
            }
            return ok;
        }

        private static void SendReportToServer()
        {
            var vc0 = Multiclient.Clients.FirstOrDefault();
            if (vc0 == null) return;

            foreach (var res in R.Results.Where(x => !x.Ok))
            {
                vc0.Send(new LoadReportEvent
                {
                    Line = res.Section + " / " + res.Name + (string.IsNullOrEmpty(res.Detail) ? "" : " — " + res.Detail),
                    Ok = false,
                });
                Thread.Sleep(15);
            }

            vc0.Send(new LoadReportEvent
            {
                Line = R.ToCompactString(),
                Ok = R.Failed == 0,
                Final = true,
                Passed = R.Passed,
                Failed = R.Failed,
            });
            Thread.Sleep(800); // дать outbound-буферу уйти в сокет
        }
    }
}
