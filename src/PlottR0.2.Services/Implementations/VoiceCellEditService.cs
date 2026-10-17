using PlottR0._2.Contracts.Dtos.Trial;
using PlottR0._2.Services.Abstractions;

namespace PlottR0._2.Services.Implementations;

/// <summary>
/// Concrete <see cref="IVoiceCellEditService"/>: transcribes one uploaded clip, parses ONE
/// rating number (0..10) out of the transcript and writes it into the explicitly addressed
/// cell by REUSING <see cref="ITrialService.EditCellAsync"/> — so the per-trial access gate,
/// the per-trial write serialization, the durable mirror, the audit row and the opaque
/// version all stay exactly where they already live. No workbook, blob or DB access here.
/// <para/>
/// The trial access/existence pre-check runs BEFORE transcription so an unauthorized caller
/// or unknown trial never reaches the speech-to-text provider.
/// </summary>
public class VoiceCellEditService : IVoiceCellEditService
{
    private readonly ITranscriptionService _transcription;
    private readonly ITrialService _trialService;

    public VoiceCellEditService(ITranscriptionService transcription, ITrialService trialService)
    {
        _transcription = transcription;
        _trialService = trialService;
    }

    /// <inheritdoc />
    public async Task<VoiceCellEditResult> TranscribeAndWriteAsync(
        Guid trialId,
        VoiceCellEditRequest request,
        Stream audio,
        string fileName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        // Validate the addressed cell BEFORE any lookup or transcription — same messages
        // and same order as ITrialService.EditCellAsync.
        if (request.Plot <= 0)
            throw new ArgumentException("Plot must be greater than zero.", nameof(request));
        if (request.Subsample <= 0)
            throw new ArgumentException("Subsample must be greater than zero.", nameof(request));
        if (request.TraitOrdinal is < 1 or > 9)
            throw new ArgumentException("TraitOrdinal must be within 1..9.", nameof(request));

        // Access + existence pre-check BEFORE spending STT: same existence-check-wins-then-
        // per-trial-access gates EditCellAsync enforces, exposed for fail-fast.
        await _trialService.EnsureTrialAccessAsync(trialId, ct);

        var transcription = await _transcription.TranscribeAsync(audio, fileName, ct);
        var parsed = TranscriptParser.Parse(transcription.Text);

        // Single-cell contract: exactly ONE number must be understood. A glued multi-digit
        // token that only the ceiling-bounded repair could re-split (Repaired) is NOT a
        // confident single number — report it as not-understood so the UI asks for a retry
        // rather than writing an arbitrarily chosen digit.
        if (parsed.FirstValue is not int value || parsed.Repaired)
        {
            return new VoiceCellEditResult(
                trialId.ToString(),
                request.Plot,
                request.Subsample,
                request.TraitOrdinal,
                Value: null,
                VoiceCellEditResult.StatusNotUnderstood,
                Written: false,
                Skipped: true,
                SkippedReason: null,
                PriorValue: null,
                CellAddress: string.Empty,
                Version: string.Empty,
                FileName: null,
                transcription.Text,
                DateTime.UtcNow);
        }

        // Reuse the full single-cell edit path (access gate, write gate, durable mirror,
        // audit row, version) — the voice flow is a value source, not a second write path.
        var cell = await _trialService.EditCellAsync(
            trialId,
            new EditCellRequest
            {
                Plot = request.Plot,
                Subsample = request.Subsample,
                TraitOrdinal = request.TraitOrdinal,
                Value = value,
                Overwrite = request.Overwrite,
            },
            ct);

        return new VoiceCellEditResult(
            cell.TrialId,
            cell.Plot,
            cell.Subsample,
            cell.TraitOrdinal,
            Value: value,
            cell.Written
                ? VoiceCellEditResult.StatusWritten
                : cell.SkippedReason ?? VoiceCellEditResult.StatusUnexpected,
            cell.Written,
            cell.Skipped,
            cell.SkippedReason,
            cell.PriorValue,
            cell.CellAddress,
            cell.Version,
            cell.FileName,
            transcription.Text,
            DateTime.UtcNow);
    }
}
