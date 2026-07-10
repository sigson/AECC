using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AECC.Collections;
using AECC.Core.Logging;
using AECC.Extensions;

using AECC.Core.Serialization; // резиденты Core: State/Shadow/participant (мигрируют после слайса 2 DBComponent)

using AECC.Core; // видимость Core больше не наследуется от родительского неймспейса

namespace AECC.Serialization
{
    /// <summary>
    /// ФАЗА 4, вынос сборок (ТЗ 4.7, breaking): «SlicedSerializeStorage и родня — внутренние
    /// методы Serialization». Тела перенесены ДОСЛОВНО из EntityComponentStorage; extension-
    /// синтаксис сохраняет прежние вызовы `entity.entityComponents.SlicedSerializeStorage(...)`
    /// (нужен using AECC.Core.Serialization). Доступ к internal-членам хранилища (Store,
    /// changedComponents, entity, Kid) — через InternalsVisibleTo("AECC.Serialization").
    /// Порядок хуков участника и дисциплина локов (ячейка → SerialLocker) — без изменений.
    /// </summary>
    public static class EntityComponentStorageSerialization
    {
        public static Dictionary<long, byte[]> SlicedSerializeStorage(this EntityComponentStorage s, ISerializationAdapter serializationAdapter, bool serializeOnlyChanged, bool clearChanged)
        {
            if (serializeOnlyChanged)
            {
                //using (this.StabilizationLocker.ReadLock())//lock (this.serializationLocker)
                {
                    DictionaryWrapper<Type, ECSComponent> serializedContainer = new DictionaryWrapper<Type, ECSComponent>();
                    Dictionary<long, byte[]> slicedComponents = new Dictionary<long, byte[]>();
                    var cachedChangedComponents = s.changedComponents.Keys.ToList();
                    List<Type> errorList = new List<Type>();
                    foreach (var changedComponent in cachedChangedComponents)
                    {
                        if (Defines.LogECSEntitySerializationComponents)
                        {
                            NLogger.Log($"Will serialized changed component {changedComponent} in {s.entity.AliasName}:{s.entity.instanceId}");
                        }
                        s.Store.ExecuteReadLocked(EntityComponentStorage.Kid(changedComponent), (key, component) =>
                        {
                            using (MemoryStream writer = new MemoryStream())
                            {
                                var pairComponent = new KeyValuePair<long, ECSComponent>(component.GetId(), component);
                                //var component = pairComponent.Value;
                                byte[] serializedData = null;
                                lock (component.SerialLocker)
                                {
                                    component.EnterToSerialization();

                                    // Фаза 4, шаг 2 (ТЗ 4.7): интерфейс участника вместо is DBComponent
                                    var participant = component as AECC.Abstractions.ISerializationParticipant;
                                    if (participant != null)
                                    {
                                        participant.BeforeSnapshot(serializeOnlyChanged, clearChanged);
                                    }

                                    //NetSerializer.Serializer.Default.Serialize(writer, component);
                                    serializedData = serializationAdapter.SerializeECSComponent(component);

                                    if (participant != null)
                                    {
                                        participant.AfterSnapshot(clearChanged);
                                    }
                                    component.AfterSerialization();
                                }
                                slicedComponents[pairComponent.Key] = serializedData;//writer.ToArray();
                                if (clearChanged)
                                    s.changedComponents.Remove(component.GetTypeFast(), out _);
                            }
                        });
                    }
                    return slicedComponents;
                }
            }
            else
            {
                //using (this.StabilizationLocker.ReadLock())//lock (this.serializationLocker)
                {
                    Dictionary<long, byte[]> slicedComponents = new Dictionary<long, byte[]>();
                    // ОПТИМИЗАЦИЯ ПАМЯТИ: зеркало SerializationContainer упразднено — полный
                    // срез читается напрямую из живого Store (ключ ТОТ ЖЕ: typeUid ==
                    // component.GetId()). Дисциплина та же, что и в changed-ветке выше:
                    // снапшот ключей + ExecuteReadLocked по каждому. Лок теперь берётся на
                    // РЕАЛЬНОЙ ячейке компонента (а не на ячейке зеркала), т.е. срез
                    // корректнее прежнего исключается с мутациями компонента.
                    var cacheStoreKeys = s.Store.Keys.ToList();
                    foreach (var pairComponentKey in cacheStoreKeys)
                    {
                        s.Store.ExecuteReadLocked(pairComponentKey, (key, pairComponent) => { 
                            using (MemoryStream writer = new MemoryStream())
                            {
                                if (!(pairComponent as ECSComponent).Unregistered)
                                {
                                    if (Defines.LogECSEntitySerializationComponents)
                                    {
                                        NLogger.Log($"Will serialized component {pairComponent.GetType()} in {s.entity.AliasName}:{s.entity.instanceId}");
                                    }

                                    // Фаза 4, шаг 2 (ТЗ 4.7): интерфейс участника вместо is DBComponent
                                    var participant = pairComponent as AECC.Abstractions.ISerializationParticipant;
                                    if (participant != null)
                                    {
                                        participant.BeforeSnapshot(serializeOnlyChanged, clearChanged);
                                    }

                                    //NetSerializer.Serializer.Default.Serialize(writer, pairComponent);
                                    var serializedData = serializationAdapter.SerializeECSComponent((pairComponent as ECSComponent));

                                    slicedComponents[pairComponentKey] = serializedData;//writer.ToArray();
                                    if (participant != null)
                                    {
                                        participant.AfterSnapshot(clearChanged);
                                    }
                                    if (clearChanged)
                                        s.changedComponents.Remove((pairComponent as ECSComponent).GetTypeFast(), out _);
                                }
                            }
                            });
                    }
                    return slicedComponents;
                }
                return null;
            }
        }

