using AECC.Core;
using AECC.Core.Logging;
using AECC.Core.Serialization;
using AECC.ECS.DefaultObjects.ECSComponents;
using AECC.ECS.Events.ECSEvents;
using AECC.Extensions;
using AECC.Network;
using System;
using System.Collections.Generic;
using System.Linq;
using TestShared.Components;

namespace TestShared.Systems
{
    // =========================================================================
    //  WorldSyncSystem — система сериализации и отправки сущностей клиентам
    //
    //  Логика:
    //    1. Раз в 100мс находит все сущности с HealthComponent + PositionComponent
    //       через EntityManager.SearchGraph (SIMD поиск по метрикам)
    //    2. Блокирует компоненты через ExecuteReadLockedComponent
    //    3. Модифицирует компоненты (симуляция движения, урон, счёт)
    //    4. Сериализует каждую сущность через EntityNetSerializer с GDAP-фильтром
    //    5. Упаковывает результат в UpdateEntitiesEvent
    //    6. Отправляет NetworkEvent клиенту
    // =========================================================================

    public class WorldSyncSystem : ECSExecutableContractContainer
    {
        private static readonly Random _rng = new Random(42);

        public override void Initialize()
        {
            // --- Настройка системы как time-depend (таймерный цикл) ---
            TimeDependExecution = true;
            RemoveAfterExecution = false;
            DelayRunMilliseconds = 100; // каждые 100мс
            AsyncExecution = true;
            PartialEntityFiltering = true;
            LoggingLevel = ContractLoggingLevel.None;

            // --- Фильтр: сущности с HealthComponent + PositionComponent ---
            // Ключ 0 — wildcard, будет подставлен для каждой найденной сущности
            EntityComponentPresenceSign = new Dictionary<long, Dictionary<long, bool>>()
            {
                {
                    0, new Dictionary<long, bool>()
                    {
                        { HealthComponent.Id, true },
                        { PositionComponent.Id, true }
                    }
                }
            };

            ContractConditions = new Dictionary<long, List<Func<ECSEntity, bool>>>()
            {
                { 0, new List<Func<ECSEntity, bool>>() { entity => entity.Alive } }
            };

            // --- Серверная фильтрация: запускать только на сервере ---
            WorldFilter = (world) => world.WorldType == ECSWorld.WorldTypeEnum.Server;

            // --- Основной исполнитель ---
            ContractExecutable = (contract, entities) =>
            {
                ProcessEntities(entities);
            };
        }

