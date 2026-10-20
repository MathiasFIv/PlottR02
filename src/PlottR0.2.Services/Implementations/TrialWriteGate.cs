using System.Collections.Concurrent;
using PlottR0._2.Services.Abstractions;

namespace PlottR0._2.Services.Implementations;

/// <summary>
/// One reference-counted semaphore per trial: the entry is reserved before awaiting, so a
/// concurrent release can only retire it once the last holder has left — never out from under a
/// waiter. Registered as a singleton so the semaphores are shared by every scope.
/// </summary>
public sealed class TrialWriteGate : ITrialWriteGate
{
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    private readonly object _gate = new();

    public int TrackedTrialCount
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public async Task<IDisposable> AcquireAsync(Guid trialId, CancellationToken ct = default)
    {
        Entry entry;
        lock (_gate)
        {
            entry = _entries.GetOrAdd(trialId, _ => new Entry());

            entry.Users++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            Retire(trialId, entry, acquired: false);
            throw;
        }

        return new Releaser(this, trialId, entry);
    }

    private void Retire(Guid trialId, Entry entry, bool acquired)
    {
        if (acquired)
        {
            entry.Semaphore.Release();
        }

        lock (_gate)
        {
            entry.Users--;
            if (entry.Users == 0)
            {
                _entries.TryRemove(new KeyValuePair<Guid, Entry>(trialId, entry));
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int Users;
    }

    private sealed class Releaser : IDisposable
    {
        private readonly Guid _trialId;
        private readonly Entry _entry;
        private TrialWriteGate? _owner;

        public Releaser(TrialWriteGate owner, Guid trialId, Entry entry)
        {
            _owner = owner;
            _trialId = trialId;
            _entry = entry;
        }

        // The owner is taken exactly once, so a double dispose cannot hand out a permit the next
        // holder may already own nor corrupt the reservation count.
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Retire(_trialId, _entry, acquired: true);
        }
    }
}
