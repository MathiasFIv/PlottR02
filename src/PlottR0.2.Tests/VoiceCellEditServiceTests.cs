using PlottR0._2.Contracts.Dtos.Trial;
using PlottR0._2.Contracts.Dtos.User;
using PlottR0._2.Contracts.Ratings;
using PlottR0._2.Contracts.Transcription;
using PlottR0._2.Services.Abstractions;
using PlottR0._2.Services.Implementations;
using PlottR0._2.Tests.Support;

namespace PlottR0._2.Tests;

/// <summary>
/// Unit tests for <see cref="VoiceCellEditService"/> — the "double-tap a cell → record →
/// number is saved" orchestration: transcribe ONE clip, parse ONE number (0..10) out of it,
/// and write it into the addressed cell through <see cref="ITrialService.EditCellAsync"/>.
/// Pure unit tests (no WebApplicationFactory, no Testcontainers): a hand-rolled fake
/// <see cref="ITrialService"/> plus the shared queue-driven
/// <see cref="FakeTranscriptionService"/> drive the flow, so every call count and forwarded
/// argument is asserted directly.
/// </summary>
public sealed class VoiceCellEditServiceTests
{
    private static readonly Guid TrialId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static VoiceCellEditRequest Request(int plot = 101, int subsample = 1, int traitOrdinal = 1, bool overwrite = true)
        => new() { Plot = plot, Subsample = subsample, TraitOrdinal = traitOrdinal, Overwrite = overwrite };

    private static MemoryStream Audio() => new([1, 2, 3, 4]);

    private static VoiceCellEditService CreateService(
        FakeTranscriptionService transcription, FakeTrialService trialService)
        => new(transcription, trialService);

    // ── Happy path: one spoken number lands in the addressed cell ────────────

