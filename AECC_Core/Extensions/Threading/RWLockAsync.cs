using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using AECC.Core.Logging;

public class RWLockAsync : IDisposable
{
    private readonly AsyncReaderWriterLock _lockObj;
    private readonly bool _isMockMode;

    // Пустышка для безопасного выхода из using, если лок не был получен из-за ошибки
    private struct DummyDisposable : IDisposable
    {
        public void Dispose() { }
    }

    public RWLockAsync()
    {
        // Поддержка ваших старых флагов компиляции/настроек
        if (Defines.OneThreadMode)
        {
            _isMockMode = true;
        }
        else
        {
            _lockObj = new AsyncReaderWriterLock();
        }
    }

    /// <summary>
    /// Асинхронное получение блокировки на чтение.
    /// Использование: using (await rwlock.ReadLockAsync()) { ... }
    /// </summary>
    public async ValueTask<IDisposable> ReadLockAsync(CancellationToken cancellationToken = default)
    {
        if (_isMockMode) return new DummyDisposable();

        // Проверка из вашей старой логики.
        // Примечание: AsyncReaderWriterLock на самом деле БЕЗОПАСНО позволяет брать лок на чтение 
        // внутри лока на запись, но мы оставляем эту проверку для сохранения старого поведения.
        if (_lockObj.IsWriteLockHeld)
        {
            if (!Defines.IgnoreNonDangerousExceptions)
                NLogger.Error("HALT! DEADLOCK ESCAPE! You tried to enter read lock inner write locked thread!");
            
            return new DummyDisposable();
        }

        try
        {
            // Возвращает AsyncReaderWriterLock.Releaser, который реализует IDisposable
            return await _lockObj.ReadLockAsync(cancellationToken);
        }
        catch (Exception e)
        {
            if (!Defines.IgnoreNonDangerousExceptions)
                NLogger.Error(e);
            return new DummyDisposable();
        }
    }

    /// <summary>
    /// Асинхронное получение блокировки на запись.
    /// Использование: using (await rwlock.WriteLockAsync()) { ... }
    /// </summary>
    public async ValueTask<IDisposable> WriteLockAsync(CancellationToken cancellationToken = default)
    {
        if (_isMockMode) return new DummyDisposable();

        // AsyncReaderWriterLock КАТЕГОРИЧЕСКИ запрещает брать лок на запись, если у потока 
        // уже есть лок на чтение (бросает InvalidOperationException).
        // Исключение: если это специальный UpgradeableReadLock.
        if (_lockObj.IsReadLockHeld && !_lockObj.IsUpgradeableReadLockHeld)
        {
            if (!Defines.IgnoreNonDangerousExceptions)
                NLogger.Error("HALT! DEADLOCK ESCAPE! You tried to enter write lock while read lock is held!");
            
            return new DummyDisposable();
        }

        try
        {
            // Возвращает AsyncReaderWriterLock.Releaser, который реализует IDisposable
            return await _lockObj.WriteLockAsync(cancellationToken);
        }
        catch (Exception e)
        {
            if (!Defines.IgnoreNonDangerousExceptions)
                NLogger.Error(e);
            return new DummyDisposable();
        }
    }

    /// <summary>
    /// Выполнение СИНХРОННОГО экшена под АСИНХРОННЫМ локом на чтение.
    /// </summary>
    public async Task ExecuteReadLockedAsync(Action action, CancellationToken cancellationToken = default)
    {
        using (await ReadLockAsync(cancellationToken))
        {
            action();
        }
    }

    /// <summary>
    /// Выполнение СИНХРОННОГО экшена под АСИНХРОННЫМ локом на запись.
    /// </summary>
    public async Task ExecuteWriteLockedAsync(Action action, CancellationToken cancellationToken = default)
    {
        using (await WriteLockAsync(cancellationToken))
        {
            action();
        }
    }

    /// <summary>
    /// Выполнение АСИНХРОННОГО экшена под локом на чтение.
    /// </summary>
    public async Task ExecuteReadLockedAsync(Func<Task> asyncAction, CancellationToken cancellationToken = default)
    {
        using (await ReadLockAsync(cancellationToken))
        {
            await asyncAction();
        }
    }

    /// <summary>
    /// Выполнение АСИНХРОННОГО экшена под локом на запись.
    /// </summary>
    public async Task ExecuteWriteLockedAsync(Func<Task> asyncAction, CancellationToken cancellationToken = default)
    {
        using (await WriteLockAsync(cancellationToken))
        {
            await asyncAction();
        }
    }

    public void Dispose()
    {
        _lockObj?.Dispose();
    }
}