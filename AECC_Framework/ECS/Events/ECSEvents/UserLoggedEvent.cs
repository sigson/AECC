using AECC.Core;
using AECC.Harness.Services;
using AECC.ECS.Core;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AECC.Network;

namespace AECC.ECS.DefaultObjects.Events.ECSEvents
{
    [LowLevelNetworkEvent]
    [NetworkScore(0)]
    [System.Serializable]
    [TypeUid(25)]
    public class UserLoggedEvent : NetworkEvent
    {
        static public new long Id { get; set; } = 25;
        [System.NonSerialized]
        [Newtonsoft.Json.JsonIgnore]
        public ECSEntity userEntity;
        public long userEntityId;
        public string Username;
        public bool userRelogin = false;
        [System.NonSerialized]
        [Newtonsoft.Json.JsonIgnore]
        public static Action<UserLoggedEvent> actionAfterLoggin = (loggedEvent) => { };
        public override void Execute()
        {
            actionAfterLoggin(this);
            if(GlobalProgramState.instance.ProgramType == GlobalProgramState.ProgramTypeEnum.Client)
            {
                GlobalProgramState.instance.PlayerEntityId = userEntityId;
                GlobalProgramState.instance.Username = Username;
            }
        }
    }
}
