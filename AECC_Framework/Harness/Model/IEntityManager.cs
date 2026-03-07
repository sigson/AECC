using AECC.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AECC.Harness.Model
{
public abstract
#if GODOT4_0_OR_GREATER
    partial
#endif
    class IEntityManager : IECSObjectManager<ECSEntity>
    {
        public ECSEntity ManagerEntity
        {
            get
            {
                return this.ManagerECSObject;
            }
            set
            {
                this.ManagerECSObject = value;
            }
        }

        public long ManagerEntityId => this.ManagerECSObjectId;
    }

}
