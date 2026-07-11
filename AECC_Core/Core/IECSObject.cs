using AECC.Core.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Collections.Concurrent;
using AECC.Extensions;
using System.IO;
using System.Threading.Tasks;
using AECC.Extensions.ThreadingSync;
using System.Runtime.Serialization;
using AECC.Collections;
using AECC.Locking;
using AECC.Core.Serialization;
using AECC.Core.BuiltInTypes.Types.AtomicType;

namespace AECC.Core
{
    [System.Serializable]
    [TypeUid(0)]
    public class IECSObject : IDObject
    {
        static public new long Id { get; set; } = 0;

        [System.NonSerialized]
        [IgnoreDataMember] // P1: без этого MakeReverseShallowCopy не переинициализирует локер
        public object SerialLocker = new object();

        /// <summary>Opaque-слот пер-объектной сериализационной тени — модель ХРАНИТ, не
        /// интерпретирует (внутри живёт AECC.Core.Serialization.SerializationShadow: автомат
        /// NoData/Changed/Freezed + retry-счётчик + логика Snapshot/Restore/AfterRestore).
        /// [NonSerialized]: у десериализованного инстанса тень пересоздаётся с нуля
        /// (ChangesState → NoData, deserializeErrorCount → 0).</summary>
        [System.NonSerialized]
        [IgnoreDataMember]
        public object serializationShadow;

        // Форвардинг в тень (владение у Serialization; внешние касания работают без изменений).
        [IgnoreDataMember]
        public IECSObjectSerializedStateMode ChangesState
        {
            get { return SerializationShadow.Of(this).ChangesState; }
            set { SerializationShadow.Of(this).ChangesState = value; }
        }

        // WIRE-поле протокола: «отправитель материализовал свежее зеркало детей».
        // Остаётся сериализуемыми ДАННЫМИ модели (получатель читает его из пришедшего
        // инстанса); интерпретация — только в SerializationShadow.
        public bool HasChildChanges = true; //after creation = yes
        public long ownerECSObjectId;
        public bool ChildDispose = false; //for db component may be true
        public bool RebaseChildren = true;

        [System.NonSerialized]
        private IECSObject ownerECSObjectStorage = null;
        public IECSObject ownerECSObject {
            get{
                return ownerECSObjectStorage;
            }
            set{
                ownerECSObjectStorage = value;
                // P4: value == null — легальный сценарий (отвязка ребёнка от удаляемого
                // родителя: RemoveChildObject / ReparentChildrenUpwards). Раньше здесь был NRE.
                ownerECSObjectId = value != null ? value.instanceId : 0;
                OnUpdateOwner(value);
                ChangesState = IECSObjectSerializedStateMode.Changed;
            }
        }

        /// <summary>
        /// serialization container where dictionary key is child ECSObject instanceId and value is array of id path with types to real IECSObject, example idlong;cmp / idlong;ent where cmp - component, ent - entity
        /// </summary>
        // Зеркало детей сериализации ленивое: поле остаётся полем с тем же именем (набор
        // сериализуемых членов не меняется, null round-trip'ится как null), но материализуется
        // только пайплайном сериализации (SnapshotPass); все прочие обращения null-guard'нуты.
        // Так листовые объекты (например компоненты), у которых зеркало не используется,
        // не аллоцируют для него словарь.
        public Dictionary<long, IECSObjectPathContainer> childECSObjectsId = null;
        [System.NonSerialized]
        private LockedDictionarySlim<long, IECSObject> storagechildECSObjects;
        private LockedDictionarySlim<long, IECSObject> childECSObjects
        {
            get
            {
                if (storagechildECSObjects == null)
                {
                    // World-level child tree on LockedDictionarySlim, HoldKeys OFF
                    // (children are never reserved by absence).
                    storagechildECSObjects = new LockedDictionarySlim<long, IECSObject>();
                }
                return storagechildECSObjects;
            }
            set
            {
                storagechildECSObjects = value;
            }
        }
        
        /// <summary>Внутренний доступ пайплайна сериализации к живому словарю детей
        /// (материализация зеркала в SnapshotPass, чистка лишних в RestorePass).</summary>
        internal LockedDictionarySlim<long, IECSObject> ChildrenForSerialization
        {
            get { return childECSObjects; }
        }

        /// <summary>P5: живое дерево детей БЕЗ материализации пустого словаря
        /// (null == детей не было). Нужно графовым операциям удаления.</summary>
        internal LockedDictionarySlim<long, IECSObject> ChildrenLiveOrNull
        {
            get { return storagechildECSObjects; }
        }

        /// <summary>Мост тени к пользовательскому хуку (хук остаётся protected-API модели,
        /// вызывается сериализацией через shadow).</summary>
        internal void RunAfterDeserializationImpl()
        {
            AfterDeserializationImpl();
        }

        protected virtual void OnUpdateOwner(IECSObject newOwner)
        {
            
        }

