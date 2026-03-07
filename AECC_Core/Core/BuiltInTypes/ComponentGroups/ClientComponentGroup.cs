using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AECC.Core.BuiltInTypes.ComponentsGroup
{
    [System.Serializable]
    [TypeUid(8)]
    public class ClientComponentGroup : ECSComponentGroup
    {
        static public new long Id { get; set; } = 8;
    }
}
