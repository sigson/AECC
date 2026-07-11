using System;
using System.Collections.Generic;

namespace AECC.Core
{
    /// <summary>
    /// Identity-sidecar: значения привязаны к instanceId и переживают подмену инстанса при
    /// клиентском UpdateDeserialize.
    ///
    /// Бэкенд — потокобезопасный <see cref="SharedFieldTable"/>. Статический API работает в
    /// процессном скоупе (ProcessScope = 0); пер-мировые данные и числовые системные слоты —
    /// через SharedFieldTable напрямую (потребители кэшируют ссылку на строку для горячего пути).
    /// </summary>
    public class ECSSharedField<T> : IDisposable
    {
        public T Value { get => GetValue(); set => SetValue(value); }

        private readonly long entityId;
        private readonly string fieldName;
        // Строка идентичности резолвится один раз при конструировании, дальше — работа по ссылке.
        private readonly SharedFieldTable.Row row;

        public ECSSharedField(long id, string name, T value)
        {
            entityId = id;
            fieldName = name;
            row = SharedFieldTable.GetRow(SharedFieldTable.ProcessScope, id);

            // Существующее значение выигрывает, иначе кладём переданное (атомарно).
            object stored = row.Named.GetOrAdd(name, (object)value);
            Value = stored is T typed ? typed : value;
        }

        private T GetValue()
        {
            object value;
            if (row.TryGetNamed(fieldName, out value) && value is T typedValue)
            {
                return typedValue;
            }
            return default(T);
        }

        private void SetValue(T value)
        {
            row.Named[fieldName] = value;
        }

        /// <summary>
        /// Обновляет значение в кеше
        /// </summary>
        public void UpdateValue(T newValue)
        {
            Value = newValue;
        }

        public static T GetOrAdd(long id, string name, Func<T> valueFactory)
        {
            var row = SharedFieldTable.GetRow(SharedFieldTable.ProcessScope, id);
            object stored = row.Named.GetOrAdd(name, _ => (object)valueFactory());
            if (stored is T typedValue)
            {
                return typedValue;
            }
            // Значение чужого типа под этим именем — заменить своим.
            T newValue = valueFactory();
            row.Named[name] = newValue;
            return newValue;
        }

        /// <summary>
        /// Получает значение из кеша или добавляет значение по умолчанию
        /// </summary>
        public static T GetOrAdd(long id, string name, T defaultValue = default(T))
        {
            return GetOrAdd(id, name, () => defaultValue);
        }

        /// <summary>
        /// Получает значение из кеша по ID и имени
        /// </summary>
        public static T GetCachedValue(long id, string name)
        {
            SharedFieldTable.Row row;
            object value;
            if (SharedFieldTable.TryGetRow(SharedFieldTable.ProcessScope, id, out row)
                && row.TryGetNamed(name, out value))
            {
                return (T)value;
            }
            return default(T);
        }

        /// <summary>
        /// Проверяет наличие значения в кеше
        /// </summary>
        public static bool HasCachedValue(long id, string name)
        {
            SharedFieldTable.Row row;
            object value;
            return SharedFieldTable.TryGetRow(SharedFieldTable.ProcessScope, id, out row)
                   && row.TryGetNamed(name, out value);
        }

        /// <summary>
        /// Устанавливает значение в кеш напрямую
        /// </summary>
        public static object SetCachedValue(long id, string name, object value)
        {
            SharedFieldTable.GetRow(SharedFieldTable.ProcessScope, id).Named[name] = value;
            return value;
        }

        /// <summary>
        /// Удаляет конкретное значение из кеша
        /// </summary>
        public static bool RemoveCachedValue(long id, string name)
        {
            SharedFieldTable.Row row;
            object _;
            return SharedFieldTable.TryGetRow(SharedFieldTable.ProcessScope, id, out row)
                   && row.Named.TryRemove(name, out _);
        }

        /// <summary>
        /// Удаляет все значения для конкретного ID по всем скоупам (процессному и пер-мировым) —
        /// контракт «OnRemoved вычищает всё».
        /// </summary>
        public static bool RemoveAllCachedValuesForId(long id)
        {
            return SharedFieldTable.RemoveIdentityEverywhere(id);
        }

        /// <summary>
        /// Очищает весь кеш
        /// </summary>
        public static void ClearCache()
        {
            SharedFieldTable.Clear();
        }

        /// <summary>
        /// Получает количество закешированных ID
        /// </summary>
        public static int GetCachedIdsCount()
        {
            return SharedFieldTable.IdentityCount(SharedFieldTable.ProcessScope);
        }

        /// <summary>
        /// Получает количество закешированных полей для конкретного ID
        /// </summary>
        public static int GetCachedFieldsCount(long id)
        {
            SharedFieldTable.Row row;
            return SharedFieldTable.TryGetRow(SharedFieldTable.ProcessScope, id, out row) ? row.NamedCount : 0;
        }

        /// <summary>
        /// Получает все имена полей для конкретного ID
        /// </summary>
        public static IEnumerable<string> GetCachedFieldNames(long id)
        {
            SharedFieldTable.Row row;
            if (SharedFieldTable.TryGetRow(SharedFieldTable.ProcessScope, id, out row))
            {
                return row.NamedKeys;
            }
            return new List<string>();
        }

        public void Dispose()
        {
            // Dispose намеренно ничего не чистит: значение остаётся в SharedFieldTable по identity.
        }
    }
}
