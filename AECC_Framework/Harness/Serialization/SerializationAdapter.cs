using AECC.ECS.ECSCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using AECC.Core.Logging;
using AECC.Core.Serialization;
using static AECC.Core.Serialization.EntitySerializer;
using AECC.Extensions;
using AECC.Core;
using NECS.ECS.ECSCore;

namespace AECC.Harness.Serialization
{
    public class SerializationAdapter : ISerializationAdapter
    {

        [System.Serializable]
        public class SerializedEvent
        {
            public int TId = 0;
            public byte[] EventData;
            [System.NonSerialized]
            [Newtonsoft.Json.JsonIgnore]
            private ECSEvent cEvent;

            public SerializedEvent() { }
            public SerializedEvent(ECSEvent e)
            {
                cEvent = e;
                TId = Convert.ToInt32(e.GetId());
            }

            public ECSEvent Deserialize()
            {
                return SerializationAdapter.DeserializeECSEvent(EventData, TId);
            }

            public byte[] Serialize()
            {
                EventData = SerializationAdapter.SerializeECSEvent(cEvent);
                return SerializationAdapter.SerializeAdapterEvent(this);
            }
        }
        private JsonSerializer storeJsonSerializer = null;
        private JsonSerializer cacheJsonSerializer
        {
            get
            {
                if(storeJsonSerializer == null)
                {
                    storeJsonSerializer = JsonSerializer.CreateDefault();
                }
                return storeJsonSerializer;
            }
        }
        public byte[] SerializeAdapterEntity(SerializedEntity entity)
        {
            if (Defines.AOTMode)
            {
                return Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(entity));
            }
            else
            {
                using (var memoryStream = new MemoryStream())
                {
                    NetSerializer.Serializer.Default.Serialize(memoryStream, entity);
                    return memoryStream.ToArray();
                }
            }
        }

        public SerializedEntity DeserializeAdapterEntity(byte[] entity)
        {
            if(entity == null || entity.Length == 0)
            {
                NLogger.Error("Failed to deserialize empty adapter entity");
                return null;
            }
            if(Defines.AOTMode)
            {
                return JsonConvert.DeserializeObject<SerializedEntity>(Encoding.UTF8.GetString(entity));
            }
            else
            {
                using (var memoryStream = new MemoryStream())
                {
                    memoryStream.Write(entity, 0, entity.Length);
                    memoryStream.Position = 0;
                    return (SerializedEntity)ReflectionCopy.MakeReverseShallowCopy(NetSerializer.Serializer.Default.Deserialize(memoryStream));
                }
            }
        }

        public static byte[] SerializeAdapterEvent(SerializedEvent entity)
        {
            if (Defines.AOTMode)
            {
                return Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(entity));
            }
            else
            {
                using (var memoryStream = new MemoryStream())
                {
                    NetSerializer.Serializer.Default.Serialize(memoryStream, entity);
                    return memoryStream.ToArray();
                }
            }
        }

        public static SerializedEvent DeserializeAdapterEvent(byte[] entity)
        {
            if(entity == null || entity.Length == 0)
            {
                NLogger.Error("Failed to deserialize empty adapter event");
                return null;
            }
            if (Defines.AOTMode)
            {
                return JsonConvert.DeserializeObject<SerializedEvent>(Encoding.UTF8.GetString(entity));
            }
            else
            {
                using (var memoryStream = new MemoryStream())
                {
                    memoryStream.Write(entity, 0, entity.Length);
                    memoryStream.Position = 0;
                    return (SerializedEvent)ReflectionCopy.MakeReverseShallowCopy(NetSerializer.Serializer.Default.Deserialize(memoryStream));
                }
            }
        }


        public byte[] SerializeECSComponent(ECSComponent component)
        {
            if (Defines.AOTMode)
            {
                return Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(component));
            }
            else
            {
                using (var memoryStream = new MemoryStream())
                {
                    NetSerializer.Serializer.Default.Serialize(memoryStream, component);
                    return memoryStream.ToArray();
                }
            }
        }

        public ECSComponent DeserializeECSComponent(byte[] component, long typeId)
        {
            if(component == null || component.Length == 0)
            {
                NLogger.Error("Failed to deserialize empty adapter event");
                return null;
            }
            if (Defines.AOTMode)
            {
                return (ECSComponent)JsonConvert.DeserializeObject(Encoding.UTF8.GetString(component), EntitySerializer.TypeStorage[typeId]);
            }
            else
            {
                using (var memoryStream = new MemoryStream())
                {
                    memoryStream.Write(component, 0, component.Length);
                    memoryStream.Position = 0;
                    return (ECSComponent)ReflectionCopy.MakeReverseShallowCopy(NetSerializer.Serializer.Default.Deserialize(memoryStream));
                }
            }
        }

        public byte[] SerializeECSEntity(ECSEntity entity)
        {
            if (Defines.AOTMode)
            {
                return Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(entity));
            }
            else
            {
                using (var memoryStream = new MemoryStream())
                {
                    NetSerializer.Serializer.Default.Serialize(memoryStream, entity);
                    return memoryStream.ToArray();
                }
            }
        }

        public ECSEntity DeserializeECSEntity(byte[] entity)
        {
            if(entity == null || entity.Length == 0)
            {
                NLogger.Error("Failed to deserialize empty ECSEntity");
                return null;
            }
            if (Defines.AOTMode)
            {
                return JsonConvert.DeserializeObject<ECSEntity>(Encoding.UTF8.GetString(entity));
            }
            else
            {
                using (var memoryStream = new MemoryStream())
                {
                    memoryStream.Write(entity, 0, entity.Length);
                    memoryStream.Position = 0;
                    return (ECSEntity)ReflectionCopy.MakeReverseShallowCopy(NetSerializer.Serializer.Default.Deserialize(memoryStream));
                }
            }
        }

        public static byte[] SerializeECSEvent(ECSEvent ecsevent)
        {
            if (Defines.AOTMode)
            {
                return Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(ecsevent));
            }
            else
            {
                using (var memoryStream = new MemoryStream())
                {
                    NetSerializer.Serializer.Default.Serialize(memoryStream, ecsevent);
                    return memoryStream.ToArray();
                }
            }
        }

        public static ECSEvent DeserializeECSEvent(byte[] ecsevent, long typeId)
        {
            if(ecsevent == null || ecsevent.Length == 0)
            {
                NLogger.Error("Failed to deserialize empty ECSEvent");
                return null;
            }
            if (Defines.AOTMode)
            {
                return (ECSEvent)JsonConvert.DeserializeObject(Encoding.UTF8.GetString(ecsevent), EntitySerializer.TypeStorage[typeId]);
            }
            else
            {
                using (var memoryStream = new MemoryStream())
                {
                    memoryStream.Write(ecsevent, 0, ecsevent.Length);
                    memoryStream.Position = 0;
                    return (ECSEvent)ReflectionCopy.MakeReverseShallowCopy(NetSerializer.Serializer.Default.Deserialize(memoryStream));
                }
            }
        }
    }
}
