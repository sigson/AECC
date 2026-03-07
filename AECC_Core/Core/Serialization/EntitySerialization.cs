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

namespace AECC.Core.Serialization
{
    /// <summary>
    /// Базовый абстрактный класс для сериализации сущностей.
    /// Хранит статические данные, адаптер и объявляет контракты для сериализаторов.
    /// </summary>
    public abstract class EntitySerializer
    {
        #region setupData
        public static DictionaryWrapper<long, Type> TypeStorage = new DictionaryWrapper<long, Type>();
        public static DictionaryWrapper<string, Type> TypeStringStorage = new DictionaryWrapper<string, Type>();
        public static DictionaryWrapper<Type, long> TypeIdStorage = new DictionaryWrapper<Type, long>();

        public static HashSet<Type> SerializationCache = new HashSet<Type>();

        public static ConcurrentDictionary<Type, ISerializationAdapter> ActiveSerializationAdapters = new ConcurrentDictionary<Type, ISerializationAdapter>();

        public ISerializationAdapter serializationAdapter;
        public ECSWorld worldOwner;

        // Оставили реализацию в базовом классе, так как она работает со статическим кешем
        public void InitSerialize(ECSWorld world, ISerializationAdapter adapter)
        {
            if (SerializationCache.Count == 0)
            {
                var nonSerializedSet = new HashSet<Type>() { };

                var ecsObjects = ECSAssemblyExtensions.GetAllSubclassOf(typeof(IECSObject)).Where(x => !x.IsAbstract).Where(x => !nonSerializedSet.Contains(x)).ToHashSet();
                ecsObjects.Select(x => Activator.CreateInstance(x)).Cast<IECSObject>().ForEach(x =>
                {
                    if (TypeStorage.ContainsKey(x.GetId()))
                        NLogger.Error("Error adding " + x.GetType().Name + " id " + x.GetId() + " is presened as " + TypeStorage[x.GetId()].Name);
                    TypeStorage[x.GetId()] = x.GetType();
                    TypeIdStorage[x.GetType()] = x.GetId();
                    TypeStringStorage[x.GetType().Name] = x.GetType();

                    if (x is BaseCustomType)
                        return;

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
            worldOwner = world;
        }

        [System.Serializable]
        public class SerializedEntity
        {
            public byte[] Entity;
            [System.NonSerialized]
            public ECSEntity desEntity = null;
            [System.NonSerialized]
            public DictionaryWrapper<long, ECSComponent> SerializationContainer = new DictionaryWrapper<long, ECSComponent>();
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

            public void DeserializeComponents()
            {
                foreach (var sComp in Components)
                {
                    SerializationContainer[sComp.Key] = adapter.DeserializeECSComponent(sComp.Value, sComp.Key);
                }
            }
        }
        #endregion

        // Абстрактные объявления (приватные методы изменены на protected для возможности override)
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