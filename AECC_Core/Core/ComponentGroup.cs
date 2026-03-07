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
    [System.Serializable]
    [TypeUid(6)]
    public class ECSComponentGroup : ECSComponent
    {
        public static new long Id { get; set; } = 6;
    }
}
