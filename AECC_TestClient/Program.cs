using AECC.Core;
using AECC.Core.Logging;
using AECC.Core.Serialization;
using AECC.Extensions;
using AECC.Harness.Model;
using AECC.Harness.Services;
using AECC.Network;
using TestShared.Components;

namespace TestClient
{
    /// <summary>
    /// Клиент AECC-фреймворка. Демонстрирует:
    ///   1. IService.RegisterAllServices() + InitializeAllServices()
    ///   2. Подключение к серверу через WebSocket
    ///   3. Приём UpdateEntitiesEvent → EntityWorldSerializer.UpdateDeserialize()
    ///   4. Развёртывание серверных сущностей в клиентском мире
    ///   5. Проверка что ServerSecretComponent НЕ виден (GDAP фильтрация)
    /// </summary>
    class Program
    {
        private const string SERVER_HOST = "127.0.0.1";
        private const int SERVER_PORT = 6667;

        static void Main(string[] args)
        {
            Console.WriteLine("╔══════════════════════════════════════════════╗");
            Console.WriteLine("║     AECC Framework — TEST CLIENT            ║");
            Console.WriteLine("╚══════════════════════════════════════════════╝");

            //Defines.AOTMode = true;
            Defines.IgnoreNonDangerousExceptions = true;
            Defines.ServiceSetupLogging = true;
            Defines.ECSNetworkTypeLogging = true;

            // =====================================================
            //  ШАГ 1: Регистрация всех сервисов
            // =====================================================
            Console.WriteLine("\n[1] RegisterAllServices()...");
            IService.RegisterAllServices(new List<Type>{ typeof(ConstantService) });

            // =====================================================
            //  ШАГ 2: Конфигурация сервисов ДО инициализации
            // =====================================================
            Console.WriteLine("[2] Configuring services...");

            // --- GlobalProgramState ---
            GlobalProgramState.instance.ProgramType = GlobalProgramState.ProgramTypeEnum.Client;
            GlobalProgramState.instance.persistentDataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ClientData");
            GlobalProgramState.instance.streamingAssetsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ClientData");

            // Создаём папки конфигов
            var dataDir = GlobalProgramState.instance.persistentDataPath;
            if (!Directory.Exists(dataDir))
                Directory.CreateDirectory(dataDir);
            var gameDataDir = Path.Combine(dataDir, "GameData");
            if (!Directory.Exists(gameDataDir))
                Directory.CreateDirectory(gameDataDir);
            var configDir = Path.Combine(gameDataDir, "Config");
            if (!Directory.Exists(configDir))
                Directory.CreateDirectory(configDir);

            // --- NetworkService: клиентское подключение к серверу ---
            var serverDest = new NetworkDestination
            {
                Host = SERVER_HOST,
                Port = SERVER_PORT,
                Protocol = NetworkProtocol.TCP,
                IsListener = false,
                BufferSize = 65536
            };
            NetworkService.instance.EndpointConfigs.Add(serverDest);

            // Сохраняем destination для клиентской логики
            GlobalProgramState.instance.ClientNetworkGameDestination = serverDest;

            Console.WriteLine($"   ProgramType: Client");
            Console.WriteLine($"   Connecting to: {SERVER_HOST}:{SERVER_PORT} (WebSocket)");

            // =====================================================
            //  ШАГ 3: Callback после инициализации всех сервисов
            // =====================================================
            IService.SyncManager.OnAllServicesCompleted += () =>
            {
                Console.WriteLine("\n[✓] All services initialized!");
                OnAllServicesReady();
            };

            // =====================================================
            //  ШАГ 4: Запуск инициализации
            // =====================================================
            Console.WriteLine("\n[3] InitializeAllServices()...");
            IService.InitializeAllServices();

            // =====================================================
            //  ШАГ 5: Основной цикл — отображение состояния
            // =====================================================
            Console.WriteLine("[4] Client main loop. Press 'q' to quit, 's' for status.\n");

            var running = true;
            var displayTimer = new Timer(_ =>
            {
                if (!running) return;
                DisplayClientWorld();
            }, null, 5000, 3000);

            while (running)
            {
                // if (Console.KeyAvailable)
                // {
                //     var key = Console.ReadKey(true).KeyChar;
                //     if (key == 'q' || key == 'Q')
                //         running = false;
                //     else if (key == 's' || key == 'S')
                //         DisplayClientWorld();
                // }
                Thread.Sleep(100);
            }

            displayTimer.Dispose();
            Console.WriteLine("\nClient shutting down...");
        }

