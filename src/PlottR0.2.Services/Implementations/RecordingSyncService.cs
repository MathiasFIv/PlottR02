using Microsoft.Extensions.Logging;
using PlottR0._2.Contracts;
using PlottR0._2.Contracts.Ratings;
using PlottR0._2.Contracts.Recordings;
using PlottR0._2.Contracts.Repositories;
using PlottR0._2.Contracts.Transcription;
using PlottR0._2.Domain;
using PlottR0._2.Services.Abstractions;

namespace PlottR0._2.Services.Implementations;

public sealed class RecordingSyncService : IRecordingSyncService
{
    private readonly IVoiceRecordingRepository _voiceRecordings;
    private readonly IVoiceBlobStore _blobStore;
    private readonly ITranscriptionService _transcriptionService;
    private readonly ITrialRepository _trials;
    private readonly IRatingRepository _ratings;
    private readonly ITrialWorkbookStore _trialWorkbooks;
    private readonly ITrialWriteGate _writeGate;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<RecordingSyncService> _logger;

    public const int MinRating = 0;

    // Mirrors TranscriptParser.ValueCeiling (a drift test pins the two together).
    public const int MaxRating = 10;

    // A parse below this score needs manual review; a repaired (glued-digit) parse is capped at
    // 0.70, so it always lands in review.
    public const double ReviewConfidenceThreshold = 0.75;

    private const int MaxTranscriptionErrorLength = 300;

    private static readonly IReadOnlySet<int> EmptySubsampleSet = new HashSet<int>();

    private static readonly TranscriptParseResult EmptyParse = new();

    public RecordingSyncService(
        IVoiceRecordingRepository voiceRecordings,
        IVoiceBlobStore blobStore,
        ITranscriptionService transcriptionService,
        ITrialRepository trials,
        IRatingRepository ratings,
        ITrialWorkbookStore trialWorkbooks,
        ITrialWriteGate writeGate,
        ICurrentUser currentUser,
        ILogger<RecordingSyncService> logger)
    {
        _voiceRecordings = voiceRecordings;
        _blobStore = blobStore;
        _transcriptionService = transcriptionService;
        _trials = trials;
        _ratings = ratings;
        _trialWorkbooks = trialWorkbooks;
        _writeGate = writeGate;
        _currentUser = currentUser;
        _logger = logger;
    }

