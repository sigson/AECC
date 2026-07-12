using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AECC.Core;
using MessagePack;

namespace AECC.ECS.Types.AtomicType
{
    [System.Serializable]
    [MessagePackObject]
    [TypeUid(103)]
    public class WorldPoint : BaseCustomType
    {
        [IgnoreMember] static new public long Id { get; set; } = 103;
        [Key(0)] public Vector3S Position = new Vector3S();
        [Key(1)] public Vector3S Rotation = new Vector3S();
        public WorldPoint2D Get2D() => new WorldPoint2D(){Position = new Vector2S(Position.x, Position.y), Rotation = Rotation};
    }
}
