using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using AECC.Core.Logging;
using AECC.Extensions;
using System.Collections.Concurrent;
using System.IO;
using AECC.Core.BuiltInTypes.ComponentsGroup;

namespace AECC.Core
{
    public class ECSComponentManager
    {
        public static ECSComponentGroup GlobalProgramComponentGroup;

        private ECSWorld world;
        public ECSComponentManager(ECSWorld world)
        {
            this.world = world;
            // Профиль вместо WorldType-ифа (идея 1.15); прежний else-if исчерпывал все три вида.
            if (this.world.Profile.ClientComponentGroups)
                GlobalProgramComponentGroup = new ClientComponentGroup();
            else
                GlobalProgramComponentGroup = new ServerComponentGroup();
        }
    }
}
