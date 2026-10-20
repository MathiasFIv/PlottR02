namespace PlottR0._2.Contracts.Repositories;

public interface IVoiceBlobStore
{
    Task<string> SaveAsync(Guid trialId, string clientRecordingId, string mimeType, Stream content, CancellationToken ct = default);

    Task DeleteAsync(string relativePath, CancellationToken ct = default);
}
