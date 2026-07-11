using System;
using System.Collections.Generic;

namespace AECC.Abstractions
{
    /// <summary>
    /// Type registry mapping between .NET <see cref="Type"/> and its numeric/name identity.
    ///
    /// The <c>[TypeUid(int)]</c> attribute is the single source of type identity; on
    /// initialization the registry scans IDObject descendants via reflection and sets their
    /// static Id backing fields accordingly.
    ///
    /// Hot-path contract: type-id is world-independent (determined by the attribute), so the
    /// map is process-global and immutable after initialization. The interface exists for
    /// ownership/initialization/testability, not as a hot-path indirection: consumers should
    /// cache a reference to the registry in a field at construction time rather than
    /// resolving it through IWorldContext on every call. <see cref="GetId"/> must be memoized.
    /// </summary>
    public interface ITypeRegistry
    {
        /// <summary>
        /// Id from the [TypeUid] attribute. Works for unregistered types too; returns 0 if the
        /// attribute is absent. Memoized.
        /// </summary>
        long GetId(Type type);

        /// <summary>Registered type by id; null if not registered.</summary>
        Type GetType(long id);

        /// <summary>Registered type by short name; null if not found.</summary>
        Type GetType(string name);

        /// <summary>Id of a registered type; 0 if not registered.</summary>
        long GetRegisteredId(Type type);

        /// <summary>Exception-free variants for hot paths (e.g. SearchGraph, component restore).</summary>
        bool TryGetType(long id, out Type type);
        bool TryGetRegisteredId(Type type, out long id);

        /// <summary>All registered Type -> id pairs (used to initialize ContractsManager indexes).</summary>
        IEnumerable<KeyValuePair<Type, long>> RegisteredTypes { get; }
    }
}
