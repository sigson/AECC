using System;
using System.Collections.Generic;

namespace AECC.Abstractions
{
    /// <summary>
    /// Реестр типов (ТЗ 4.3). Инкапсулирует бывшие статики
    /// <c>EntitySerializer.TypeStorage / TypeIdStorage / TypeStringStorage</c> и расширения
    /// <c>TypeId() / IdToECSType() / NameToECSType()</c>.
    ///
    /// Идея 1.14 неприкосновенна: атрибут <c>[TypeUid(int)]</c> — единственный источник
    /// идентичности типа; механизм «reflection-скан IDObject-наследников при инициализации +
    /// выставление статических Id-бэкинг-филдов» сохраняется.
    ///
    /// МАНДАТ ГОРЯЧЕГО ПУТИ (ТЗ 4.3, анти-бомба 7.1): type-id мирo-независим (детерминирован
    /// атрибутом) → карта процессно-глобальная, immutable после инициализации; интерфейс —
    /// это владение/инициализация/тестируемость, а НЕ точка прохода горячего пути: ссылка на
    /// реестр кэшируется полем при конструировании потребителя; резолв через IWorldContext на
    /// каждый вызов ЗАПРЕЩЁН. <see cref="GetId"/> обязан быть мемоизирован (дефект 6.1:
    /// прежний Type.TypeId() делал GetCustomAttribute + аллокацию + try/catch на каждый вызов).
    /// </summary>
    public interface ITypeRegistry
    {
        /// <summary>
        /// Id по атрибуту [TypeUid] (семантика бывшего <c>Type.TypeId()</c>): работает и для
        /// незарегистрированных типов, 0 при отсутствии атрибута. Мемоизировано.
        /// </summary>
        long GetId(Type type);

        /// <summary>Зарегистрированный тип по id (семантика <c>long.IdToECSType()</c>): null, если не зарегистрирован.</summary>
        Type GetType(long id);

        /// <summary>Зарегистрированный тип по короткому имени (семантика <c>string.NameToECSType()</c>): null, если нет.</summary>
        Type GetType(string name);

        /// <summary>Id зарегистрированного типа (семантика <c>Type.IdToECSType()</c>): 0, если не зарегистрирован.</summary>
        long GetRegisteredId(Type type);

        /// <summary>Try-варианты для горячих путей без исключений (SearchGraph, восстановление компонентов).</summary>
        bool TryGetType(long id, out Type type);
        bool TryGetRegisteredId(Type type, out long id);

        /// <summary>Все зарегистрированные пары Тип→id (потребитель — инициализация индексов ContractsManager).</summary>
        IEnumerable<KeyValuePair<Type, long>> RegisteredTypes { get; }
    }
}
