using System;
using System.IO;
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
using AECC.TestServer;

namespace AECC.LoadServer
{
    /// <summary>
    /// AECC_LoadServer — сервер нагрузочного клиент-серверного сессионного теста.
    ///
    /// Запуск:  dotnet run -c Release --project tests/AECC_LoadServer [timeoutSec=300]
    /// Затем:   dotnet run -c Release --project tests/AECC_LoadClient [clients] [durationSec] [prefix]
    ///
    /// Все параметры нагрузки — в AECC_LoadShared/LoadKit.cs (LK.*), переопределяются
    /// переменными окружения AECC_LOAD_*. AECC_LOAD_VERIFYMODE=false отключает двойные
    /// проверки для замера чистой нагрузочной способности.
    ///
    /// Exit code 0 — все серверные проверки и присланный отчёт мультиклиента без провалов.
    /// </summary>
    public static class Program
    {
        private static readonly TestReport Report = new TestReport("AECC · LOAD SERVER (сессионный нагрузочный тест)");

        public static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            LK.ApplyEnvOverrides();

            // Дефолт масштабируется от ёмкости мультиклиента: фоновый спавн клиентов
            // занимает MulticlientCapacity × ClientSpawnDelayMs, плюс ожидания входа
            // (до 180 c) и сам сценарий (~300 c).
            int defaultTimeoutSec = 300 + 180 + LK.MulticlientCapacity * LK.ClientSpawnDelayMs / 1000;
            int clientTimeoutSec = args.Length > 0 && int.TryParse(args[0], out var t) ? t : defaultTimeoutSec;

            // Режим конкуренции фиксируется в момент конструирования локов — ДО миров.
            Bootstrapping.ConfigureKernel(multiThread: true);
            SerializationBootstrap.GetSerializationAdapter = () => new SerializationAdapter();

            PrepareFileSystem();

            // Мир — ДО сервисов (AuthorizationRealization обязан вернуть сущность
            // с проставленным ECSWorldOwner).
            var world = Bootstrapping.CreateWorld(LK.WorldId, ECSWorld.WorldTypeEnum.Server, new SerializationAdapter());

            var db = new SqliteDbProvider();
            bool servicesOk = StartServices(db);

            Report.Section("LS0 · сервисы");
            Report.Check("все IService инициализированы", servicesOk);
            Report.Check("NetworkService слушает " + LK.Host + ":" + LK.Port,
                NetworkService.instance.Servers != null && NetworkService.instance.Servers.Count >= 1);
            Report.Check("DBService получил провайдера", DBService.instance.DBProvider != null);

            Bootstrapping.RestoreWorldResolver();
            Report.Check("ECSWorld.GetWorld резолвит серверный мир",
                ECSWorld.GetWorld(LK.WorldId) != null && ECSWorld.GetWorld(LK.WorldId).instanceId == LK.WorldId);

            LoadGameServer.Start(world, Report);

            Console.WriteLine();
            NLogger.LogSuccess("[LOAD-SERVER] ГОТОВ. Запускайте AECC_LoadClient (ждём до " + clientTimeoutSec + " c)");
            Console.WriteLine();

            bool finished = LoadGameServer.ClientFinished.Wait(TimeSpan.FromSeconds(clientTimeoutSec));
            if (!finished)
                Report.Check("мультиклиент завершил сценарий в отведённое время", false,
                    "таймаут " + clientTimeoutSec + " c");

            Thread.Sleep(400);
            try { LoadGameServer.Verify(); }
            catch (Exception ex) { Report.Check("серверная верификация без исключений", false, ex.ToString()); }

            LoadGameServer.Stop();
            Report.PrintSummary();

            try { world.Dispose(); } catch { }
            return Report.Failed == 0 ? 0 : 1;
        }

        private static void PrepareFileSystem()
        {
            var root = AppContext.BaseDirectory;
            var gameData = Path.Combine(root, "GameData");
            var gameConfig = Path.Combine(gameData, "GameConfig");
            var dbDir = Path.Combine(gameData, "Config");
            Directory.CreateDirectory(gameConfig);
            Directory.CreateDirectory(dbDir);
            TryDelete(Path.Combine(dbDir, "Users.db"));
            TryDelete(Path.Combine(dbDir, "Users.db-shm"));
            TryDelete(Path.Combine(dbDir, "Users.db-wal"));
            TryDelete(Path.Combine(gameData, "zippedconfig.zip"));
        }

        private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

        private static bool StartServices(SqliteDbProvider db)
        {
            var allDone = new ManualResetEventSlim(false);
            IService.SyncManager.OnAllServicesCompleted += () => allDone.Set();

            IService.RegisterAllServices();

            GlobalProgramState.instance.ProgramType = GlobalProgramState.ProgramTypeEnum.Server;

            NetworkService.instance.EndpointConfigs.Add(new NetworkDestination
            {
                Host = LK.Host,
                Port = LK.Port,
                Protocol = NetworkProtocol.TCP,
                IsListener = true,
                BufferSize = 65536,
            });

            // SQLiteDefaultDBProvider вырезан препроцессором в netstandard2.0-сборке фреймворка
            // (FRAMEWORK_MAP §10.3) ⇒ подставляем свой провайдер (линкуется из AECC_TestServer).
            DBService.instance.DBProvider = db;

            // Предзасев конфига: DBService.InitializeProcess читает baseconfig безусловно,
            // сервисы стартуют параллельно ⇒ без предзасева возможна гонка (NRE).
            ConstantService.instance.SetupConfigs(GlobalProgramState.instance.GameConfigDir);
            PatchBaseConfigPort();

            IService.InitializeAllServices();

            bool ok = allDone.Wait(TimeSpan.FromSeconds(60));
            if (!ok) NLogger.Error("[LOAD-SERVER] сервисы не завершили инициализацию за 60 c");
            return ok;
        }

        private static void PatchBaseConfigPort()
        {
            try
            {
                var file = Path.Combine(GlobalProgramState.instance.GameConfigDir, "baseconfig.json");
                if (!File.Exists(file)) return;
                var obj = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(file));
                obj["Networking"]["Port"] = LK.Port.ToString();
                obj["Networking"]["HostAddress"] = LK.Host;
                File.WriteAllText(file, obj.ToString());
                ConstantService.instance.SetupConfigs(GlobalProgramState.instance.GameConfigDir);
            }
            catch (Exception ex)
            {
                NLogger.Error("[LOAD-SERVER] не удалось поправить baseconfig: " + ex.Message);
            }
        }
    }
}
