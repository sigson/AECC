using System;
using System.Collections.Generic;
using AECC.Core.Logging;

namespace AECC.Core
{
    /// <summary>
    /// Событийный реестр отложенной десериализации — замена ретрай-таймеров.
    /// Объект, чья десериализация не завершилась из-за ещё не пришедшей ссылки
    /// (корневой сущности пути, а её компоненты приходят в рамках того же AddNewEntity),
    /// регистрирует здесь повторную попытку. Попытки сливаются при приходе сущностей
    /// (ECSEntityManager.AddNewEntityReaction) вместо периодического опроса.
    ///
    /// Потокобезопасность и порядок локов:
    ///  - Register/Unregister берут _lock ТОЛЬКО на вставку/удаление и сразу отпускают.
    ///  - Drain снимает снапшот и очищает словарь под _lock, а сами попытки исполняет
    ///    УЖЕ БЕЗ _lock. Поэтому _lock и SerialLocker объектов никогда не удерживаются
    ///    одновременно — инверсии порядка локов (а значит дедлока) нет.
    ///  - Ровно-однократность слива гарантируется очисткой под локом: каждая
    ///    зарегистрированная попытка достаётся ровно одному Drain; неуспешная попытка
    ///    перерегистрирует себя сама.
    /// Ключ — сам объект-регистрант, поэтому повторная регистрация перезаписывает прежнюю
    /// (без накопления дубликатов). Счётчик попыток/сдача (dead-letter) живут в самом
    /// потребителе (как и раньше), здесь — только момент повторной попытки.
    /// </summary>
    public class PendingDeserializationRegistry
    {
        private readonly object _lock = new object();
        private readonly Dictionary<object, Action> _pending = new Dictionary<object, Action>();

        public void Register(object key, Action retry)
        {
            if (key == null || retry == null) return;
            lock (_lock)
            {
                _pending[key] = retry;
            }
        }

        public void Unregister(object key)
        {
            if (key == null) return;
            lock (_lock)
            {
                _pending.Remove(key);
            }
        }

        public void Drain()
        {
            List<Action> snapshot;
            lock (_lock)
            {
                if (_pending.Count == 0) return;
                snapshot = new List<Action>(_pending.Values);
                _pending.Clear();
            }
            foreach (var retry in snapshot)
            {
                try
                {
                    retry();
                }
                catch (Exception ex)
                {
                    NLogger.Error($"PendingDeserialization retry error: {ex.Message}");
                }
            }
        }
    }
}