        public static Dictionary<long, byte[]> SerializeStorage(this EntityComponentStorage s, ISerializationAdapter serializationAdapter, bool serializeOnlyChanged, bool clearChanged) // OBSOLETE
        {
            Dictionary<long, byte[]> serializeContainer = new Dictionary<long, byte[]>();
            if (serializeOnlyChanged)
            {
                foreach (var changedComponent in s.changedComponents)
                {
                    var component = s.Store.GetOrThrow(EntityComponentStorage.Kid(changedComponent.Key));
                    if (Defines.LogECSEntitySerializationComponents)
                    {
                        NLogger.Log($"Will serialized component {component.GetType()} in {s.entity.AliasName}:{s.entity.instanceId}");
                    }
                    serializeContainer[component.GetId()] = serializationAdapter.SerializeECSComponent(component);
                }
            }
            else
            {
                // ОПТИМИЗАЦИЯ ПАМЯТИ: полный проход — по живому Store (зеркало упразднено;
                // ключ идентичен: typeUid == component.GetId()).
                foreach (var changedComponent in s.Store)
                {
                    serializeContainer[changedComponent.Key] = serializationAdapter.SerializeECSComponent(changedComponent.Value);
                }
            }
            if (clearChanged)
                s.changedComponents.Clear();
            return serializeContainer;
        }

        /// <summary>
        /// Распаковка компонентов пакета. ОПТИМИЗАЦИЯ ПАМЯТИ (финальный шаг упразднения
        /// зеркала): бывший пер-сущностный SerializationContainer полностью удалён из
        /// кодовой базы — распакованные компоненты ТРАНЗИТНЫ (живут в пределах одного
        /// вызова сериализатора: распаковка → перенос в живой Store), поэтому буфер
        /// возвращается ЛОКАЛЬНЫМ словарём, а не хранится в состоянии сущности.
        /// </summary>
        public static Dictionary<long, ECSComponent> DeserializeStorage(this EntityComponentStorage s, ISerializationAdapter serializationAdapter, Dictionary<long, byte[]> serializedComponents)
        {
            var landed = new Dictionary<long, ECSComponent>(serializedComponents.Count);
            foreach (var serComponent in serializedComponents)
            {
                landed[serComponent.Key] = (ECSComponent)serializationAdapter.DeserializeECSComponent(serComponent.Value, serComponent.Key);
            }
            return landed;
        }

    }
}
