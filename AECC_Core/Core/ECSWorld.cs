using System;
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
        
        public void InitWorldScope(Func<Type, bool> staticContractFiltering)
        {
            SingletonFallback = this;
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