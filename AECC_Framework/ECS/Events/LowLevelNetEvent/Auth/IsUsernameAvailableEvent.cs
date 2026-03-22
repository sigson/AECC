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
namespace AECC.ECS.DefaultObjects.Events.LowLevelNetEvent.Auth
{
    [LowLevelNetworkEvent]
    [NetworkScore(0)]
    [System.Serializable]
    [TypeUid(27)]
    public class IsUsernameAvailableEvent : NetworkEvent
    {
        public string Username = "";
        public bool IsAvailable = false;
        [System.NonSerialized]
        [Newtonsoft.Json.JsonIgnore]
        public static Action<IsUsernameAvailableEvent> action = (errorEvent) => { };
        static public new long Id { get; set; } = 27;
        public override void Execute()
        {
            if (GlobalProgramState.instance.ProgramType == GlobalProgramState.ProgramTypeEnum.Server)
                IsAvailable = DBService.instance.DBProvider.UsernameAvailable(this.Username);
            if (GlobalProgramState.instance.ProgramType == GlobalProgramState.ProgramTypeEnum.Client)
                action(this);
        }
    }
}
