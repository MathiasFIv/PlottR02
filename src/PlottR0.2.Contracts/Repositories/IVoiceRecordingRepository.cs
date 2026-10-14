using PlottR0._2.Domain;

namespace PlottR0._2.Contracts.Repositories;

public interface IVoiceRecordingRepository : IRepository<VoiceRecording, Guid>
{
    Task<VoiceRecording> CreateAsync(VoiceRecording recording, CancellationToken ct = default);
    Task<VoiceRecording?> GetByClientRecordingIdAsync(string clientRecordingId, CancellationToken ct = default);
    Task UpdateAsync(VoiceRecording recording, CancellationToken ct = default);
}
