using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace AECC.Core
{
    /// <summary>
    /// Профиль мира (ТЗ 4.5.6, идея 1.15): поведенческая разница Server/Client/Offline —
    /// неприкосновенная идея, но читается она В ОДНОМ МЕСТЕ. Флаги ВЫЧИСЛЯЮТСЯ ПРИ СОЗДАНИИ
    /// мира (замена россыпи `if (WorldType == ...)` по шести файлам); имена — по ПОВЕДЕНИЮ,
    /// а не по типу мира.
    ///
    /// Вырожденное тройное условие (ТЗ 2.3):
    ///   (!Cut) || (owner==null && !Cut) || (owner!=null && Kind!=Offline && !Cut)
    /// алгебраически равно `!Defines.CutClientServerCollections` — все world-type-ветки
    /// МЕРТВЫ. По мандату 4.5.6 оно вычисляется один раз в один bool
    /// (<see cref="MaintainsSerializationMirrors"/> / <see cref="EagerAccessPolicyCollections"/>);
    /// мёртвая семантика сознательно НЕ «чинится» — поведение сохраняется дословно.
    /// </summary>
    public sealed class WorldProfile
    {
        public readonly ECSWorld.WorldTypeEnum Kind;

        /// <summary>Вести сериализационные зеркала (RemovedComponents, fastEntityComponentsId;
        /// бывш. также SerializationContainer — удалён, оптимизация памяти). Бывш. вырожденное тройное условие == !Defines.Cut...</summary>
        public readonly bool MaintainsSerializationMirrors;

        /// <summary>Создавать коллекции политик доступа в конструкторе сущности.
        /// То же вырожденное условие (значение идентично Maintains...), имя — по потребителю.</summary>
        public readonly bool EagerAccessPolicyCollections;

        /// <summary>Клиентская десериализация: ссылки могут прийти позже → событийный retry
        /// (идея 1.8). false = серверная/оффлайн синхронная ветка.</summary>
        public readonly bool ClientRetryOnMissingRefs;

        /// <summary>Состояние lifecycle-диспетчера — identity-keyed в ECSSharedField
        /// (клиент: инстансы подменяются при UpdateDeserialize, идея 1.11);
        /// false — поле инстанса (сервер: инстансы стабильны, таблица была бы регрессией; ТЗ 4.5.2).</summary>
        public readonly bool IdentityKeyedLifecycleState;

        /// <summary>Path-контейнеры всегда перечитывают кэш (клиентский режим AlwaysUpdateCache, идея 1.5).</summary>
        public readonly bool AlwaysUpdatePathCache;

        /// <summary>Компонент с ownerDB маркирует изменения через DB-агрегатор (Server/Offline; идея 1.12).</summary>
        public readonly bool DbAuthoritativeChangeMarking;

        /// <summary>OnAdded по умолчанию помечает компонент изменённым (только Server).</summary>
        public readonly bool ServerMarksChangedOnAdd;

        /// <summary>Клиентские компонент-группы вместо серверных (ветвление конструктора ComponentManager).</summary>
        public readonly bool ClientComponentGroups;

        /// <summary>Интервал активного прохода time-depend контрактов (бывший hardcode 5 мс
        /// в InitWorldScope; ТЗ 4.5.7 — таймер уходит в Start()).</summary>
        public readonly int TimeDependContractsIntervalMs;

        /// <summary>Группа, ИСКЛЮЧАЕМАЯ из зачистки при UpdateDeserialize-фильтре
        /// (ТЗ 4.7: «клиент фильтрует Server-группу и наоборот» → профиль). Свойство
        /// ленивое: статические Id групп выставляются TypeUid-механизмом 1.14 позже
        /// создания профиля. Транзитно резолвится через BuiltIn-статики (одна сборка);
        /// при выносе AECC.BuiltIn (фаза 7) значение инжектится конфигурацией мира —
        /// Runtime/Serialization не должны ссылаться на BuiltIn (граф §3).</summary>
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
            bool cut = Defines.CutClientServerCollections; // захват при создании мира (4.5.6)
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
        /// Профильное чтение вырожденного условия для объектов, у которых мира может не быть:
        /// с миром — захваченный при создании мира bool; без мира — живое чтение Defines
        /// (в точности прежняя семантика null-owner-ветки).
        /// </summary>
        public static bool SerializationCollections(ECSWorld world)
        {
            return world != null ? world.Profile.MaintainsSerializationMirrors : !Defines.CutClientServerCollections;
        }
    }

    /// <summary>
    /// Инстансный реестр миров (ТЗ 4.5.5): long → ECSWorld вместо статических Func
    /// `ECSWorld.Get*`. Мир регистрируется в Configure/InitWorldScope и уходит в Dispose.
    /// Статические Func-фасады со старыми сигнатурами остаются на переходный период
    /// ([Obsolete], их дефолтные реализации ходят сюда); точка переопределения
    /// <c>ECSWorld.GetWorld</c> сохраняется для интеграций и тестов.
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
