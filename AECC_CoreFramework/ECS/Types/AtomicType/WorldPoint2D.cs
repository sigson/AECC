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
    [TypeUid(104)]
    public class WorldPoint2D : BaseCustomType
    {
        [IgnoreMember] static new public long Id { get; set; } = 104;
        [Key(0)] public Vector2S Position = new Vector2S();
        [Key(1)] public Vector3S Rotation = new Vector3S();
    }
}
