namespace AECC.Abstractions
{
    /// <summary>
    /// Implemented by components that need hooks around snapshotting and restore. The
    /// serializer and storage interact only through this interface, not any concrete
    /// component type:
    ///
    ///   BeforeSnapshot — called under the component's SerialLocker, before
    ///                    adapter.SerializeECSComponent;
    ///   AfterSnapshot  — called immediately after adapter.SerializeECSComponent, under the
    ///                    same lock;
    ///   AfterRestore   — called on restore, with clientRetry == Profile.ClientRetryOnMissingRefs
    ///                    (client-side retry on missing references). Called on the live
    ///                    instance from storage: restoring mode keeps the old aggregator
    ///                    instance and has it take over the incoming payload.
    /// </summary>
    public interface ISerializationParticipant
    {
        void BeforeSnapshot(bool serializeOnlyChanged, bool clearChanged);
        void AfterSnapshot(bool clearChanged);
        void AfterRestore(bool clientRetry);
    }
}
