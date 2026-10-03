using System.Collections.Concurrent;

namespace VdsServiceUpdater.Deploy;

/// <summary>Неблокирующая взаимная блокировка по файлу: второй деплой в тот же файл получает отказ (409), а не очередь.</summary>
public sealed class TargetLocks
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public IDisposable? TryAcquire(string file)
    {
        var sem = _locks.GetOrAdd(Path.GetFullPath(file), _ => new SemaphoreSlim(1, 1));
        return sem.Wait(0) ? new Releaser(sem) : null;
    }

    private sealed class Releaser(SemaphoreSlim sem) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) sem.Release(); }
    }
}
