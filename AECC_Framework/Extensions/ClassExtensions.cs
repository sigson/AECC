using System;
using System.IO;
using AECC.Extensions.ThreadingSync;
using AECC.GameEngineAPI;
using AECC.Harness.Model;
using AECC.Harness.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AECC.Extensions
{
    public static class JsonUtil
    {
        public static string JsonPrettify(string json)
        {
            using (var stringReader = new StringReader(json))
            using (var stringWriter = new StringWriter())
            {
                var jsonReader = new JsonTextReader(stringReader);
                var jsonWriter = new JsonTextWriter(stringWriter) { Formatting = Formatting.Indented };
                jsonWriter.WriteToken(jsonReader);
                return stringWriter.ToString();
            }
        }

        public static T GetObjectByPath<T>(this JObject storage, string path, bool fixPath = true)
        {
            return storage.GetJTokenByPath(path, fixPath).ToObject<T>();
        }

        public static JToken GetJTokenByPath(this JObject storage, string path, bool fixPath = true)
        {
            if (fixPath)
                path = path.Replace(GlobalProgramState.instance.PathAltSeparator, GlobalProgramState.instance.PathSeparator);

            var pathSplit = path.Split(GlobalProgramState.instance.PathSeparator[0]);
            var nowStorage = storage[pathSplit[0]];
            for (int i = 1; i < pathSplit.Length; i++)
            {
                if (!Lambda.TryExecute(() => nowStorage = nowStorage[pathSplit[i]]))
                    if (!Lambda.TryExecute(() => nowStorage = nowStorage[int.Parse(pathSplit[i])]))
                        throw new Exception("Wrong JObject iterator");
            }
            return nowStorage;
        }
    }

    public static class ClassEx2
    {
        public static ConfigObj GetConfig(this string str)
        {
            return ConstantService.instance.GetByConfigPath(str);
        }
    }

    #if UNITY_5_3_OR_NEWER

    public static class ManagerSpace
    {
        #region Instantiate
        public static UnityEngine.Object InstantiatedProcess(UnityEngine.Object instantiated, IEntityManager entityManagerOwner = null)
        {
            if (entityManagerOwner != null)
            {
                if (instantiated is UnityEngine.GameObject)
                {
                    (instantiated as UnityEngine.GameObject).GetComponentsInChildren<IManagable>().ForEach(x => (x as IManagable).ownerManagerSpace = entityManagerOwner);
                }
            }
            return instantiated;
        }

        public static T Instantiate<T>(T original, UnityEngine.Transform parent, IEntityManager entityManagerOwner = null) where T : UnityEngine.Object
        {
            var instantiated = UnityEngine.Object.Instantiate<T>(original, parent);
            return (T)InstantiatedProcess(instantiated, entityManagerOwner);
        }
        public static UnityEngine.Object Instantiate(UnityEngine.Object original, UnityEngine.Vector3 position, UnityEngine.Quaternion rotation, IEntityManager entityManagerOwner = null)
        {
            var instantiated = UnityEngine.Object.Instantiate(original, position, rotation);
            return InstantiatedProcess(instantiated, entityManagerOwner);
        }

        public static T Instantiate<T>(T original, UnityEngine.Transform parent, bool worldPositionStays, IEntityManager entityManagerOwner = null) where T : UnityEngine.Object
        {
            var instantiated = UnityEngine.Object.Instantiate<T>(original, parent, worldPositionStays);
            return (T)InstantiatedProcess(instantiated, entityManagerOwner);
        }

        public static UnityEngine.Object Instantiate(UnityEngine.Object original, IEntityManager entityManagerOwner = null)
        {
            var instantiated = UnityEngine.Object.Instantiate(original);
            return InstantiatedProcess(instantiated, entityManagerOwner);
        }

        public static UnityEngine.Object Instantiate(UnityEngine.Object original, UnityEngine.Vector3 position, UnityEngine.Quaternion rotation, UnityEngine.Transform parent, IEntityManager entityManagerOwner = null)
        {
            var instantiated = UnityEngine.Object.Instantiate(original, position, rotation, parent);
            return InstantiatedProcess(instantiated, entityManagerOwner);
        }

        public static UnityEngine.Object Instantiate(UnityEngine.Object original, UnityEngine.Transform parent, bool instantiateInWorldSpace, IEntityManager entityManagerOwner = null)
        {
            var instantiated = UnityEngine.Object.Instantiate(original, parent, instantiateInWorldSpace);
            return InstantiatedProcess(instantiated, entityManagerOwner);
        }

        public static T Instantiate<T>(T original, IEntityManager entityManagerOwner = null) where T : UnityEngine.Object
        {
            var instantiated = UnityEngine.Object.Instantiate<T>(original);
            return (T)InstantiatedProcess(instantiated, entityManagerOwner);
        }

        public static T Instantiate<T>(T original, UnityEngine.Vector3 position, UnityEngine.Quaternion rotation, IEntityManager entityManagerOwner = null) where T : UnityEngine.Object
        {
            var instantiated = UnityEngine.Object.Instantiate<T>(original, position, rotation);
            return (T)InstantiatedProcess(instantiated, entityManagerOwner);
        }

        public static T Instantiate<T>(T original, UnityEngine.Vector3 position, UnityEngine.Quaternion rotation, UnityEngine.Transform parent, IEntityManager entityManagerOwner = null) where T : UnityEngine.Object
        {
            var instantiated = UnityEngine.Object.Instantiate<T>(original, position, rotation, parent);
            return (T)InstantiatedProcess(instantiated, entityManagerOwner);
        }

        public static UnityEngine.Object Instantiate(UnityEngine.Object original, UnityEngine.Transform parent, IEntityManager entityManagerOwner = null)
        {
            var instantiated = UnityEngine.Object.Instantiate(original, parent);
            return InstantiatedProcess(instantiated, entityManagerOwner);
        }
        #endregion
        #region Component

        private static UnityEngine.Component ComponentProcess(UnityEngine.Component component, IEntityManager entityManagerOwner)
        {
            if (component is IManagable && entityManagerOwner != null)
                (component as IManagable).ownerManagerSpace = entityManagerOwner;
            return component;
        }

        public static UnityEngine.Component AddComponent<T>(this UnityEngine.GameObject gameObject, IEntityManager entityManagerOwner) where T : UnityEngine.Component
        {
            return (T)ComponentProcess(gameObject.AddComponent<T>(), entityManagerOwner);
        }
        public static UnityEngine.Component AddComponent(this UnityEngine.GameObject gameObject, Type typeComponent, IEntityManager entityManagerOwner)
        {
            return ComponentProcess(gameObject.AddComponent(typeComponent), entityManagerOwner);
        }
        #endregion
    }
    
#else
    // public static class ManagerSpace
    // {
    //     #region Instantiate
    //     public static EngineApiObjectBehaviour InstantiatedProcess(EngineApiObjectBehaviour instantiated, IEntityManager entityManagerOwner = null)
    //     {
    //         if (entityManagerOwner != null)
    //         {
    //             if (instantiated is EngineApiObjectBehaviour)
    //             {
    //                 (instantiated as EngineApiObjectBehaviour).GetComponentsInChildren<IManagable>().ForEach(x => (x as IManagable).ownerManagerSpace = entityManagerOwner);
    //             }
    //         }
    //         return instantiated;
    //     }
    //     #endregion
    // }
#endif
}