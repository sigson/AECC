using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace AECC.Core
{
    /// <summary>
    /// Профиль мира: поведенческая разница Server/Client/Offline читается в одном месте.
    /// Флаги вычисляются при создании мира; имена — по поведению, а не по типу мира.
    /// </summary>
    public sealed class WorldProfile
    {
        public readonly ECSWorld.WorldTypeEnum Kind;

        /// <summary>Вести сериализационные зеркала (RemovedComponents, fastEntityComponentsId).</summary>
        public readonly bool MaintainsSerializationMirrors;

        /// <summary>Создавать коллекции политик доступа в конструкторе сущности.
        /// Значение идентично MaintainsSerializationMirrors, имя — по потребителю.</summary>
        public readonly bool EagerAccessPolicyCollections;

        /// <summary>Клиентская десериализация: ссылки могут прийти позже → событийный retry.
        /// false = серверная/оффлайн синхронная ветка.</summary>
        public readonly bool ClientRetryOnMissingRefs;

        /// <summary>Состояние lifecycle-диспетчера — identity-keyed в ECSSharedField
        /// (клиент: инстансы подменяются при UpdateDeserialize);
        /// false — поле инстанса (сервер: инстансы стабильны, таблица была бы регрессией).</summary>
        public readonly bool IdentityKeyedLifecycleState;

        /// <summary>Path-контейнеры всегда перечитывают кэш (клиентский режим AlwaysUpdateCache).</summary>
        public readonly bool AlwaysUpdatePathCache;

        /// <summary>Компонент с ownerDB маркирует изменения через DB-агрегатор (Server/Offline).</summary>
        public readonly bool DbAuthoritativeChangeMarking;

        /// <summary>OnAdded по умолчанию помечает компонент изменённым (только Server).</summary>
        public readonly bool ServerMarksChangedOnAdd;

        /// <summary>Клиентские компонент-группы вместо серверных (ветвление конструктора ComponentManager).</summary>
        public readonly bool ClientComponentGroups;

        /// <summary>Интервал активного прохода time-depend контрактов.</summary>
        public readonly int TimeDependContractsIntervalMs;

        /// <summary>Группа, исключаемая из зачистки при UpdateDeserialize-фильтре
        /// (клиент фильтрует Server-группу и наоборот). Свойство ленивое: статические Id
        /// групп выставляются TypeUid-механизмом позже создания профиля. Резолвится через
        /// BuiltIn-статики; Runtime/Serialization не должны ссылаться на BuiltIn напрямую.</summary>
        public long RestoreFilterForeignGroupId
        {
            get
            {
                return ClientRetryOnMissingRefs
                    ? AECC.Core.BuiltInTypes.ComponentsGroup.ServerComponentGroup.Id
                    : AECC.Core.BuiltInTypes.ComponentsGroup.ClientComponentGroup.Id;
            }
        }

        public WorldProfile(ECSWorld.WorldTypeEnum kind, int timeDependContractsIntervalMs = 5)
        {
            Kind = kind;
            bool cut = Defines.CutClientServerCollections; // захват при создании мира
            MaintainsSerializationMirrors = !cut;
            EagerAccessPolicyCollections = !cut;
            ClientRetryOnMissingRefs = kind == ECSWorld.WorldTypeEnum.Client;
            IdentityKeyedLifecycleState = kind == ECSWorld.WorldTypeEnum.Client;
            AlwaysUpdatePathCache = kind == ECSWorld.WorldTypeEnum.Client;
            ClientComponentGroups = kind == ECSWorld.WorldTypeEnum.Client;
            DbAuthoritativeChangeMarking = kind == ECSWorld.WorldTypeEnum.Server || kind == ECSWorld.WorldTypeEnum.Offline;
            ServerMarksChangedOnAdd = kind == ECSWorld.WorldTypeEnum.Server;
            TimeDependContractsIntervalMs = timeDependContractsIntervalMs;
        }

        /// <summary>
        /// Профильное чтение для объектов, у которых мира может не быть: с миром —
        /// захваченный при создании мира bool; без мира — живое чтение Defines.
        /// </summary>
        public static bool SerializationCollections(ECSWorld world)
        {
            return world != null ? world.Profile.MaintainsSerializationMirrors : !Defines.CutClientServerCollections;
        }
    }

    /// <summary>
    /// Инстансный реестр миров: long → ECSWorld вместо статических Func `ECSWorld.Get*`.
    /// Мир регистрируется в Configure/InitWorldScope и уходит в Dispose.
    /// Статические Func-фасады со старыми сигнатурами остаются как [Obsolete] и делегируют
    /// сюда; точка переопределения <c>ECSWorld.GetWorld</c> сохраняется для интеграций и тестов.
    /// </summary>
    public sealed class WorldRegistry
    {
        /// <summary>Процессный реестр по умолчанию (Configure может получить свой — для изоляции тестов).</summary>
        public static readonly WorldRegistry Default = new WorldRegistry();

        private readonly ConcurrentDictionary<long, ECSWorld> _worlds = new ConcurrentDictionary<long, ECSWorld>();

        public void Register(ECSWorld world)
        {
            if (world == null) return;
            _worlds[world.instanceId] = world;
        }

        public void Unregister(long instanceId)
        {
            ECSWorld _;
            _worlds.TryRemove(instanceId, out _);
        }

        public bool TryGet(long instanceId, out ECSWorld world) { return _worlds.TryGetValue(instanceId, out world); }

        public IDictionary<long, ECSWorld> All() { return new Dictionary<long, ECSWorld>(_worlds); }
    }
}
