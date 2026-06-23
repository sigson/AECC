using System;
using System.Threading;

/// <summary>
/// No-op реализация IReaderWriterLockSlim для OneThreadMode.
/// Все операции захвата/освобождения — пустышки, попытки — успех, счётчики — 0.
/// Никаких NotImplementedException: в однопоточном режиме это единственный путь.
/// </summary>
public class MockReaderWriterLockSlim : IReaderWriterLockSlim
{
    public int WaitingReadCount => 0;
    public int RecursiveWriteCount => 0;
    public int RecursiveUpgradeCount => 0;
    public int RecursiveReadCount => 0;
    public LockRecursionPolicy RecursionPolicy => LockRecursionPolicy.SupportsRecursion;
    public bool IsWriteLockHeld => false;
    public bool IsUpgradeableReadLockHeld => false;
    public bool IsReadLockHeld => false;
    public int CurrentReadCount => 0;
    public int WaitingUpgradeCount => 0;
    public int WaitingWriteCount => 0;

    public void Dispose() { }

    public void EnterReadLock() { }
    public void EnterUpgradeableReadLock() { }
    public void EnterWriteLock() { }
    public void ExitReadLock() { }
    public void ExitUpgradeableReadLock() { }
    public void ExitWriteLock() { }

    public bool TryEnterReadLock(TimeSpan timeout) => true;
    public bool TryEnterReadLock(int millisecondsTimeout) => true;
    public bool TryEnterUpgradeableReadLock(int millisecondsTimeout) => true;
    public bool TryEnterUpgradeableReadLock(TimeSpan timeout) => true;
    public bool TryEnterWriteLock(int millisecondsTimeout) => true;
    public bool TryEnterWriteLock(TimeSpan timeout) => true;
}
