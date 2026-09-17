using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PlottR0._2.Contracts.Ratings;
using PlottR0._2.Contracts.Recordings;
using PlottR0._2.Contracts.Repositories;
using PlottR0._2.Contracts.Transcription;
using PlottR0._2.Domain;
using PlottR0._2.Services.Abstractions;

namespace PlottR0._2.Services.Implementations;

/// <summary>
/// Concrete <see cref="IRecordingSyncService"/> that wires the V2E "send now" pipeline
/// end-to-end (design.md V2E auto-sync, locked plan):
/// <list type="number">
///   <item><description>Normalize the confirmed fields (plot/subsample ≤ 0 are treated as
///   unconfirmed so the transcript becomes authoritative; value keeps 0 as legal).</description></item>
///   <item><description>Buffer the audio into a rewindable stream.</description></item>
///   <item><description>Idempotency gate on <see cref="VoiceRecording.ClientRecordingId"/> —
///   a repeat key short-circuits as a <see cref="VoiceRecordingStatus.Replay"/>.</description></item>
///   <item><description>Trial gate — unknown trials throw <see cref="KeyNotFoundException"/>.</description></item>
///   <item><description>Value gate (A) — out-of-range confirmed values are stored but never
///   transcribed/written.</description></item>
///   <item><description>Persist the audio blob via <see cref="IVoiceBlobStore"/>.</description></item>
///   <item><description>Create the <c>VoiceRecording</c> audit row (task contract: the row exists before any scoring).</description></item>
///   <item><description>Transcribe; a sidecar failure degrades to an empty transcript.</description></item>
///   <item><description>Parse the transcript via <see cref="TranscriptParser"/>.</description></item>
///   <item><description>Value gate (B) — when NO legal value can be resolved (confirmed or
///   parsed), the recording is flagged for review and never written.</description></item>
///   <item><description>Single-cell locked-overwrite policy write of the RESOLVED
///   plot/subsample/value into a workbook copy. The read source chains: the newest existing
///   <c>*_out_*.xlsx</c> copy in the trial's working directory is the input for the next
///   sync; falls back to the pristine <c>SourceWorkbookPath</c> when no copy exists yet.</description></item>
///   <item><description>Commit decision: <c>Written</c> / <c>SkippedPresent</c> / <c>Flagged</c>.</description></item>
///   <item><description>Contract update — persist status, parsed fields, transcript, confidence.</description></item>
///   <item><description>Build and return the <see cref="SyncVoiceRecordingResult"/>.</description></item>
/// </list>
/// Corrections (<see cref="CorrectAsync"/>) reuse the stored blob, row and transcript —
/// no re-upload or re-transcription ever happens on the correction path.
/// Uses only repository/service contracts. The trial workbook is addressed by an ABSOLUTE
/// path resolved against the repo-root <c>Service/</c> directory (the same walk
/// <c>TrialWorkbookStore</c>/<c>ClosedXmlRatingRepository</c> perform), so this layer stays
/// free of Repository-project references.
/// </summary>
public sealed class RecordingSyncService : IRecordingSyncService
{
    private readonly IVoiceRecordingRepository _voiceRecordings;
    private readonly IVoiceBlobStore _blobStore;
    private readonly ITranscriptionService _transcriptionService;
    private readonly ITrialRepository _trials;
    private readonly IRatingRepository _ratings;
    private readonly ILogger<RecordingSyncService> _logger;

    /// <summary>Lowest legal rating value (design.md §9).</summary>
    public const int MinRating = 0;

    /// <summary>Highest legal rating value (design.md §9).</summary>
    public const int MaxRating = 10;

    /// <summary>
    /// Complete parses scoring below this confidence require manual review — the write still
    /// proceeds from the resolved (confirmed ?? parsed) fields.
    /// </summary>
    public const double ReviewConfidenceThreshold = 0.75;

    /// <summary>
    /// Empty subsample set used when no legal subsample can be resolved — the write attempt
    /// is skipped and the recording is flagged rather than writing to every subsample.
    /// </summary>
    private static readonly IReadOnlySet<int> EmptySubsampleSet = new HashSet<int>();

