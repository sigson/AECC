using System;
using System.Collections.Generic;
using AECC.Collections;
using AECC.Core.Logging;
using AECC.Core.BuiltInTypes.Types.AtomicType;
using AECC.Extensions.ThreadingSync;

namespace AECC.Core.Serialization
{
    /// <summary>
    /// Пер-объектная сериализационная ТЕНЬ. Владеет трёхфазным автоматом инвалидации
    /// NoData → Changed → Freezed, счётчиком retry-десериализации и всей логикой
    /// Serialization/Deserialization/AfterDeserialization для IECSObject.
    /// В модели остаются только пользовательские хуки EnterToSerializationImpl /
    /// AfterSerializationImpl / AfterDeserializationImpl и SerialLocker (контракт с
    /// прикладным кодом).
    ///
    /// Хранение — opaque-слот на объекте (IECSObject.serializationShadow типа object:
    /// модель хранит, не интерпретирует). Внешняя таблица instanceId → shadow дала бы
    /// лукап + контенцию на каждом касании ChangesState, поэтому используется слот.
    /// Слот [NonSerialized]: у свежедесериализованного инстанса тень создаётся заново
    /// (ChangesState → NoData (enum-default 0), DeserializeErrorCount → 0).
    ///
    /// ГРАНИЦА ДАННЫХ: HasChildChanges и childECSObjectsId — сериализуемые WIRE-поля
    /// протокола (получатель читает их из пришедшего инстанса; зеркало — половина
    /// «двойного представления»). Физически они остаются полями IECSObject как инертные
    /// данные; вся их интерпретация — только здесь. Перенос их в [NonSerialized]-слот
    /// сломал бы восстановление дерева детей у любого field-based адаптера.
    ///
    /// Модель зовёт тень напрямую (см. также EntitySerializationState).
    /// </summary>
    public sealed class SerializationShadow
    {
        // ───── автомат инвалидации ─────
        public IECSObject.IECSObjectSerializedStateMode ChangesState = IECSObject.IECSObjectSerializedStateMode.NoData;

        // ───── retry-счётчик событийной десериализации ─────
        public int DeserializeErrorCount = 0;

        /// <summary>
        /// Тень из opaque-слота объекта (создаёт при первом обращении). Горячие касания —
        /// одно чтение поля + as-cast; аллокация — однократно на инстанс.
        /// </summary>
        public static SerializationShadow Of(IECSObject obj)
        {
            var shadow = obj.serializationShadow as SerializationShadow;
            if (shadow == null)
            {
                shadow = new SerializationShadow();
                obj.serializationShadow = shadow;
            }
            return shadow;
        }

        /// <summary>Мутация модели (дети/владелец) инвалидирует снапшот.</summary>
        public void MarkChanged()
        {
            ChangesState = IECSObject.IECSObjectSerializedStateMode.Changed;
        }

        /// <summary>
        /// Снапшот-проход: Freezed потребляется в NoData (+сброс флага); Changed
        /// материализует зеркало детей лениво в момент сериализации и замерзает
        /// с HasChildChanges = true.
        /// </summary>
        public void SnapshotPass(IECSObject owner)
        {
            if (ChangesState == IECSObject.IECSObjectSerializedStateMode.Freezed)
            {
                ChangesState = IECSObject.IECSObjectSerializedStateMode.NoData;
                owner.HasChildChanges = false;
            }
            if (ChangesState == IECSObject.IECSObjectSerializedStateMode.Changed)
            {
                // Ленивое зеркало: материализуем именно здесь — в точке заполнения.
                if (owner.childECSObjectsId == null)
                    owner.childECSObjectsId = new Dictionary<long, IECSObjectPathContainer>();
                owner.childECSObjectsId.Clear();
                foreach (var childpair in owner.ChildrenForSerialization)
                {
                    owner.childECSObjectsId[childpair.Key] = new IECSObjectPathContainer(owner.ECSWorldOwnerId, true) { ECSObject = childpair.Value };
                }
                ChangesState = IECSObject.IECSObjectSerializedStateMode.Freezed;
                owner.HasChildChanges = true;
            }
        }

        /// <summary>
        /// Восстановление дерева из зеркала. Порядок фиксирован: сначала добор
        /// недостающих детей, затем чистка лишних.
        /// </summary>
        public bool RestorePass(IECSObject owner, bool retryGetECSObjects = false)
        {
            var newchildECSObjects = new DictionaryWrapper<long, IECSObject>();

            // Ленивое зеркало: null эквивалентно пустому словарю — нижний проход по
            // ChildrenForSerialization всё равно вычистит лишних детей.
            if (retryGetECSObjects && owner.childECSObjectsId != null)
            {
                foreach (var entry in owner.childECSObjectsId)
                {
                    if (owner.ContainsChildObject(entry.Key))
                        continue;

                    if (entry.Value.ECSObject == null)
                    {
                        return false;
                    }
                }
            }

            if (owner.childECSObjectsId != null)
            {
                foreach (var entry in owner.childECSObjectsId)
                {
                    if (owner.ContainsChildObject(entry.Key))
                        continue;

                    newchildECSObjects[entry.Key] = entry.Value.ECSObject;
                    if (entry.Value.ECSObject != null)
                    {
                        owner.AddChildObject(entry.Value.ECSObject);
                    }
                }
            }
            foreach (var entry in owner.ChildrenForSerialization)
            {
                if (!newchildECSObjects.ContainsKey(entry.Key))
                {
                    owner.RemoveChildObject(entry.Key);
                }
            }

            return true;
        }

        /// <summary>
        /// Пост-десериализация: профильная ветка с событийным retry через
        /// PendingDeserializationRegistry (register-then-recheck), cap 30 + dead-letter-лог.
        /// Порядок локов Drain vs SerialLocker — инвариант реестра, не менять.
        /// </summary>
        public void AfterRestore(IECSObject owner)
        {
            if (!owner.ECSWorldOwner.Profile.ClientRetryOnMissingRefs)
            {
                using (new SharedLock.Scope(owner.SerialLocker))
                {
                    if (owner.HasChildChanges)
                        RestorePass(owner);
                    owner.RunAfterDeserializationImpl();
                }
            }
            else
            {
                using (new SharedLock.Scope(owner.SerialLocker))
                {
                    bool deserres = true;
                    if (owner.HasChildChanges)
                        deserres = RestorePass(owner, true);
                    if (!deserres)
                    {
                        var registry = owner.ECSWorldOwner.entityManager.PendingDeserialization;
                        if (DeserializeErrorCount < 30)
                        {
                            DeserializeErrorCount++;
                            // Событийная замена ретрай-таймера: повторить при приходе недостающей сущности.
                            registry.Register(owner, () => owner.AfterDeserialization());
                            // register-then-recheck: сущность могла прийти между проверкой и регистрацией.
                            if (!RestorePass(owner, true))
                                return;
                        }
                        else
                        {
                            NLogger.Error("client: error deserialize");
                            return;
                        }
                    }
                    DeserializeErrorCount = 0;
                    owner.ECSWorldOwner.entityManager.PendingDeserialization.Unregister(owner);
                    owner.RunAfterDeserializationImpl();
                }
            }
        }
    }
}
