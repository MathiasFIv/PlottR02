using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PlottR0._2.Contracts;
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
///   <item><description>Locked-overwrite policy write of the RESOLVED values into a workbook
///   copy in CATEGORY-BLOCK APPEND mode: the values land in the row's next free cells inside
///   the Part Rated category block (row-8 metadata LEAF1/LEAF2/LEAF3) so successive voices
///   on one plot accumulate leaf by leaf, never overwriting present cells (a correction
///   later re-runs the same category-block append scan on the stored row instead). The read
///   source chains: the newest existing <c>*_out_*.xlsx</c> copy in the trial's working
///   directory is the input for the next sync; falls back to the pristine
///   <c>SourceWorkbookPath</c> when no copy exists yet.</description></item>
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
    private readonly ICurrentUser _currentUser;
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
    /// A parse that stated nothing, used when <see cref="BuildResult"/> derives default-target
    /// usage for paths that never ran a parse (replay, value gate A, out-of-range store). Those
    /// paths have an empty transcript, so resolving them as a parse whose fields are all
    /// <see langword="null"/> matches how a real empty parse behaves for default resolution.
    /// </summary>
    private static readonly TranscriptParseResult EmptyParse = new();

    /// <summary>
    /// Creates a new <see cref="RecordingSyncService"/>.
    /// </summary>
    /// <param name="voiceRecordings">Repository of <c>VoiceRecording</c> audit rows.</param>
    /// <param name="blobStore">File-system blob store for the raw audio.</param>
    /// <param name="transcriptionService">Speech-to-text sidecar gateway.</param>
    /// <param name="trials">Trial repository used for the trial gate and workbook path.</param>
    /// <param name="ratings">Workbook-backed rating repository performing the policy write.</param>
    /// <param name="currentUser">
    /// The acting identity, used for the per-trial access gate: a field worker may only sync
    /// to (or correct) trials they are assigned to.
    /// </param>
    /// <param name="logger">Logger for sync diagnostics.</param>
    public RecordingSyncService(
        IVoiceRecordingRepository voiceRecordings,
        IVoiceBlobStore blobStore,
        ITranscriptionService transcriptionService,
        ITrialRepository trials,
        IRatingRepository ratings,
        ICurrentUser currentUser,
        ILogger<RecordingSyncService> logger)
    {
        _voiceRecordings = voiceRecordings;
        _blobStore = blobStore;
        _transcriptionService = transcriptionService;
        _trials = trials;
        _ratings = ratings;
        _currentUser = currentUser;
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

        // The RESOLVED trait ordinal for the audit row: an absent form field binds to 0, and a
        // non-positive value means the client confirmed no anchor, so the row stores the
        // normalized 1 — the anchor the write actually used (see
        // VoiceRecording.ResolvedTraitOrdinal). Values above 9 pass normalization unchanged
        // and are stored verbatim as the flagged row's evidence.
        var cTraitOrdinal = NormalizeTraitOrdinal(command.TraitOrdinal);

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

        // Step 3b — Access gate: managers may sync to any trial; a field worker may only
        // sync to a trial they are assigned to. Ordered AFTER the existence check so an
        // unknown trial stays a 404 (matching the pre-increment behaviour) and BEFORE any
        // blob/row/workbook write. (The replay path is gated by ReplayAsync.)
        await EnsureCanAccessTrialAsync(trial.Id, ct);

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
            trial.Id, clientId, blobPath, mimeType, cPlot, cSubsample, cValue, cTraitOrdinal,
            command.CreatedAt, VoiceRecordingStatus.Received);
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
        // against the workbook in the policy write and the review gate below. The one
        // exception is the parser's ceiling-bounded glued-digit repair, which re-splits a
        // value token Whisper glued together ("Values 398" → 3, 9, 8) and caps confidence
        // at 0.70. The RAW transcript stays the source of truth on the row and the echo,
        // so the log line below is where the repair becomes visible to ops.
        var parsed = TranscriptParser.Parse(transcript);

        if (parsed.Repaired)
        {
            _logger.LogWarning(
                "Voice recording {ClientRecordingId} had glued digits repaired into separate values " +
                "(raw transcript {Transcript}); confidence capped below the review threshold.",
                clientId, transcript);
        }

        // Step 9 — Resolution (design.md §3): the confirmed field wins per field; a missing
        // or non-positive confirmed field falls back to the parsed transcript value, then to
        // the CLIENT-SENT DEFAULT TARGET (design.md V2E default memory, "plot/subsample
        // default memory"): a values-only utterance that omitted plot/subsample re-uses the
        // target the PWA remembers from the previous successful utterance. The subsample
        // default is guarded — a NEW plot spoken in the transcript (different from the
        // default plot) never inherits the previous subsample cursor. Values resolve as a
        // LIST: a confirmed value is a single-cell correction (one entry), while an
        // unconfirmed value resolves to the transcript's multi-value list ("values 3 6 1").
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

        // Step 10 — Value gate B: when NO legal value can be resolved (no confirmed value
        // and the transcript stated none, or ANY resolved value is out of range), the
        // recording is flagged for review and NEVER written — a rating sheet must not receive
        // an invalid or guessed value.
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
                submittedAt: DateTimeOffset.UtcNow);
        }

        // Step 10b — TraitOrdinal gate (defense in depth): traitOrdinal spans 1..9; the
        // voice client may omit it (binds to 0), which the server interprets as 1 — the
        // append scan then starts at column C and walks the Part Rated category blocks
        // (row 8). Only an explicit traitOrdinal above 9 is invalid and flagged.
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
                submittedAt: DateTimeOffset.UtcNow);
        }

        // Step 11 — Policy write (design.md §3): the sync offers exactly the RESOLVED
        // subsample with the resolved VALUES list in CATEGORY-BLOCK APPEND mode
        // (scanForFreeCells): the values are written into the row's next free cells inside
        // the Part Rated category block containing them (row-8 metadata: LEAF1/LEAF2/LEAF3),
        // never overwriting present cells and never spilling across a category boundary. An
        // unconfirmed-and-unparsed plot or subsample leaves nothing addressable, so the
        // write attempt is skipped and the outcome is Flagged.
        var subsampleSet = subsample is int s && s > 0
            ? (IReadOnlySet<int>)new HashSet<int> { s }
            : EmptySubsampleSet;

        PolicyWritePlotColumnResult? policy = null;
        if (plot is int resolvedPlot && values.Count > 0 && subsampleSet.Count > 0)
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
                    traitOrdinal,
                    values,
                    subsampleSet,
                    scanForFreeCells: true);

                // Trial status lifecycle (dashboard badge): a policy write that actually
                // LANDED (non-null with written cells) advances the trial. The recompute
                // re-reads the copy this write just exported — the same workbook the sheet
                // endpoint serves — so the status reflects exactly what the UI shows.
                if (policy is not null && policy.WrittenCells.Count > 0)
                {
                    await RecomputeTrialStatusAsync(trial, outputDirectory, workbookPath, ct);
                }
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
        recording.ParsedValue = parsed.FirstValue;
        recording.Confidence = parsed.Confidence;
        recording.UpdatedAt = DateTime.UtcNow;
        await _voiceRecordings.UpdateAsync(recording, ct);

        // Step 14 — Review gate, logging and result (design.md §3): any transcription
        // failure, an unresolved target field, per-field disagreement with the confirmed
        // fields, or a non-Written status requires a human review. The parser's
        // IsComplete/confidence no longer disables a VOICE-ONLY values utterance outright:
        // under a client-sent default target (UsedDefaultTarget) such an utterance
        // deliberately omits plot/subsample, so its low-confidence partial parse is
        // expected — the write itself is the authority and a Written result needs no review.
        // When ANY part of the target was spoken, or no defaults applied, the existing
        // conservative checks stay intact.
        //
        // The IsComplete term is deliberately NOT redundant-looking: today the parser scores
        // every incomplete parse at confidence 0.5, below the threshold, so the confidence
        // term already forces review. It is kept because it encodes a different failure mode
        // (a missing field) than the confidence term (a fuzzy parse), and the two would
        // diverge if the parser's confidence bands changed.
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
            traitOrdinal);
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

        // TraitOrdinal normalization (mirrors the sync path): the voice client may omit
        // traitOrdinal (binds to 0), which the server interprets as 1 — the correction's
        // append scan then starts at column C and walks the Part Rated category blocks.
        // Only an explicit traitOrdinal above 9 remains invalid and is flagged below.
        // The RESOLVED (normalized) value is persisted on both paths.
        traitOrdinal = NormalizeTraitOrdinal(traitOrdinal);

        var recording = await _voiceRecordings.GetByClientRecordingIdAsync(clientRecordingId, ct);
        if (recording is null)
        {
            throw new KeyNotFoundException($"Voice recording '{clientRecordingId}' was not found.");
        }

        // Access gate: correcting a recording is a write against its trial, so a non-manager
        // must be assigned to that trial (the recording's own trial id is already loaded, so
        // this costs one indexed membership check). Ordered before any row update.
        await EnsureCanAccessTrialAsync(recording.TrialId ?? Guid.Empty, ct);

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

        // TraitOrdinal gate (mirrors the sync path): only an explicit traitOrdinal ABOVE 9 is
        // invalid — it cannot address a cell, so the correction is flagged without writing.
        // A missing/zero ordinal was already normalized to 1 above.
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

        // Correction re-runs the same category-block append scan the voice sync uses (single
        // confirmed value → first free cell of the row's Part Rated categories, strict block
        // rule, skipped occupied cells silently). The stored blob + transcript are reused —
        // no re-upload, no re-transcription.
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
                    new[] { cValue },
                    new HashSet<int> { confirmedSubsample },
                    scanForFreeCells: true);

                // Trial status lifecycle (dashboard badge) — same rule as the voice sync: a
                // correction that actually wrote values advances the trial; the recompute
                // re-reads the copy this write just exported.
                if (policy is not null && policy.WrittenCells.Count > 0)
                {
                    await RecomputeTrialStatusAsync(trial, outputDirectory, workbookPath, ct);
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
    /// True when the actor's role sits above the per-trial assignment matrix: a manager may
    /// record against (or correct) any trial without an explicit assignment. A null role
    /// (legacy/anonymous actor) is never a manager.
    /// </summary>
    private static bool IsManager(UserRole? role)
        => role is UserRole.SystemAdmin or UserRole.Researcher or UserRole.TrialManager;

    /// <summary>
    /// Ensures the current actor may touch <paramref name="trialId"/>: managers always may;
    /// every other actor must be explicitly assigned to the trial. A missing identity or a
    /// trial the caller is not assigned to is denied identically. Called before any blob,
    /// row or workbook write so a denied sync leaves no trace.
    /// </summary>
    /// <exception cref="ForbiddenAccessException">The actor is not permitted to access the trial.</exception>
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
    /// Answers a repeated sync with <see cref="VoiceRecordingStatus.Replay"/> WITHOUT touching
    /// the stored row: the first submission's terminal status (e.g. Written) must not be
    /// clobbered by a retry, so no <see cref="IVoiceRecordingRepository.UpdateAsync"/> is
    /// performed. No blob, transcription or workbook work happens either.
    /// </summary>
    private async Task<SyncVoiceRecordingResult> ReplayAsync(
        VoiceRecording existing,
        SyncVoiceRecordingCommand command,
        int? cPlot,
        int? cSubsample,
        int? cValue,
        CancellationToken ct)
    {
        // Access gate: a replay still exposes the committed outcome for the recording's
        // trial, so a non-manager must remain assigned to that trial. Managers bypass.
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
            // Resolution input = the original commit's parse (rebuilt from the stored row's
            // parsed fields), so the echo reports the cell that was ACTUALLY written — never
            // the caller's possibly-stale defaults. The echo (Parsed*/Confidence) stays null
            // via `parsed: null` above, preserving the replay contract.
            parsedForResolution: ReconstructParsed(existing),
            // Trait anchor = the anchor the COMMIT actually used, recovered from the stored
            // row, so a replayed trait-4 recording still reports TraitOrdinal 4 even when the
            // retry omits the field (which would otherwise echo the normalized 1). For rows
            // predating the column, the persisted value is null and the anchor is 1 (the only
            // ordinal a pre-migration row could ever have used); the caller's value is never used.
            traitOrdinal: existing.ResolvedTraitOrdinal ?? 1);
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
            trial.Id, clientId, blobPath, mimeType, cPlot, cSubsample, cValue,
            NormalizeTraitOrdinal(command.TraitOrdinal), command.CreatedAt,
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
    /// flag anything that could not be written (missing workbook, unknown plot, a subsample
    /// with no workbook row, or values dropped past trait 9 / past the current Part Rated
    /// category block).
    /// </summary>
    private static string SelectStatus(PolicyWritePlotColumnResult? policy)
    {
        if (policy is null || !policy.PlotExists || policy.MissingSubsampleIndices.Count > 0)
        {
            return VoiceRecordingStatus.Flagged;
        }

        if (policy.OutOfTraitRangeValueCount > 0)
        {
            // A spoken rating was lost (no column beyond trait 9 in fixed-column mode, or no
            // free cell left in the current Part Rated category block in append mode): flag
            // even when other cells were written — the human review must see the dropped values.
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
    /// Recomputes the trial's status after a SUCCESSFUL workbook write (one that actually
    /// wrote values — this is only invoked when <c>policy.WrittenCells.Count &gt; 0</c>) and
    /// persists the forward-only lifecycle transition (design.md §3, dashboard badge):
    /// <list type="bullet">
    /// <item><description><see cref="TrialStatus.Created"/> → <see cref="TrialStatus.InProgress"/> while the rating
    /// region is only partially plotted (some key rows still carry empty cells);</description></item>
    /// <item><description>→ <see cref="TrialStatus.Completed"/> as soon as EVERY key row carries ALL nine
    /// trait values (null = empty; 0 is a real value per design.md Rule 2).</description></item>
    /// </list>
    /// The region is re-read from the output copy the write just exported — the SAME workbook
    /// the dashboard sheet endpoint resolves (see <see cref="ITrialWorkbookStore.ResolveExport"/>)
    /// — never from the write's INPUT, which is stale by definition. The recompute is
    /// best-effort bookkeeping: an empty region is NOT progress (Created stays Created until
    /// a real value lands) and a region-read failure leaves the current status untouched
    /// rather than failing an otherwise successful sync. An already-Completed trial never
    /// regresses, even if the region is no longer fully plotted.
    /// </summary>
    private async Task RecomputeTrialStatusAsync(
        Trial trial,
        string outputDirectory,
        string workbookPath,
        CancellationToken ct)
    {
        var writtenPath = ResolveJustWrittenOutputCopy(outputDirectory, workbookPath);

        SheetRegion? region;
        try
        {
            region = writtenPath is null ? null : _ratings.ReadSheetRegion(writtenPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException)
        {
            _logger.LogWarning(ex,
                "Could not re-read workbook '{Path}' to recompute trial {TrialId} status; leaving '{Status}' unchanged.",
                writtenPath, trial.Id, trial.Status);
            return;
        }

        // Fully plotted = a non-empty region whose EVERY key row carries all trait values.
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

        // Forward-only: persist ONLY a real transition (never a regression, never a no-op).
        if (newStatus != trial.Status)
        {
            trial.Status = newStatus;
            await _trials.UpdateStatusAsync(trial.Id, newStatus, ct);
            _logger.LogInformation(
                "Trial {TrialId} status advanced to {Status} after a successful workbook write.",
                trial.Id, newStatus);
        }
    }

    /// <summary>
    /// Resolves the absolute path of the output copy the JUST-FINISHED policy write produced:
    /// the only <c>*.xlsx</c> in <paramref name="outputDirectory"/> whose name is the current
    /// input file's base name plus <c>"_out_"</c>. Successive syncs chain (each output becomes
    /// the next input), so the current input's base name embeds every previous copy's stamp
    /// and the pattern matches at most ONE file — THIS write's copy. Same-second timestamp
    /// ties cannot select a neighbouring copy, unlike
    /// <see cref="TryGetNewestOutputCopy(System.String)"/>, which ranks by the second-granularity
    /// file-name stamp. Returns <see langword="null"/> when no output copy exists (nothing was
    /// written).
    /// </summary>
    private static string? ResolveJustWrittenOutputCopy(string outputDirectory, string workbookPath)
    {
        if (!Directory.Exists(outputDirectory))
        {
            return null;
        }

        var prefix = $"{Path.GetFileNameWithoutExtension(workbookPath)}_out_";
        return Directory.GetFiles(outputDirectory, "*.xlsx")
            .FirstOrDefault(file => Path.GetFileName(file).StartsWith(prefix, StringComparison.Ordinal));
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
        || (cValue is int cv && parsed.FirstValue is int pv && cv != pv);

    /// <summary>
    /// Reports whether the resolved target fell back to a CLIENT-SENT DEFAULT
    /// (<see cref="SyncVoiceRecordingCommand.DefaultPlot"/>/<see cref="SyncVoiceRecordingCommand.DefaultSubsample"/>)
    /// because the confirmed fields AND the transcript both omitted the field. An explicit
    /// NEW plot in the transcript never inherits the previous subsample cursor, so the
    /// subsample default applies only when the transcript stated no plot or stated the same
    /// plot as the default — the same guard Step 9's resolution uses, so the review gate and
    /// the result echo can never disagree about what the write actually used.
    /// </summary>
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

    /// <summary>Normalizes a client-confirmed trait ordinal. Absent, 0 and negative all mean
    /// "unspecified" and map to 1 (the first Part Rated column); values above 9 are returned
    /// as-is so the caller's existing &gt; 9 flag gate still sees them.</summary>
    private static int NormalizeTraitOrdinal(int? confirmed)
        => confirmed is int v && v > 1 ? v : 1;

    /// <summary>
    /// Assembles the result DTO, honouring a <see langword="null"/> parse and a
    /// <see langword="null"/> policy write. The resolved Plot/Subsample/Value (confirmed ??
    /// resolution parse ?? client default — the same resolution the sync path applied in
    /// Step 9, so the echo always reflects what the write attempted) travel on the wire, and
    /// <see cref="SyncVoiceRecordingResult.WasTranscriptOnly"/> reports whether NO confirmed
    /// field was supplied. <see cref="SyncVoiceRecordingResult.UsedDefaultTarget"/> reports
    /// whether a client-sent default filled part of the target. The parsed transcript fields
    /// travel even when transcription failed (nulls with confidence 0) or a partial parse ran
    /// (some nulls). <see cref="SyncVoiceRecordingResult.PlotExists"/> reports whether the
    /// workbook confirmed the plot — always <see langword="false"/> on replay, under a value
    /// gate, or when no policy write ran.
    /// <para>
    /// The ECHO and the RESOLUTION intentionally take separate inputs: <paramref name="parsed"/>
    /// feeds the echoed Parsed*/Value/Confidence (kept <see langword="null"/> on replay by
    /// contract), while <paramref name="parsedForResolution"/> — defaulting to
    /// <paramref name="parsed"/>, then to an empty parse on null — feeds Plot/Subsample/
    /// UsedDefaultTarget. A replay passes the stored row's reconstructed parse so its echo
    /// reflects the committed write instead of the caller's possibly-stale defaults.
    /// </para>
    /// <para>
    /// <see cref="SyncVoiceRecordingResult.TraitOrdinal"/> echoes the anchor the outcome was
    /// committed under: <paramref name="traitOrdinal"/> — the resolved/persisted anchor,
    /// passed by the write path and by a replay (recovered from the stored row) — when the
    /// caller supplied one, else the command's, always through
    /// <see cref="NormalizeTraitOrdinal"/>. Every path therefore reports a NORMALIZED
    /// ordinal (1..9, or the explicit out-of-range value that got flagged) and never 0.
    /// </para>
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
        int? traitOrdinal = null,
        bool replayed = false,
        TranscriptParseResult? parsedForResolution = null)
    {
        // Same default-target rule as Step 9: a client-sent default fills a field the
        // confirmed value AND the transcript both omitted (with the subsample guard against
        // a NEW spoken plot). Callers that ran no parse and pass no `parsedForResolution`
        // (replay, out-of-range store) fall back to the empty parse — "nothing omitted",
        // which is exactly the empty-transcript truth on those paths.
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
            Confidence = parsed?.Confidence,
            WrittenCells = policy?.WrittenCells ?? [],
            SkippedPresentCells = policy?.SkippedPresentCells ?? [],
            MissingSubsampleIndices = policy?.MissingSubsampleIndices ?? [],
            OutOfTraitRangeValueCount = policy?.OutOfTraitRangeValueCount ?? 0,
            UsedDefaultTarget = usedDefaultPlot || usedDefaultSubsample,
        };
    }

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
            Values = recording.ParsedValue is int v ? new[] { v } : Array.Empty<int>(),
            Confidence = recording.Confidence ?? 0,
        };

    /// <summary>
    /// Creates the audit row, persisting the NORMALIZED confirmed fields (plot/subsample
    /// already nulled when non-positive) so the row never stores a target the sync refused
    /// to consider confirmed. <paramref name="resolvedTraitOrdinal"/> carries the RESOLVED
    /// ordinal — the anchor the write actually used — see
    /// <see cref="VoiceRecording.ResolvedTraitOrdinal"/>.
    /// </summary>
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