    /// <summary>
    /// Creates a new <see cref="RecordingSyncService"/>.
    /// </summary>
    /// <param name="voiceRecordings">Repository of <c>VoiceRecording</c> audit rows.</param>
    /// <param name="blobStore">File-system blob store for the raw audio.</param>
    /// <param name="transcriptionService">Speech-to-text sidecar gateway.</param>
    /// <param name="trials">Trial repository used for the trial gate and workbook path.</param>
    /// <param name="ratings">Workbook-backed rating repository performing the policy write.</param>
    /// <param name="logger">Logger for sync diagnostics.</param>
    public RecordingSyncService(
        IVoiceRecordingRepository voiceRecordings,
        IVoiceBlobStore blobStore,
        ITranscriptionService transcriptionService,
        ITrialRepository trials,
        IRatingRepository ratings,
        ILogger<RecordingSyncService> logger)
    {
        _voiceRecordings = voiceRecordings;
        _blobStore = blobStore;
        _transcriptionService = transcriptionService;
        _trials = trials;
        _ratings = ratings;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SyncVoiceRecordingResult> SyncAsync(
        Stream audio,
        string fileName,
        SyncVoiceRecordingCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(command);

        // Step 0 — Normalize the confirmed fields BEFORE any gate (design.md §3). A null OR
        // non-positive plot/subsample means "not confirmed" so the transcript becomes
        // authoritative for that field; value 0 is a legal "no plants" rating and is NEVER
        // treated as absent. All later decisions use these normalized values (c*), never the
        // raw command fields, so an all-zeros legacy payload degrades to a transcript-only sync.
        var cPlot = command.Plot is int cp && cp > 0 ? cp : (int?)null;
        var cSubsample = command.Subsample is int cs && cs > 0 ? cs : (int?)null;
        var cValue = command.Value;

        // Step 1 — Buffer: the blob store and the transcription service each consume the
        // stream, so one rewindable buffer feeds both without re-reading the caller's source.
        using var buffered = new MemoryStream();
        await audio.CopyToAsync(buffered, ct);

        var clientId = command.ClientRecordingId.ToString();

        // Step 2 — Idempotency gate: the client recording id is the PWA idempotency key.
        // A repeat short-circuits BEFORE any blob/workbook work; the existing row is simply
        // re-marked Replay and the caller gets a Replayed result.
        var existing = await _voiceRecordings.GetByClientRecordingIdAsync(clientId, ct);
        if (existing is not null)
        {
            return await ReplayAsync(existing, command, cPlot, cSubsample, cValue, ct);
        }

        // Step 3 — Trial gate: an unknown trial is a hard client error, not a flaggable sync.
        var trial = await _trials.GetByIdAsync(command.TrialId, ct);
        if (trial is null)
        {
            throw new KeyNotFoundException($"Trial '{command.TrialId}' was not found.");
        }

        var mimeType = MimeTypeFromFileName(fileName);

        // Step 4 — Value gate A: an out-of-range CONFIRMED value is never transcribed or
        // written, but the recording is STILL stored and an audit row created (Status=Flagged)
        // so nothing is lost.
        if (cValue is < MinRating or > MaxRating)
        {
            return await StoreOutOfRangeAsync(
                trial, clientId, mimeType, buffered, command, cPlot, cSubsample, cValue, ct);
        }

        // Step 5 — Blob save: persist the raw audio for audit/re-transcription.
        buffered.Position = 0;
        var blobPath = await _blobStore.SaveAsync(trial.Id, clientId, mimeType, buffered, ct);

        // Step 6 — Audit row: the VoiceRecording row (Received) exists BEFORE any downstream
        // work and carries the NORMALIZED confirmed fields from the command (contract truth).
        var recording = CreateRecording(
            trial.Id, clientId, blobPath, mimeType, cPlot, cSubsample, cValue, command.CreatedAt,
            VoiceRecordingStatus.Received);
        try
        {
            await _voiceRecordings.CreateAsync(recording, ct);
        }
        catch (DbUpdateException ex) when (IsClientRecordingIdConflict(ex))
        {
            // TOCTOU: a concurrent double-submit raced past the idempotency gate and committed
            // first. Its row owns the same blob path (same client key), so the blob stays and
            // the loser is answered as a Replay without touching the winner's row.
            var winner = await _voiceRecordings.GetByClientRecordingIdAsync(clientId, ct);
            if (winner is null)
            {
                throw; // The conflict row vanished — surface the original failure.
            }

            _logger.LogInformation(
                "Voice recording {ClientRecordingId} raced with a concurrent insert; treated as replay (row {RecordingId}).",
                clientId, winner.Id);

            return await ReplayAsync(winner, command, cPlot, cSubsample, cValue, ct);
        }
        catch (Exception)
        {
            // The audit row never committed — delete the orphaned blob so the working
            // directory does not accumulate unreferenced audio, then rethrow.
            await _blobStore.DeleteAsync(blobPath, ct);
            throw;
        }

        // Step 7 — Transcribe. A sidecar failure (downtime/timeout) degrades to an empty
        // transcript; the sync then proceeds from the resolved fields and the recording is
        // flagged for review. Only the documented failure signals are caught — programming
        // errors must surface.
        var transcript = string.Empty;
        var transcriptionFailed = false;
        buffered.Position = 0;
        try
        {
            var transcribed = await _transcriptionService.TranscribeAsync(buffered, fileName, ct);
            transcript = transcribed.Text;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A caller-cancelled request must propagate BEFORE the sidecar-outage catch
            // below swallows TaskCanceledException as a degradation.
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            transcriptionFailed = true;
            _logger.LogError(ex,
                "Transcription failed for voice recording {ClientRecordingId}; " +
                "continuing the sync from the resolved fields only.",
                clientId);
        }

        // Step 8 — Parse: format-only by contract — range/existence validation happens
        // against the workbook in the policy write and the review gate below.
        var parsed = TranscriptParser.Parse(transcript);

        // Step 9 — Resolution (design.md §3): the confirmed field wins per field; a missing
        // or non-positive confirmed field falls back to the parsed transcript value.
        var plot = cPlot ?? parsed.Plot;
        var subsample = cSubsample ?? parsed.Subsample;
        var value = cValue ?? parsed.Value;

        // Step 10 — Value gate B: when NO legal value can be resolved (no confirmed value
        // and the transcript stated none, or an out-of-range parsed value), the recording is
        // flagged for review and NEVER written — a rating sheet must not receive an invalid
        // or guessed value.
        if (value is null or < MinRating or > MaxRating)
        {
            recording.Status = VoiceRecordingStatus.Flagged;
            recording.Transcript = transcript;
            recording.ParsedPlot = parsed.Plot;
            recording.ParsedSubsample = parsed.Subsample;
            recording.ParsedValue = parsed.Value;
            recording.Confidence = parsed.Confidence;
            recording.UpdatedAt = DateTime.UtcNow;
            await _voiceRecordings.UpdateAsync(recording, ct);

            _logger.LogWarning(
                "Voice recording {ClientRecordingId} resolved to no legal value " +
                "(confirmed {ConfirmedValue}, parsed {ParsedValue}); flagged without writing.",
                clientId, cValue, parsed.Value);

            return BuildResult(
                command,
                cPlot, cSubsample, cValue,
                VoiceRecordingStatus.Flagged,
                needsReview: true,
                transcript,
                parsed,
                policy: null,
                submittedAt: DateTimeOffset.UtcNow);
        }

        // Step 11 — Single-cell policy write (design.md §3): the sync offers exactly the
        // RESOLVED subsample. An unconfirmed-and-unparsed plot or subsample leaves nothing
        // addressable, so the write attempt is skipped and the outcome is Flagged.
        var subsampleSet = subsample is int s && s > 0
            ? (IReadOnlySet<int>)new HashSet<int> { s }
            : EmptySubsampleSet;

        PolicyWritePlotColumnResult? policy = null;
        if (plot is int resolvedPlot && value is int resolvedValue && subsampleSet.Count > 0)
        {
            try
            {
                var serviceDir = ResolveServiceDirectory();
                var outputDirectory = Path.Combine(serviceDir, "Working", trial.Id.ToString());

                // Successive syncs chain the read source: the newest output copy (if any) is
                // the input, so a later recording sees cells written by an earlier one. Without
                // a copy yet, the pristine source workbook is used.
                var workbookPath = TryGetNewestOutputCopy(outputDirectory)
                    ?? Path.GetFullPath(Path.Combine(serviceDir, trial.SourceWorkbookPath ?? string.Empty));

                policy = _ratings.WritePlotColumnPreserving(
                    workbookPath,
                    outputDirectory,
                    resolvedPlot,
                    command.TraitOrdinal,
                    resolvedValue,
                    subsampleSet);
            }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException)
            {
                _logger.LogError(ex,
                    "Policy write could not be performed for voice recording {ClientRecordingId}; flagged for review.",
                    clientId);
            }
        }

        // Step 12 — Commit decision (design.md V2E flowchart): write what is free, skip what
        // is already present, flag what cannot be written.
        var status = SelectStatus(policy);

        // Step 13 — Contract update: persist the terminal status, the parse outcome and the
        // transcript alongside the confirmed fields already stored on the row.
        recording.Status = status;
        recording.Transcript = transcript;
        recording.ParsedPlot = parsed.Plot;
        recording.ParsedSubsample = parsed.Subsample;
        recording.ParsedValue = parsed.Value;
        recording.Confidence = parsed.Confidence;
        recording.UpdatedAt = DateTime.UtcNow;
        await _voiceRecordings.UpdateAsync(recording, ct);

        // Step 14 — Review gate, logging and result (design.md §3): any transcription
        // failure, incomplete/low-confidence parse, per-field disagreement with the confirmed
        // fields, or non-Written status requires a human review.
        var needsReview = transcriptionFailed
            || !parsed.IsComplete
            || parsed.Confidence < ReviewConfidenceThreshold
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
            submittedAt: DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public async Task<SyncVoiceRecordingResult> CorrectAsync(
        string clientRecordingId,
        int plot,
        int subsample,
        int value,
        int traitOrdinal,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(clientRecordingId);

        // Normalize like the sync path: non-positive plot/subsample means the correction
        // cannot address a cell; value 0 stays legal.
        var cPlot = plot > 0 ? plot : (int?)null;
        var cSubsample = subsample > 0 ? subsample : (int?)null;
        if (cPlot is null || cSubsample is null)
        {
            _logger.LogDebug(
                "Correction for voice recording {ClientRecordingId} discarded non-positive plot {Plot}/subsample {Subsample}; nothing addressable will be written.",
                clientRecordingId, plot, subsample);
        }

        var cValue = value;

        var recording = await _voiceRecordings.GetByClientRecordingIdAsync(clientRecordingId, ct);
        if (recording is null)
        {
            throw new KeyNotFoundException($"Voice recording '{clientRecordingId}' was not found.");
        }

        // Terminal rows are immutable: a Written/SkippedPresent outcome can only be revised
        // by direct workbook editing, not by a correction request.
        if (recording.Status is VoiceRecordingStatus.Written or VoiceRecordingStatus.SkippedPresent)
        {
            throw new InvalidOperationException(
                $"Voice recording '{clientRecordingId}' is already {recording.Status} and cannot be corrected.");
        }

        // Value gate A (mirrors the sync path): an out-of-range review value is never written.
        // The row is updated with the reviewer's confirmed fields and flagged for attention.
        if (cValue is < MinRating or > MaxRating)
        {
            recording.ConfirmedPlot = cPlot;
            recording.ConfirmedSubsample = cSubsample;
            recording.ConfirmedValue = cValue;
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

        var trial = await _trials.GetByIdAsync(recording.TrialId ?? Guid.Empty, ct);
        if (trial is null)
        {
            throw new KeyNotFoundException($"Trial '{recording.TrialId}' was not found.");
        }

        // Single-cell policy write at the reviewer-confirmed target (the correction offers
        // only the confirmed subsample, exactly like the transcript-authoritative sync).
        // The stored blob and transcript are reused — no re-upload, no re-transcription.
        PolicyWritePlotColumnResult? policy = null;
        if (cPlot is int confirmedPlot && cSubsample is int confirmedSubsample)
        {
            try
            {
                var serviceDir = ResolveServiceDirectory();
                var outputDirectory = Path.Combine(serviceDir, "Working", trial.Id.ToString());

                var workbookPath = TryGetNewestOutputCopy(outputDirectory)
                    ?? Path.GetFullPath(Path.Combine(serviceDir, trial.SourceWorkbookPath ?? string.Empty));

                policy = _ratings.WritePlotColumnPreserving(
                    workbookPath,
                    outputDirectory,
                    confirmedPlot,
                    traitOrdinal,
                    cValue,
                    new HashSet<int> { confirmedSubsample });
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
        recording.Status = status;
        recording.UpdatedAt = DateTime.UtcNow;
        await _voiceRecordings.UpdateAsync(recording, ct);

        // The reviewer supplied the full target, so no transcription/parse review signal
        // applies — only the commit outcome determines review.
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

    /// <summary>
    /// Answers a repeated sync with <see cref="VoiceRecordingStatus.Replay"/> WITHOUT touching
    /// the stored row: the first submission's terminal status (e.g. Written) must not be
    /// clobbered by a retry, so no <see cref="IVoiceRecordingRepository.UpdateAsync"/> is
    /// performed. No blob, transcription or workbook work happens either.
    /// </summary>
    private Task<SyncVoiceRecordingResult> ReplayAsync(
        VoiceRecording existing,
        SyncVoiceRecordingCommand command,
        int? cPlot,
        int? cSubsample,
        int? cValue,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "Voice recording {ClientRecordingId} is a replay (row {RecordingId}); skipped without transcription, write or row update.",
            command.ClientRecordingId, existing.Id);

        return Task.FromResult(BuildResult(
            command,
            cPlot, cSubsample, cValue,
            VoiceRecordingStatus.Replay,
            needsReview: false,
            transcript: string.Empty,
            parsed: null,
            policy: null,
            submittedAt: DateTimeOffset.UtcNow,
            replayed: true));
    }

    /// <summary>
    /// Stores an out-of-range recording (blob + Flagged audit row) without transcribing
    /// or writing. The audio is still kept as review evidence.
    /// </summary>
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
            trial.Id, clientId, blobPath, mimeType, cPlot, cSubsample, cValue, command.CreatedAt,
            VoiceRecordingStatus.Flagged);
        try
        {
            await _voiceRecordings.CreateAsync(recording, ct);
        }
        catch (DbUpdateException ex) when (IsClientRecordingIdConflict(ex))
        {
            // TOCTOU: a concurrent double-submit raced past the idempotency gate and committed
            // first. Its row owns the same blob path (same client key), so the blob stays and
            // the loser is answered as a Replay without touching the winner's row.
            var winner = await _voiceRecordings.GetByClientRecordingIdAsync(clientId, ct);
            if (winner is null)
            {
                throw; // The conflict row vanished — surface the original failure.
            }

            _logger.LogInformation(
                "Voice recording {ClientRecordingId} raced with a concurrent insert; treated as replay (row {RecordingId}).",
                clientId, winner.Id);

            return await ReplayAsync(winner, command, cPlot, cSubsample, cValue, ct);
        }
        catch (Exception)
        {
            // The audit row never committed — delete the orphaned blob so the working
            // directory does not accumulate unreferenced audio, then rethrow.
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

    /// <summary>
    /// Applies the commit flowchart: write what was free, skip what was already present,
    /// flag anything that could not be written (missing workbook, unknown plot or a
    /// subsample with no workbook row).
    /// </summary>
    private static string SelectStatus(PolicyWritePlotColumnResult? policy)
    {
        if (policy is null || !policy.PlotExists || policy.MissingSubsampleIndices.Count > 0)
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

    /// <summary>
    /// True when the parsed transcript disagrees with a CONFIRMED command field — checked
    /// PER FIELD, so a partial parse still flags a disagreement on the fields it did state
    /// (design.md §3). The write still uses the confirmed fields (contract truth); a
    /// disagreement only flags review.
    /// </summary>
    private static bool MismatchesConfirmed(
        TranscriptParseResult parsed,
        int? cPlot,
        int? cSubsample,
        int? cValue)
        => (cPlot is int cp && parsed.Plot is int pp && cp != pp)
        || (cSubsample is int cs && parsed.Subsample is int ps && cs != ps)
        || (cValue is int cv && parsed.Value is int pv && cv != pv);

    /// <summary>
    /// Assembles the result DTO, honouring a <see langword="null"/> parse and a
    /// <see langword="null"/> policy write. The resolved Plot/Subsample/Value (confirmed ??
    /// parsed) travel on the wire, and <see cref="SyncVoiceRecordingResult.WasTranscriptOnly"/>
    /// reports whether NO confirmed field was supplied. The parsed transcript fields travel
    /// even when transcription failed (nulls with confidence 0) or a partial parse ran
    /// (some nulls). <see cref="SyncVoiceRecordingResult.PlotExists"/> reports whether the
    /// workbook confirmed the plot — always <see langword="false"/> on replay, under a value
    /// gate, or when no policy write ran.
    /// </summary>
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
        bool replayed = false)
        => new()
        {
            ClientRecordingId = command.ClientRecordingId,
            TrialId = command.TrialId,
            Status = status,
            Plot = cPlot ?? parsed?.Plot,
            Subsample = cSubsample ?? parsed?.Subsample,
            Value = cValue ?? parsed?.Value,
            ParsedPlot = parsed?.Plot,
            ParsedSubsample = parsed?.Subsample,
            ParsedValue = parsed?.Value,
            PlotExists = policy?.PlotExists ?? false,
            TraitOrdinal = command.TraitOrdinal,
            NeedsReview = needsReview,
            Replayed = replayed,
            WasTranscriptOnly = cPlot is null && cSubsample is null && cValue is null,
            SubmittedAt = submittedAt,
            Transcript = transcript,
            Confidence = parsed?.Confidence,
            WrittenCells = policy?.WrittenCells ?? [],
            SkippedPresentCells = policy?.SkippedPresentCells ?? [],
            MissingSubsampleIndices = policy?.MissingSubsampleIndices ?? [],
        };

    /// <summary>
    /// Builds the command-shaped input the correction result derives its metadata from. The
    /// confirmed target fields are deliberately left null — BuildResult resolves them from
    /// the normalized c* parameters; only the id, the trial and the forwarded trait ride here.
    /// </summary>
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

    /// <summary>
    /// Rebuilds the parse outcome of the stored transcript so a correction result carries the
    /// same parsed-field shape as a sync result (confidence 0 when the row never stored one).
    /// </summary>
    private static TranscriptParseResult ReconstructParsed(VoiceRecording recording)
        => new()
        {
            Plot = recording.ParsedPlot,
            Subsample = recording.ParsedSubsample,
            Value = recording.ParsedValue,
            Confidence = recording.Confidence ?? 0,
        };

    /// <summary>
    /// Creates the audit row, persisting the NORMALIZED confirmed fields (plot/subsample
    /// already nulled when non-positive) so the row never stores a target the sync refused
    /// to consider confirmed.
    /// </summary>
    private static VoiceRecording CreateRecording(
        Guid trialId,
        string clientId,
        string blobPath,
        string mimeType,
        int? confirmedPlot,
        int? confirmedSubsample,
        int? confirmedValue,
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
            CreatedAt = createdAt?.UtcDateTime ?? DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    /// <summary>
    /// Maps the uploaded file name to the MIME type the blob store derives its extension from
    /// (webm/wav/mp4 supported by the sidecar; anything else is stored as an opaque binary).
    /// </summary>
    private static string MimeTypeFromFileName(string fileName)
        => Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".webm" => "audio/webm",
            ".wav" => "audio/wav",
            ".mp4" or ".m4a" => "audio/mp4",
            _ => "application/octet-stream",
        };

    /// <summary>
    /// Resolves the repo-root <c>Service/</c> directory by walking up from
    /// <c>AppContext.BaseDirectory</c>, mirroring <c>TrialWorkbookStore.ResolveServiceDirectory()</c>.
    /// The Services project must not reference the Repository project, so this walk is
    /// replicated here rather than reused.
    /// </summary>
    private static string ResolveServiceDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "Service")))
            {
                return Path.Combine(dir.FullName, "Service");
            }

            dir = dir.Parent;
        }

