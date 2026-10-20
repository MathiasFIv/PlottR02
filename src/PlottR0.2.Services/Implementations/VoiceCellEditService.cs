using PlottR0._2.Contracts.Dtos.Trial;
using PlottR0._2.Services.Abstractions;

namespace PlottR0._2.Services.Implementations;

public class VoiceCellEditService : IVoiceCellEditService
{
    private readonly ITranscriptionService _transcription;
    private readonly ITrialService _trialService;

    public VoiceCellEditService(ITranscriptionService transcription, ITrialService trialService)
    {
        _transcription = transcription;
        _trialService = trialService;
    }

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

        if (request.Plot <= 0)
            throw new ArgumentException("Plot must be greater than zero.", nameof(request));
        if (request.Subsample <= 0)
            throw new ArgumentException("Subsample must be greater than zero.", nameof(request));
        if (request.TraitOrdinal is < 1 or > 9)
            throw new ArgumentException("TraitOrdinal must be within 1..9.", nameof(request));

        await _trialService.EnsureTrialAccessAsync(trialId, ct);

        var transcription = await _transcription.TranscribeAsync(audio, fileName, ct);
        var parsed = TranscriptParser.Parse(transcription.Text);

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
