using System;
using System.Threading;
using BitCollections.Special;

public class MetricIndex
{
    // Неизменяемый снимок для потоков чтения.
    // Volatile гарантирует чтение самой свежей ссылки.
    private volatile RoaringBitmap _bitmap = RoaringBitmap.Create();
    
    // L0 Буфер (MemTable)
    private readonly int[] _memTable;
    private int _memTableCount = 0;
    private readonly object _flushLock = new object(); // Блокировка только для писателей микро-шарда

    public MetricIndex(int bufferSize = 2048)
    {
        _memTable = new int[bufferSize];
    }

    // Снимок для мгновенного lock-free чтения
    public RoaringBitmap Snapshot => _bitmap;

    // Путь записи: O(1) добавление в буфер без выделения памяти
    public void Add(int objectId)
    {
        lock (_flushLock)
        {
            _memTable[_memTableCount++] = objectId;
            // При достижении лимита инициируем сброс
            if (_memTableCount >= _memTable.Length)
            {
                FlushInternal();
            }
        }
    }

    // Принудительный сброс (используется перед точным поиском)
    public void Flush()
    {
        if (_memTableCount == 0) return;
        lock (_flushLock)
        {
            FlushInternal();
        }
    }

    private void FlushInternal()
    {
        if (_memTableCount == 0) return;

        // Формируем горизонтальную дельту
        int[] deltaArray = new int[_memTableCount];
        Array.Copy(_memTable, deltaArray, _memTableCount);
        var delta = RoaringBitmap.Create(deltaArray);

        // MVCC In-Place обновление без блокировки читателей
        var current = _bitmap;
        while (true)
        {
            var next = current | delta; // Создает новую иммутабельную версию
            var actual = Interlocked.CompareExchange(ref _bitmap, next, current);
            if (actual == current) break;
            current = actual; // Если другой поток успел обновить, повторяем с новой базой
        }

        _memTableCount = 0;
    }

    public void Remove(int objectId)
    {
        // Сначала сбрасываем то, что было в буфере
        lock (_flushLock) { FlushInternal(); }

        var current = _bitmap;
        while (true)
        {
            // AndNot вычитает бит objectId из текущего битмапа
            var next = RoaringBitmap.AndNot(current, RoaringBitmap.Create(new[] { objectId }));
            var actual = Interlocked.CompareExchange(ref _bitmap, next, current);
            if (actual == current) break;
            current = actual;
        }
    }
}