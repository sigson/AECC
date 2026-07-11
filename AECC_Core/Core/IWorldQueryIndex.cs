using System;
using System.Collections.Generic;

namespace AECC.Core
{
    /// <summary>
    /// Контракт индекса запросов, объявленный в Core: Runtime ПУБЛИКУЕТ события
    /// (EntityAdded / EntityRemoving→Removed вокруг переподчинения / ComponentAdded /
    /// ComponentRemoved), реализация живёт СВЕРХУ (сборка AECC.Query, EntityQueryIndex
    /// поверх герметизированного DefaultEcs). Дисциплина вызова — как у
    /// IComponentStoreListener: синхронный прямой вызов, параметры вместо args-объектов,
    /// без multicast/замыканий.
    ///
    /// Индекс — eventually-consistent относительно хранилища: корректность контрактов
    /// держится на локах и перепроверке в TryExecuteContract, не на свежести индекса.
    /// </summary>
    public interface IWorldQueryIndex
    {
        /// <summary>Приход сущности: аллокация узла (+реюз id), прописка у предков и корня,
        /// Comp-метрики наличного состава.</summary>
        void OnEntityAdded(ECSEntity entity);

        /// <summary>Снятие узла у предков ДО переподчинения детей: идёт по ЕЩЁ СТАРОЙ
        /// цепочке ownerECSObject — инвариант порядка. Возвращает, был ли у сущности узел
        /// (гейт всей последовательности удаления, включая reparent).</summary>
        bool OnEntityRemoving(ECSEntity entity);

        /// <summary>Финальный шаг удаления (ПОСЛЕ переподчинения): чистка маппингов и
        /// возврат graph-id в очередь реюза.</summary>
        void OnEntityRemoved(ECSEntity entity);

        void OnComponentAdded(ECSEntity entity, ECSComponent component);
        void OnComponentRemoved(ECSEntity entity, ECSComponent component);

        /// <summary>Поиск (world.Query.Search): AND-пересечение, AND NOT, scope-сужение
        /// до потомков, Flush-барьер, реюз id.</summary>
        IEnumerable<ECSEntity> Search(ECSEntity parentScope = null, Type[] withComponentTypes = null, Type[] withoutComponentTypes = null);

        /// <summary>Обратный индекс componentTypeId → владельцы — пересечение от меньшей
        /// кардинальности с ранним выходом.</summary>
        HashSet<ECSEntity> FilterEntitiesForComponents(List<long> components);

        /// <summary>Транзитная витрина обратного индекса для [Obsolete]-фасада
        /// ContractsManager.ComponentOwners (внутренние потребители контрактов).</summary>
        IDictionary<long, HashSet<ECSEntity>> ComponentOwnersView { get; }
    }
}
