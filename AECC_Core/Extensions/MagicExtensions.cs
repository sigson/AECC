using AECC.Core;
using AECC.Core.Logging;
using AECC.Extensions.ThreadingSync;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AECC.Extensions
{

    public static class MagicExtensions
    {
        public static string PathSystemSeparator(this object nullobject)
        {
            #if GODOT
                return "/";
            #else
                return Path.DirectorySeparatorChar.ToString();
            #endif
        }
    }
}
