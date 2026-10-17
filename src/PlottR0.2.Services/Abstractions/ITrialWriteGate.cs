namespace PlottR0._2.Services.Abstractions;

/// <summary>
/// Serializes the read-modify-write critical section that mutates a trial's rating workbook.
/// </summary>
/// <remarks>
/// <para>
/// Every workbook mutation resolves the newest accumulated <c>*_out_*.xlsx</c> copy, writes
/// into it and exports a NEW copy named from the input copy plus a second-granular timestamp.
/// Two mutations of the same trial that run concurrently therefore resolve the SAME input
/// copy, and the later export silently overwrites the earlier one — a lost update. Holding
/// this gate for the duration of the critical section orders the writers of one trial strictly
/// and chains their copies, while writers of different trials keep running in parallel.
/// </para>
/// <para>
/// Serialization is per PROCESS. That is the intended scope: the gate protects the
/// single-instance application (registered as a singleton in
/// <c>ServiceCollectionExtensions.ConfigureApplication</c>), it is not a distributed lock.
/// Coordination across several instances would need an optimistic concurrency token
/// (<c>expectedVersion</c>) on the write seam — a deliberate v2 follow-up, not implemented here.
/// </para>
/// </remarks>
public interface ITrialWriteGate
{
    /// <summary>
    /// Waits until <paramref name="trialId"/> is free, then holds it until the returned
    /// handle is disposed. Dispose the handle exactly once (a <c>using</c> around the
    /// critical section). Cancellation while waiting acquires nothing and throws.
    /// </summary>
    /// <param name="trialId">The trial whose writers must be ordered.</param>
    /// <param name="ct">Aborts the wait; never releases a permit held by someone else.</param>
    /// <returns>A handle whose <see cref="IDisposable.Dispose"/> releases the trial.</returns>
    Task<IDisposable> AcquireAsync(Guid trialId, CancellationToken ct = default);
}
