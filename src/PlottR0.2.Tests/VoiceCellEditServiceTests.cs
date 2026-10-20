using PlottR0._2.Contracts.Dtos.Trial;
using PlottR0._2.Contracts.Dtos.User;
using PlottR0._2.Contracts.Ratings;
using PlottR0._2.Contracts.Transcription;
using PlottR0._2.Services.Abstractions;
using PlottR0._2.Services.Implementations;
using PlottR0._2.Tests.Support;

namespace PlottR0._2.Tests;

public sealed class VoiceCellEditServiceTests
{
    private static readonly Guid TrialId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static VoiceCellEditRequest Request(int plot = 101, int subsample = 1, int traitOrdinal = 1, bool overwrite = true)
        => new() { Plot = plot, Subsample = subsample, TraitOrdinal = traitOrdinal, Overwrite = overwrite };

    private static MemoryStream Audio() => new([1, 2, 3, 4]);

    private static VoiceCellEditService CreateService(
        FakeTranscriptionService transcription, FakeTrialService trialService)
        => new(transcription, trialService);

    [Fact]
    public async Task TranscribeAndWriteAsync_SingleNumber_WritesValueAndCarriesVersionAndPriorValue()
    {
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

        var result = await service.TranscribeAndWriteAsync(TrialId, Request(), Audio(), "clip.webm");

        transcription.CallCount.Should().Be(1);
        trialService.EditCellCallCount.Should().Be(1);
        trialService.LastEditTrialId.Should().Be(TrialId);
        trialService.LastEditRequest.Should().NotBeNull();
        trialService.LastEditRequest!.Plot.Should().Be(101);
        trialService.LastEditRequest.Subsample.Should().Be(1);
        trialService.LastEditRequest.TraitOrdinal.Should().Be(1);
        trialService.LastEditRequest.Value.Should().Be(7);
        trialService.LastEditRequest.Overwrite.Should().BeTrue("the default opt-in is forwarded unchanged");

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
        var transcription = new FakeTranscriptionService(
            new TranscriptionResult { Text = "3 6 1" });
        var trialService = new FakeTrialService();
        var service = CreateService(transcription, trialService);

        var result = await service.TranscribeAndWriteAsync(TrialId, Request(), Audio(), "clip.webm");

        trialService.LastEditRequest!.Value.Should().Be(3, "only the first parsed value is written");
        result.Value.Should().Be(3);
        result.Status.Should().Be(VoiceCellEditResult.StatusWritten);
    }

    [Fact]
    public async Task TranscribeAndWriteAsync_OverwriteFalse_IsForwardedToEditCell()
    {
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

        var result = await service.TranscribeAndWriteAsync(
            TrialId, Request(overwrite: false), Audio(), "clip.webm");

        trialService.LastEditRequest!.Overwrite.Should().BeFalse();
        result.Status.Should().Be(VoiceCellEditResult.StatusCellAlreadyFilled);
        result.Written.Should().BeFalse();
        result.PriorValue.Should().Be(5);
    }

    [Fact]
    public async Task TranscribeAndWriteAsync_EmptyTranscript_ReturnsNotUnderstoodWithoutEditing()
    {
        var transcription = new FakeTranscriptionService(
            new TranscriptionResult { Text = string.Empty });
        var trialService = new FakeTrialService();
        var service = CreateService(transcription, trialService);

        var result = await service.TranscribeAndWriteAsync(TrialId, Request(), Audio(), "clip.webm");

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
        var transcription = new FakeTranscriptionService(
            new TranscriptionResult { Text = "398" });
        var trialService = new FakeTrialService();
        var service = CreateService(transcription, trialService);

        var result = await service.TranscribeAndWriteAsync(TrialId, Request(), Audio(), "clip.webm");

        result.Status.Should().Be(VoiceCellEditResult.StatusNotUnderstood);
        result.Value.Should().BeNull();
        result.Written.Should().BeFalse();
        result.Transcript.Should().Be("398");
        trialService.EditCellCallCount.Should().Be(0);
    }

    [Fact]
    public async Task TranscribeAndWriteAsync_RowNotFound_MapsSkipReasonToStatus()
    {
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

        var result = await service.TranscribeAndWriteAsync(
            TrialId, Request(subsample: 9), Audio(), "clip.webm");

        result.Status.Should().Be(VoiceCellEditResult.StatusRowNotFound);
        result.Written.Should().BeFalse();
        result.Skipped.Should().BeTrue();
        result.SkippedReason.Should().Be(SingleCellWriteResult.RowNotFound);
        result.CellAddress.Should().BeEmpty();
    }

    [Fact]
    public async Task TranscribeAndWriteAsync_EditCellThrowsForbidden_Propagates()
    {
        var transcription = new FakeTranscriptionService(
            new TranscriptionResult { Text = "7" });
        var trialService = new FakeTrialService
        {
            EditCellExceptionToThrow = new ForbiddenAccessException("no"),
        };
        var service = CreateService(transcription, trialService);

        var act = () => service.TranscribeAndWriteAsync(TrialId, Request(), Audio(), "clip.webm");
        await act.Should().ThrowAsync<ForbiddenAccessException>();
    }

    [Fact]
    public async Task TranscribeAndWriteAsync_AccessCheckFails_PropagatesBeforeTranscribing()
    {
        var transcription = new FakeTranscriptionService();
        var trialService = new FakeTrialService
        {
            EnsureAccessExceptionToThrow = new ForbiddenAccessException(
                $"You do not have access to trial '{TrialId}'."),
        };
        var service = CreateService(transcription, trialService);

        var act = () => service.TranscribeAndWriteAsync(TrialId, Request(), Audio(), "clip.webm");
        await act.Should().ThrowAsync<ForbiddenAccessException>()
            .WithMessage("*do not have access*");

        transcription.CallCount.Should().Be(0, "the access gate must fail fast, before STT");
        trialService.EditCellCallCount.Should().Be(0);
    }

    [Fact]
    public async Task TranscribeAndWriteAsync_AccessCheckAndEditCell_AreBothCalledOnce()
    {
        var transcription = new FakeTranscriptionService(
            new TranscriptionResult { Text = "7" });
        var trialService = new FakeTrialService();
        var service = CreateService(transcription, trialService);

        await service.TranscribeAndWriteAsync(TrialId, Request(), Audio(), "clip.webm");

        trialService.EnsureAccessCallCount.Should().Be(1);
        trialService.EditCellCallCount.Should().Be(1);
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(-1, 1, 1)]
    [InlineData(101, 0, 1)]
    [InlineData(101, 1, 0)]
    [InlineData(101, 1, 10)]
    public async Task TranscribeAndWriteAsync_OutOfRangeFields_ThrowArgumentBeforeTranscribing(
        int plot, int subsample, int traitOrdinal)
    {
        var transcription = new FakeTranscriptionService();
        var trialService = new FakeTrialService();
        var service = CreateService(transcription, trialService);

        var act = () => service.TranscribeAndWriteAsync(
            TrialId, Request(plot, subsample, traitOrdinal), Audio(), "clip.webm");
        await act.Should().ThrowAsync<ArgumentException>();

        transcription.CallCount.Should().Be(0);
        trialService.EnsureAccessCallCount.Should().Be(0);
        trialService.EditCellCallCount.Should().Be(0);
    }

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
