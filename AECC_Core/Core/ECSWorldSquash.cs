using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AECC.Collections;
using AECC.Core.Logging;
using AECC.Extensions;
using AECC.Extensions.ThreadingSync;

namespace AECC.Core
{
    /// <summary>
    /// Расширение ECSWorld для операции сквоша миров.
    /// ВАЖНО: В основном файле ECSWorld.cs необходимо добавить ключевое слово 'partial' к объявлению класса:
    ///   public partial class ECSWorld { ... }
    /// (в Godot-сборках это уже сделано через #if GODOT4_0_OR_GREATER)
    /// </summary>
    public partial class ECSWorld
    {
        /// <summary>
        /// Сквошит (объединяет) все сущности из нескольких исходных миров в один целевой мир.
        /// Обрабатывает сущности из обоих хранилищ: EntityStorage (sync) и EntityStorageAsync.
        /// Исходные миры после операции превращаются в прозрачные прокси.
        /// </summary>
        public static void SquashWorlds(ECSWorld targetWorld, params ECSWorld[] sourceWorlds)
        {
            SquashWorlds(targetWorld, (IEnumerable<ECSWorld>)sourceWorlds);
        }

        /// <summary>
        /// Сквошит (объединяет) все сущности из нескольких исходных миров в один целевой мир.
        /// 
        /// Обрабатывает оба хранилища:
        /// - EntityStorage (sync, ILockedDictionary) — сущности, добавленные через AddNewEntity
        /// - EntityStorageAsync (LockedDictionaryAsync) — сущности, добавленные через AddNewEntityAsync
        /// 
        /// Порядок блокировок (MultiThread):
        /// 1. Target: EntityStorage → EntityStorageAsync
        /// 2. Source (по instanceId): EntityStorage → EntityStorageAsync
        /// 3. Component storage каждой сущности (sync и/или async, по entity instanceId)
        /// </summary>
        public static void SquashWorlds(ECSWorld targetWorld, IEnumerable<ECSWorld> sourceWorlds)
        {
            if (targetWorld == null)
                throw new ArgumentNullException(nameof(targetWorld));
            if (sourceWorlds == null)
                throw new ArgumentNullException(nameof(sourceWorlds));
            if (targetWorld.entityManager == null)
                throw new InvalidOperationException("Target world is not initialized (entityManager is null)");

            var worlds = sourceWorlds
                .Where(w => w != null && w.instanceId != targetWorld.instanceId && w.entityManager != null)
                .OrderBy(w => w.instanceId)
                .ToList();

            if (worlds.Count == 0)
                return;

            if (Defines.OneThreadMode)
            {
                SquashWorldsOneThread(targetWorld, worlds);
            }
            else
            {
                SquashWorldsMultiThread(targetWorld, worlds);
            }
        }

        // ============================================================================
        //  ОДНОПОТОЧНЫЙ РЕЖИМ
        // ============================================================================
        private static void SquashWorldsOneThread(ECSWorld targetWorld, List<ECSWorld> sourceWorlds)
        {
            var entitiesToRegister = new List<(ECSEntity entity, bool wasAsync)>();

            foreach (var sourceWorld in sourceWorlds)
            {
                // --- Sync EntityStorage ---
                var syncKeys = sourceWorld.entityManager.Repository.Keys.ToList();
                foreach (var entityId in syncKeys)
                {
                    if (!sourceWorld.entityManager.Repository.TryGetValue(entityId, out var entity))
                        continue;

                    sourceWorld.entityManager.Repository.UnsafeRemove(entityId, out _);

                    entity.ECSWorldOwner = targetWorld;
                    entity.manager = targetWorld.entityManager;
                    SquashUpdateComponentWorldReferences(entity, targetWorld);

                    if (!targetWorld.entityManager.Repository.UnsafeAdd(entityId, entity))
                    {
                        NLogger.Error($"SquashWorlds[OneThread]: не удалось добавить sync-сущность {entityId} ({entity.AliasName}) в целевой мир {targetWorld.instanceId}");
                        continue;
                    }

                    entitiesToRegister.Add((entity, false));
                }

                // Редирект даже в однопоточном режиме
                sourceWorld.entityManager.ActivateSquashRedirect(targetWorld.entityManager);
            }

            // Фаза регистрации (только sync — async-ветка удалена)
            foreach (var (entity, wasAsync) in entitiesToRegister)
            {
                targetWorld.entityManager.AddNewEntityReaction(entity);
            }
        }

