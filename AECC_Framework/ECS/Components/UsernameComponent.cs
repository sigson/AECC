using AECC.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AECC.ECS.DefaultObjects.ECSComponents
{
    [System.Serializable]
    [TypeUid(24)]
    public class UsernameComponent : ECSComponent
    {
        static public new long Id { get; set; }
        static public new System.Collections.Generic.List<System.Action> StaticOnChangeHandlers { get; set; }
        public string Username = "";
    }
}
