namespace AECC.Abstractions
{
    /// <summary>
    /// Участник сериализации (фаза 4, шаг 2; ТЗ 4.7): сериализатор и хранилище больше НЕ
    /// знают конкретный DBComponent — компонент, которому нужны хуки вокруг снапшота и
    /// после восстановления, реализует этот интерфейс. Семантика вызовов дословно прежняя:
    ///
    ///   BeforeSnapshot — точка бывшего SerializeDB(serializeOnlyChanged, clearChanged):
    ///                    под SerialLocker компонента, ДО adapter.SerializeECSComponent;
    ///   AfterSnapshot  — точка бывшего AfterSerializationDB(clearChanged): сразу ПОСЛЕ
    ///                    adapter.SerializeECSComponent, под тем же локом;
    ///   AfterRestore   — точка бывшего UnserializeDB()/UnserializeDB(true) при
    ///                    восстановлении: clientRetry == Profile.ClientRetryOnMissingRefs
    ///                    (событийный ретрай клиентской ветки, идея 1.8). Вызывается на
    ///                    ЖИВОМ инстансе из хранилища (restoring-режим сохраняет старый
    ///                    инстанс DB-агрегатора, перенимая serializedDB).
    ///
    /// Restoring-перенос payload (`serializedDB` в ComponentChanged хранилища) — отдельная
    /// механика восстановления, НЕ снапшот-хук; уезжает вместе с пайплайном при выносе
    /// сборки Serialization.
    /// </summary>
    public interface ISerializationParticipant
    {
        void BeforeSnapshot(bool serializeOnlyChanged, bool clearChanged);
        void AfterSnapshot(bool clearChanged);
        void AfterRestore(bool clientRetry);
    }
}
