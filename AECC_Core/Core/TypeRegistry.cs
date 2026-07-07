using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using AECC.Abstractions;
using AECC.Collections;
using AECC.Core.Logging;

namespace AECC.Core
{
    /// <summary>
    /// Процессно-глобальный реестр типов (ТЗ 4.3, фаза 2). Реализация переходно живёт в
    /// AECC.Core (Abstractions — 0 реализаций; Runtime появится в фазе 3).
    ///
    /// Владеет бывшими статиками EntitySerializer.Type*Storage: сами DictionaryWrapper-карты
    /// переехали сюда, статики сериализатора стали [Obsolete]-фасадами на эти же инстансы —
    /// внешний код со старыми обращениями работает без изменений, но всё ядро переведено на
    /// реестр (приёмка фазы 2: ни одного прямого обращения к Type*Storage вне реестра/фасада).
    ///
    /// Карта заполняется один раз при инициализации (InitSerialize, механизм 1.14) и далее
    /// immutable по дисциплине; чтения — lock-free.
    ///
    /// Дефект 6.1 закрыт: <see cref="GetId"/> мемоизирует резолв [TypeUid] в
    /// ConcurrentDictionary — рефлексия (GetCustomAttribute + аллокация атрибута + try/catch)
    /// исполняется один раз на тип за процесс вместо каждого Add/Get/Remove/Contains
    /// компонента по Type. Неуспех (нет атрибута) НЕ кэшируется: ошибка логируется на каждый
    /// вызов, как в исходном Type.TypeId(), — диагностический контракт не ослаблен.
    /// </summary>
    public sealed class TypeRegistry : ITypeRegistry
    {
        /// <summary>Единственный процессный инстанс (мандат ТЗ 4.3: карта Type→id мирo-независима).</summary>
        public static readonly TypeRegistry Global = new TypeRegistry();

        // Бывшие EntitySerializer.TypeStorage / TypeStringStorage / TypeIdStorage —
        // те же DictionaryWrapper-инстансы, теперь во владении реестра ([Obsolete]-фасады
        // сериализатора возвращают именно их, поэтому поведение старых вызовов идентично,
        // включая KeyNotFoundException индексатора).
        internal readonly DictionaryWrapper<long, Type> ById = new DictionaryWrapper<long, Type>();
        internal readonly DictionaryWrapper<string, Type> ByName = new DictionaryWrapper<string, Type>();
        internal readonly DictionaryWrapper<Type, long> RegisteredIds = new DictionaryWrapper<Type, long>();

        // Мемоизация атрибутного резолва (дефект 6.1). Только успешные резолвы.
        private readonly ConcurrentDictionary<Type, long> _attributeIdCache = new ConcurrentDictionary<Type, long>();

        // ───────── ITypeRegistry ─────────

        public long GetId(Type type)
        {
            long id;
            if (_attributeIdCache.TryGetValue(type, out id))
                return id;
            return ResolveAttributeId(type);
        }

        private long ResolveAttributeId(Type type)
        {
            try
            {
                long id = type.GetCustomAttribute<TypeUidAttribute>().Id;
                _attributeIdCache[type] = id;
                return id;
            }
            catch
            {
                // Дословное сообщение исходного Type.TypeId(); неуспех не кэшируем —
                // частота диагностики сохранена.
                NLogger.Error(type.ToString() + " no have static id field or ID attribute");
            }
            return default(long);
        }

        public Type GetType(long id)
        {
            Type t;
            return ById.TryGetValue(id, out t) ? t : default(Type);
        }

        public Type GetType(string name)
        {
            Type t;
            return ByName.TryGetValue(name, out t) ? t : default(Type);
        }

        public long GetRegisteredId(Type type)
        {
            long id;
            return RegisteredIds.TryGetValue(type, out id) ? id : default(long);
        }

        public bool TryGetType(long id, out Type type) { return ById.TryGetValue(id, out type); }
        public bool TryGetRegisteredId(Type type, out long id) { return RegisteredIds.TryGetValue(type, out id); }

        public IEnumerable<KeyValuePair<Type, long>> RegisteredTypes { get { return RegisteredIds; } }

        // ───────── инициализация (только InitSerialize, механизм 1.14) ─────────

        /// <summary>
        /// Регистрация типа при reflection-скане инициализации. Проверка коллизии id и её
        /// сообщение — дословно из исходного InitSerialize.
        /// </summary>
        internal void Register(long id, Type type)
        {
            Type existing;
            if (ById.TryGetValue(id, out existing))
                NLogger.Error("Error adding " + type.Name + " id " + id + " is presened as " + existing.Name);
            ById[id] = type;
            RegisteredIds[type] = id;
            ByName[type.Name] = type;
        }

        /// <summary>Индексаторная семантика бывшего TypeStorage[id] (KeyNotFoundException при отсутствии).</summary>
        internal Type GetTypeOrThrow(long id) { return ById[id]; }

        /// <summary>Индексаторная семантика бывшего TypeIdStorage[type] (KeyNotFoundException при отсутствии) — контракт GId&lt;T&gt;().</summary>
        internal long GetRegisteredIdOrThrow(Type type) { return RegisteredIds[type]; }
    }
}
