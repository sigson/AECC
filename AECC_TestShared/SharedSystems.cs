using System;
using System.Collections.Generic;
using System.Threading;
using AECC.Core;

namespace AECC.TestKit
{
    // ─────────────────────────────────────────────────────────────────────────
    //  ВАЖНО про WorldFilter:
    //  ECSContractsManager.InitializeSystems() делает
    //      Activator.CreateInstance(тип) → .Where(x => x.WorldFilter(world)) → потом Initialize()
    //  ⇒ WorldFilter обязан быть выставлен В КОНСТРУКТОРЕ, иначе на момент фильтрации там
    //    ещё дефолтный (world) => true, и система поднимется во ВСЕХ мирах процесса.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Авторитарная серверная симуляция: интегрирует Position по Velocity каждые 50 мс
    /// и помечает компонент изменённым (⇒ он попадёт в следующий срез роллинга).
    /// Транзакционность: в MultiThread-режиме контракт исполняется под read-токенами
    /// Position/Velocity (см. AcquireContractTargets), т.е. компонент не может быть снят
    /// параллельно во время тела.
    /// </summary>
    public class MovementSystem : ECSExecutableContractContainer
    {
        public static int Ticks;

        public MovementSystem()
        {
            // ДО фильтрации:
            WorldFilter = (w) => w.WorldType == ECSWorld.WorldTypeEnum.Server;
        }

        public override void Initialize()
        {
            TimeDependExecution = true;
            AsyncExecution = false;
            RemoveAfterExecution = false;
            NoPresenceSignAllowed = false;
            PartialEntityFiltering = true;
            DelayRunMilliseconds = 50;

            ContractConditions = new Dictionary<long, List<Func<ECSEntity, bool>>>
            {
                { 0, new List<Func<ECSEntity, bool>> { (e) => e.Alive } }
            };

            EntityComponentPresenceSign = new Dictionary<long, Dictionary<long, bool>>
            {
                { 0, new Dictionary<long, bool>
                    {
                        { TK.Uid<PositionComponent>(), true  },
                        { TK.Uid<VelocityComponent>(), true  },
                    }
                }
            };

            ContractExecutableSingle = (contract, entity) =>
            {
                var pos = entity.TryGetComponent<PositionComponent>();
                var vel = entity.TryGetComponent<VelocityComponent>();
                if (pos == null || vel == null) return;

                lock (pos.SerialLocker)
                {
                    pos.X += vel.VX;
                    pos.Y += vel.VY;
                }
                pos.MarkAsChanged();                 // ⇒ dirty-set ⇒ следующий срез уедет клиенту
                Interlocked.Increment(ref Ticks);
            };
        }
    }

    /// <summary>
    /// Time-depend система для локальной (Offline) батареи: считает тики по сущностям
    /// с LifecycleProbeComponent. Проверяет, что таймер мира (ECSWorld.Start → IScheduler)
    /// реально гоняет RunTimeDependContracts.
    /// </summary>
    public class OfflineTickSystem : ECSExecutableContractContainer
    {
        public static int Ticks;
        public static long LastEntity;

        public OfflineTickSystem()
        {
            WorldFilter = (w) => w.WorldType == ECSWorld.WorldTypeEnum.Offline;
        }

        public override void Initialize()
        {
            TimeDependExecution = true;
            AsyncExecution = false;
            RemoveAfterExecution = false;
            NoPresenceSignAllowed = false;
            PartialEntityFiltering = true;
            DelayRunMilliseconds = 10;

            ContractConditions = new Dictionary<long, List<Func<ECSEntity, bool>>>
            {
                { 0, new List<Func<ECSEntity, bool>> { (e) => true } }
            };

            EntityComponentPresenceSign = new Dictionary<long, Dictionary<long, bool>>
            {
                { 0, new Dictionary<long, bool>
                    {
                        { TK.Uid<LifecycleProbeComponent>(), true  },
                        // и ОТСУТСТВИЕ BlockerComponent — проверяем absence-sign контракта
                        { TK.Uid<BlockerComponent>(),        false },
                    }
                }
            };

            ContractExecutableSingle = (contract, entity) =>
            {
                Interlocked.Increment(ref Ticks);
                Interlocked.Exchange(ref LastEntity, entity.instanceId);
            };
        }
    }
}
