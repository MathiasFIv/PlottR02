namespace PlottR0._2.Services.Abstractions;

/// <summary>
/// Serializes the whole read-modify-write of a trial workbook, one writer at a time per trial:
/// concurrent syncs, corrections and manual edits all contend here, so none of them resolve the
/// same input copy and silently drop the other's write.
/// </summary>
public interface ITrialWriteGate
{
    Task<IDisposable> AcquireAsync(Guid trialId, CancellationToken ct = default);
}
