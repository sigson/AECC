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
        // Фабрика дефолтного адаптера сериализации живёт в
        // AECC.Serialization.SerializationBootstrap — мир не знает типов сериализации.

        // Источник истины — инстансный WorldRegistry. Статический Func — фасад над ним:
        // дефолт ходит в реестр, а сама точка переопределения GetWorld сохраняется
        // (интеграции и тесты подменяют её).
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

        /// <summary>Точка поиска — world.Query.Search(scope, with, without). Реализация —
        /// AECC.Query.EntityQueryIndex; монтаж — QueryBootstrap.Attach (заполняет этот слот
        /// и entityManager.QueryIndex). null = мир без поиска.</summary>
        public IWorldQueryIndex Query;
        public bool Initialized = false;
        public enum WorldTypeEnum
        {
            Server,
            Client,
            Offline
        }
        public WorldTypeEnum WorldType = WorldTypeEnum.Offline;
        // Поля object-типизированы (гейт «Model/Core без ссылки на Serialization»): мир
        // ХРАНИТ сериализатор/адаптер, не интерпретирует их. Монтаж —
        // AECC.Serialization.SerializationBootstrap.Attach(world, adapter): он создаёт
        // EntityNetSerializer, зовёт InitSerialize и заполняет оба слота. Типизированный
        // доступ — только со стороны Serialization/приложения.
        public object EntityWorldSerializer;
        public object serializationAdapter = null;

        /// <summary>Планировщик мира: инжектируемая абстракция над TaskEx/TimerCompat.
        /// Подмена на детерминированный — до Configure/Start.</summary>
        public AECC.Abstractions.IScheduler Scheduler = DefaultScheduler.Instance;

        private WorldProfile _profile;

        /// <summary>Профиль мира: флаги вычислены при создании (первое обращение /
        /// Configure) — последующие смены WorldType на профиль не влияют.</summary>
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

        // ─────── Явный lifecycle мира: Create → Configure → Start → [Squash] → Dispose ───────

        /// <summary>Конфигурация мира: профиль, адаптер сериализации, реестр. Регистрирует мир
        /// в реестре и поднимает менеджеры. Таймеры НЕ стартуют — это Start().</summary>
        public void Configure(WorldProfile profile = null, object adapter = null,
                              WorldRegistry registry = null, Func<Type, bool> staticContractFiltering = null)
        {
            AECC.Core.Logging.KernelBootstrap.EnsureInstalled();
            SingletonFallback = this;
            _profile = profile ?? new WorldProfile(WorldType);
            _registry = registry ?? WorldRegistry.Default;
            _registry.Register(this);
            // Монтаж сериализации — SerializationBootstrap.Attach; adapter-параметр
            // Configure сохранён слотом для совместимости порядка вызова.
            if (adapter != null) serializationAdapter = adapter;
            entityManager = new ECSEntityManager(this);
            componentManager = new ECSComponentManager(this);
            contractsManager = new ECSContractsManager(this, staticContractFiltering);
            contractsManager.InitializeSystems();
        }

        /// <summary>Старт активностей мира: таймер time-depend контрактов через IScheduler,
        /// интервал берётся из профиля.</summary>
        public void Start()
        {
            if (timeDependContractsTimer != null) return;
            timeDependContractsTimer = Scheduler.Schedule(Profile.TimeDependContractsIntervalMs,
                () => contractsManager.RunTimeDependContracts(), repeating: true);
            Initialized = true;
        }

        /// <summary>Остановка активностей и уход из реестра. Squash-редиректы мира остаются
        /// активными (мёртвый мир — прозрачный прокси).</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var t = timeDependContractsTimer;
            timeDependContractsTimer = null;
            if (t != null) t.Dispose();
            if (_registry != null) _registry.Unregister(this.instanceId);
            SharedFieldTable.DropWorld(this.instanceId);
        }

        /// <summary>Фасад: Configure + Start одним вызовом.</summary>
        public void InitWorldScope(Func<Type, bool> staticContractFiltering)
        {
            Configure(staticContractFiltering: staticContractFiltering);
            Start();
        }
    }
}