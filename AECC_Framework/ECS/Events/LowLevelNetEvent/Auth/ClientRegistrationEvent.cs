using AECC.Core;
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
    [NetworkScore(400)]
    [System.Serializable]
    [TypeUid(22)]
    public class ClientRegistrationEvent : NetworkEvent
    {
        [Key(10)] public string Username = "";
        [Key(11)] public string Password = "";
        [Key(12)] public string Email = "";
        [Key(13)] public string HardwareId = "";
        [Key(14)] public string CaptchaResultHash = "";
        static public new long Id { get; set; } = 22;
        public override void Execute()
        {
            if (GlobalProgramState.instance.ProgramType == GlobalProgramState.ProgramTypeEnum.Server)
                AuthService.instance.RegistrationProcess(this);
        }

        public override bool CheckPacket()
        {
            if (GlobalProgramState.instance.ProgramType == GlobalProgramState.ProgramTypeEnum.Server)
            {
                if (Username.Any(p => !char.IsLetterOrDigit(p)))
                {
                    return false;
                }
                if (Email.Any(p => !char.IsLetterOrDigit(p) && !(p == '@' || p == '.' || p == '_' || p == '-')))
                {
                    return false;
                }
                if (Username.Length > 32 || Password.Length > 32 || Email.Length > 32)
                    return false;
            }
            return true;
        }
    }
}
