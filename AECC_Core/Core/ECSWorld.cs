using System;
using System.Collections.Generic;
using AECC.Core.Serialization;
using AECC.Extensions;

namespace AECC.Core
{
    public partial class ECSWorld : IDisposable
    {
        private static ECSWorld SingletonFallback = null;
        public static Func<ECSWorld> GetSingletonFallback = () =>
        {
            if(SingletonFallback == null)
            {
                SingletonFallback = new ECSWorld();
                SingletonFallback.InitWorldScope((val) => true);
            }
            return SingletonFallback;
        };
        // ФАЗА 4, вынос сборок: фабрика дефолтного адаптера уехала в
        // AECC.Serialization.SerializationBootstrap (мир не знает типов сериализации).

        // ФАЗА 3, шаг 5 (ТЗ 4.5.5): источник истины — инстансный WorldRegistry. Статические
        // Func остаются переходными фасадами: их ДЕФОЛТЫ ходят в реестр, а сама точка
        // переопределения GetWorld сохраняется (интеграции и тесты подменяют её).
        public static Func<long, ECSWorld> GetWorld = (long instance) =>
        {
            ECSWorld w;
            return WorldRegistry.Default.TryGet(instance, out w) ? w : GetSingletonFallback();
        };

        [Obsolete("Фаза 3 (ТЗ 4.5.5): используйте WorldRegistry (Default.All())")]
        public static Func<IDictionary<long, ECSWorld>> GetAllWorlds = () => WorldRegistry.Default.All();

        [Obsolete("Фаза 3 (ТЗ 4.5.5): резолвите мир через WorldRegistry/GetWorld")]
        public static Func<long, ECSWorld> GetEntityWorld = (long entityinstance) => GetSingletonFallback();

        [Obsolete("Фаза 3 (ТЗ 4.5.5): резолвите мир через WorldRegistry/GetWorld")]
        public static Func<long, (ECSWorld world, ECSEntity entity)> GetWorldAndEntity = (long entityinstance) =>
        {
            GetSingletonFallback().entityManager.TryGetEntitySyncronized(entityinstance, out var resentt);
            return (GetSingletonFallback(), resentt);
        };
        public long instanceId = Guid.NewGuid().GuidToLong();
        public string WorldMetaData = "";
        public ECSContractsManager contractsManager;
        public ECSEntityManager entityManager;
        public ECSComponentManager componentManager;

        /// <summary>ФАЗА 5 (ТЗ 4.6, breaking): точка поиска — world.Query.Search(scope, with,
        /// without). Реализация — AECC.Query.EntityQueryIndex; монтаж — QueryBootstrap.Attach
        /// (заполняет этот слот и entityManager.QueryIndex). null = мир без поиска.</summary>
        public IWorldQueryIndex Query;
        public bool Initialized = false;
        public enum WorldTypeEnum
        {
            Server,
            Client,
            Offline
        }
        public WorldTypeEnum WorldType = WorldTypeEnum.Offline;
        // ФАЗА 4, вынос сборок (гейт «Model/Core без ссылки на Serialization»): поля
        // ретипизированы в object — мир ХРАНИТ сериализатор/адаптер, не интерпретирует.
        // Монтаж — AECC.Serialization.SerializationBootstrap.Attach(world, adapter):
        // он создаёт EntityNetSerializer, зовёт InitSerialize и заполняет оба слота.
        // Типизированный доступ — только со стороны Serialization/приложения (breaking по ТЗ).
        public object EntityWorldSerializer;
        public object serializationAdapter = null;

        /// <summary>Планировщик мира (ТЗ 4.3): инжектируемая абстракция над TaskEx/TimerCompat.
        /// Подмена на детерминированный — до Configure/Start.</summary>
        public AECC.Abstractions.IScheduler Scheduler = DefaultScheduler.Instance;

        private WorldProfile _profile;

        /// <summary>Профиль мира (ТЗ 4.5.6): флаги вычислены при создании (первое обращение /
        /// Configure) — последующие смены WorldType на профиль НЕ влияют (санкционированная
        /// семантика «флаги при создании»).</summary>
        public WorldProfile Profile
        {
            get
            {
                if (_profile == null) _profile = new WorldProfile(WorldType);
                return _profile;
            }
        }

        private WorldRegistry _registry;
        private IDisposable timeDependContractsTimer;
        private bool _disposed;

        // ─────── Явный lifecycle мира (ТЗ 4.5.7): Create → Configure → Start → [Squash] → Dispose ───────

        /// <summary>Конфигурация мира: профиль, адаптер сериализации, реестр. Регистрирует мир
        /// в реестре и поднимает менеджеры. Таймеры НЕ стартуют — это Start().</summary>
        public void Configure(WorldProfile profile = null, object adapter = null,
                              WorldRegistry registry = null, Func<Type, bool> staticContractFiltering = null)
        {
            AECC.Core.Logging.KernelBootstrap.EnsureInstalled(); // диагностика лок-ядра (ТЗ 4.1.2)
            SingletonFallback = this;
            _profile = profile ?? new WorldProfile(WorldType);
            _registry = registry ?? WorldRegistry.Default;
            _registry.Register(this);
            // Монтаж сериализации — SerializationBootstrap.Attach (вынос сборок, ТЗ 4.7);
            // adapter-параметр Configure сохранён слотом для прежнего порядка вызова.
            if (adapter != null) serializationAdapter = adapter;
            entityManager = new ECSEntityManager(this);
            componentManager = new ECSComponentManager(this);
            contractsManager = new ECSContractsManager(this, staticContractFiltering);
            contractsManager.InitializeSystems();
        }

        /// <summary>Старт активностей мира: таймер time-depend контрактов через IScheduler
        /// (бывший побочный эффект InitWorldScope; интервал — из профиля, прежний дефолт 5 мс).</summary>
        public void Start()
        {
            if (timeDependContractsTimer != null) return;
            timeDependContractsTimer = Scheduler.Schedule(Profile.TimeDependContractsIntervalMs,
                () => contractsManager.RunTimeDependContracts(), repeating: true);
            Initialized = true;
        }

        /// <summary>Остановка активностей и уход из реестра. Squash-редиректы мира остаются
        /// активными (мёртвый мир — прозрачный прокси, идея 1.9).</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var t = timeDependContractsTimer;
            timeDependContractsTimer = null;
            if (t != null) t.Dispose();
            if (_registry != null) _registry.Unregister(this.instanceId);
            SharedFieldTable.DropWorld(this.instanceId); // ТЗ 4.5.8б: мир умирает со своими данными
        }

        /// <summary>Переходный фасад прежней инициализации: Configure + Start одним вызовом,
        /// поведение дословно прежнее.</summary>
        public void InitWorldScope(Func<Type, bool> staticContractFiltering)
        {
            Configure(staticContractFiltering: staticContractFiltering);
            Start();
        }
    }
}