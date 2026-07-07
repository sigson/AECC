using System;
using System.Collections.Generic;
using AECC.Collections;
using AECC.Core.Logging;
using AECC.Core.BuiltInTypes.Types.AtomicType;
using AECC.Extensions.ThreadingSync;

namespace AECC.Core.Serialization
{
    /// <summary>
    /// Пер-объектная сериализационная ТЕНЬ (фаза 4, шаг 3; ТЗ 4.4/4.7, стратегия 3.4/3.7).
    /// Владеет трёхфазным автоматом инвалидации NoData → Changed → Freezed (идея 1.6 —
    /// дословно), счётчиком retry-десериализации и всей логикой
    /// Serialization/Deserialization/AfterDeserialization, выселенной из IECSObject.
    /// В модели остались только пользовательские хуки EnterToSerializationImpl /
    /// AfterSerializationImpl / AfterDeserializationImpl и SerialLocker (контракт с
    /// прикладным кодом, ТЗ 4.4).
    ///
    /// Хранение — OPAQUE-СЛОТ на объекте (IECSObject.serializationShadow типа object:
    /// модель хранит, не интерпретирует) — ДЕФОЛТ по ТЗ/анти-бомбе 7.4: внешняя таблица
    /// instanceId → shadow дала бы лукап + контенцию на каждом касании ChangesState.
    /// Слот [NonSerialized]: у свежедесериализованного инстанса тень создаётся заново —
    /// это ДОСЛОВНО прежняя семантика сброса ([NonSerialized] ChangesState → NoData
    /// (enum-default 0), deserializeErrorCount → 0).
    ///
    /// ГРАНИЦА ДАННЫХ (уточнение к передаточному отчёту, сверено с ТЗ 4.4/стратегией 3.4):
    /// HasChildChanges и childECSObjectsId — сериализуемые WIRE-поля протокола (получатель
    /// читает их из пришедшего инстанса; зеркало — половина «двойного представления»
    /// идеи 1.4, ориентир Model). Физически они остаются полями IECSObject как инертные
    /// ДАННЫЕ; вся их ИНТЕРПРЕТАЦИЯ — только здесь. Перенос их в [NonSerialized]-слот
    /// сломал бы восстановление дерева детей у любого field-based адаптера — эскалировано
    /// в журнале (§11.4).
    ///
    /// Транзитно (до физического выноса сборок, шаг 4.4): модель зовёт тень напрямую
    /// (прецедент шага 1 — EntitySerializationState); при выносе точка связи модели
    /// сводится к MarkChanged() за интерфейсом Abstractions («типизированный слот через
    /// интерфейс» — санкционировано стратегией 3.7), остальное дёргает только Serialization.
    /// </summary>
    public sealed class SerializationShadow
    {
        // ───── автомат инвалидации (идея 1.6; бывший [NonSerialized] IECSObject.ChangesState) ─────
        public IECSObject.IECSObjectSerializedStateMode ChangesState = IECSObject.IECSObjectSerializedStateMode.NoData;

        // ───── retry-счётчик событийной десериализации (бывший [NonSerialized] deserializeErrorCount) ─────
        public int DeserializeErrorCount = 0;

        /// <summary>
        /// Тень из opaque-слота объекта (создаёт при первом обращении). Горячие касания —
        /// одно чтение поля + as-cast; аллокация — однократно на инстанс (мандат 7.4:
        /// слот вместо таблицы).
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

        /// <summary>Мутация модели (дети/владелец) инвалидирует снапшот (идея 1.6).</summary>
        public void MarkChanged()
        {
            ChangesState = IECSObject.IECSObjectSerializedStateMode.Changed;
        }

        /// <summary>
        /// Снапшот-проход (бывший IECSObject.SerializationProcess — тело дословно):
        /// Freezed потребляется в NoData (+сброс флага); Changed материализует зеркало
        /// детей лениво в момент сериализации и замерзает с HasChildChanges = true.
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
        /// Восстановление дерева из зеркала (бывший IECSObject.DeserializationProcess —
        /// тело дословно, включая порядок «сначала добор, потом чистка лишних»).
        /// </summary>
        public bool RestorePass(IECSObject owner, bool retryGetECSObjects = false)
        {
            var newchildECSObjects = new DictionaryWrapper<long, IECSObject>();

            if (retryGetECSObjects)
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
        /// Пост-десериализация (бывший IECSObject.AfterDeserialization — тело дословно):
        /// профильная ветка (идеи 1.8/1.15), событийный retry через
        /// PendingDeserializationRegistry с register-then-recheck, cap 30 + dead-letter-лог.
        /// Порядок локов Drain vs SerialLocker — инвариант реестра, не менять.
        /// </summary>
        public void AfterRestore(IECSObject owner)
        {
            if (!owner.ECSWorldOwner.Profile.ClientRetryOnMissingRefs) // профиль вместо WorldType-ифа (идеи 1.8/1.15)
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
