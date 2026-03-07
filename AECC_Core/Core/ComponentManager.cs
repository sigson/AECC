
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using AECC.Core.Logging;
using AECC.Extensions;
using System.Collections.Concurrent;
using AECC.Extensions;
using AECC.Core.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using AECC.Core.BuiltInTypes.ComponentsGroup;

namespace AECC.Core
{
    public class ECSComponentManager
    {
        public static Dictionary<long, List<Action<ECSEntity, ECSComponent>>> OnChangeCallbacksDB = new Dictionary<long, List<Action<ECSEntity, ECSComponent>>>();

        public static ECSComponentGroup GlobalProgramComponentGroup;

        private ECSWorld world;
        public ECSComponentManager(ECSWorld world)
        {
            this.world = world;
            if (this.world.WorldType == ECSWorld.WorldTypeEnum.Client)
                GlobalProgramComponentGroup = new ClientComponentGroup();
            else if(this.world.WorldType == ECSWorld.WorldTypeEnum.Server || this.world.WorldType == ECSWorld.WorldTypeEnum.Offline )
                GlobalProgramComponentGroup = new ServerComponentGroup();
        }
    }
}
