using System;
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
        //public static Func<ECSWorld> GetWorld = () => SingletonFallback;
        public static Func<Dictionary<long, ECSWorld>> GetAllWorlds = () => new Dictionary<long, ECSWorld>() { {SingletonFallback.instanceId, SingletonFallback} };
        public static Func<long, ECSWorld> GetWorld = (long instance) => SingletonFallback;
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
        
        public void InitWorldScope(Func<Type, bool> staticContractFiltering)
        {
            SingletonFallback = this;
            EntityWorldSerializer.InitSerialize(this, new DummySerializationAdapter());
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