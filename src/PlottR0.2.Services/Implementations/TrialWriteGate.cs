using System.Collections.Concurrent;
using PlottR0._2.Services.Abstractions;

namespace PlottR0._2.Services.Implementations;

/// <summary>
/// Default <see cref="ITrialWriteGate"/>: one <see cref="SemaphoreSlim"/> (initially 1, max 1)
/// per trial id, entries dropped once no caller holds or waits on them.
/// </summary>
/// <remarks>
/// <para>
/// Entries are reference counted: <see cref="Entry.Users"/> counts the current holder AND
/// every caller still waiting, and is reserved under <see cref="_gate"/> BEFORE awaiting. An
/// entry is therefore removed only when it is truly idle. Removing on "the semaphore looks
/// free" instead is racy — a queued waiter completes the instant the permit is released, and
/// the next caller would then be handed a SECOND semaphore for the same trial, i.e. exactly
/// the overlapping critical section this class exists to prevent.
/// </para>
/// <para>
/// <see cref="_gate"/> guards the reference counts and the add/remove decisions; it does not
/// replace the per-trial semaphore, which is what actually serializes the workbook work.
/// </para>
/// </remarks>
public sealed class TrialWriteGate : ITrialWriteGate
{
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    /// <summary>Guards <see cref="Entry.Users"/> and the add/remove decisions.</summary>
    private readonly object _gate = new();

    /// <summary>
    /// Diagnostic/test hook: the number of trials currently holding an entry — a caller
    /// inside the gate or waiting on one. Returns to zero once every caller has disposed its
    /// handle; that is the bounded-memory guarantee (no entry outlives its users).
    /// </summary>
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

    /// <inheritdoc />
    public async Task<IDisposable> AcquireAsync(Guid trialId, CancellationToken ct = default)
    {
        Entry entry;
        lock (_gate)
        {
            entry = _entries.GetOrAdd(trialId, _ => new Entry());

            // Reserve BEFORE awaiting: a concurrent release may then only retire this entry
            // once we are finished with it, never out from under us.
            entry.Users++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // Nothing was acquired, so nothing may be released — only the reservation goes.
            Retire(trialId, entry, acquired: false);
            throw;
        }

        return new Releaser(this, trialId, entry);
    }

    /// <summary>
    /// Releases the permit when one was taken and drops the caller's reservation; the entry
    /// itself is removed once the last reservation goes.
    /// </summary>
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
                // Idle: no holder and nobody about to become one, because every caller that
                // picked this entry up already incremented Users under this same lock.
                _entries.TryRemove(new KeyValuePair<Guid, Entry>(trialId, entry));
            }
        }
    }

    /// <summary>One serializing semaphore plus its live-caller count.</summary>
    private sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int Users;
    }

    /// <summary>Handle handed out by <see cref="AcquireAsync"/>; idempotent on dispose.</summary>
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

        public void Dispose()
        {
            // A double dispose must not release a permit the next holder may already own nor
            // corrupt the reservation count, so the owner is taken exactly once.
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Retire(_trialId, _entry, acquired: true);
        }
    }
}
