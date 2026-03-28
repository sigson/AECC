using AECC.Core;
using AECC.ECS.DefaultObjects.Events.ECSEvents;
using AECC.Harness.Services;
using AECC.ECS.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AECC.Network;
using MessagePack;

namespace AECC.ECS.DefaultObjects.Events.LowLevelNetEvent.Auth
{
    [LowLevelNetworkEvent]
    [NetworkScore(0)]
    [System.Serializable]
    [TypeUid(26)]
    public class AuthActionFailedEvent : NetworkEvent
    {
        [Key(10)] public int EventId = 0;
        [Key(11)] public string Reason = "";
        [System.NonSerialized]
        [Newtonsoft.Json.JsonIgnore]
        [IgnoreMemberAttribute] public static Action<AuthActionFailedEvent> action = (errorEvent) => { };
        static public new long Id { get; set; } = 26;
        public override void Execute()
        {
            if(GlobalProgramState.instance.ProgramType == GlobalProgramState.ProgramTypeEnum.Client)
            {
                action(this);
            }
        }
    }
}
