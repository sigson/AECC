using System;
using System.Collections.Generic;
using System.Threading;
using AECC.Core.Logging;
using AECC.Extensions.ThreadingSync;

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
    ///
    /// ─── ОПТИМИЗАЦИЯ ПАМЯТИ: гейт пустого реестра + коалесинг слива ───
    /// Прежде КАЖДЫЙ приход сущности/компонента планировал в пул отдельный work item
    /// `TaskEx.RunAsync(() => Drain())` — при пустом реестре (штатное состояние сервера)
    /// это миллионы бесполезных Action/WaitCallback/замыканий и неограниченный рост
    /// глобальной очереди пула. Теперь точка входа события — <see cref="RequestDrain"/>:
    /// ноль аллокаций при пустом реестре, не более ОДНОГО запланированного дрейнера
    /// одновременно (CAS по _drainActive), пропущенные во время слива события
    /// доигрываются циклом дрейнера (эпоха _requests).
    ///
    /// КОРРЕКТНОСТЬ ГОНКИ «поставщик-выжидателя против встраивателя данных» —
    /// механизм критичен для DBComponent и child/parent нодовой системы, поэтому
    /// протокол построен на ДВОЙНЫХ ПРОВЕРКАХ с обеих сторон:
    ///
    ///  Сторона встраивателя (RequestDrain):
    ///   1) инкремент эпохи _requests (Interlocked, полный барьер) СТРОГО ДО чтения
    ///      _count — факт события публикуется раньше, чем принимается решение «пусто»;
    ///   2) сам вызов RequestDrain обязан идти ПОСЛЕ публикации сущности/компонента
    ///      в хранилища мира (в ECSEntityManager это так: реакция — после Add).
    ///
    ///  Сторона регистранта (Register + его собственный re-check):
    ///   регистрант ОБЯЗАН после Register повторить проверку недостающей ссылки
    ///   (register-then-recheck — уже реализован в SerializationShadow.AfterRestore и
    ///   DBComponent.UnserializeDB). Разбор перекрытий:
    ///    а) вставка завершилась ДО чтения _count встраивателем → встраиватель видит
    ///       _count>0 → планирует слив → retry исполнится и увидит опубликованную
    ///       сущность (публикация предшествует RequestDrain в program order
    ///       встраивателя, барьеры Interlocked/монитора дают видимость);
    ///    б) вставка завершилась ПОСЛЕ чтения _count (встраиватель пропустил слив) →
    ///       монитор Register — полный барьер, и последующий re-check регистранта
    ///       читает хранилища мира УЖЕ ПОСЛЕ него → публикация, из-за которой
    ///       встраиватель успел пройти, гарантированно видна re-check'у → регистрант
    ///       завершает восстановление сам и снимает себя Unregister'ом.
    ///   Таким образом ни одно перекрытие не оставляет «вечно висящего» выжидателя.
    ///
    ///  Сторона дрейнера (DrainLoop):
    ///   после каждого слива и СНЯТИЯ флага — двойная проверка: если за время слива
    ///   пришли новые события (эпоха сдвинулась) И реестр непуст (ре-регистрации),
    ///   дрейнер сливает ещё раз. Если эпоха не менялась — ре-регистрации ждут
    ///   СЛЕДУЮЩЕГО события прихода (никаких самозапускающихся циклов «retry по кругу»).
    /// </summary>
    public class PendingDeserializationRegistry
    {
        private readonly object _lock = new object();
        private readonly Dictionary<object, Action> _pending = new Dictionary<object, Action>();

        /// <summary>Зеркало _pending.Count. ПИШЕТСЯ ТОЛЬКО ПОД _lock; volatile-чтение —
        /// дешёвый гейт RequestDrain без захвата монитора на горячем пути.</summary>
        private volatile int _count;

        /// <summary>Эпоха запросов слива: каждый RequestDrain инкрементирует ДО проверки
        /// пустоты. Дрейнер по ней отличает «во время слива пришли новые события»
        /// от «тишина» (см. двойную проверку в DrainLoop).</summary>
        private long _requests;

        /// <summary>0/1 — запланирован ли (или исполняется) дрейнер. CAS-коалесинг:
        /// сколько бы событий ни пришло, в очереди пула не более одного work item.</summary>
        private int _drainActive;

        /// <summary>Кэш делегата дрейн-цикла: планирование слива не создаёт замыканий.</summary>
        private Action _drainLoopCached;

        /// <summary>Дешёвая проверка «есть выжидатели» (volatile, без лока).</summary>
        public bool HasPending { get { return _count != 0; } }

        public void Register(object key, Action retry)
        {
            if (key == null || retry == null) return;
            lock (_lock)
            {
                _pending[key] = retry;
                _count = _pending.Count;
            }
            // ДВОЙНАЯ ПРОВЕРКА №1 (сторона регистранта) исполняется САМИМ регистрантом
            // после возврата отсюда (register-then-recheck: SerializationShadow.AfterRestore,
            // DBComponent.UnserializeDB). Реестр её не дублирует и НЕ инициирует слив сам:
            // безусловный самослив после каждой регистрации зациклил бы «retry по кругу»
            // для ссылок, которых в мире действительно ещё нет.
        }

        public void Unregister(object key)
        {
            if (key == null) return;
            lock (_lock)
            {
                if (_pending.Remove(key))
                    _count = _pending.Count;
            }
        }

        /// <summary>
        /// Точка входа события «в мир встроилась сущность/компонент». Дёшево (без
        /// аллокаций и без монитора) выходит при пустом реестре; иначе гарантирует,
        /// что зарегистрированные попытки будут слиты, планируя НЕ БОЛЕЕ одного
        /// дрейнера. ВЫЗЫВАТЬ СТРОГО ПОСЛЕ публикации данных в хранилища мира.
        /// </summary>
        public void RequestDrain()
        {
            // Порядок обязателен: эпоха — ДО чтения _count. Interlocked — полный барьер:
            // если конкурентная регистрация не видна этому чтению (_count == 0), то её
            // re-check стартует после её же монитора и увидит нашу публикацию (см. шапку).
            Interlocked.Increment(ref _requests);
            if (_count == 0) return;
            if (Interlocked.CompareExchange(ref _drainActive, 1, 0) != 0) return;
            var loop = _drainLoopCached;
            if (loop == null)
            {
                loop = DrainLoop;
                _drainLoopCached = loop; // benign race: перезапись тем же method-group
            }
            TaskEx.RunAsync(loop);
        }

        private void DrainLoop()
        {
            while (true)
            {
                long epoch = Interlocked.Read(ref _requests);
                Drain();
                Interlocked.Exchange(ref _drainActive, 0);

                // ДВОЙНАЯ ПРОВЕРКА №2 (сторона дрейнера), уже ПОСЛЕ снятия флага:
                //  - реестр пуст — сливать нечего, любые новые события пройдут свой гейт;
                //  - эпоха не менялась — есть ре-регистрации, но НОВЫХ приходов не было:
                //    они легитимно ждут следующего события (не зацикливаемся);
                //  - эпоха сдвинулась И реестр непуст — событие могло проскочить между
                //    нашим снапшотом и снятием флага (его CAS проиграл) → сливаем ещё раз.
                if (_count == 0) return;
                if (Interlocked.Read(ref _requests) == epoch) return;
                if (Interlocked.CompareExchange(ref _drainActive, 1, 0) != 0) return;
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
                _count = 0;
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
