using System.Collections.Concurrent;

namespace VertexAI.Services.Runs;

/// <summary>
/// One-at-a-time access per key inside this process. Concurrent admissions of the same run id would otherwise contend on
/// the same storage documents; serializing them here makes all but the first a cheap replay. It is an optimization, not
/// the guarantee: the admission transaction itself decides, across processes.
/// </summary>
internal sealed class KeyedLock
{
    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public int Users;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public async Task<IDisposable> AcquireAsync(string key, CancellationToken cancellationToken)
    {
        Entry entry;
        while (true)
        {
            entry = _entries.GetOrAdd(key, _ => new Entry());
            lock (entry)
            {
                if (entry.Users >= 0)
                {
                    entry.Users++;
                    break;
                }
            }
        }

        try
        {
            await entry.Gate.WaitAsync(cancellationToken);
        }
        catch
        {
            Release(key, entry);
            throw;
        }

        return new Releaser(this, key, entry);
    }

    private void Release(string key, Entry entry)
    {
        lock (entry)
        {
            entry.Users--;
            if (entry.Users == 0)
            {
                entry.Users = -1; // retired: late arrivals take a fresh entry
                _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
            }
        }
    }

    private sealed class Releaser(KeyedLock owner, string key, Entry entry) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            entry.Gate.Release();
            owner.Release(key, entry);
        }
    }
}
