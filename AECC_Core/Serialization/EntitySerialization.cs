using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using AECC.Core.Logging;
using AECC.Extensions;
using AECC.Extensions.ThreadingSync;
using AECC.Collections;

using AECC.Core;

namespace AECC.Serialization
{
    /// <summary>
    /// Базовый абстрактный класс для сериализации сущностей.
    /// Хранит статические данные, адаптер и объявляет контракты для сериализаторов.
    /// </summary>
    public abstract class EntitySerializer
    {
        #region setupData
        // These static Type<->id maps are [Obsolete] facades over the same DictionaryWrapper
        // instances owned by TypeRegistry.Global, kept for external code that still indexes
        // them directly; internal code should use ITypeRegistry.
        [Obsolete("Используйте ITypeRegistry (AECC.Core.TypeRegistry.Global)")]
        public static DictionaryWrapper<long, Type> TypeStorage { get { return TypeRegistry.Global.ById; } }
        [Obsolete("Используйте ITypeRegistry (AECC.Core.TypeRegistry.Global)")]
        public static DictionaryWrapper<string, Type> TypeStringStorage { get { return TypeRegistry.Global.ByName; } }
        [Obsolete("Используйте ITypeRegistry (AECC.Core.TypeRegistry.Global)")]
        public static DictionaryWrapper<Type, long> TypeIdStorage { get { return TypeRegistry.Global.RegisteredIds; } }

        public static HashSet<Type> SerializationCache = new HashSet<Type>();

        public static ConcurrentDictionary<Type, ISerializationAdapter> ActiveSerializationAdapters = new ConcurrentDictionary<Type, ISerializationAdapter>();

        public ISerializationAdapter serializationAdapter;
        public ECSWorld worldOwner;

        // Implementation lives in the base class because it operates on the static cache.
        public void InitSerialize(ECSWorld world, ISerializationAdapter adapter)
        {
            if (SerializationCache.Count == 0)
            {
                var nonSerializedSet = new HashSet<Type>() { };

                var ecsObjects = new HashSet<Type>(ECSAssemblyExtensions.GetAllSubclassOf(typeof(IDObject)).Where(x => !x.IsAbstract).Where(x => !nonSerializedSet.Contains(x)));
                ecsObjects.Select(x => Activator.CreateInstance(x)).Cast<IDObject>().ForEach(x =>
                {
                    TypeRegistry.Global.Register(x.GetId(), x.GetType());

                    try
                    {
                        var field = x.GetType().GetField("<Id>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                        var customAttrib = x.GetType().GetCustomAttribute<TypeUidAttribute>();
                        if (customAttrib != null && field != null)
                            field.SetValue(null, customAttrib.Id);
                        else
                            NLogger.LogError($"WARNING! Type{x.GetType().ToString()} no have static id field or ID attribute");
                    }
                    catch (Exception ex)
                    {
                        NLogger.Error(x.GetType().Name + " no have static id field or ID attribute");
                    }
                });

                ecsObjects.Add(typeof(SerializedEntity));

                SerializationCache = ecsObjects;
            }
            serializationAdapter = adapter;
            serializationAdapter.InitializeAdapterCache(SerializationCache);
            worldOwner = world;
        }

        [System.Serializable]
        public class SerializedEntity
        {
            public byte[] Entity;
            [System.NonSerialized]
            public ECSEntity desEntity = null;
            public Dictionary<long, byte[]> Components = new Dictionary<long, byte[]>();

            public ISerializationAdapter adapter;

            public SerializedEntity() { }

            public SerializedEntity(ISerializationAdapter adapter)
            {
                this.adapter = adapter;
            }

            public void DeserializeEntity()
            {
                desEntity = adapter.DeserializeECSEntity(this.Entity);
            }
        }
        #endregion

        protected abstract byte[] FullSerialize(ECSEntity entity, bool serializeOnlyChanged = false);
        protected abstract Dictionary<long, byte[]> SlicedSerialize(ECSEntity entity, bool serializeOnlyChanged = false, bool clearChanged = false);
        public abstract void SerializeEntity(ECSEntity entity, bool serializeOnlyChanged = false);
        public abstract byte[] BuildSerializedEntityWithGDAP(ECSEntity toEntity, ECSEntity fromEntity, bool ignoreNullData = false);
        public abstract byte[] BuildFullSerializedEntityWithGDAP(ECSEntity toEntity, ECSEntity fromEntity);
        protected abstract byte[] BuildFullSerializedEntity(ECSEntity Entity);
        public abstract ECSEntity Deserialize(byte[] serializedData);
        public abstract void UpdateDeserialize(byte[] serializedData);
    }

   
}