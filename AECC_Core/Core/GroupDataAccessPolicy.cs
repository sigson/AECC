using System.Reflection;
using AECC.Core.Logging;
using System.Collections.Concurrent;
using AECC.Extensions;
using AECC.Core.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace AECC.Core
{
    [System.Serializable]
    [TypeUid(17)]
    public class GroupDataAccessPolicy : IDObject, ICloneable
    {
        static public new long Id { get; set; } = 17;
        public List<long> AvailableComponents = new List<long>();
        public List<long> RestrictedComponents = new List<long>();
        public Dictionary<long, byte[]> BinAvailableComponents = new Dictionary<long, byte[]>();
        public Dictionary<long, byte[]> BinRestrictedComponents = new Dictionary<long, byte[]>();
        public bool IncludeRemovedAvailable = false;
        public bool IncludeRemovedRestricted = false;

        private static void MergeMissing(Dictionary<long, byte[]> dst, Dictionary<long, byte[]> src)
        {
            foreach (var kv in src)
                if (!dst.ContainsKey(kv.Key))
                    dst.Add(kv.Key, kv.Value);
        }

        /// <summary>
        /// O(N+M) фильтр по политикам доступа. Возвращает только бинарный набор компонентов.
        /// includeRemoved == true сигнализирует бывший кейс "#INCLUDEREMOVED#"
        /// (результирующий набор пуст, но политика требует включать удалённые).
        /// </summary>
        public static Dictionary<long, byte[]> ComponentsFilter(ECSEntity baseEntity, ECSEntity otherEntity, out bool includeRemoved)
        {
            var binFiltered = new Dictionary<long, byte[]>();
            bool includeRemovedAvailable = false;
            bool includeRemovedRestricted = false;
            includeRemoved = false;

            if (!otherEntity.emptySerialized)
            {
                // Предпостроенный индекс политик other: по instanceId (Available) и по typeId (Restricted).
                var otherByInstance = new Dictionary<long, GroupDataAccessPolicy>(otherEntity.dataAccessPolicies.Count);
                var otherByType = new Dictionary<long, List<GroupDataAccessPolicy>>();
                for (int i = 0; i < otherEntity.dataAccessPolicies.Count; i++)
                {
                    var p = otherEntity.dataAccessPolicies[i];
                    if (!otherByInstance.ContainsKey(p.instanceId))
                        otherByInstance.Add(p.instanceId, p);
                    long tid = p.GetId();
                    List<GroupDataAccessPolicy> bucket;
                    if (!otherByType.TryGetValue(tid, out bucket))
                    {
                        bucket = new List<GroupDataAccessPolicy>();
                        otherByType.Add(tid, bucket);
                    }
                    bucket.Add(p);
                }

                for (int i = 0; i < baseEntity.dataAccessPolicies.Count; i++)
                {
                    var baseDataAP = baseEntity.dataAccessPolicies[i];

                    GroupDataAccessPolicy instMatch;
                    if (otherByInstance.TryGetValue(baseDataAP.instanceId, out instMatch))
                    {
                        MergeMissing(binFiltered, instMatch.BinAvailableComponents);
                        if (instMatch.IncludeRemovedAvailable)
                            includeRemovedAvailable = true;
                    }

                    List<GroupDataAccessPolicy> typeMatches;
                    if (otherByType.TryGetValue(baseDataAP.GetId(), out typeMatches))
                    {
                        for (int b = 0; b < typeMatches.Count; b++)
                        {
                            var otherDataAP = typeMatches[b];
                            if (otherDataAP.instanceId == baseDataAP.instanceId)
                                continue; // уже учтён как Available (ветка instanceId)
                            MergeMissing(binFiltered, otherDataAP.BinRestrictedComponents);
                            if (otherDataAP.IncludeRemovedRestricted)
                                includeRemovedRestricted = true;
                        }
                    }
                }
            }

            includeRemoved = (binFiltered.Count == 0) && (includeRemovedAvailable || includeRemovedRestricted);
            return binFiltered;
        }

        public static List<long> RawComponentsFilter(ECSEntity baseEntity, ECSEntity otherEntity)
        {
            List<long> filteredComponents = new List<long>();
            for (int i = 0; i < baseEntity.dataAccessPolicies.Count; i++)
            {
                var baseDataAP = baseEntity.dataAccessPolicies[i];
                for (int i2 = 0; i2 < otherEntity.dataAccessPolicies.Count; i2++)
                {
                    var otherDataAP = otherEntity.dataAccessPolicies[i2];
                    if (baseDataAP.instanceId == otherDataAP.instanceId)
                    {
                        filteredComponents.AddRange(otherDataAP.AvailableComponents);
                    }
                    else if (baseDataAP.GetId() == otherDataAP.GetId())
                    {
                        filteredComponents.AddRange(otherDataAP.RestrictedComponents);
                    }
                }
            }
            return filteredComponents;
        }

        public object Clone()
        {
            var cloned =  MemberwiseClone() as GroupDataAccessPolicy;
            cloned.AvailableComponents = new List<long>(AvailableComponents);
            cloned.RestrictedComponents = new List<long>(RestrictedComponents);
            cloned.BinAvailableComponents = new Dictionary<long, byte[]>();
            cloned.BinRestrictedComponents = new Dictionary<long, byte[]>();
            return cloned;
        }
    }

}