        private void ProcessEntities(ECSEntity[] entities)
        {
            if (entities == null || entities.Length == 0)
                return;

            // Находим мир из первой сущности
            var world = entities[0].ECSWorldOwner;
            if (world == null) return;

            // ----------------------------------------------------------
            //  ШАГ 1: Модификация компонентов через ReadLocked / WriteLocked
            // ----------------------------------------------------------
            foreach (var entity in entities)
            {
                try
                {
                    // Блокируем компоненты для записи и модифицируем
                    entity.ExecuteWriteLockedComponent<PositionComponent, VelocityComponent>(
                        (pos, vel) =>
                        {
                            // Симуляция движения: pos += vel * dt
                            float dt = 0.1f; // 100ms
                            pos.X += vel.VX * dt;
                            pos.Y += vel.VY * dt;
                            pos.Z += vel.VZ * dt;

                            // Случайное изменение скорости
                            vel.VX += (float)(_rng.NextDouble() * 2.0 - 1.0) * 0.5f;
                            vel.VY += (float)(_rng.NextDouble() * 2.0 - 1.0) * 0.3f;
                            vel.VZ += (float)(_rng.NextDouble() * 2.0 - 1.0) * 0.5f;

                            pos.MarkAsChanged();
                            vel.MarkAsChanged();
                        });

                    // Чтение здоровья через ReadLock
                    entity.ExecuteReadLockedComponent<HealthComponent>((type, comp) =>
                    {
                        var health = (HealthComponent)comp;
                        // Симуляция: медленный урон
                        lock (health.SerialLocker)
                        {
                            health.CurrentHealth -= (float)(_rng.NextDouble() * 2.0);
                            if (health.CurrentHealth < 0) health.CurrentHealth = 0;
                            health.MarkAsChanged();
                        }
                    });

                    // Обновляем Score
                    if (entity.HasComponent<ScoreComponent>())
                    {
                        entity.ExecuteWriteLockedComponent<ScoreComponent>((score) =>
                        {
                            score.Points += _rng.Next(0, 5);
                            if (_rng.NextDouble() < 0.1)
                                score.KillCount++;
                            score.MarkAsChanged();
                        });
                    }

                    // Серверный секретный компонент
                    if (entity.HasComponent<ServerSecretComponent>())
                    {
                        entity.ExecuteWriteLockedComponent<ServerSecretComponent>((secret) =>
                        {
                            secret.LastTickProcessed = DateTime.UtcNow.Ticks;
                            secret.MarkAsChanged();
                        });
                    }
                }
                catch (Exception ex)
                {
                    NLogger.LogError($"WorldSyncSystem: error processing entity {entity.instanceId}: {ex.Message}");
                }
            }

            // ----------------------------------------------------------
            //  ШАГ 2: Сериализация сущностей через GDAP фильтрацию
            // ----------------------------------------------------------

            // Ищем все сущности-получатели (клиенты) с SocketComponent
            var clientEntities = world.entityManager.SearchGraph(
                withComponentTypes: new[] { typeof(SocketComponent) }
            ).ToList();

            if (clientEntities.Count == 0)
            {
                NLogger.Log("WorldSyncSystem: no connected clients, skipping send");
                return;
            }

            foreach (var clientEntity in clientEntities)
            {
                var socketComp = clientEntity.TryGetComponent<SocketComponent>();
                if (socketComp?.Socket == null) continue;

                var serializedEntities = new List<byte[]>();

                foreach (var gameEntity in entities)
                {
                    try
                    {
                        // Сначала сериализуем сущность (подготавливает GDAP-данные)
                        world.EntityWorldSerializer.SerializeEntity(gameEntity, true);

                        // Затем строим пакет с GDAP-фильтрацией:
                        // clientEntity (кому) -> gameEntity (что), с учётом совпадающих GDAP
                        var data = world.EntityWorldSerializer.BuildSerializedEntityWithGDAP(
                            clientEntity, gameEntity, ignoreNullData: false);

                        if (data != null && data.Length > 0)
                        {
                            serializedEntities.Add(data);
                        }
                    }
                    catch (Exception ex)
                    {
                        NLogger.LogError($"WorldSyncSystem: serialization error for entity {gameEntity.instanceId}: {ex.Message}");
                    }
                }

                if (serializedEntities.Count > 0)
                {
                    // ----------------------------------------------------------
                    //  ШАГ 3: Упаковка в UpdateEntitiesEvent и отправка
                    // ----------------------------------------------------------
                    var updateEvent = new UpdateEntitiesEvent
                    {
                        Entities = serializedEntities,
                        EntityIdRecipient = clientEntity.instanceId,
                        WorldOwnerId = world.instanceId,
                        Destination = NetworkDestination.ForSocket(socketComp.Socket.Id)
                    };

                    try
                    {
                        NetworkService.instance.EventManager.Dispatch(updateEvent);
                        NLogger.Log($"WorldSyncSystem: sent {serializedEntities.Count} entities to client {clientEntity.instanceId} (socket {socketComp.Socket.Id})");
                    }
                    catch (Exception ex)
                    {
                        NLogger.LogError($"WorldSyncSystem: dispatch error: {ex.Message}\n {new System.Diagnostics.StackTrace(ex, true)}");
                    }
                }
            }
        }
    }
}
