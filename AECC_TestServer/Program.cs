using System;
using System.IO;
using System.Linq;
using System.Threading;
using AECC.Core;
using AECC.Core.Logging;
using AECC.Harness.Model;
using AECC.Harness.Serialization;
using AECC.Harness.Services;
using AECC.Network;
using AECC.Serialization;
using AECC.TestKit;

namespace AECC.TestServer
{
    /// <summary>
    /// AECC_TestServer — сервер интеграционного теста.
    ///
    ///   ФАЗА A: локальная батарея ядра ECS (EcsCoreSuite) — без сети.
    ///   ФАЗА B: полный харнесс (сервисы → сеть → БД → авторизация → авторитарный роллинг).
    ///
    /// Запуск:  dotnet run --project AECC_TestServer      (первым!)
    /// Затем:   dotnet run --project AECC_TestClient
    ///
    /// Exit code 0 — все проверки (серверные + присланные клиентом) прошли.
    /// </summary>
    public static class Program
    {
        private static readonly TestReport Report = new TestReport("AECC · SERVER (ФАЗА A: ядро ECS, ФАЗА B: харнесс)");

        public static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            int clientTimeoutSec = args.Length > 0 && int.TryParse(args[0], out var t) ? t : 180;

            // ── 0. Ядро ─────────────────────────────────────────────────────
            // ВАЖНО: режим конкуренции фиксируется в момент конструирования локов,
            // поэтому флаги выставляются ДО создания любых миров/сущностей.
            Bootstrapping.ConfigureKernel(multiThread: true);
            SerializationBootstrap.GetSerializationAdapter = () => new SerializationAdapter();

            PrepareFileSystem();

            // ── ФАЗА A ──────────────────────────────────────────────────────
            Console.WriteLine();
            Console.WriteLine("############ ФАЗА A — локальная батарея ядра ECS ############");
            EcsCoreSuite.Run(Report);

            // ── ФАЗА B ──────────────────────────────────────────────────────
            Console.WriteLine();
            Console.WriteLine("############ ФАЗА B — харнесс: сервисы / сеть / БД / авторизация ############");

            // Мир сервера создаём ДО сервисов: AuthService.AuthorizationRealization обязан
            // вернуть сущность с уже проставленным ECSWorldOwner.
            // Побочный эффект: конструктор ECSComponentManager перезапишет статик
            // GlobalProgramComponentGroup на ServerComponentGroup (мы группы всё равно
            // навешиваем явно — Groups.Server/Groups.Client).
            var world = Bootstrapping.CreateWorld(TK.WorldId, ECSWorld.WorldTypeEnum.Server, new SerializationAdapter());

            var db = new SqliteDbProvider();
            bool servicesOk = StartServices(db);

            Report.Section("S0 · сервисы");
            Report.Check("все IService инициализированы", servicesOk);
            Report.Check("NetworkService поднял слушателя",
                NetworkService.instance.Servers != null && NetworkService.instance.Servers.Count >= 1);
            Report.Check("ConstantService загрузил baseconfig",
                ConstantService.instance.GetByConfigPath("baseconfig") != null);
            Report.Check("DBService получил провайдера", DBService.instance.DBProvider != null);
            Report.Check("DBPath прочитан из конфига", !string.IsNullOrEmpty(DBService.instance.DBPath),
                "DBPath=" + DBService.instance.DBPath);

            // ECSService подменил ECSWorld.GetWorld на create-on-miss — возвращаем канон.
            Bootstrapping.RestoreWorldResolver();
            Report.Check("ECSWorld.GetWorld резолвит наш мир по id",
                ECSWorld.GetWorld(TK.WorldId) != null && ECSWorld.GetWorld(TK.WorldId).instanceId == TK.WorldId);

            GameServer.Start(world, Report);

            Console.WriteLine();
            NLogger.LogSuccess("[SERVER] ГОТОВ. Запускайте AECC_TestClient (ждём до " + clientTimeoutSec + " c)");
            Console.WriteLine();

            bool finished = GameServer.ClientFinished.Wait(TimeSpan.FromSeconds(clientTimeoutSec));
            if (!finished)
                Report.Check("клиент завершил сценарий в отведённое время", false,
                    "таймаут " + clientTimeoutSec + " c — клиент не прислал финальный отчёт");

