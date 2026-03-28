using AECC.Core;
using AECC.Core.Serialization;
using AECC.ECS.Core;
using AECC.Network;
using MessagePack;
using System.Collections.Generic;

namespace AECC.ECS.Events.ECSEvents
{
    [System.Serializable]
    [MessagePackObject]
    [TypeUid(15)]
    public class UpdateEntitiesEvent : NetworkEvent
    {
        [IgnoreMemberAttribute]  static public new long Id { get; set; } = 15;
        [Key(10)] public long EntityIdRecipient; //ID of user with socket component
        [Key(11)] public List<byte[]> Entities;
        public override void Execute()
        {
            foreach (var entity in Entities)
            {
                ECSWorld.GetWorld(this.WorldOwnerId).EntityWorldSerializer.UpdateDeserialize(entity);
            }
        }
    }
}
