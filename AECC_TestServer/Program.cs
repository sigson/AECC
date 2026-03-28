using AECC.Core;
using AECC.Core.Logging;
using AECC.Core.Serialization;
using AECC.ECS.DefaultObjects.ECSComponents;
using AECC.Extensions;
using AECC.Harness.Model;
using AECC.Harness.Services;
using AECC.Network;
using TestShared.Components;
using TestShared.Systems;

namespace TestServer
{
    /// <summary>
    /// Сервер AECC-фреймворка. Демонстрирует полный цикл:
    ///   1. IService.RegisterAllServices() — автоматическая регистрация всех сервисов
    ///   2. Конфигурация (ProgramType, EndpointConfigs, пути)
    ///   3. IService.InitializeAllServices() — запуск пайплайна инициализации
    ///   4. OnAllServicesCompleted — создание мира, спавн сущностей, GDAP
    ///   5. WorldSyncSystem (ECS-система) автоматически работает каждые 100мс
    /// </summary>
    class Program
    {
        private const int SERVER_PORT = 6667;
        private const int ENTITY_COUNT = 5;
        private static ECSWorld _serverWorld;
        private static readonly Random _rng = new Random(123);

        static void Main(string[] args)
        {
            Console.WriteLine("╔══════════════════════════════════════════════╗");
            Console.WriteLine("║     AECC Framework — TEST SERVER            ║");
            Console.WriteLine("╚══════════════════════════════════════════════╝");

            //Defines.AOTMode = true;
            Defines.IgnoreNonDangerousExceptions = true;
            Defines.ServiceSetupLogging = true;
            Defines.ECSNetworkTypeLogging = true;

            // =====================================================
            //  ШАГ 1: Регистрация всех сервисов (автоматически через рефлексию)
            // =====================================================
            Console.WriteLine("\n[1] RegisterAllServices()...");
            IService.RegisterAllServices();

            // =====================================================
            //  ШАГ 2: Конфигурация сервисов ДО InitializeAllServices()
            //  (сервисы уже зарегистрированы как синглтоны, можно обращаться через .instance)
            // =====================================================
            Console.WriteLine("[2] Configuring services...");

            // --- GlobalProgramState ---
            GlobalProgramState.instance.ProgramType = GlobalProgramState.ProgramTypeEnum.Server;
            GlobalProgramState.instance.persistentDataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ServerData");
            GlobalProgramState.instance.streamingAssetsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ServerData");

            // Создаём папки для конфигов (ConstantService может к ним обращаться)
            var dataDir = GlobalProgramState.instance.persistentDataPath;
            if (!Directory.Exists(dataDir))
                Directory.CreateDirectory(dataDir);
            var gameDataDir = Path.Combine(dataDir, "GameData");
            if (!Directory.Exists(gameDataDir))
                Directory.CreateDirectory(gameDataDir);
            var configDir = Path.Combine(gameDataDir, "Config");
            if (!Directory.Exists(configDir))
                Directory.CreateDirectory(configDir);

            // --- NetworkService: серверный WebSocket эндпоинт ---
            NetworkService.instance.EndpointConfigs.Add(new NetworkDestination
            {
                Host = "0.0.0.0",
                Port = SERVER_PORT,
                Protocol = NetworkProtocol.TCP,
                IsListener = true,
                BufferSize = 65536
            });

            Console.WriteLine($"   ProgramType: Server");
            Console.WriteLine($"   WebSocket listener: 0.0.0.0:{SERVER_PORT}");

            // =====================================================
            //  ШАГ 3: Подписка на завершение инициализации всех сервисов
            // =====================================================
            IService.SyncManager.OnAllServicesCompleted += () =>
            {
                Console.WriteLine("\n[✓] All services initialized successfully!");
                OnAllServicesReady();
            };

            // =====================================================
            //  ШАГ 4: Запуск пайплайна инициализации
            // =====================================================
            Console.WriteLine("\n[3] InitializeAllServices()...");
            IService.InitializeAllServices();

            // =====================================================
            //  ШАГ 5: Основной цикл
            // =====================================================
            Console.WriteLine("[4] Server main loop. Press 'q' to quit, 's' for status.\n");

            var running = true;
            var statusTimer = new Timer(_ =>
            {
                if (!running || _serverWorld == null) return;
                PrintWorldStatus();
            }, null, 5000, 5000);

            while (running)
            {
                // if (Console.KeyAvailable)
                // {
                //     var key = Console.ReadKey(true).KeyChar;
                //     if (key == 'q' || key == 'Q')
                //         running = false;
                //     else if (key == 's' || key == 'S')
                //         PrintWorldStatus();
                // }
                Thread.Sleep(100);
            }

            statusTimer.Dispose();
            Console.WriteLine("\nServer shutting down...");
        }

