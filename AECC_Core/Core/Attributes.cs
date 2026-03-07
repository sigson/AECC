using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using AECC.Extensions;
using AECC.Core.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace AECC.Core
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface)]
    public sealed class TypeUidAttribute : Attribute
    {
        public int Id { get; set; }

        public TypeUidAttribute(int id)
        {
            Id = id;
        }
    }
}
