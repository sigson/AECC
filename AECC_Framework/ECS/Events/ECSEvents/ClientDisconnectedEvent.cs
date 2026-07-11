using AECC.Core;
using AECC.ECS.Core;
using AECC.Network;
using MessagePack;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AECC.ECS.DefaultObjects.Events.ECSEvents
{
    [NetworkScore(0)]
    [System.Serializable]
    [MessagePackObject] // P2
    [TypeUid(17)]
    // ВНИМАНИЕ: у события нет ни одного [Key] — это ок, MessagePack сериализует пустую карту.
    public class ClientDisconnectedEvent : NetworkEvent
    {
        static public new long Id { get; set; } = 17;
        public override void Execute()
        {
            
        }
    }
}