        // Fallback: resolve relative to the current working directory.
        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "Service"));
    }

    /// <summary>
    /// Returns the newest existing output copy in the trial's working directory — the file
    /// whose name ends in <c>_out_yyyyMMdd_HHmmss.xlsx</c> with the greatest timestamp — or
    /// <see langword="null"/> when no copy exists yet. The <see langword="last"/> <c>_out_</c>
    /// marker is used, so a chained copy (one created from another output copy) parses to its
    /// own newest stamp. Used by the policy write to make successive syncs accumulate on
    /// earlier output.
    /// </summary>
    private static string? TryGetNewestOutputCopy(string outputDirectory)
    {
        if (!Directory.Exists(outputDirectory))
        {
            return null;
        }

        string? newestPath = null;
        DateTime newestStamp = default;
        const string marker = "_out_";
        foreach (var file in Directory.GetFiles(outputDirectory, "*.xlsx"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var markerIndex = name.LastIndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                continue;
            }

            var stamp = name[(markerIndex + marker.Length)..];
            if (DateTime.TryParseExact(
                    stamp,
                    "yyyyMMdd_HHmmss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsedStamp)
                && (newestPath is null || parsedStamp > newestStamp))
            {
                newestStamp = parsedStamp;
                newestPath = file;
            }
        }

        return newestPath;
    }

    /// <summary>
    /// True when a <see cref="DbUpdateException"/> was caused by the
    /// <c>IX_VoiceRecordings_ClientRecordingId</c> unique index — a concurrent double-submit
    /// raced past the idempotency gate and committed first. Npgsql surfaces the underlying
    /// Postgres <c>23505 unique_violation</c> inside the inner exception message.
    /// </summary>
    private static bool IsClientRecordingIdConflict(DbUpdateException ex)
    {
        var message = ex.InnerException?.Message;
        return message is not null
            && (message.Contains("IX_VoiceRecordings_ClientRecordingId", StringComparison.OrdinalIgnoreCase)
                || message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase));
    }
}