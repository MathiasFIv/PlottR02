using PlottR0._2.Contracts.Recordings;
using PlottR0._2.Domain;

namespace PlottR0._2.Services.Abstractions;

public interface IRecordingSyncService
{
    /// <summary>
    /// Idempotent on the PWA's client recording id: a repeat call is answered as a replay of the
    /// committed row (no transcription, no second blob, no workbook write, and the stored row is
    /// never touched). A lost insert race on that key is answered the same way, never as an error.
    /// </summary>
    Task<SyncVoiceRecordingResult> SyncAsync(
        Stream audio,
        string fileName,
        SyncVoiceRecordingCommand command,
        CancellationToken ct = default);

    Task<SyncVoiceRecordingResult> CorrectAsync(
        string clientRecordingId,
        int plot,
        int subsample,
        int value,
        int traitOrdinal,
        CancellationToken ct = default);
}