            Thread.Sleep(300);
            try { GameServer.Verify(db); }
            catch (Exception ex) { Report.Check("серверная верификация без исключений", false, ex.ToString()); }

            GameServer.Stop();
            Report.PrintSummary();

            try { world.Dispose(); } catch { }
            return Report.Failed == 0 ? 0 : 1;
        }

        // ─────────────────────────────────────────────────────────────────────
        private static void PrepareFileSystem()
        {
            // GlobalProgramState.GameDataDir == <каталог exe>/GameData
            var root = AppContext.BaseDirectory;
            var gameData = Path.Combine(root, "GameData");
            var gameConfig = Path.Combine(gameData, "GameConfig");
            var dbDir = Path.Combine(gameData, "Config");

            Directory.CreateDirectory(gameConfig);
            Directory.CreateDirectory(dbDir);

            // чистый старт: сносим прошлую БД и прошлый зип конфигов
            TryDelete(Path.Combine(dbDir, "Users.db"));
            TryDelete(Path.Combine(dbDir, "Users.db-shm"));
            TryDelete(Path.Combine(dbDir, "Users.db-wal"));
            TryDelete(Path.Combine(gameData, "zippedconfig.zip"));
        }

        private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

        // ─────────────────────────────────────────────────────────────────────
        private static bool StartServices(SqliteDbProvider db)
        {
            var allDone = new ManualResetEventSlim(false);
            IService.SyncManager.OnAllServicesCompleted += () => allDone.Set();

            // 1) поднять синглтоны всех IService (рефлексия по загруженным сборкам)
            IService.RegisterAllServices();

            // 2) сконфигурировать ДО запуска шагов
            GlobalProgramState.instance.ProgramType = GlobalProgramState.ProgramTypeEnum.Server;

            NetworkService.instance.EndpointConfigs.Add(new NetworkDestination
            {
                Host = TK.Host,
                Port = TK.Port,
                Protocol = NetworkProtocol.TCP,
                IsListener = true,
                BufferSize = 65536,
            });

            // SQLiteDefaultDBProvider вырезан препроцессором в netstandard2.0-сборке фреймворка
            // (см. FRAMEWORK_MAP §10.3) ⇒ подставляем свой провайдер.
            DBService.instance.DBProvider = db;

            // 3) ПРЕД-ЗАСЕВ КОНФИГА.
            // DBService.InitializeProcess читает ConstantService.GetByConfigPath("baseconfig")
            // безусловно, а сервисы стартуют параллельно ⇒ без предзасева возможен NRE
            // (гонка порядка шагов). SetupConfigs(path) создаёт baseconfig.json из
            // GlobalProgramState.BaseConfigDefault и наполняет ConstantDB, не помечая Loaded.
            ConstantService.instance.SetupConfigs(GlobalProgramState.instance.GameConfigDir);
            PatchBaseConfigPort();

            // 4) погнали
            IService.InitializeAllServices();

            bool ok = allDone.Wait(TimeSpan.FromSeconds(60));
            if (!ok) NLogger.Error("[SERVER] сервисы не завершили инициализацию за 60 c");
            return ok;
        }

        /// <summary>
        /// В BaseConfigDefault зашит порт 6666; тест слушает TK.Port. Приводим конфиг в
        /// соответствие, чтобы клиент, читающий baseconfig, видел тот же порт.
        /// </summary>
        private static void PatchBaseConfigPort()
        {
            try
            {
                var file = Path.Combine(GlobalProgramState.instance.GameConfigDir, "baseconfig.json");
                if (!File.Exists(file)) return;
                var json = File.ReadAllText(file);
                var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
                obj["Networking"]["Port"] = TK.Port.ToString();
                obj["Networking"]["HostAddress"] = TK.Host;
                File.WriteAllText(file, obj.ToString());

                // перечитать в ConstantDB (SetupConfigs перезаписывает записи по тому же пути)
                ConstantService.instance.SetupConfigs(GlobalProgramState.instance.GameConfigDir);
            }
            catch (Exception ex)
            {
                NLogger.Error("[SERVER] не удалось поправить baseconfig: " + ex.Message);
            }
        }
    }
}