        /// <summary>
        /// Вызывается из OnAllServicesCompleted.
        /// ECSService, NetworkService — уже проинициализированы и работают.
        /// </summary>
        static void OnAllServicesReady()
        {
            // =====================================================
            //  Создание серверного ECS-мира
            // =====================================================
            Console.WriteLine("\n  [World] Creating server ECS world...");

            _serverWorld = ECSService.instance.GetWorld();
            _serverWorld.WorldType = ECSWorld.WorldTypeEnum.Server;
            _serverWorld.WorldMetaData = "TestServerWorld";

            // Подключаем сериализатор (JSON через Newtonsoft.Json при AOTMode)
            _serverWorld.EntityWorldSerializer = new EntityNetSerializer();
            _serverWorld.EntityWorldSerializer.InitSerialize(
                _serverWorld,
                new AECC.Harness.Serialization.SerializationAdapter());

            Console.WriteLine($"  [World] ID: {_serverWorld.instanceId}");
            Console.WriteLine($"  [World] Type: {_serverWorld.WorldType}");

            // =====================================================
            //  Спавн игровых сущностей со случайными компонентами
            // =====================================================
            Console.WriteLine($"\n  [Entities] Spawning {ENTITY_COUNT} game entities...");

            for (int i = 0; i < ENTITY_COUNT; i++)
            {
                var entity = CreateGameEntity(i);
                var compNames = string.Join(", ", entity.entityComponents.Components.Select(c => c.GetType().Name));
                Console.WriteLine($"    Entity #{i}: {entity.AliasName} (ID={entity.instanceId})");
                Console.WriteLine($"      Components: [{compNames}]");
                Console.WriteLine($"      GDAP policies: {entity.dataAccessPolicies.Count}");
            }

            // =====================================================
            //  Обработка подключений клиентов
            // =====================================================
            NetworkService.instance.OnSocketReady += (socket) =>
            {
                Console.WriteLine($"\n  >>> CLIENT CONNECTED: Socket={socket.Id}, Addr={socket.Address}");
                var clientEntity = CreateClientEntity(socket);
                Console.WriteLine($"  >>> Client entity created: {clientEntity.AliasName} (ID={clientEntity.instanceId})");
                Console.WriteLine($"  >>> WorldSyncSystem will now send GDAP-filtered data to this client");
            };

            NetworkService.instance.OnSocketDisconnected += (socket) =>
            {
                Console.WriteLine($"\n  <<< CLIENT DISCONNECTED: Socket={socket.Id}");
            };

            Console.WriteLine("\n  [Ready] Server is fully operational. Waiting for client connections...");
        }

        // =====================================================
        //  Создание игровой сущности
        // =====================================================
        static ECSEntity CreateGameEntity(int index)
        {
            var entity = new ECSEntity();
            entity.AliasName = $"GameEntity_{index}";
            entity.Alive = true;

            _serverWorld.entityManager.AddNewEntity(entity, silent: true);

            // Обязательные компоненты
            entity.AddComponentSilent(new HealthComponent
            {
                CurrentHealth = 80f + (float)(_rng.NextDouble() * 40f),
                MaxHealth = 100f + (float)(_rng.NextDouble() * 50f)
            });

            entity.AddComponentSilent(new PositionComponent
            {
                X = (float)(_rng.NextDouble() * 100.0 - 50.0),
                Y = (float)(_rng.NextDouble() * 10.0),
                Z = (float)(_rng.NextDouble() * 100.0 - 50.0)
            });

            // Случайные компоненты
            if (_rng.NextDouble() > 0.3)
            {
                entity.AddComponentSilent(new VelocityComponent
                {
                    VX = (float)(_rng.NextDouble() * 10.0 - 5.0),
                    VY = 0f,
                    VZ = (float)(_rng.NextDouble() * 10.0 - 5.0)
                });
            }

            if (_rng.NextDouble() > 0.5)
            {
                entity.AddComponentSilent(new ScoreComponent
                {
                    Points = _rng.Next(0, 1000),
                    KillCount = _rng.Next(0, 20)
                });
            }

            // Серверный секретный компонент — НЕ виден клиенту через GDAP
            entity.AddComponentSilent(new ServerSecretComponent
            {
                InternalState = $"secret_data_{index}",
                LastTickProcessed = DateTime.UtcNow.Ticks
            });

            // GDAP: определяет видимость компонентов для клиентов
            entity.dataAccessPolicies.Add(TestGDAP.CreateForPlayer());

            entity.entityComponents.RegisterAllComponents();
            _serverWorld.entityManager.AddNewEntityReaction(entity);

            return entity;
        }

        // =====================================================
        //  Создание клиентской сущности с SocketComponent
        // =====================================================
        static ECSEntity CreateClientEntity(ISocketAdapter socket)
        {
            var clientEntity = new ECSEntity();
            clientEntity.AliasName = $"Client_{socket.Id}";
            clientEntity.Alive = true;

            _serverWorld.entityManager.AddNewEntity(clientEntity, silent: true);

            var socketComp = new SocketComponent();
            socketComp.Socket = socket;
            clientEntity.AddComponentSilent(socketComp);

            clientEntity.dataAccessPolicies.Add(TestGDAP.CreateForPlayer());

            clientEntity.entityComponents.RegisterAllComponents();
            _serverWorld.entityManager.AddNewEntityReaction(clientEntity);

            return clientEntity;
        }

        // =====================================================
        //  Статус мира
        // =====================================================
        static void PrintWorldStatus()
        {
            if (_serverWorld == null) return;

            Console.WriteLine("─────────────────────────────────────────────");
            Console.WriteLine($"[STATUS] {_serverWorld.WorldMetaData} | Entities: {_serverWorld.entityManager.EntityStorage.Count}");

            foreach (var kvp in _serverWorld.entityManager.EntityStorage)
            {
                var e = kvp.Value;
                var compList = string.Join(", ", e.entityComponents.Components.Select(c => c.GetType().Name));
                Console.Write($"  {e.AliasName}: [{compList}]");

                if (e.HasComponent<HealthComponent>())
                    Console.Write($" HP={e.GetComponent<HealthComponent>().CurrentHealth:F1}");
                if (e.HasComponent<PositionComponent>())
                {
                    var p = e.GetComponent<PositionComponent>();
                    Console.Write($" Pos=({p.X:F1},{p.Y:F1},{p.Z:F1})");
                }
                if (e.HasComponent<ScoreComponent>())
                    Console.Write($" Pts={e.GetComponent<ScoreComponent>().Points}");

                Console.WriteLine();
            }
            Console.WriteLine("─────────────────────────────────────────────");
        }
    }
}