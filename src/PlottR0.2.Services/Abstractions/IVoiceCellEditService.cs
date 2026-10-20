using PlottR0._2.Contracts.Dtos.Trial;

namespace PlottR0._2.Services.Abstractions;

public interface IVoiceCellEditService
{
    /// <summary>
    /// Checks trial access before transcription, so an unauthorised caller never reaches the
    /// speech-to-text provider. The write itself goes through the normal cell-edit path, keeping
    /// the write gate, overwrite refusal, audit trail and versioning in one place.
    /// </summary>
    Task<VoiceCellEditResult> TranscribeAndWriteAsync(
        Guid trialId,
        VoiceCellEditRequest request,
        Stream audio,
        string fileName,
        CancellationToken ct = default);
}
