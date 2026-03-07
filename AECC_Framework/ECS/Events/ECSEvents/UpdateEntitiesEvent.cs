using AECC.Core;
using AECC.Core.Serialization;
using NECS.ECS.ECSCore;
using System.Collections.Generic;

namespace AECC.ECS.Events.ECSEvents
{
    [System.Serializable]
    [TypeUid(15)]
    public class UpdateEntitiesEvent : ECSEvent
    {
        static public new long Id { get; set; } = 15;
        public long EntityIdRecipient; //ID of user with socket component
        public List<byte[]> Entities;
        public override void Execute()
        {
            foreach (var entity in Entities)
            {
                ECSWorld.GetWorld(this.WorldOwnerId).EntityWorldSerializer.UpdateDeserialize(entity);
            }
        }
    }
}