    [Fact]
    public async Task TranscribeAndWriteAsync_SingleNumber_WritesValueAndCarriesVersionAndPriorValue()
    {
        // Arrange — transcript "7", and the underlying edit reports a successful overwrite.
        var transcription = new FakeTranscriptionService(
            new TranscriptionResult { Text = "7", Language = "en", Duration = 0.8 });
        var trialService = new FakeTrialService
        {
            EditCellResultToReturn = new CellEditResult(
                TrialId.ToString(), 101, 1, 1, 7, Written: true, Skipped: false, SkippedReason: null,
                PriorValue: 4, CellAddress: "C22", Version: "abcd1234-1", FileName: "f_out_1.xlsx",
                GeneratedAt: DateTime.UtcNow),
        };
        var service = CreateService(transcription, trialService);

        // Act
        var result = await service.TranscribeAndWriteAsync(TrialId, Request(), Audio(), "clip.webm");

        // Assert — EditCellAsync was called exactly once with the parsed value
        transcription.CallCount.Should().Be(1);
        trialService.EditCellCallCount.Should().Be(1);
        trialService.LastEditTrialId.Should().Be(TrialId);
        trialService.LastEditRequest.Should().NotBeNull();
        trialService.LastEditRequest!.Plot.Should().Be(101);
        trialService.LastEditRequest.Subsample.Should().Be(1);
        trialService.LastEditRequest.TraitOrdinal.Should().Be(1);
        trialService.LastEditRequest.Value.Should().Be(7);
        trialService.LastEditRequest.Overwrite.Should().BeTrue("the default opt-in is forwarded unchanged");

        // Assert — the result mirrors the write outcome and carries version/prior value
        result.Status.Should().Be(VoiceCellEditResult.StatusWritten);
        result.Written.Should().BeTrue();
        result.Skipped.Should().BeFalse();
        result.Value.Should().Be(7);
        result.Version.Should().Be("abcd1234-1", "the new opaque version must reach the client");
        result.PriorValue.Should().Be(4);
        result.CellAddress.Should().Be("C22");
        result.FileName.Should().Be("f_out_1.xlsx");
        result.TrialId.Should().Be(TrialId.ToString());
        result.Transcript.Should().Be("7");
        result.GeneratedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task TranscribeAndWriteAsync_MultipleNumbers_WritesFirstValue_SingleCellContract()
    {
        // Arrange — "3 6 1" parses as values [3, 6, 1]; this flow writes ONE cell: the first.
        var transcription = new FakeTranscriptionService(
            new TranscriptionResult { Text = "3 6 1" });
        var trialService = new FakeTrialService();
        var service = CreateService(transcription, trialService);

        // Act
        var result = await service.TranscribeAndWriteAsync(TrialId, Request(), Audio(), "clip.webm");

        // Assert
        trialService.LastEditRequest!.Value.Should().Be(3, "only the first parsed value is written");
        result.Value.Should().Be(3);
        result.Status.Should().Be(VoiceCellEditResult.StatusWritten);
    }

    [Fact]
    public async Task TranscribeAndWriteAsync_OverwriteFalse_IsForwardedToEditCell()
    {
        // Arrange — the caller opts into the locked-overwrite refusal.
        var transcription = new FakeTranscriptionService(
            new TranscriptionResult { Text = "7" });
        var trialService = new FakeTrialService
        {
            EditCellResultToReturn = new CellEditResult(
                TrialId.ToString(), 101, 1, 1, 7, Written: false, Skipped: true,
                SkippedReason: SingleCellWriteResult.CellAlreadyFilled,
                PriorValue: 5, CellAddress: "C22", Version: "abcd1234-0", FileName: "f.xlsx",
                GeneratedAt: DateTime.UtcNow),
        };
        var service = CreateService(transcription, trialService);

        // Act
        var result = await service.TranscribeAndWriteAsync(
            TrialId, Request(overwrite: false), Audio(), "clip.webm");

        // Assert — the opt-in reached the write path, and the refusal maps to its status
        trialService.LastEditRequest!.Overwrite.Should().BeFalse();
        result.Status.Should().Be(VoiceCellEditResult.StatusCellAlreadyFilled);
        result.Written.Should().BeFalse();
        result.PriorValue.Should().Be(5);
    }

    // ── Not understood: no number ⇒ no write ─────────────────────────────────

    [Fact]
    public async Task TranscribeAndWriteAsync_EmptyTranscript_ReturnsNotUnderstoodWithoutEditing()
    {
        // Arrange — an empty transcript is valid STT output, but carries no number.
        var transcription = new FakeTranscriptionService(
            new TranscriptionResult { Text = string.Empty });
        var trialService = new FakeTrialService();
        var service = CreateService(transcription, trialService);

        // Act
        var result = await service.TranscribeAndWriteAsync(TrialId, Request(), Audio(), "clip.webm");

        // Assert — reported, never written; the write path was NOT invoked
        result.Status.Should().Be(VoiceCellEditResult.StatusNotUnderstood);
        result.Value.Should().BeNull();
        result.Written.Should().BeFalse();
        result.Transcript.Should().Be(string.Empty);
        result.Version.Should().BeEmpty("nothing was written, so there is no new version");
        transcription.CallCount.Should().Be(1, "the clip must still be transcribed");
        trialService.EditCellCallCount.Should().Be(0, "an unparseable transcript must never reach the write path");
    }

    [Fact]
    public async Task TranscribeAndWriteAsync_RepairedGluedDigits_ReturnsNotUnderstoodWithoutEditing()
    {
        // Arrange — "398" is a glued multi-digit token only the bounded repair could
        // re-split; it is not a confident single number, so it is never written.
        var transcription = new FakeTranscriptionService(
            new TranscriptionResult { Text = "398" });
        var trialService = new FakeTrialService();
        var service = CreateService(transcription, trialService);

        // Act
        var result = await service.TranscribeAndWriteAsync(TrialId, Request(), Audio(), "clip.webm");

        // Assert
        result.Status.Should().Be(VoiceCellEditResult.StatusNotUnderstood);
        result.Value.Should().BeNull();
        result.Written.Should().BeFalse();
        result.Transcript.Should().Be("398");
        trialService.EditCellCallCount.Should().Be(0);
    }

    // ── Skip mapping: row not found ──────────────────────────────────────────

    [Fact]
    public async Task TranscribeAndWriteAsync_RowNotFound_MapsSkipReasonToStatus()
    {
        // Arrange — the addressed (plot, subsample) row does not exist in the workbook.
        var transcription = new FakeTranscriptionService(
            new TranscriptionResult { Text = "7" });
        var trialService = new FakeTrialService
        {
            EditCellResultToReturn = new CellEditResult(
                TrialId.ToString(), 101, 9, 1, 7, Written: false, Skipped: true,
                SkippedReason: SingleCellWriteResult.RowNotFound,
                PriorValue: null, CellAddress: string.Empty, Version: "abcd1234-0", FileName: "f.xlsx",
                GeneratedAt: DateTime.UtcNow),
        };
        var service = CreateService(transcription, trialService);

        // Act
        var result = await service.TranscribeAndWriteAsync(
            TrialId, Request(subsample: 9), Audio(), "clip.webm");

        // Assert — the skip reason becomes the flat status the voice UI reads
        result.Status.Should().Be(VoiceCellEditResult.StatusRowNotFound);
        result.Written.Should().BeFalse();
        result.Skipped.Should().BeTrue();
        result.SkippedReason.Should().Be(SingleCellWriteResult.RowNotFound);
        result.CellAddress.Should().BeEmpty();
    }

    // ── Exception propagation ────────────────────────────────────────────────

    [Fact]
    public async Task TranscribeAndWriteAsync_EditCellThrowsForbidden_Propagates()
    {
        // Arrange — the underlying edit path rejects the actor.
        var transcription = new FakeTranscriptionService(
            new TranscriptionResult { Text = "7" });
        var trialService = new FakeTrialService
        {
            EditCellExceptionToThrow = new ForbiddenAccessException("no"),
        };
        var service = CreateService(transcription, trialService);

        // Act & Assert — the controller maps this to 403; the service must not swallow it
        var act = () => service.TranscribeAndWriteAsync(TrialId, Request(), Audio(), "clip.webm");
        await act.Should().ThrowAsync<ForbiddenAccessException>();
    }

    [Fact]
    public async Task TranscribeAndWriteAsync_AccessCheckFails_PropagatesBeforeTranscribing()
    {
        // Arrange — the access pre-check runs BEFORE any speech-to-text spend, so the
        // transcription fake is scripted with ZERO results and would throw if invoked.
        var transcription = new FakeTranscriptionService();
        var trialService = new FakeTrialService
        {
            EnsureAccessExceptionToThrow = new ForbiddenAccessException(
                $"You do not have access to trial '{TrialId}'."),
        };
        var service = CreateService(transcription, trialService);

        // Act & Assert
        var act = () => service.TranscribeAndWriteAsync(TrialId, Request(), Audio(), "clip.webm");
        await act.Should().ThrowAsync<ForbiddenAccessException>()
            .WithMessage("*do not have access*");

        // Assert — never transcribed, never written
        transcription.CallCount.Should().Be(0, "the access gate must fail fast, before STT");
        trialService.EditCellCallCount.Should().Be(0);
    }

    [Fact]
    public async Task TranscribeAndWriteAsync_AccessCheckAndEditCell_AreBothCalledOnce()
    {
        // Arrange — the happy path must run the pre-check first, then transcribe, then edit.
        var transcription = new FakeTranscriptionService(
            new TranscriptionResult { Text = "7" });
        var trialService = new FakeTrialService();
        var service = CreateService(transcription, trialService);

        // Act
        await service.TranscribeAndWriteAsync(TrialId, Request(), Audio(), "clip.webm");

        // Assert
        trialService.EnsureAccessCallCount.Should().Be(1);
        trialService.EditCellCallCount.Should().Be(1);
    }

    // ── Request validation ───────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 1, 1)]   // plot ≤ 0
    [InlineData(-1, 1, 1)]  // plot < 0
    [InlineData(101, 0, 1)] // subsample ≤ 0
    [InlineData(101, 1, 0)] // traitOrdinal < 1
    [InlineData(101, 1, 10)]// traitOrdinal > 9
    public async Task TranscribeAndWriteAsync_OutOfRangeFields_ThrowArgumentBeforeTranscribing(
        int plot, int subsample, int traitOrdinal)
    {
        // Arrange — validation fires before any lookup or transcription, mirroring
        // ITrialService.EditCellAsync's message style.
        var transcription = new FakeTranscriptionService();
        var trialService = new FakeTrialService();
        var service = CreateService(transcription, trialService);

        // Act & Assert
        var act = () => service.TranscribeAndWriteAsync(
            TrialId, Request(plot, subsample, traitOrdinal), Audio(), "clip.webm");
        await act.Should().ThrowAsync<ArgumentException>();

        // Assert — nothing downstream was touched
        transcription.CallCount.Should().Be(0);
        trialService.EnsureAccessCallCount.Should().Be(0);
        trialService.EditCellCallCount.Should().Be(0);
    }

    // ── Fake ITrialService ───────────────────────────────────────────────────

    /// <summary>
    /// Minimal <see cref="ITrialService"/> double: records the access pre-check and the
    /// <c>EditCellAsync</c> call's arguments, and either returns the scripted result or
    /// throws the scripted exception. Every other member is unsupported.
    /// </summary>
    private sealed class FakeTrialService : ITrialService
    {
        public CellEditResult EditCellResultToReturn { get; set; } = new(
            TrialId.ToString(), 101, 1, 1, 7, Written: true, Skipped: false, SkippedReason: null,
            PriorValue: null, CellAddress: "C22", Version: "abcd1234-1", FileName: "f_out_1.xlsx",
            GeneratedAt: DateTime.UtcNow);

        public Exception? EditCellExceptionToThrow { get; set; }

        public Exception? EnsureAccessExceptionToThrow { get; set; }

        public int EnsureAccessCallCount { get; private set; }

        public int EditCellCallCount { get; private set; }

        public Guid? LastEditTrialId { get; private set; }

        public EditCellRequest? LastEditRequest { get; private set; }

        public Task EnsureTrialAccessAsync(Guid trialId, CancellationToken ct = default)
        {
            EnsureAccessCallCount++;
            return EnsureAccessExceptionToThrow is not null
                ? Task.FromException(EnsureAccessExceptionToThrow)
                : Task.CompletedTask;
        }

        public Task<CellEditResult> EditCellAsync(Guid trialId, EditCellRequest request, CancellationToken ct = default)
        {
            EditCellCallCount++;
            LastEditTrialId = trialId;
            LastEditRequest = request;
            return EditCellExceptionToThrow is not null
                ? Task.FromException<CellEditResult>(EditCellExceptionToThrow)
                : Task.FromResult(EditCellResultToReturn);
        }

        public Task<TrialSummary> CreateTrialAsync(string fileName, Stream content, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<TrialSummary>> ListTrialsAsync(CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task AssignTrialUsersAsync(Guid trialId, AssignTrialUsersRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<UserResponse>> ListTrialUsersAsync(Guid trialId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<WorkbookExport> GetWorkbookExportAsync(Guid trialId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<TrialSheet> GetSheetAsync(Guid trialId, string? since, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
