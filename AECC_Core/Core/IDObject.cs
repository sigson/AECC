using System;
using System.Reflection;
using System.Runtime.Serialization;
using AECC.Extensions;
using AECC.Core.Logging;
using AECC.Core.Serialization;

namespace AECC.Core
{
    /// <summary>
    /// Единый тонкий корень идентичности для всех сериализуемых типов экосистемы
    /// (IECSObject, ECSComponentGroup, GroupDataAccessPolicy, BaseCustomType-линия).
    /// Содержит ТОЛЬКО идентичность и привязку к миру; lock зеркал, дерево детей и
    /// serialization-lifecycle живут ниже, в IECSObject, и не протекают на чистые data-типы.
    /// abstract — чтобы не попадать под Activator.CreateInstance в InitSerialize.
    /// </summary>
    [System.Serializable]
    public abstract class IDObject
    {
        static public long Id { get; set; } = 0;
        static public long GId<T>() => EntitySerializer.TypeIdStorage[typeof(T)];
        public long instanceId = Guid.NewGuid().GuidToLongR();

        public long ECSWorldOwnerId = 0;
        [IgnoreDataMember]
        [System.NonSerialized]
        public ECSWorld ECSWorldOwnerCache = null;
        [IgnoreDataMember]
        public ECSWorld ECSWorldOwner {
            get {
                if (ECSWorldOwnerId == 0)
                {
                    if (!Defines.IgnoreNonDangerousExceptions)
                        NLogger.LogError($"IDObject '{instanceId}: {this.GetType().Name}': ECSWorldOwnerId == 0");
                    return null;
                }
                else
                {
                    if(ECSWorldOwnerCache != null && ECSWorldOwnerCache.instanceId == ECSWorldOwnerId)
                    {
                        return ECSWorldOwnerCache;
                    }
                }
                ECSWorldOwnerCache = ECSWorld.GetWorld(ECSWorldOwnerId);
                return ECSWorldOwnerCache;
            }
            set
            {
                ECSWorldOwnerId = value.instanceId;
                ECSWorldOwnerCache = value;
            }
        }

        [System.NonSerialized]
        public Type ObjectType;
        [System.NonSerialized]
        protected long ReflectionId = 0;

        /// <summary>
        /// Единственная копия резолва type-id на всю экосистему. Базовый Id намеренно 0,
        /// поэтому путь всегда идёт через [TypeUid] конкретного типа (ReflectionId кешируется).
        /// </summary>
        public long GetId()
        {
            if (Id == 0)
                try
                {
                    if (ObjectType == null)
                    {
                        ObjectType = GetType();
                    }
                    if (ReflectionId == 0)
                        ReflectionId = ObjectType.GetCustomAttribute<TypeUidAttribute>().Id;
                    return ReflectionId;
                }
                catch
                {
                    NLogger.Error(this.GetType().ToString() + "Could not find Id field");
                    return 0;
                }
            else
                return Id;
        }
    }
}
