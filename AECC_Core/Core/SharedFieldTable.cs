using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace AECC.Core
{
    /// <summary>Числовые id системных полей (ТЗ 4.5.8г): без string-хеша на горячем пути.
    /// Строковые имена остаются для произвольных пользовательских полей.</summary>
    public static class SystemFieldId
    {
        public const int LifecycleState = 0;
        public const int Count = 1;
    }

    /// <summary>
    /// Упрочнённый бэкенд identity-sidecar (фаза 3, шаг 8; ТЗ 4.5.8; чинит дефект 6.3).
    /// Идея 1.11 неприкосновенна: данные привязаны к идентичности (instanceId), а не к
    /// инстансу — и переживают подмену инстанса при клиентском UpdateDeserialize.
    ///
    /// Упрочнения, не меняющие идею:
    /// (а) потокобезопасность: ConcurrentDictionary вместо голого Dictionary, писавшегося
    ///     из lifecycle-потоков (гонка данных 6.3);
    /// (б) пер-мировость: скоуп = instanceId мира (0 — процессный скоуп legacy-статиков
    ///     ECSSharedField) — миры не видят чужие поля и УМИРАЮТ СО СВОИМИ ДАННЫМИ
    ///     (ECSWorld.Dispose → DropWorld). Дисциплина очистки по идентичности
    ///     (RemoveAllCachedValuesForId) проходит ПО ВСЕМ скоупам — контракт «OnRemoved
    ///     вычищает всё для id» сохранён дословно;
    /// (в) мандат горячего пути: таблица НЕ резолвится на каждый доступ — потребитель
    ///     кэширует ссылку (паттерн «GetOrAdd однажды → работа по ссылке»; клиентский
    ///     LifecycleState — ровно один резолв на инстанс, дефект 6.2, см. ECSComponent);
    /// (г) системные поля — числовые слоты в массиве строки (без словаря и string-хеша).
    /// </summary>
    public static class SharedFieldTable
    {
        /// <summary>Строка идентичности: системные слоты (массив по SystemFieldId) +
        /// лениво создаваемые именованные пользовательские поля.</summary>
        public sealed class Row
        {
            private readonly object[] _system = new object[SystemFieldId.Count];
            private ConcurrentDictionary<string, object> _named;

            public ConcurrentDictionary<string, object> Named
            {
                get
                {
                    var n = _named;
                    if (n != null) return n;
                    var created = new ConcurrentDictionary<string, object>();
                    n = Interlocked.CompareExchange(ref _named, created, null);
                    return n ?? created;
                }
            }

            public bool TryGetNamed(string name, out object value)
            {
                var n = _named;
                if (n == null) { value = null; return false; }
                return n.TryGetValue(name, out value);
            }

            public object GetSystem(int fieldId) { return Volatile.Read(ref _system[fieldId]); }

            /// <summary>Атомарный GetOrAdd системного слота (фабрика может исполниться и
            /// проиграть гонку — победивший инстанс един для всех, как у ConcurrentDictionary).</summary>
            public object GetOrAddSystem(int fieldId, Func<object> factory)
            {
                var existing = Volatile.Read(ref _system[fieldId]);
                if (existing != null) return existing;
                var created = factory();
                existing = Interlocked.CompareExchange(ref _system[fieldId], created, null);
                return existing ?? created;
            }

            public int NamedCount { get { var n = _named; return n == null ? 0 : n.Count; } }
            public IEnumerable<string> NamedKeys { get { var n = _named; return n == null ? (IEnumerable<string>)new string[0] : n.Keys; } }
        }

        // Скоуп (мир, 0 = процессный) → идентичность → строка. Вложенная раскладка
        // позволяет дёшево умирать миру (снятие скоупа) и дёшево чистить идентичность
        // по всем скоупам (миров — единицы; очистка — холодный путь OnRemoved).
        private static readonly ConcurrentDictionary<long, ConcurrentDictionary<long, Row>> _scopes =
            new ConcurrentDictionary<long, ConcurrentDictionary<long, Row>>();

        public const long ProcessScope = 0;

        private static ConcurrentDictionary<long, Row> Scope(long worldScope)
        {
            return _scopes.GetOrAdd(worldScope, _ => new ConcurrentDictionary<long, Row>());
        }

        /// <summary>Строка идентичности в скоупе (создаётся при первом обращении).
        /// Потребители горячего пути кэшируют результат (мандат 4.5.8в).</summary>
        public static Row GetRow(long worldScope, long id)
        {
            return Scope(worldScope).GetOrAdd(id, _ => new Row());
        }

        public static bool TryGetRow(long worldScope, long id, out Row row)
        {
            return Scope(worldScope).TryGetValue(id, out row);
        }

        /// <summary>Очистка идентичности ПО ВСЕМ скоупам (контракт идеи 1.11:
        /// RemoveAllCachedValuesForId при OnRemoved/OnRemove вычищает всё для id).</summary>
        public static bool RemoveIdentityEverywhere(long id)
        {
            bool removed = false;
            foreach (var scope in _scopes)
            {
                Row _;
                removed |= scope.Value.TryRemove(id, out _);
            }
            return removed;
        }

        /// <summary>Мир умирает со своими данными (ТЗ 4.5.8б; зовётся из ECSWorld.Dispose).</summary>
        public static void DropWorld(long worldScope)
        {
            if (worldScope == ProcessScope) return; // процессный скоуп не убиваем
            ConcurrentDictionary<long, Row> _;
            _scopes.TryRemove(worldScope, out _);
        }

        /// <summary>Полная очистка (семантика прежнего ECSSharedField.ClearCache).</summary>
        public static void Clear() { _scopes.Clear(); }

        // ───── диагностические пробы (сетка 9(и), счётчики прежнего API) ─────

        public static bool HasSystemValue(long worldScope, long id, int fieldId)
        {
            Row row;
            return TryGetRow(worldScope, id, out row) && row.GetSystem(fieldId) != null;
        }

        public static int IdentityCount(long worldScope)
        {
            ConcurrentDictionary<long, Row> scope;
            return _scopes.TryGetValue(worldScope, out scope) ? scope.Count : 0;
        }
    }
}
