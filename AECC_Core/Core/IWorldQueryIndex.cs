using System;
using System.Collections.Generic;

namespace AECC.Core
{
    /// <summary>
    /// ФАЗА 5 (ТЗ 4.6, идея 1.10): контракт индекса запросов, объявленный в Core —
    /// Runtime ПУБЛИКУЕТ события (EntityAdded / EntityRemoving→Removed вокруг
    /// переподчинения / ComponentAdded / ComponentRemoved), реализация живёт СВЕРХУ
    /// (сборка AECC.Query, EntityQueryIndex поверх герметизированного DefaultEcs) —
    /// проверенный паттерн направления фазы 4 (Core-интерфейс, имплементор выше).
    /// Дисциплина вызова — как у IComponentStoreListener: синхронный прямой вызов,
    /// параметры вместо args-объектов, без multicast/замыканий (мандат 4.5.1).
    ///
    /// Индекс — eventually-consistent относительно хранилища (честная оговорка ТЗ 4.6):
    /// корректность контрактов держится на локах и перепроверке в TryExecuteContract,
    /// не на свежести индекса.
    /// </summary>
    public interface IWorldQueryIndex
    {
        /// <summary>Приход сущности: аллокация узла (+реюз id), прописка у предков и корня,
        /// Comp-метрики наличного состава. Бывш. графовая часть AddNewEntityReaction.</summary>
        void OnEntityAdded(ECSEntity entity);

        /// <summary>Шаги 1–2 бывшего InternalGraphRemoval (ДО переподчинения детей:
        /// снятие узла у предков идёт по ЕЩЁ СТАРОЙ цепочке ownerECSObject — инвариант
        /// порядка, анти-бомба 7.9). Возвращает, был ли у сущности узел (гейт всей
        /// последовательности удаления, включая reparent, — дословно прежний if).</summary>
        bool OnEntityRemoving(ECSEntity entity);

        /// <summary>Шаг 4 бывшего InternalGraphRemoval (ПОСЛЕ переподчинения): чистка
        /// маппингов и возврат graph-id в очередь реюза.</summary>
        void OnEntityRemoved(ECSEntity entity);

        void OnComponentAdded(ECSEntity entity, ECSComponent component);
        void OnComponentRemoved(ECSEntity entity, ECSComponent component);

        /// <summary>Поиск (breaking ТЗ 4.6: world.Query.Search). Семантика — сетка 9(з):
        /// AND-пересечение, AND NOT, scope-сужение до потомков, Flush-барьер, реюз id.</summary>
        IEnumerable<ECSEntity> Search(ECSEntity parentScope = null, Type[] withComponentTypes = null, Type[] withoutComponentTypes = null);

        /// <summary>Слияние 1.10 (остаток фазы 5): обратный индекс componentTypeId →
        /// владельцы — пересечение от меньшей кардинальности с ранним выходом.</summary>
        HashSet<ECSEntity> FilterEntitiesForComponents(List<long> components);

        /// <summary>Транзитная витрина обратного индекса для [Obsolete]-фасада
        /// ContractsManager.ComponentOwners (внутренние потребители контрактов).</summary>
        IDictionary<long, HashSet<ECSEntity>> ComponentOwnersView { get; }
    }
}
