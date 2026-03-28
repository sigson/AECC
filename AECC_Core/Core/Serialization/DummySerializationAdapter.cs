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
    public class DummySerializationAdapter : ISerializationAdapter
    {
        public SerializedEntity DeserializeAdapterEntity(byte[] entity)
        {
            throw new NotImplementedException();
        }

        public ECSComponent DeserializeECSComponent(byte[] component, long typeId)
        {
            throw new NotImplementedException();
        }

        public ECSEntity DeserializeECSEntity(byte[] entity)
        {
            throw new NotImplementedException();
        }

        public void InitializeAdapterCache(IEnumerable<Type> types)
        {
            
        }

        public byte[] SerializeAdapterEntity(SerializedEntity entity)
        {
            throw new NotImplementedException();
        }

        public byte[] SerializeECSComponent(ECSComponent component)
        {
            throw new NotImplementedException();
        }

        public byte[] SerializeECSEntity(ECSEntity entity)
        {
            throw new NotImplementedException();
        }
    }
}
