using System;
using System.Collections.Generic;
using AECC.Core.Serialization;
using AECC.Extensions;

namespace AECC.Core
{
    public
#if GODOT4_0_OR_GREATER
    partial
#endif
    class ECSWorld
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
        public static Func<IDictionary<long, ECSWorld>> GetAllWorlds = () => new Dictionary<long, ECSWorld>() { {GetSingletonFallback().instanceId, GetSingletonFallback()} };
        public static Func<long, ECSWorld> GetWorld = (long instance) => GetSingletonFallback();
        public static Func<long, ECSWorld> GetEntityWorld = (long entityinstance) => GetSingletonFallback();
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
        public bool Initialized = false;
        public enum WorldTypeEnum
        {
            Server,
            Client,
            Offline
        }
        public WorldTypeEnum WorldType = WorldTypeEnum.Offline;
        public EntityNetSerializer EntityWorldSerializer = new EntityNetSerializer();
        public ISerializationAdapter serializationAdapter = new DummySerializationAdapter();
        
        public void InitWorldScope(Func<Type, bool> staticContractFiltering)
        {
            SingletonFallback = this;
            EntityWorldSerializer.InitSerialize(this, serializationAdapter);
            entityManager = new ECSEntityManager(this);
            componentManager = new ECSComponentManager(this);
            contractsManager = new ECSContractsManager(this, staticContractFiltering);
            contractsManager.InitializeSystems();
            var timer = new TimerCompat(5, (obj, arg) => contractsManager.RunTimeDependContracts(), true);
            timer.Start();
            Initialized = true;
        }
    }
}