using System;

namespace AECC.Abstractions
{
    /// <summary>
    /// Тонкая абстракция над NLogger (ТЗ 4.3). Поверхность повторяет реально используемые
    /// ядром каналы; расширение — по мере выселения подсистем (фазы 3–7).
    /// </summary>
    public interface INLogger
    {
        void Log(object content);
        void Warn(object content);
        void Error(object content);
        void ErrorLocking(object content);
        void Debug(object content);
    }

    /// <summary>
    /// Планировщик (ТЗ 4.3): абстракция над TaskEx.RunAsync и TimerCompat. Критичен для
    /// детерминированных тестов lifecycle-очередей и time-depend контрактов: с инжектируемым
    /// планировщиком порядок Add→Change→Remove проверяется детерминированно (второй слой
    /// сетки фазы 0, добавляется после этой фазы).
    /// </summary>
    public interface IScheduler
    {
        /// <summary>Асинхронное исполнение (семантика TaskEx.RunAsync: уважает режимы приложения).</summary>
        void Run(Action action);

        /// <summary>Периодический/одноразовый тик (семантика TimerCompat). Dispose останавливает таймер.</summary>
        IDisposable Schedule(int intervalMs, Action tick, bool repeating);
    }

    /// <summary>Часы (ТЗ 4.3): абстракция над DateTime/TimerCompat.TimerDateTime для тестируемости времени.</summary>
    public interface IClock
    {
        long UtcNowTicks { get; }
        DateTime UtcNow { get; }
    }

    /// <summary>Поведенческий вид мира (идея 1.15). Дублирует ECSWorld.WorldTypeEnum на время миграции;
    /// в фазе 3 ECSWorld переходит на этот enum.</summary>
    public enum WorldKind
    {
        Server,
        Client,
        Offline,
    }

    /// <summary>
    /// Контекст мира (ТЗ 4.3): то немногое, что модели действительно нужно от мира.
    /// Заменяет цепочку «IDObject → static ECSWorld.GetWorld → весь мир».
    ///
    /// Вводится в фазе 2 как контракт; ПОТРЕБИТЕЛИ переводятся в фазе 3 (Runtime-разрез):
    /// там же интерфейс дорастает доступом к pending-десериализации и профилю мира
    /// (типы появляются в фазах 3–4). Инстансный кэш мира на IDObject (ECSWorldOwnerCache
    /// с валидацией по id) сохраняется — резолв контекста только на кэш-промахе.
    /// </summary>
    public interface IWorldContext
    {
        long InstanceId { get; }
        WorldKind Kind { get; }
        ITypeRegistry Types { get; }
    }
}
