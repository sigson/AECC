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
    /// Процессно-глобальный реестр типов. Реализация живёт в AECC.Core
    /// (Abstractions — 0 реализаций).
    ///
    /// Владеет картами Type⇄id/name: те же DictionaryWrapper-инстансы также доступны через
    /// [Obsolete]-фасады EntitySerializer.Type*Storage для внешнего кода со старыми
    /// обращениями.
    ///
    /// Карта заполняется один раз при инициализации (InitSerialize) и далее immutable по
    /// дисциплине; чтения — lock-free.
    ///
    /// <see cref="GetId"/> мемоизирует резолв [TypeUid] в ConcurrentDictionary — рефлексия
    /// (GetCustomAttribute + аллокация атрибута + try/catch) исполняется один раз на тип за
    /// процесс вместо каждого Add/Get/Remove/Contains компонента по Type. Неуспех (нет
    /// атрибута) не кэшируется: ошибка логируется на каждый вызов.
    /// </summary>
    public sealed class TypeRegistry : ITypeRegistry
    {
        /// <summary>Единственный процессный инстанс: карта Type→id мирo-независима.</summary>
        public static readonly TypeRegistry Global = new TypeRegistry();

        // DictionaryWrapper-инстансы для Type⇄id/name; также доступны через [Obsolete]-фасады
        // EntitySerializer.TypeStorage/TypeStringStorage/TypeIdStorage, поэтому поведение
        // старых вызовов идентично, включая KeyNotFoundException индексатора.
        internal readonly DictionaryWrapper<long, Type> ById = new DictionaryWrapper<long, Type>();
        internal readonly DictionaryWrapper<string, Type> ByName = new DictionaryWrapper<string, Type>();
        internal readonly DictionaryWrapper<Type, long> RegisteredIds = new DictionaryWrapper<Type, long>();

        // Мемоизация атрибутного резолва. Только успешные резолвы.
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
                // Неуспех не кэшируем, чтобы диагностика логировалась на каждый вызов.
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

        // ───────── инициализация (только InitSerialize) ─────────

        /// <summary>
        /// Регистрация типа при reflection-скане инициализации. Проверяет коллизию id.
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

        /// <summary>Индексаторная семантика: KeyNotFoundException при отсутствии.</summary>
        internal Type GetTypeOrThrow(long id) { return ById[id]; }

        /// <summary>Индексаторная семантика: KeyNotFoundException при отсутствии — контракт GId&lt;T&gt;().</summary>
        internal long GetRegisteredIdOrThrow(Type type) { return RegisteredIds[type]; }
    }
}