        /// <summary>
        /// Вызывается когда все сервисы готовы.
        /// NetworkService уже подключается к серверу.
        /// UpdateEntitiesEvent.Execute() автоматически вызывает UpdateDeserialize().
        /// </summary>
        static void OnAllServicesReady()
        {
            Console.WriteLine("\n  [Client] Services ready, connection to server in progress...");

            NetworkService.instance.OnSocketReady += (socket) =>
            {
                Console.WriteLine($"\n  >>> CONNECTED TO SERVER: Socket={socket.Id}");
                Console.WriteLine("  >>> Now receiving UpdateEntitiesEvent with GDAP-filtered world data");
                Console.WriteLine("  >>> ServerSecretComponent should NOT appear on client side\n");
            };

            NetworkService.instance.OnSocketDisconnected += (socket) =>
            {
                Console.WriteLine($"\n  <<< DISCONNECTED FROM SERVER: Socket={socket.Id}");
            };
        }

        // =====================================================
        //  Отображение клиентского мира
        //  (сущности появляются после UpdateDeserialize)
        // =====================================================
        static void DisplayClientWorld()
        {
            var allWorlds = ECSWorld.GetAllWorlds();

            Console.WriteLine("═══════════════════════════════════════════════");
            Console.WriteLine($"[CLIENT STATE] Known worlds: {allWorlds.Count}");

            int totalEntities = 0;
            bool gdapLeakDetected = false;

            foreach (var worldKvp in allWorlds)
            {
                var world = worldKvp.Value;
                world.WorldType = ECSWorld.WorldTypeEnum.Client;
            world.WorldMetaData = "TestServerWorld";

            // Подключаем сериализатор (JSON через Newtonsoft.Json при AOTMode)
            world.EntityWorldSerializer = new EntityNetSerializer();
            world.EntityWorldSerializer.InitSerialize(
                world,
                new AECC.Harness.Serialization.SerializationAdapter());
                var entityCount = world.entityManager.EntityStorage.Count;
                totalEntities += entityCount;

                Console.WriteLine($"\n  World: {world.WorldMetaData} (ID={world.instanceId}, Type={world.WorldType})");
                Console.WriteLine($"  Entities: {entityCount}");

                foreach (var ekvp in world.entityManager.EntityStorage)
                {
                    var e = ekvp.Value;
                    var compNames = e.entityComponents.Components
                        .Select(c => c.GetType().Name).ToList();

                    Console.Write($"    [{e.AliasName}] ID={e.instanceId}");
                    Console.Write($" Comps=[{string.Join(", ", compNames)}]");

                    // GDAP-проверка: ServerSecretComponent НЕ должен присутствовать
                    if (e.HasComponent<ServerSecretComponent>())
                    {
                        Console.Write(" ⚠ GDAP LEAK!");
                        gdapLeakDetected = true;
                    }

                    Console.WriteLine();

                    // Показываем доступные данные
                    if (e.HasComponent<HealthComponent>())
                    {
                        var h = e.GetComponent<HealthComponent>();
                        Console.WriteLine($"      HP: {h.CurrentHealth:F1}/{h.MaxHealth:F1} {(h.IsDead ? "[DEAD]" : "[ALIVE]")}");
                    }
                    if (e.HasComponent<PositionComponent>())
                    {
                        var p = e.GetComponent<PositionComponent>();
                        Console.WriteLine($"      Pos: ({p.X:F2}, {p.Y:F2}, {p.Z:F2})");
                    }
                    if (e.HasComponent<VelocityComponent>())
                    {
                        var v = e.GetComponent<VelocityComponent>();
                        Console.WriteLine($"      Vel: ({v.VX:F2}, {v.VY:F2}, {v.VZ:F2})");
                    }
                    if (e.HasComponent<ScoreComponent>())
                    {
                        var s = e.GetComponent<ScoreComponent>();
                        Console.WriteLine($"      Score: Pts={s.Points} Kills={s.KillCount}");
                    }
                }
            }

            if (totalEntities == 0)
            {
                Console.WriteLine("  (no entities received yet — waiting for server sync)");
            }

            if (!gdapLeakDetected && totalEntities > 0)
            {
                Console.WriteLine("\n  [GDAP OK] ServerSecretComponent is properly filtered — not visible on client");
            }

            Console.WriteLine("═══════════════════════════════════════════════\n");
        }
    }
}