using AECC.Core;
using AECC.Network;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AECC.ECS.DefaultObjects.ECSComponents
{
    [System.Serializable]
    [TypeUid(23)]
    public class SocketComponent : ECSComponent
    {
        static public new long Id { get; set; }
        static public new System.Collections.Generic.List<System.Action> StaticOnChangeHandlers { get; set; }
        [System.NonSerialized]
        public ISocketAdapter Socket;
    }
}