        private bool CompareChildsWithNew(Dictionary<long, List<string>> dict1, Dictionary<long, List<string>> dict2)
        {
            if (dict1.Count != dict2.Count)
                return false;

            foreach (var key in dict1.Keys)
            {
                if (!dict2.ContainsKey(key))
                    return false;

                var list1 = dict1[key];
                var list2 = dict2[key];

                if (list1.Count != list2.Count)
                    return false;

                for (int i = 0; i < list1.Count; i++)
                {
                    if (list1[i] != list2[i])
                        return false;
                }
            }

            return true;
        }

        public void AddChildObject(IECSObject value, bool updateOwner = true)
        {
            bool isChanged = false;
            childECSObjects.ExecuteOnAddLocked(value.instanceId, value, (key, component) =>
            {
                childECSObjects[value.instanceId] = value;
                isChanged = true;
                ChangesState = IECSObjectSerializedStateMode.Changed;
            });
            if (isChanged)
            {
                if (updateOwner)
                    value.ownerECSObject = this;
                OnAddChildObject(value);
            }
            else
                NLogger.Error($"IECSObject '{instanceId}: {this.GetType().Name}': childECSObjects.ContainsKey({value.instanceId} - {value.GetType().Name})");
        }

        protected virtual void OnAddChildObject(IECSObject value)
        {
            
        }

        public bool RemoveChildObject(long key, bool updateOwner = true)
        {
            IECSObject removed = null;
            var result = childECSObjects.Remove(key, out removed);
            if(!result)
            {
                NLogger.Error($"IECSObject '{instanceId}: {this.GetType().Name}': childECSObjects.TryRemove({key})");
            }
            else
            {
                if(removed != null)
                {
                    if(updateOwner)
                        removed.ownerECSObject = null;
                    OnRemoveChildObject(removed);
                }
                ChangesState = IECSObjectSerializedStateMode.Changed;
            }
            return result;
        }

        protected virtual void OnRemoveChildObject(IECSObject value)
        {
            
        }

        public bool ContainsChildObject(long key)
        {
            return childECSObjects.ContainsKey(key);
        }

        public void ClearChildObjects()
        {
            childECSObjects.Clear();
        }

        public bool TryGetChildObject(long key, out IECSObject value)
        {
            return childECSObjects.TryGetValue(key, out value);
        }

        public bool TryGetChildObjectReadLocked(long key, out IECSObject value, out RWToken lockToken)
        {
            return childECSObjects.TryGetLockedElement(key, out value, out lockToken);
        }

        public bool TryGetChildObjectWriteLocked(long key, out IECSObject value, out RWToken lockToken)
        {
            return childECSObjects.TryGetLockedElement(key, out value, out lockToken, true);
        }

        public RWToken GetLockedStorage()
        {
            return childECSObjects.LockStorage();
        }

        public void IECSDispose()
        {
            // Нет ни одного ребёнка (ленивое дерево не материализовано) — обходить/чистить
            // нечего, и НЕ материализуем пустой storage только ради обхода. Для листовых
            // объектов (компонентов) это убирает лишнюю аллокацию LockedDictionarySlim на
            // каждом удалении.
            if (storagechildECSObjects == null) return;
            if(ChildDispose)
            {
                foreach (var childpair in childECSObjects)
                {
                    childpair.Value.ChainedIECSDispose();
                }
                ClearChildObjects();
            }
            else
            {
                if (RebaseChildren)
                {
                    foreach (var childpair in childECSObjects)
                    {
                        if (this.ownerECSObject != null)
                        {
                            childpair.Value.ownerECSObject = this.ownerECSObject;
                        }
                        else
                        {
                            NLogger.Error($"IECSObject '{instanceId}: {this.GetType().Name}': no has ownerECSObject");
                        }
                    }
                }
                ClearChildObjects();
            }
        }

        public virtual void ChainedIECSDispose()
        {
            
        }

        // Тела SerializationProcess/DeserializationProcess живут в
        // AECC.Core.Serialization.SerializationShadow (SnapshotPass/RestorePass) — эта модель
        // автомат инвалидации не интерпретирует.

        private void AfterDeserializationChildChanges()
        {
            
        }

        /// <summary>
        /// signalise IECSObject for starting process serialization
        /// </summary>
        public void EnterToSerialization()
        {
            SerializationShadow.Of(this).SnapshotPass(this);
            //lock(SerialLocker)
            {
                EnterToSerializationImpl();
            }
        }

        /// <summary>
        /// override this method for store property values to serializable fields
        /// </summary>
        protected virtual void EnterToSerializationImpl()
        {

        }

        public void AfterSerialization()
        {
            //lock(SerialLocker)
            {
                AfterSerializationImpl();
            }
        }

        protected virtual void AfterSerializationImpl()
        {

        }

        // deserializeErrorCount и вся пост-десериализация (профильная ветка, событийный
        // retry с register-then-recheck, cap+dead-letter) живут в SerializationShadow.AfterRestore.
        public void AfterDeserialization()
        {
            SerializationShadow.Of(this).AfterRestore(this);
        }
        protected virtual void AfterDeserializationImpl()
        {

        }

        public enum IECSObjectSerializedStateMode
        {
            NoData,
            Changed,
            Freezed
        }
    }
}
