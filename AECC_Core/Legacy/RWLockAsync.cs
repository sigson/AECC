using System;
using System.Threading;
using System.Threading.Tasks;
using AECC.Core.Logging;

#if NET5_0_OR_GREATER
using Microsoft.VisualStudio.Threading;
#endif

public class RWLockAsync : IDisposable
{
#if NET5_0_OR_GREATER

    private readonly AsyncReaderWriterLock _lockObj;
    private readonly bool _isMockMode;

    private struct DummyDisposable : IDisposable
    {
        public void Dispose() { }
    }

    public RWLockAsync()
    {
        if (Defines.OneThreadMode)
        {
            _isMockMode = true;
        }
        else
        {
            _lockObj = new AsyncReaderWriterLock();
        }
    }

    public async ValueTask<IDisposable> ReadLockAsync(CancellationToken cancellationToken = default)
    {
        if (_isMockMode) return new DummyDisposable();

        if (_lockObj.IsWriteLockHeld)
        {
            if (!Defines.IgnoreNonDangerousExceptions)
                NLogger.Error("HALT! DEADLOCK ESCAPE! You tried to enter read lock inner write locked thread!");

            return new DummyDisposable();
        }

        try
        {
            return await _lockObj.ReadLockAsync(cancellationToken);
        }
        catch (Exception e)
        {
            if (!Defines.IgnoreNonDangerousExceptions)
                NLogger.Error(e);
            return new DummyDisposable();
        }
    }

    public async ValueTask<IDisposable> WriteLockAsync(CancellationToken cancellationToken = default)
    {
        if (_isMockMode) return new DummyDisposable();

        if (_lockObj.IsReadLockHeld && !_lockObj.IsUpgradeableReadLockHeld)
        {
            if (!Defines.IgnoreNonDangerousExceptions)
                NLogger.Error("HALT! DEADLOCK ESCAPE! You tried to enter write lock while read lock is held!");

            return new DummyDisposable();
        }

        try
        {
            return await _lockObj.WriteLockAsync(cancellationToken);
        }
        catch (Exception e)
        {
            if (!Defines.IgnoreNonDangerousExceptions)
                NLogger.Error(e);
            return new DummyDisposable();
        }
    }

    public void Dispose()
    {
        _lockObj?.Dispose();
    }

#else // !NET5_0_OR_GREATER — заглушка

    /// <summary>
    /// Мок-аналог Awaitable/Releaser: ничего не делает, но поддерживает using (await ...) паттерн.
    /// </summary>
    public readonly struct LockReleaser : IDisposable
    {
        public void Dispose() { }
    }

    public RWLockAsync() { }

    public ValueTask<IDisposable> ReadLockAsync(CancellationToken cancellationToken = default)
    {
        return new ValueTask<IDisposable>(new LockReleaser());
    }

    public ValueTask<IDisposable> WriteLockAsync(CancellationToken cancellationToken = default)
    {
        return new ValueTask<IDisposable>(new LockReleaser());
    }

    public void Dispose() { }

#endif

    // —— Общие хелперы (одинаковы для обеих веток) ——

    public async Task ExecuteReadLockedAsync(Action action, CancellationToken cancellationToken = default)
    {
        using (await ReadLockAsync(cancellationToken))
        {
            action();
        }
    }

    public async Task ExecuteWriteLockedAsync(Action action, CancellationToken cancellationToken = default)
    {
        using (await WriteLockAsync(cancellationToken))
        {
            action();
        }
    }

    public async Task ExecuteReadLockedAsync(Func<Task> asyncAction, CancellationToken cancellationToken = default)
    {
        using (await ReadLockAsync(cancellationToken))
        {
            await asyncAction();
        }
    }

    public async Task ExecuteWriteLockedAsync(Func<Task> asyncAction, CancellationToken cancellationToken = default)
    {
        using (await WriteLockAsync(cancellationToken))
        {
            await asyncAction();
        }
    }
}