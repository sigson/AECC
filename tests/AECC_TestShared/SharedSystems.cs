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
    ///
    /// ⚠️ ВАЖНЫЙ ИНВАРИАНТ ЯДРА (грабли, на которые легко наступить):
    /// в MultiThread-режиме контракт исполняет тело, УДЕРЖИВАЯ read-токены на всех компонентах,
    /// заявленных в EntityComponentPresenceSign как «должен присутствовать»
    /// (см. ECSExecutableContractContainer.AcquireContractTargets).
    /// А MarkAsChanged() берёт WRITE-лок на ячейку того же компонента.
    /// Write под read в том же потоке — cross-mode reentry: RWCell не вешает поток,
    /// а печатает «HALT! DEADLOCK ESCAPE!» и выполняет операцию БЕЗ ЛОКА (dummy-токен).
    ///
    /// ⇒ Компонент, который система МУТИРУЕТ и помечает изменённым, НЕЛЬЗЯ заявлять
    ///   в presence-sign. Заявляем только Velocity (её читаем), а Position достаём в теле.
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
                // Position проверяем условием, а НЕ presence-sign: иначе на ней будет висеть
                // read-токен, и MarkAsChanged() ниже уйдёт в deadlock-escape.
                { 0, new List<Func<ECSEntity, bool>> { (e) => e.Alive && e.HasComponent<PositionComponent>() } }
            };

            EntityComponentPresenceSign = new Dictionary<long, Dictionary<long, bool>>
            {
                { 0, new Dictionary<long, bool>
                    {
                        { TK.Uid<VelocityComponent>(), true  },   // только читаем ⇒ read-токен безопасен
                    }
                }
            };

            ContractExecutableSingle = (contract, entity) =>
            {
                var pos = entity.TryGetComponent<PositionComponent>();
                var vel = entity.TryGetComponent<VelocityComponent>();
                if (pos == null || vel == null) return;

                double vx, vy;
                lock (vel.SerialLocker) { vx = vel.VX; vy = vel.VY; }

                // Position под контрактными токенами НЕ висит ⇒ write-лок берётся честно.
                entity.ExecuteWriteLockedComponent<PositionComponent>((p) =>
                {
                    p.X += vx;
                    p.Y += vy;
                });
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
