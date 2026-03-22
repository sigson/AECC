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
using AECC.ECS.Core;

namespace AECC.Harness.Serialization
{
    public class SerializationAdapter : ISerializationAdapter
    {
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
    }
}