    /// <summary>
    /// Idempotent sync (see <see cref="IRecordingSyncService.SyncAsync"/>). The review terms overlap
    /// on purpose: the <c>!IsComplete</c> term is a forward guard for a different failure mode, so it
    /// must not be "cleaned up" merely because the confidence term also fires today.
    /// </summary>
    public async Task<SyncVoiceRecordingResult> SyncAsync(
        Stream audio,
        string fileName,
        SyncVoiceRecordingCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(command);

        var cPlot = command.Plot is int cp && cp > 0 ? cp : (int?)null;
        var cSubsample = command.Subsample is int cs && cs > 0 ? cs : (int?)null;
        var cValue = command.Value;

        var cTraitOrdinal = NormalizeTraitOrdinal(command.TraitOrdinal);

        using var buffered = new MemoryStream();
        await audio.CopyToAsync(buffered, ct);

        var clientId = command.ClientRecordingId.ToString();

        var existing = await _voiceRecordings.GetByClientRecordingIdAsync(clientId, ct);
        if (existing is not null)
        {
            return await ReplayAsync(existing, command, cPlot, cSubsample, cValue, ct);
        }

        var trial = await _trials.GetByIdAsync(command.TrialId, ct);
        if (trial is null)
        {
            throw new KeyNotFoundException($"Trial '{command.TrialId}' was not found.");
        }

        await EnsureCanAccessTrialAsync(trial.Id, ct);

        var mimeType = MimeTypeFromFileName(fileName);

        if (cValue is < MinRating or > MaxRating)
        {
            return await StoreOutOfRangeAsync(
                trial, clientId, mimeType, buffered, command, cPlot, cSubsample, cValue, ct);
        }

        buffered.Position = 0;
        var blobPath = await _blobStore.SaveAsync(trial.Id, clientId, mimeType, buffered, ct);

        var recording = CreateRecording(
            trial.Id, clientId, blobPath, mimeType, cPlot, cSubsample, cValue, cTraitOrdinal,
            command.CreatedAt, VoiceRecordingStatus.Received);
        try
        {
            await _voiceRecordings.CreateAsync(recording, ct);
        }
        catch (ClientRecordingIdConflictException)
        {
            var winner = await _voiceRecordings.GetByClientRecordingIdAsync(clientId, ct);
            if (winner is null)
            {
                throw;
            }

            _logger.LogInformation(
                "Voice recording {ClientRecordingId} raced with a concurrent insert; treated as replay (row {RecordingId}).",
                clientId, winner.Id);

            return await ReplayAsync(winner, command, cPlot, cSubsample, cValue, ct);
        }
        catch (Exception)
        {
            await _blobStore.DeleteAsync(blobPath, ct);
            throw;
        }

        var transcript = string.Empty;
        var transcriptionFailed = false;
        var transcriptionError = string.Empty;
        buffered.Position = 0;
        try
        {
            var transcribed = await _transcriptionService.TranscribeAsync(buffered, fileName, ct);
            transcript = transcribed.Text;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            transcriptionFailed = true;
            transcriptionError = DescribeTranscriptionFailure(ex);
            _logger.LogError(ex,
                "Transcription failed for voice recording {ClientRecordingId}; " +
                "continuing the sync from the resolved fields only.",
                clientId);
        }

        var parsed = TranscriptParser.Parse(transcript);

        if (parsed.Repaired)
        {
            _logger.LogWarning(
                "Voice recording {ClientRecordingId} had glued digits repaired into separate values " +
                "(raw transcript {Transcript}); confidence capped below the review threshold.",
                clientId, transcript);
        }

        var defaultPlot = command.DefaultPlot is int dp && dp > 0 ? dp : (int?)null;
        var defaultSubsample = command.DefaultSubsample is int ds && ds > 0 ? ds : (int?)null;
        var plot = cPlot ?? parsed.Plot ?? defaultPlot;
        var subsample = cSubsample ?? parsed.Subsample;
        if (subsample is null && defaultSubsample is int dsub
            && (parsed.Plot is null || parsed.Plot == defaultPlot))
        {
            subsample = dsub;
        }

        IReadOnlyList<int> values = cValue is int cv ? new[] { cv } : parsed.Values;

        if (values.Count == 0 || values.Any(v => v is < MinRating or > MaxRating))
        {
            recording.Status = VoiceRecordingStatus.Flagged;
            recording.Transcript = transcript;
            recording.ParsedPlot = parsed.Plot;
            recording.ParsedSubsample = parsed.Subsample;
            recording.ParsedValue = parsed.FirstValue;
            recording.Confidence = parsed.Confidence;
            recording.UpdatedAt = DateTime.UtcNow;
            await _voiceRecordings.UpdateAsync(recording, ct);

            _logger.LogWarning(
                "Voice recording {ClientRecordingId} resolved to no legal value " +
                "(confirmed {ConfirmedValue}, parsed values {ParsedValues}); flagged without writing.",
                clientId, cValue, parsed.Values);

            return BuildResult(
                command,
                cPlot, cSubsample, cValue,
                VoiceRecordingStatus.Flagged,
                needsReview: true,
                transcript,
                parsed,
                policy: null,
                submittedAt: DateTimeOffset.UtcNow,
                transcriptionError: transcriptionError);
        }

        var traitOrdinal = NormalizeTraitOrdinal(command.TraitOrdinal);
        if (traitOrdinal > 9)
        {
            recording.Status = VoiceRecordingStatus.Flagged;
            recording.Transcript = transcript;
            recording.ParsedPlot = parsed.Plot;
            recording.ParsedSubsample = parsed.Subsample;
            recording.ParsedValue = parsed.FirstValue;
            recording.Confidence = parsed.Confidence;
            recording.UpdatedAt = DateTime.UtcNow;
            await _voiceRecordings.UpdateAsync(recording, ct);

            _logger.LogWarning(
                "Voice recording {ClientRecordingId} carries traitOrdinal {TraitOrdinal} outside 1..9; flagged without writing.",
                clientId, command.TraitOrdinal);

            return BuildResult(
                command,
                cPlot, cSubsample, cValue,
                VoiceRecordingStatus.Flagged,
                needsReview: true,
                transcript,
                parsed,
                policy: null,
                submittedAt: DateTimeOffset.UtcNow,
                transcriptionError: transcriptionError);
        }

        var subsampleSet = subsample is int s && s > 0
            ? (IReadOnlySet<int>)new HashSet<int> { s }
            : EmptySubsampleSet;

        PolicyWritePlotColumnResult? policy = null;
        if (plot is int resolvedPlot && values.Count > 0 && subsampleSet.Count > 0)
        {
            try
            {
                using var writeGate = await _writeGate.AcquireAsync(trial.Id, ct);

                var outputDirectory = _trialWorkbooks.GetWorkingDirectory(trial.Id);

                var resolvedWorkbook = await _trialWorkbooks.ResolveExportAsync(
                    trial.Id, trial.SourceWorkbookPath ?? string.Empty, ct)
                    ?? throw new FileNotFoundException(
                        "No source workbook is available for the trial.", trial.SourceWorkbookPath);

                policy = _ratings.WritePlotColumnPreserving(
                    resolvedWorkbook.FilePath,
                    outputDirectory,
                    resolvedPlot,
                    traitOrdinal,
                    values,
                    subsampleSet,
                    scanForFreeCells: true);

                if (policy is not null && policy.WrittenCells.Count > 0)
                {
                    await RecomputeTrialStatusAsync(trial, policy.OutputPath!, ct);

                    await _trialWorkbooks.PersistTrialFilesAsync(trial.Id, ct);
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException)
            {
                _logger.LogError(ex,
                    "Policy write could not be performed for voice recording {ClientRecordingId}; flagged for review.",
                    clientId);
            }
        }

        var status = SelectStatus(policy);

        recording.Status = status;
        recording.Transcript = transcript;
        recording.ParsedPlot = parsed.Plot;
        recording.ParsedSubsample = parsed.Subsample;
        recording.ParsedValue = parsed.FirstValue;
        recording.Confidence = parsed.Confidence;
        recording.UpdatedAt = DateTime.UtcNow;
        await _voiceRecordings.UpdateAsync(recording, ct);

        var (usedDefaultPlot, usedDefaultSubsample) = ResolveDefaultUsage(cPlot, cSubsample, parsed, command);
        var usedDefaultTarget = usedDefaultPlot || usedDefaultSubsample;
        var targetFullyKnown = (cPlot is not null || parsed.Plot is not null || usedDefaultPlot)
            && (cSubsample is not null || parsed.Subsample is not null || usedDefaultSubsample);
        var needsReview = transcriptionFailed
            || !targetFullyKnown
            || (!usedDefaultTarget && !parsed.IsComplete)
            || (!usedDefaultTarget && parsed.Confidence < ReviewConfidenceThreshold)
            || MismatchesConfirmed(parsed, cPlot, cSubsample, cValue)
            || status is VoiceRecordingStatus.Flagged or VoiceRecordingStatus.SkippedPresent;

        _logger.LogInformation(
            "Voice recording {ClientRecordingId} synced with status {Status} (needs review: {NeedsReview}).",
            clientId, status, needsReview);

        return BuildResult(
            command,
            cPlot, cSubsample, cValue,
            status,
            needsReview,
            transcript,
            parsed,
            policy,
            submittedAt: DateTimeOffset.UtcNow,
            traitOrdinal,
            transcriptionError: transcriptionError);
    }

    public async Task<SyncVoiceRecordingResult> CorrectAsync(
        string clientRecordingId,
        int plot,
        int subsample,
        int value,
        int traitOrdinal,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(clientRecordingId);

        var cPlot = plot > 0 ? plot : (int?)null;
        var cSubsample = subsample > 0 ? subsample : (int?)null;
        if (cPlot is null || cSubsample is null)
        {
            _logger.LogDebug(
                "Correction for voice recording {ClientRecordingId} discarded non-positive plot {Plot}/subsample {Subsample}; nothing addressable will be written.",
                clientRecordingId, plot, subsample);
        }

        var cValue = value;

        traitOrdinal = NormalizeTraitOrdinal(traitOrdinal);

        var recording = await _voiceRecordings.GetByClientRecordingIdAsync(clientRecordingId, ct);
        if (recording is null)
        {
            throw new KeyNotFoundException($"Voice recording '{clientRecordingId}' was not found.");
        }

        await EnsureCanAccessTrialAsync(recording.TrialId ?? Guid.Empty, ct);

        if (recording.Status is VoiceRecordingStatus.Written or VoiceRecordingStatus.SkippedPresent)
        {
            throw new InvalidOperationException(
                $"Voice recording '{clientRecordingId}' is already {recording.Status} and cannot be corrected.");
        }

        if (cValue is < MinRating or > MaxRating)
        {
            recording.ConfirmedPlot = cPlot;
            recording.ConfirmedSubsample = cSubsample;
            recording.ConfirmedValue = cValue;
            recording.ResolvedTraitOrdinal = traitOrdinal;
            recording.Status = VoiceRecordingStatus.Flagged;
            recording.UpdatedAt = DateTime.UtcNow;
            await _voiceRecordings.UpdateAsync(recording, ct);

            _logger.LogWarning(
                "Correction for voice recording {ClientRecordingId} carries value {Value} outside {MinRating}..{MaxRating}; flagged without writing.",
                clientRecordingId, cValue, MinRating, MaxRating);

            return BuildResult(
                BuildCorrectionCommand(clientRecordingId, recording, traitOrdinal),
                cPlot, cSubsample, cValue,
                VoiceRecordingStatus.Flagged,
                needsReview: true,
                recording.Transcript ?? string.Empty,
                ReconstructParsed(recording),
                policy: null,
                submittedAt: DateTimeOffset.UtcNow);
        }

        if (traitOrdinal > 9)
        {
            recording.ConfirmedPlot = cPlot;
            recording.ConfirmedSubsample = cSubsample;
            recording.ConfirmedValue = cValue;
            recording.ResolvedTraitOrdinal = traitOrdinal;
            recording.Status = VoiceRecordingStatus.Flagged;
            recording.UpdatedAt = DateTime.UtcNow;
            await _voiceRecordings.UpdateAsync(recording, ct);

            _logger.LogWarning(
                "Correction for voice recording {ClientRecordingId} carries traitOrdinal {TraitOrdinal} outside 1..9; flagged without writing.",
                clientRecordingId, traitOrdinal);

            return BuildResult(
                BuildCorrectionCommand(clientRecordingId, recording, traitOrdinal),
                cPlot, cSubsample, cValue,
                VoiceRecordingStatus.Flagged,
                needsReview: true,
                recording.Transcript ?? string.Empty,
                ReconstructParsed(recording),
                policy: null,
                submittedAt: DateTimeOffset.UtcNow);
        }

        var trial = await _trials.GetByIdAsync(recording.TrialId ?? Guid.Empty, ct);
        if (trial is null)
        {
            throw new KeyNotFoundException($"Trial '{recording.TrialId}' was not found.");
        }

        PolicyWritePlotColumnResult? policy = null;
        if (cPlot is int confirmedPlot && cSubsample is int confirmedSubsample)
        {
            try
            {
                using var writeGate = await _writeGate.AcquireAsync(trial.Id, ct);

                var outputDirectory = _trialWorkbooks.GetWorkingDirectory(trial.Id);

                var resolvedWorkbook = await _trialWorkbooks.ResolveExportAsync(
                    trial.Id, trial.SourceWorkbookPath ?? string.Empty, ct)
                    ?? throw new FileNotFoundException(
                        "No source workbook is available for the trial.", trial.SourceWorkbookPath);

                policy = _ratings.WritePlotColumnPreserving(
                    resolvedWorkbook.FilePath,
                    outputDirectory,
                    confirmedPlot,
                    traitOrdinal,
                    new[] { cValue },
                    new HashSet<int> { confirmedSubsample },
                    scanForFreeCells: true);

                if (policy is not null && policy.WrittenCells.Count > 0)
                {
                    await RecomputeTrialStatusAsync(trial, policy.OutputPath!, ct);

                    await _trialWorkbooks.PersistTrialFilesAsync(trial.Id, ct);
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException)
            {
                _logger.LogError(ex,
                    "Correction policy write could not be performed for voice recording {ClientRecordingId}; flagged for review.",
                    clientRecordingId);
            }
        }

        var status = SelectStatus(policy);

        recording.ConfirmedPlot = cPlot;
        recording.ConfirmedSubsample = cSubsample;
        recording.ConfirmedValue = cValue;
        recording.ResolvedTraitOrdinal = traitOrdinal;
        recording.Status = status;
        recording.UpdatedAt = DateTime.UtcNow;
        await _voiceRecordings.UpdateAsync(recording, ct);

        var needsReview = status is VoiceRecordingStatus.Flagged or VoiceRecordingStatus.SkippedPresent;

        _logger.LogInformation(
            "Voice recording {ClientRecordingId} corrected with status {Status} (needs review: {NeedsReview}).",
            clientRecordingId, status, needsReview);

        return BuildResult(
            BuildCorrectionCommand(clientRecordingId, recording, traitOrdinal),
            cPlot, cSubsample, cValue,
            status,
            needsReview,
            recording.Transcript ?? string.Empty,
            ReconstructParsed(recording),
            policy,
            submittedAt: DateTimeOffset.UtcNow);
    }

    private static bool IsManager(UserRole? role)
        => role is UserRole.SystemAdmin or UserRole.Researcher or UserRole.TrialManager;

    private async Task EnsureCanAccessTrialAsync(Guid trialId, CancellationToken ct)
    {
        if (IsManager(_currentUser.Role))
            return;

        if (_currentUser.UserId is not Guid userId
            || !await _trials.IsAssignedAsync(trialId, userId, ct))
        {
            throw new ForbiddenAccessException(
                $"You do not have access to trial '{trialId}'.");
        }
    }

    /// <summary>
    /// Answers a repeat submission without touching the stored row: the loser of an insert race is
    /// treated as a replay of the winner, so a terminal status already persisted is never clobbered.
    /// </summary>
    private async Task<SyncVoiceRecordingResult> ReplayAsync(
        VoiceRecording existing,
        SyncVoiceRecordingCommand command,
        int? cPlot,
        int? cSubsample,
        int? cValue,
        CancellationToken ct)
    {
        await EnsureCanAccessTrialAsync(existing.TrialId ?? Guid.Empty, ct);

        _logger.LogInformation(
            "Voice recording {ClientRecordingId} is a replay (row {RecordingId}); skipped without transcription, write or row update.",
            command.ClientRecordingId, existing.Id);

        return BuildResult(
            command,
            cPlot, cSubsample, cValue,
            VoiceRecordingStatus.Replay,
            needsReview: false,
            transcript: string.Empty,
            parsed: null,
            policy: null,
            submittedAt: DateTimeOffset.UtcNow,
            replayed: true,

            parsedForResolution: ReconstructParsed(existing),

            traitOrdinal: existing.ResolvedTraitOrdinal ?? 1);
    }

    private async Task<SyncVoiceRecordingResult> StoreOutOfRangeAsync(
        Trial trial,
        string clientId,
        string mimeType,
        Stream audio,
        SyncVoiceRecordingCommand command,
        int? cPlot,
        int? cSubsample,
        int? cValue,
        CancellationToken ct)
    {
        audio.Position = 0;
        var blobPath = await _blobStore.SaveAsync(trial.Id, clientId, mimeType, audio, ct);

        var recording = CreateRecording(
            trial.Id, clientId, blobPath, mimeType, cPlot, cSubsample, cValue,
            NormalizeTraitOrdinal(command.TraitOrdinal), command.CreatedAt,
            VoiceRecordingStatus.Flagged);
        try
        {
            await _voiceRecordings.CreateAsync(recording, ct);
        }
        catch (ClientRecordingIdConflictException)
        {
            var winner = await _voiceRecordings.GetByClientRecordingIdAsync(clientId, ct);
            if (winner is null)
            {
                throw;
            }

            _logger.LogInformation(
                "Voice recording {ClientRecordingId} raced with a concurrent insert; treated as replay (row {RecordingId}).",
                clientId, winner.Id);

            return await ReplayAsync(winner, command, cPlot, cSubsample, cValue, ct);
        }
        catch (Exception)
        {
            await _blobStore.DeleteAsync(blobPath, ct);
            throw;
        }

        _logger.LogWarning(
            "Voice recording {ClientRecordingId} carries value {Value} outside {MinRating}..{MaxRating}; stored but flagged.",
            clientId, cValue, MinRating, MaxRating);

        return BuildResult(
            command,
            cPlot, cSubsample, cValue,
            VoiceRecordingStatus.Flagged,
            needsReview: true,
            transcript: string.Empty,
            parsed: null,
            policy: null,
            submittedAt: DateTimeOffset.UtcNow);
    }

    private static string SelectStatus(PolicyWritePlotColumnResult? policy)
    {
        if (policy is null || !policy.PlotExists || policy.MissingSubsampleIndices.Count > 0)
        {
            return VoiceRecordingStatus.Flagged;
        }

        if (policy.OutOfTraitRangeValueCount > 0)
        {
            return VoiceRecordingStatus.Flagged;
        }

        if (policy.WrittenCells.Count > 0)
        {
            return VoiceRecordingStatus.Written;
        }

        return policy.SkippedPresentCells.Count > 0
            ? VoiceRecordingStatus.SkippedPresent
            : VoiceRecordingStatus.Flagged;
    }

    private async Task RecomputeTrialStatusAsync(
        Trial trial,
        string writtenPath,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(writtenPath))
        {
            return;
        }

        SheetRegion? region;
        try
        {
            region = _ratings.ReadSheetRegion(writtenPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException)
        {
            _logger.LogWarning(ex,
                "Could not re-read workbook '{Path}' to recompute trial {TrialId} status; leaving '{Status}' unchanged.",
                writtenPath, trial.Id, trial.Status);
            return;
        }

        var regionUsable = region is not null && region.Columns.Count > 0 && region.Rows.Count > 0;
        var fullyPlotted = regionUsable
            && region!.Rows.All(row =>
                row.Values.Count == region.Columns.Count
                && row.Values.All(value => value.HasValue));

        var newStatus = fullyPlotted
            ? TrialStatus.Completed
            : regionUsable && trial.Status == TrialStatus.Created
                ? TrialStatus.InProgress
                : trial.Status;

        if (newStatus != trial.Status)
        {
            trial.Status = newStatus;
            await _trials.UpdateStatusAsync(trial.Id, newStatus, ct);
            _logger.LogInformation(
                "Trial {TrialId} status advanced to {Status} after a successful workbook write.",
                trial.Id, newStatus);
        }
    }

    private static bool MismatchesConfirmed(
        TranscriptParseResult parsed,
        int? cPlot,
        int? cSubsample,
        int? cValue)
        => (cPlot is int cp && parsed.Plot is int pp && cp != pp)
        || (cSubsample is int cs && parsed.Subsample is int ps && cs != ps)
        || (cValue is int cv && parsed.FirstValue is int pv && cv != pv);

    private static (bool usedDefaultPlot, bool usedDefaultSubsample) ResolveDefaultUsage(
        int? cPlot, int? cSubsample, TranscriptParseResult parsed, SyncVoiceRecordingCommand command)
    {
        var defaultPlot = command.DefaultPlot is int dp && dp > 0 ? dp : (int?)null;
        var defaultSubsample = command.DefaultSubsample is int ds && ds > 0 ? ds : (int?)null;
        var usedDefaultPlot = cPlot is null && parsed.Plot is null && defaultPlot is not null;
        var usedDefaultSubsample = cSubsample is null && parsed.Subsample is null
            && defaultSubsample is not null
            && (parsed.Plot is null || parsed.Plot == defaultPlot);
        return (usedDefaultPlot, usedDefaultSubsample);
    }

    private static int NormalizeTraitOrdinal(int? confirmed)
        => confirmed is int v && v > 1 ? v : 1;

    private static SyncVoiceRecordingResult BuildResult(
        SyncVoiceRecordingCommand command,
        int? cPlot,
        int? cSubsample,
        int? cValue,
        string status,
        bool needsReview,
        string transcript,
        TranscriptParseResult? parsed,
        PolicyWritePlotColumnResult? policy,
        DateTimeOffset submittedAt,
        int? traitOrdinal = null,
        bool replayed = false,
        TranscriptParseResult? parsedForResolution = null,
        string transcriptionError = "")
    {
        var resolutionParsed = parsedForResolution ?? parsed ?? EmptyParse;
        var defaultPlot = command.DefaultPlot is int dp && dp > 0 ? dp : (int?)null;
        var defaultSubsample = command.DefaultSubsample is int ds && ds > 0 ? ds : (int?)null;

        var plot = cPlot ?? resolutionParsed.Plot ?? defaultPlot;
        var subsample = cSubsample ?? resolutionParsed.Subsample;
        if (subsample is null && defaultSubsample is int dsub
            && (resolutionParsed.Plot is null || resolutionParsed.Plot == defaultPlot))
        {
            subsample = dsub;
        }

        var (usedDefaultPlot, usedDefaultSubsample) = ResolveDefaultUsage(
            cPlot, cSubsample, resolutionParsed, command);

        return new()
        {
            ClientRecordingId = command.ClientRecordingId,
            TrialId = command.TrialId,
            Status = status,
            Plot = plot,
            Subsample = subsample,
            Value = cValue ?? parsed?.FirstValue,
            ParsedPlot = parsed?.Plot,
            ParsedSubsample = parsed?.Subsample,
            ParsedValue = parsed?.FirstValue,
            PlotExists = policy?.PlotExists ?? false,
            TraitOrdinal = NormalizeTraitOrdinal(traitOrdinal ?? command.TraitOrdinal),
            NeedsReview = needsReview,
            Replayed = replayed,
            WasTranscriptOnly = cPlot is null && cSubsample is null && cValue is null,
            SubmittedAt = submittedAt,
            Transcript = transcript,
            TranscriptionError = transcriptionError,
            Confidence = parsed?.Confidence,
            WrittenCells = policy?.WrittenCells ?? [],
            SkippedPresentCells = policy?.SkippedPresentCells ?? [],
            MissingSubsampleIndices = policy?.MissingSubsampleIndices ?? [],
            OutOfTraitRangeValueCount = policy?.OutOfTraitRangeValueCount ?? 0,
            UsedDefaultTarget = usedDefaultPlot || usedDefaultSubsample,
        };
    }

    private static string DescribeTranscriptionFailure(Exception ex)
    {
        var message = (ex.Message ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return message.Length > MaxTranscriptionErrorLength ? message[..MaxTranscriptionErrorLength] + "â€¦" : message;
    }

    private static SyncVoiceRecordingCommand BuildCorrectionCommand(
        string clientRecordingId,
        VoiceRecording recording,
        int traitOrdinal)
        => new()
        {
            ClientRecordingId = Guid.Parse(clientRecordingId),
            TrialId = recording.TrialId ?? Guid.Empty,
            TraitOrdinal = traitOrdinal,
        };

    private static TranscriptParseResult ReconstructParsed(VoiceRecording recording)
        => new()
        {
            Plot = recording.ParsedPlot,
            Subsample = recording.ParsedSubsample,
            Values = recording.ParsedValue is int v ? new[] { v } : Array.Empty<int>(),
            Confidence = recording.Confidence ?? 0,
        };

    private static VoiceRecording CreateRecording(
        Guid trialId,
        string clientId,
        string blobPath,
        string mimeType,
        int? confirmedPlot,
        int? confirmedSubsample,
        int? confirmedValue,
        int? resolvedTraitOrdinal,
        DateTimeOffset? createdAt,
        string status)
        => new()
        {
            Id = Guid.NewGuid(),
            ClientRecordingId = clientId,
            TrialId = trialId,
            BlobPath = blobPath,
            MimeType = mimeType,
            Status = status,
            ConfirmedPlot = confirmedPlot,
            ConfirmedSubsample = confirmedSubsample,
            ConfirmedValue = confirmedValue,
            ResolvedTraitOrdinal = resolvedTraitOrdinal,
            CreatedAt = createdAt?.UtcDateTime ?? DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    private static string MimeTypeFromFileName(string fileName)
        => Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".webm" => "audio/webm",
            ".wav" => "audio/wav",
            ".mp4" or ".m4a" => "audio/mp4",
            _ => "application/octet-stream",
        };
}