        // ============================================================================
        //  МНОГОПОТОЧНЫЙ РЕЖИМ
        //
        //  Порядок блокировок (фиксированный, предотвращает deadlock):
        //    1. Target EntityStorage (sync)
        //    2. Target EntityStorageAsync
        //    3. Source EntityStorage (sync) — по возрастанию world.instanceId
        //    4. Source EntityStorageAsync — по возрастанию world.instanceId
        //    5. Component storage каждой сущности:
        //       - sync (GetWriteLockedComponentStorage) — если есть sync-компоненты
        //       - async (GetWriteLockedComponentStorageAsync) — если есть async-компоненты
        //       — по возрастанию entity.instanceId
        //
        //  Используем List<IDisposable> вместо List<RWLock.LockToken>,
        //  т.к. async-блокировки возвращают IDisposable, а не RWLock.LockToken.
        //  Для async-блокировок используем .Result / .AsTask().Result
        //  (аналогично паттерну isAsync => EntityStorageAsync.GetCountAsync().Result).
        // ============================================================================
        private static void SquashWorldsMultiThread(ECSWorld targetWorld, List<ECSWorld> sourceWorlds)
        {
            var allLockTokens = new List<IDisposable>();
            var entitiesToRegister = new List<(ECSEntity entity, bool wasAsync)>();

            try
            {
                // === ФАЗА 1: Блокировки хранилищ сущностей ===

                // 1.1. Target sync
                allLockTokens.Add(targetWorld.entityManager.Repository.LockStorage());

                // 1.2. Source sync (по порядку instanceId)
                foreach (var sourceWorld in sourceWorlds)
                {
                    allLockTokens.Add(sourceWorld.entityManager.Repository.LockStorage());
                }

                // === ФАЗА 2: Сбор сущностей + блокировка компонентных хранилищ ===

                var collectedEntities = new List<(ECSEntity entity, ECSWorld source, bool wasAsync)>();

                foreach (var sourceWorld in sourceWorlds)
                {
                    // --- Sync сущности ---
                    var syncKeys = sourceWorld.entityManager.Repository.Keys.ToList();
                    syncKeys.Sort();
                    foreach (var entityId in syncKeys)
                    {
                        if (!sourceWorld.entityManager.Repository.TryGetValue(entityId, out var entity))
                            continue;

                        LockEntityComponentStorages(entity, allLockTokens);
                        collectedEntities.Add((entity, sourceWorld, false));
                    }
                }

                // === ФАЗА 3: Атомарный перенос ===

                foreach (var (entity, sourceWorld, wasAsync) in collectedEntities)
                {
                    // 3.1. Удаляем из исходного хранилища
                    sourceWorld.entityManager.Repository.UnsafeRemove(entity.instanceId, out _);

                    // 3.2. Обновляем мировые ссылки
                    entity.ECSWorldOwner = targetWorld;
                    entity.manager = targetWorld.entityManager;
                    SquashUpdateComponentWorldReferences(entity, targetWorld);

                    // 3.3. Вносим в целевое хранилище
                    bool added = targetWorld.entityManager.Repository.UnsafeAdd(entity.instanceId, entity);

                    if (!added)
                    {
                        NLogger.Error($"SquashWorlds: не удалось добавить sync-сущность {entity.instanceId} ({entity.AliasName}) в целевой мир {targetWorld.instanceId}");
                        continue;
                    }

                    entitiesToRegister.Add((entity, wasAsync));
                }

                // === ФАЗА 3.5: Активация редиректа ===
                // Выполняется ДО освобождения блокировок.
                // volatile _squashRedirectTarget гарантирует видимость для проснувшихся потоков.
                //
                // Сценарий для async-потоков:
                //   AddNewEntityAsync заблокирован на EntityStorageAsync.ExecuteOnAddLockedAsync → GlobalLocker.
                //   После освобождения: сущность добавляется в мёртвый async storage.
                //   Rescue-логика внутри AddNewEntityAsync обнаруживает ResolveRedirect() != null,
                //   забирает из мёртвого storage, делегирует в target.AddNewEntityAsync.
                foreach (var sourceWorld in sourceWorlds)
                {
                    sourceWorld.entityManager.ActivateSquashRedirect(targetWorld.entityManager);
                }
            }
            finally
            {
                // === ФАЗА 4: Освобождение блокировок в обратном порядке ===
                for (int i = allLockTokens.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        allLockTokens[i]?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        NLogger.Error($"SquashWorlds: ошибка при освобождении блокировки: {ex.Message}");
                    }
                }
            }

            // === ФАЗА 5: Регистрация в граф-поисковике и контрактном кэше ===
            foreach (var (entity, wasAsync) in entitiesToRegister)
            {
                targetWorld.entityManager.AddNewEntityReaction(entity);
            }
        }

        /// <summary>
        /// Захватывает write-блокировки на хранилища компонентов сущности.
        /// Проверяет оба хранилища — sync и async.
        /// Если в sync-хранилище есть компоненты → блокируем sync.
        /// Если в async-хранилище есть компоненты → блокируем async.
        /// (Сущность может использовать оба хранилища одновременно.)
        /// </summary>
        private static void LockEntityComponentStorages(ECSEntity entity, List<IDisposable> lockTokens)
        {
            // Sync component storage (всегда существует, проверяем наличие компонентов)
            try
            {
                var syncLock = entity.entityComponents.GetWriteLockedComponentStorage();
                lockTokens.Add(syncLock);
            }
            catch (Exception ex)
            {
                NLogger.Error($"SquashWorlds: не удалось захватить sync component storage для сущности {entity.instanceId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Обновляет ECSWorldOwner у всех компонентов сущности на целевой мир.
        /// </summary>
        private static void SquashUpdateComponentWorldReferences(ECSEntity entity, ECSWorld targetWorld)
        {
            // Sync компоненты
            try
            {
                var syncComponents = entity.entityComponents.Components;
                if (syncComponents != null)
                {
                    foreach (var component in syncComponents)
                    {
                        if (component == null) continue;
                        component.ECSWorldOwnerId = targetWorld.instanceId;
                        component.ECSWorldOwnerCache = targetWorld;
                    }
                }
            }
            catch (Exception ex)
            {
                NLogger.Error($"SquashWorlds: ошибка при обновлении sync-компонентов сущности {entity.instanceId}: {ex.Message}");
            }
        }
    }
}
