using AECC.ECS.DefaultObjects.Events.ECSEvents;
using AECC.ECS.ECSCore;
using AECC.Harness.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AECC.ECS.DefaultObjects.Events.LowLevelNetEvent.Auth
{
    [LowLevelNetworkEvent]
    [NetworkScore(0)]
    [System.Serializable]
    [TypeUid(26)]
    public class AuthActionFailedEvent : ECSEvent
    {
        public int EventId = 0;
        public string Reason = "";
        [System.NonSerialized]
        [Newtonsoft.Json.JsonIgnore]
        public static Action<AuthActionFailedEvent> action = (errorEvent) => { };
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
