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

namespace AECC.Collections
{
    public class TupleList<T1, T2> : List<Tuple<T1, T2>> where T1 : IComparable
    {
        public void Add(T1 item, T2 item2)
        {
            Add(new Tuple<T1, T2>(item, item2));
        }

        public new void Sort()
        {
            Comparison<Tuple<T1, T2>> c = (a, b) => a.Item1.CompareTo(b.Item1);
            base.Sort(c);
        }
        public void ReverseSort()
        {
            Comparison<Tuple<T1, T2>> c = (a, b) => b.Item1.CompareTo(a.Item1);
            base.Sort(c);
        }

    }
}