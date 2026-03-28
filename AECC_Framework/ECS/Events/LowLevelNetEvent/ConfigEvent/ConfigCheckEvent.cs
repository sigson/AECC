using AECC.Core;
using AECC.Harness.Services;
using AECC.ECS.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AECC.Network;

namespace AECC.ECS.DefaultObjects.Events.LowLevelNetEvent.ConfigEvent
{
    [LowLevelNetworkEvent]
    [NetworkScore(100)]
    [System.Serializable]
    [TypeUid(19)]
    public class ConfigCheckEvent : NetworkEvent
    {
        static public new long Id { get; set; } = 19;
        public long configHash;
        public override void Execute()
        {
            if (GlobalProgramState.instance.ProgramType == GlobalProgramState.ProgramTypeEnum.Server)
            {
                byte[] newconfig = null;
                if(configHash != ConstantService.instance.hashConfigFilesZip)
                {
                    newconfig = ConstantService.instance.ConfigFilesZip.ToArray();
                }
                NetworkService.instance.EventManager.Dispatch(new ConfigCheckResultEvent()
                {
                    NewConfig = newconfig,
                    configHash = ConstantService.instance.hashConfigFilesZip,
                    Destination = this.Destination
                });
                //NetworkingService.instance.Send(this.SocketSource, .GetNetworkPacket());
            }
            if (GlobalProgramState.instance.ProgramType == GlobalProgramState.ProgramTypeEnum.Client)
            {
                ///NetworkingService.instance.Send(NetworkingService.instance.ClientSocket, this.GetNetworkPacket());
            }
        }
    }
}
