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
    public class ConcurrentDictionaryEx<TKey, TValue> : ConcurrentDictionary<TKey, TValue>
    {
        public int FastCount;

        public IList<TKey> IKeys = new List<TKey>();
        public IList<TValue> IValues = new List<TValue>();


        public ConcurrentDictionaryEx() : base()
        {

        }

        public ConcurrentDictionaryEx(ConcurrentDictionary<TKey, TValue> keyValuePairs) : base()
        {
            foreach(var keyval in keyValuePairs)
            {
                this.TryAdd(keyval.Key, keyval.Value);
            }
        }

        public ConcurrentDictionaryEx<TKey, TValue> Upd()
        {
            for (int i = 0; i < IKeys.Count; i++)
            {
                this.TryAdd(IKeys[i], IValues[i]);
            }
            return this;
        }

        public ConcurrentDictionaryEx(IDictionary<TKey, TValue> dictionary)
        {
            foreach(var row in dictionary)
            {
                this.TryAdd(row.Key, row.Value);
            }
        }
    }
}