using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using AECC.Core.Logging;
using static AECC.Core.Serialization.EntitySerializer;

namespace AECC.Core.Serialization
{
    public interface ISerializationAdapter
    {
        byte[] SerializeAdapterEntity(SerializedEntity entity);

        SerializedEntity DeserializeAdapterEntity(byte[] entity);


        byte[] SerializeECSComponent(ECSComponent component);

        ECSComponent DeserializeECSComponent(byte[] component, long typeId);

        byte[] SerializeECSEntity(ECSEntity entity);

        ECSEntity DeserializeECSEntity(byte[] entity);
    }
}
