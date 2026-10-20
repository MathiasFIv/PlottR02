using ClosedXML.Excel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PlottR0._2.Contracts.Recordings;
using PlottR0._2.Contracts.Repositories;
using PlottR0._2.Contracts.Transcription;
using PlottR0._2.Domain;
using PlottR0._2.Repository.Repositories;
using PlottR0._2.Services.Abstractions;
using PlottR0._2.Services.Implementations;
using PlottR0._2.Tests.Support;
using PlottR0._2.Tests.Support.WorkbookBuilder;

namespace PlottR0._2.Tests;

[Collection(PostgresCollection.Name)]
public sealed class RecordingSyncServiceTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly List<Guid> _createdTrialIds = [];

    public RecordingSyncServiceTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var trialId in _createdTrialIds)
        {
            try
            {
                var serviceDir = TrialWorkbookStore.ResolveServiceDirectory();
                var workingDir = Path.Combine(serviceDir, "Working", trialId.ToString());
                if (Directory.Exists(workingDir))
                    Directory.Delete(workingDir, recursive: true);

                var recordingsDir = Path.Combine(serviceDir, "Recordings", trialId.ToString());
                if (Directory.Exists(recordingsDir))
                    Directory.Delete(recordingsDir, recursive: true);
            }
            catch
            {
            }
        }

        await _fixture.ResetDatabaseAsync();

        _fixture.CurrentUser.ResetToDefaults();
    }

    [Fact]
    public async Task SyncAsync_CompleteTranscript_WritesAndMarksWritten()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 3 value 7", Language = "en", Duration = 1.2 });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(AudioBytes(), "clip.webm", Command(trialId, clientId));

        result.Status.Should().Be(VoiceRecordingStatus.Written);
        result.Replayed.Should().BeFalse();
        result.NeedsReview.Should().BeFalse();
        result.WasTranscriptOnly.Should().BeFalse("the command supplied confirmed fields");
        result.Plot.Should().Be(101);
        result.Subsample.Should().Be(3);
        result.Value.Should().Be(7);
        result.TraitOrdinal.Should().Be(1);
        result.Transcript.Should().Be("plot 101 subsample 3 value 7");
        result.Confidence.Should().Be(0.95);
        result.ParsedPlot.Should().Be(101);
        result.ParsedSubsample.Should().Be(3);
        result.ParsedValue.Should().Be(7);
        result.PlotExists.Should().BeTrue();
        result.WrittenCells.Should().ContainSingle().Which.Subsample.Should().Be(3);
        NormalizeAddress(result.WrittenCells[0].CellAddress).Should().Be("C24");
        result.WrittenCells[0].Value.Should().Be(7);
        result.SkippedPresentCells.Should().BeEmpty();
        result.MissingSubsampleIndices.Should().BeEmpty();
        fake.CallCount.Should().Be(1);

        OutputCopies(trialId).Should().ContainSingle();
        BlobFiles(trialId).Should().ContainSingle();

        await AssertStoredAsync(clientId, row =>
        {
            row.Status.Should().Be(VoiceRecordingStatus.Written);
            row.Transcript.Should().Be("plot 101 subsample 3 value 7");
            row.ConfirmedPlot.Should().Be(101);
            row.ConfirmedSubsample.Should().Be(3);
            row.ConfirmedValue.Should().Be(7);
            row.ParsedPlot.Should().Be(101);
            row.ParsedSubsample.Should().Be(3);
            row.ParsedValue.Should().Be(7);
            row.Confidence.Should().Be(0.95);
            row.BlobPath.Should().NotBeNullOrWhiteSpace();
        });
    }

    [Fact]
    public async Task SyncAsync_TranscriptOnly_CompleteParse_WritesAndAutoAccepts()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 3 value 7" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null));

        result.Status.Should().Be(VoiceRecordingStatus.Written);
        result.Replayed.Should().BeFalse();
        result.NeedsReview.Should().BeFalse("a complete, confident, uncontested parse needs no review");
        result.WasTranscriptOnly.Should().BeTrue("no field was confirmed by the caller");
        result.Plot.Should().Be(101);
        result.Subsample.Should().Be(3);
        result.Value.Should().Be(7);
        result.Confidence.Should().Be(0.95);
        result.WrittenCells.Should().ContainSingle().Which.Subsample.Should().Be(3);
        result.WrittenCells[0].Value.Should().Be(7);
        OutputCopies(trialId).Should().ContainSingle();
        BlobFiles(trialId).Should().ContainSingle();
        fake.CallCount.Should().Be(1);

        await AssertStoredAsync(clientId, row =>
        {
            row.Status.Should().Be(VoiceRecordingStatus.Written);
            row.ConfirmedPlot.Should().BeNull();
            row.ConfirmedSubsample.Should().BeNull();
            row.ConfirmedValue.Should().BeNull();
            row.ParsedPlot.Should().Be(101);
            row.ParsedSubsample.Should().Be(3);
            row.ParsedValue.Should().Be(7);
        });
    }

    [Fact]
    public async Task SyncAsync_TranscriptOnly_PartialNoSubsample_FlagsWithoutWriting()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 value 7" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null));

        result.Status.Should().Be(VoiceRecordingStatus.Flagged);
        result.NeedsReview.Should().BeTrue("a partial parse always requires review");
        result.WasTranscriptOnly.Should().BeTrue();
        result.Value.Should().Be(7, "the parsed value still resolves");
        result.Subsample.Should().BeNull("neither the command nor the transcript supplied one");
        result.WrittenCells.Should().BeEmpty();
        OutputCopies(trialId).Should().BeEmpty();
        fake.CallCount.Should().Be(1, "a transcript-only sync must still transcribe");

        await AssertStoredAsync(clientId, row =>
        {
            row.Status.Should().Be(VoiceRecordingStatus.Flagged);
            row.ParsedPlot.Should().Be(101);
            row.ParsedSubsample.Should().BeNull();
            row.ParsedValue.Should().Be(7);
        });
    }

    [Fact]
    public async Task SyncAsync_TranscriptOnly_EmptyTranscript_FlagsWithZeroConfidence()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(new TranscriptionResult { Text = "" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null));

        result.Status.Should().Be(VoiceRecordingStatus.Flagged);
        result.NeedsReview.Should().BeTrue();
        result.WasTranscriptOnly.Should().BeTrue();
        result.Confidence.Should().Be(0, "an empty transcript parses nothing");
        result.Plot.Should().BeNull();
        result.Subsample.Should().BeNull();
        result.Value.Should().BeNull();
        result.WrittenCells.Should().BeEmpty();
        OutputCopies(trialId).Should().BeEmpty();
        BlobFiles(trialId).Should().ContainSingle("the audio is still kept as evidence");

        await AssertStoredAsync(clientId, row =>
        {
            row.Status.Should().Be(VoiceRecordingStatus.Flagged);
            row.Transcript.Should().BeEmpty();
            row.Confidence.Should().Be(0);
            row.ParsedPlot.Should().BeNull();
        });
    }

    [Fact]
    public async Task SyncAsync_TranscriptOnly_ParsedOutOfRangeValue_FlagsAfterTranscription()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 3 value 12" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null));

        result.Status.Should().Be(VoiceRecordingStatus.Flagged);
        result.NeedsReview.Should().BeTrue();
        result.WasTranscriptOnly.Should().BeTrue();
        result.Value.Should().Be(12, "the resolved value is reported even when rejected");
        result.ParsedValue.Should().Be(12);
        result.WrittenCells.Should().BeEmpty();
        OutputCopies(trialId).Should().BeEmpty();
        fake.CallCount.Should().Be(1, "gate B runs AFTER transcription, unlike gate A");

        await AssertStoredAsync(clientId, row =>
        {
            row.Status.Should().Be(VoiceRecordingStatus.Flagged);
            row.ParsedValue.Should().Be(12);
        });
    }

    [Fact]
    public async Task SyncAsync_TranscriptOnly_MultiValue_ValuesFillTraitColumnsHorizontally()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 1 values 3, 6, 1" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null));

        result.Status.Should().Be(VoiceRecordingStatus.Written);
        result.NeedsReview.Should().BeFalse("a complete, confident, uncontested parse needs no review");
        result.WasTranscriptOnly.Should().BeTrue();
        result.Value.Should().Be(3, "the first value resolves as the single-value contract");
        result.ParsedValue.Should().Be(3);
        result.WrittenCells.Should().HaveCount(3);
        result.WrittenCells.Select(c => c.Subsample).Should().AllBeEquivalentTo(1);
        result.WrittenCells.Select(c => c.Value).Should().Equal(3, 6, 1);
        result.WrittenCells.Select(c => NormalizeAddress(c.CellAddress)).Should().Equal("C22", "D22", "E22");
        result.SkippedPresentCells.Should().BeEmpty();
        result.MissingSubsampleIndices.Should().BeEmpty();
        OutputCopies(trialId).Should().ContainSingle();
        fake.CallCount.Should().Be(1);

        await AssertStoredAsync(clientId, row =>
        {
            row.Status.Should().Be(VoiceRecordingStatus.Written);
            row.ParsedPlot.Should().Be(101);
            row.ParsedSubsample.Should().Be(1);
            row.ParsedValue.Should().Be(3);
        });
    }

    [Fact]
    public async Task SyncAsync_ValuesOnlyWithDefaults_FillsTargetFromDefaultsAndWrites()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "values 3 6 1" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null,
                defaultPlot: 101, defaultSubsample: 2));

        result.Status.Should().Be(VoiceRecordingStatus.Written);
        result.NeedsReview.Should().BeFalse("a values-only utterance under a client default target is authoritative; the write itself is the review");
        result.UsedDefaultTarget.Should().BeTrue();
        result.WasTranscriptOnly.Should().BeTrue();
        result.Plot.Should().Be(101, "the client-sent default plot fills the omitted target");
        result.Subsample.Should().Be(2, "the client-sent default subsample fills the omitted target");
        result.WrittenCells.Should().HaveCount(3);
        result.WrittenCells.Select(c => c.Subsample).Should().AllBeEquivalentTo(2);
        result.WrittenCells.Select(c => c.Value).Should().Equal(3, 6, 1);

        result.WrittenCells.Select(c => NormalizeAddress(c.CellAddress)).Should().Equal("C23", "D23", "E23");
        result.SkippedPresentCells.Should().BeEmpty();
        result.MissingSubsampleIndices.Should().BeEmpty();
        OutputCopies(trialId).Should().ContainSingle();
        fake.CallCount.Should().Be(1);

        await AssertStoredAsync(clientId, row =>
        {
            row.Status.Should().Be(VoiceRecordingStatus.Written);
            row.ConfirmedPlot.Should().BeNull();
            row.ConfirmedSubsample.Should().BeNull();
            row.ConfirmedValue.Should().BeNull();
            row.ParsedPlot.Should().BeNull("the transcript never stated a plot");
            row.ParsedSubsample.Should().BeNull("the transcript never stated a subsample");
            row.ParsedValue.Should().Be(3);
        });
    }

    [Fact]
    public async Task SyncAsync_BareKeywordFreeValuesWithDefaults_FillsTargetAndWrites()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "3, 6, 1" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null,
                defaultPlot: 101, defaultSubsample: 2));

        result.Status.Should().Be(VoiceRecordingStatus.Written);
        result.NeedsReview.Should().BeFalse("the default target completes the target, so the low-confidence partial parse needs no review");
        result.UsedDefaultTarget.Should().BeTrue();
        result.WasTranscriptOnly.Should().BeTrue();
        result.Plot.Should().Be(101, "the client-sent default plot fills the omitted target");
        result.Subsample.Should().Be(2, "the client-sent default subsample fills the omitted target");
        result.Confidence.Should().Be(0.5, "a keyword-free transcript never states plot/subsample → partial");
        result.ParsedPlot.Should().BeNull("a keyword-free transcript states no plot");
        result.ParsedSubsample.Should().BeNull("a keyword-free transcript states no subsample");
        result.ParsedValue.Should().Be(3);
        result.WrittenCells.Should().HaveCount(3);
        result.WrittenCells.Select(c => c.Subsample).Should().AllBeEquivalentTo(2);
        result.WrittenCells.Select(c => c.Value).Should().Equal(3, 6, 1);
        result.WrittenCells.Select(c => NormalizeAddress(c.CellAddress)).Should().Equal("C23", "D23", "E23");
        OutputCopies(trialId).Should().ContainSingle();
        fake.CallCount.Should().Be(1);

        await AssertStoredAsync(clientId, row =>
        {
            row.Status.Should().Be(VoiceRecordingStatus.Written);
            row.ParsedPlot.Should().BeNull();
            row.ParsedSubsample.Should().BeNull();
            row.ParsedValue.Should().Be(3);
            row.Confidence.Should().Be(0.5);
        });
    }

    [Fact]
    public async Task SyncAsync_BareKeywordFreeValuesWithoutDefaults_HasNoAddressableTarget_Flags()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "3, 6, 1" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null));

        result.Status.Should().Be(VoiceRecordingStatus.Flagged);
        result.NeedsReview.Should().BeTrue("no default target → the incomplete 0.5 parse must not auto-write");
        result.UsedDefaultTarget.Should().BeFalse();
        result.Plot.Should().BeNull();
        result.Subsample.Should().BeNull();
        result.Confidence.Should().Be(0.5);
        result.WrittenCells.Should().BeEmpty();
        OutputCopies(trialId).Should().BeEmpty();
        fake.CallCount.Should().Be(1);

        await AssertStoredAsync(clientId, row => row.Status.Should().Be(VoiceRecordingStatus.Flagged));
    }

    [Fact]
    public async Task SyncAsync_DeletedBareTriple101_IsOutOfRangeAndNeverWritten()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "101 3 7" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null,
                defaultPlot: 101, defaultSubsample: 2));

        result.Status.Should().Be(VoiceRecordingStatus.Flagged);
        result.NeedsReview.Should().BeTrue();
        result.ParsedPlot.Should().BeNull("the deleted triple form no longer yields a plot");
        result.ParsedSubsample.Should().BeNull();
        result.ParsedValue.Should().Be(101, "101 survives parsing (format-only) and is reported as evidence");
        result.Value.Should().Be(101, "the resolved value is echoed even when rejected");
        result.Confidence.Should().Be(0.5);
        result.WrittenCells.Should().BeEmpty();
        result.PlotExists.Should().BeFalse("gate B runs before any workbook write");
        OutputCopies(trialId).Should().BeEmpty();
        fake.CallCount.Should().Be(1, "gate B runs after transcription, unlike gate A");

        await AssertStoredAsync(clientId, row =>
        {
            row.Status.Should().Be(VoiceRecordingStatus.Flagged);
            row.ParsedPlot.Should().BeNull();
            row.ParsedValue.Should().Be(101);
        });
    }

    [Fact]
    public async Task SyncAsync_OwnerUtterance_Glued398_WritesThreeCellsAndNeedsReview()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "Plot 101 subsamble 1.Values 398" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null));

        result.Status.Should().Be(VoiceRecordingStatus.Written);
        result.NeedsReview.Should().BeTrue("the repaired parse is capped at 0.70, below ReviewConfidenceThreshold");
        result.ParsedPlot.Should().Be(101, "the subamble alias still resolves the subsample keyword");
        result.ParsedSubsample.Should().Be(1);
        result.ParsedValue.Should().Be(3, "the reported first value agrees with the cell that was written");
        result.Value.Should().Be(3);
        result.Confidence.Should().Be(0.70, "a repair can never claim more than 0.70");
        result.WrittenCells.Should().HaveCount(3);
        result.WrittenCells.Select(c => c.Value).Should().Equal(3, 9, 8);
        result.WrittenCells.Select(c => NormalizeAddress(c.CellAddress)).Should().Equal("C22", "D22", "E22");
        result.OutOfTraitRangeValueCount.Should().Be(0, "every repaired digit fits trait columns C..E");
        result.Transcript.Should().Be("Plot 101 subsamble 1.Values 398",
            "the RAW transcript is the source of truth — the repair is never echoed over it");
        OutputCopies(trialId).Should().ContainSingle();
        fake.CallCount.Should().Be(1);

        await AssertStoredAsync(clientId, row =>
        {
            row.Status.Should().Be(VoiceRecordingStatus.Written);
            row.Transcript.Should().Be("Plot 101 subsamble 1.Values 398");
            row.ParsedValue.Should().Be(3);
            row.Confidence.Should().Be(0.70);
        });
    }

    [Fact]
    public async Task SyncAsync_BareSingleGlued398_WithDefaults_WritesThreeCells()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "398" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null,
                defaultPlot: 101, defaultSubsample: 2));

        result.Status.Should().Be(VoiceRecordingStatus.Written);
        result.UsedDefaultTarget.Should().BeTrue();
        result.Plot.Should().Be(101);
        result.Subsample.Should().Be(2);
        result.WrittenCells.Should().HaveCount(3);
        result.WrittenCells.Select(c => c.Value).Should().Equal(3, 9, 8);
        result.WrittenCells.Select(c => NormalizeAddress(c.CellAddress)).Should().Equal("C23", "D23", "E23");
        result.Confidence.Should().Be(0.5, "an incomplete parse stays at the partial band");

        result.NeedsReview.Should().BeFalse();
        OutputCopies(trialId).Should().ContainSingle();
        fake.CallCount.Should().Be(1);

        await AssertStoredAsync(clientId, row => row.Status.Should().Be(VoiceRecordingStatus.Written));
    }

    [Fact]
    public async Task SyncAsync_GluedExpansionPastTrait9_PartialWriteAndFlags()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 1 values 398" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null,
                traitOrdinal: 9, value: null));

        result.Status.Should().Be(VoiceRecordingStatus.Flagged);
        result.NeedsReview.Should().BeTrue();
        result.OutOfTraitRangeValueCount.Should().Be(2, "traits 10 and 11 have no column past K");
        result.WrittenCells.Should().ContainSingle();
        result.WrittenCells[0].Value.Should().Be(3, "the first repaired digit is the value written");
        result.WrittenCells[0].TraitOrdinal.Should().Be(9);
        NormalizeAddress(result.WrittenCells[0].CellAddress).Should().Be("K22");
        result.Confidence.Should().Be(0.70, "the repair caps the parse below the review threshold");
        result.SkippedPresentCells.Should().BeEmpty();
        OutputCopies(trialId).Should().ContainSingle("the single legal write still exports a copy");
        fake.CallCount.Should().Be(1);

        await AssertStoredAsync(clientId, row =>
        {
            row.Status.Should().Be(VoiceRecordingStatus.Flagged);
            row.ParsedValue.Should().Be(3);
            row.Confidence.Should().Be(0.70);
        });
    }

    [Fact]
    public async Task SyncAsync_ValuesOnlyWithoutDefaults_HasNoAddressableTarget_Flags()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "values 3 6 1" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null));

        result.Status.Should().Be(VoiceRecordingStatus.Flagged);
        result.NeedsReview.Should().BeTrue();
        result.UsedDefaultTarget.Should().BeFalse();
        result.Plot.Should().BeNull();
        result.Subsample.Should().BeNull();
        result.WrittenCells.Should().BeEmpty();
        OutputCopies(trialId).Should().BeEmpty();
        fake.CallCount.Should().Be(1);

        await AssertStoredAsync(clientId, row => row.Status.Should().Be(VoiceRecordingStatus.Flagged));
    }

    [Fact]
    public async Task SyncAsync_ExplicitNewPlotWithDefaults_RejectsDefaultSubsampleAndFlags()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 102 values 3 6 1" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null,
                defaultPlot: 101, defaultSubsample: 2));

        result.Status.Should().Be(VoiceRecordingStatus.Flagged);
        result.NeedsReview.Should().BeTrue("an unresolved subsample always requires review");
        result.UsedDefaultTarget.Should().BeFalse("the default subsample was refused, so no default affected the target");
        result.Plot.Should().Be(102, "the explicit spoken plot still resolves");
        result.Subsample.Should().BeNull("the guard refused the stale subsample cursor for the new plot");
        result.WrittenCells.Should().BeEmpty();
        OutputCopies(trialId).Should().BeEmpty();
        fake.CallCount.Should().Be(1);

        await AssertStoredAsync(clientId, row => row.Status.Should().Be(VoiceRecordingStatus.Flagged));
    }

    [Fact]
    public async Task SyncAsync_SameDefaultPlotWithDefaults_SubsampleDefaultsAndWrites()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 values 3 6 1" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null,
                defaultPlot: 101, defaultSubsample: 2));

        result.Status.Should().Be(VoiceRecordingStatus.Written);
        result.NeedsReview.Should().BeFalse("the default subsample completed a target whose plot the operator confirmed");
        result.UsedDefaultTarget.Should().BeTrue("the subsample came from the client default");
        result.Plot.Should().Be(101);
        result.Subsample.Should().Be(2);
        result.WrittenCells.Should().HaveCount(3);
        result.WrittenCells.Select(c => c.Subsample).Should().AllBeEquivalentTo(2);
        result.WrittenCells.Select(c => c.Value).Should().Equal(3, 6, 1);
        result.WrittenCells.Select(c => NormalizeAddress(c.CellAddress)).Should().Equal("C23", "D23", "E23");
        OutputCopies(trialId).Should().ContainSingle();
        fake.CallCount.Should().Be(1);

        await AssertStoredAsync(clientId, row => row.Status.Should().Be(VoiceRecordingStatus.Written));
    }

    [Fact]
    public async Task SyncAsync_ExplicitSubsampleWithDefaults_PlotDefaultsAndExplicitSubsampleWins()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "subsample 1 values 3 6 1" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null,
                defaultPlot: 101, defaultSubsample: 2));

        result.Status.Should().Be(VoiceRecordingStatus.Written);
        result.NeedsReview.Should().BeFalse("the spoken subsample completed the target");
        result.UsedDefaultTarget.Should().BeTrue("the plot came from the client default");
        result.Plot.Should().Be(101);
        result.Subsample.Should().Be(1, "an explicit spoken subsample always wins over the default");
        result.WrittenCells.Should().HaveCount(3);
        result.WrittenCells.Select(c => c.Subsample).Should().AllBeEquivalentTo(1);
        result.WrittenCells.Select(c => c.Value).Should().Equal(3, 6, 1);
        result.WrittenCells.Select(c => NormalizeAddress(c.CellAddress)).Should().Equal("C22", "D22", "E22");
        OutputCopies(trialId).Should().ContainSingle();
        fake.CallCount.Should().Be(1);

        await AssertStoredAsync(clientId, row => row.Status.Should().Be(VoiceRecordingStatus.Written));
    }

    [Fact]
    public async Task SyncAsync_FullyExplicitWithDefaults_DefaultsUnusedAndNoReview()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 102 subsample 1 values 3 6 1" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null,
                defaultPlot: 101, defaultSubsample: 2));

        result.Status.Should().Be(VoiceRecordingStatus.Written);
        result.NeedsReview.Should().BeFalse("a complete, confident, uncontested parse needs no review");
        result.UsedDefaultTarget.Should().BeFalse("a fully explicit utterance never touches the defaults");
        result.Plot.Should().Be(102);
        result.Subsample.Should().Be(1);
        result.WrittenCells.Should().HaveCount(3);
        result.WrittenCells.Select(c => c.Subsample).Should().AllBeEquivalentTo(1);
        result.WrittenCells.Select(c => c.Value).Should().Equal(3, 6, 1);

        result.WrittenCells.Select(c => NormalizeAddress(c.CellAddress)).Should().Equal("C26", "D26", "E26");
        OutputCopies(trialId).Should().ContainSingle();
        fake.CallCount.Should().Be(1);

        await AssertStoredAsync(clientId, row => row.Status.Should().Be(VoiceRecordingStatus.Written));
    }

    [Fact]
    public async Task SyncAsync_DefaultsMatchingSpokenTarget_UsedDefaultTargetIsFalse()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 2 values 3 6 1" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null,
                defaultPlot: 101, defaultSubsample: 2));

        result.Status.Should().Be(VoiceRecordingStatus.Written);
        result.NeedsReview.Should().BeFalse("a complete, confident, uncontested parse needs no review");
        result.UsedDefaultTarget.Should().BeFalse("the target was spoken, not defaulted; the defaults coincidentally match");
        result.Plot.Should().Be(101);
        result.Subsample.Should().Be(2);
        result.WrittenCells.Should().HaveCount(3);
        result.WrittenCells.Select(c => NormalizeAddress(c.CellAddress)).Should().Equal("C23", "D23", "E23");
        OutputCopies(trialId).Should().ContainSingle();
        fake.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task SyncAsync_ZeroDefaults_TreatedAsAbsent_FlagsValuesOnlyUtterance()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "values 3 6 1" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null,
                defaultPlot: 0, defaultSubsample: 0));

        result.Status.Should().Be(VoiceRecordingStatus.Flagged);
        result.NeedsReview.Should().BeTrue();
        result.UsedDefaultTarget.Should().BeFalse("zero defaults are normalized to absent, so no default affected the target");
        result.Plot.Should().BeNull();
        result.Subsample.Should().BeNull();
        result.WrittenCells.Should().BeEmpty();
        OutputCopies(trialId).Should().BeEmpty();
        fake.CallCount.Should().Be(1);

        await AssertStoredAsync(clientId, row => row.Status.Should().Be(VoiceRecordingStatus.Flagged));
    }

    [Fact]
    public async Task SyncAsync_TranscriptOnly_ScanForFreeCells_WritesIntoPartRatedBlock()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 1 values 3, 6, 1" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null));

        result.Status.Should().Be(VoiceRecordingStatus.Written);
        result.NeedsReview.Should().BeFalse("a complete, confident, uncontested parse needs no review");
        result.WasTranscriptOnly.Should().BeTrue();
        result.WrittenCells.Should().HaveCount(3);
        result.WrittenCells.Select(c => NormalizeAddress(c.CellAddress)).Should().Equal("C22", "D22", "E22");
        result.WrittenCells.Select(c => c.Value).Should().Equal(3, 6, 1);
        result.SkippedPresentCells.Should().BeEmpty("append mode never reports occupied cells as skipped");
        OutputCopies(trialId).Should().ContainSingle();
        BlobFiles(trialId).Should().ContainSingle();
        fake.CallCount.Should().Be(1);

        await AssertStoredAsync(clientId, row => row.Status.Should().Be(VoiceRecordingStatus.Written));
    }

    [Fact]
    public async Task SyncAsync_TranscriptOnly_ScanForFreeCells_SecondSyncAppendsNextLeaf()
    {
        var trialId = await CreateTrialAsync();
        var clientId1 = Guid.NewGuid().ToString();
        var clientId2 = Guid.NewGuid().ToString();

        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 1 values 3, 6, 1" },
            new TranscriptionResult { Text = "plot 101 subsample 1 values 4, 2, 6" });

        SyncVoiceRecordingResult first;
        using (var scope = _fixture.CreateScope())
        {
            first = await CreateService(scope, fake).SyncAsync(
                AudioBytes(), "clip.webm", Command(trialId, clientId1, plot: null, subsample: null, value: null));
        }
        first.Status.Should().Be(VoiceRecordingStatus.Written);
        first.WrittenCells.Select(c => NormalizeAddress(c.CellAddress)).Should().Equal("C22", "D22", "E22");

        SyncVoiceRecordingResult second;
        using (var scope = _fixture.CreateScope())
        {
            second = await CreateService(scope, fake).SyncAsync(
                AudioBytes(), "clip.webm", Command(trialId, clientId2, plot: null, subsample: null, value: null));
        }

        second.Status.Should().Be(VoiceRecordingStatus.Written);
        second.Replayed.Should().BeFalse();
        second.NeedsReview.Should().BeFalse();
        second.WrittenCells.Select(c => NormalizeAddress(c.CellAddress)).Should().Equal("F22", "G22", "H22");
        second.WrittenCells.Select(c => c.Value).Should().Equal(4, 2, 6);
        second.SkippedPresentCells.Should().BeEmpty();
        OutputCopies(trialId).Should().HaveCount(2);
        BlobFiles(trialId).Should().HaveCount(2);

        var newest = OutputCopies(trialId).OrderByDescending(File.GetLastWriteTimeUtc).First();
        ReadCell(newest, plot: 101, subsample: 1, traitOrdinal: 1).Should().Be(3);
        ReadCell(newest, plot: 101, subsample: 1, traitOrdinal: 3).Should().Be(1);
        ReadCell(newest, plot: 101, subsample: 1, traitOrdinal: 4).Should().Be(4);
        ReadCell(newest, plot: 101, subsample: 1, traitOrdinal: 6).Should().Be(6);

        await AssertStoredAsync(clientId2, row => row.Status.Should().Be(VoiceRecordingStatus.Written));
    }

    [Fact]
    public async Task SyncAsync_PartialFirstWrite_AdvancesTrialStatusToInProgress()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 1 value 7" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: 101, subsample: 1, value: 7));

        result.Status.Should().Be(VoiceRecordingStatus.Written);
        result.NeedsReview.Should().BeFalse();
        result.WrittenCells.Should().ContainSingle();
        (await GetTrialStatusAsync(trialId)).Should().Be(TrialStatus.InProgress);

        await AssertStoredAsync(clientId, row => row.Status.Should().Be(VoiceRecordingStatus.Written));
    }

    [Fact]
    public async Task SyncAsync_LaterWriteFillsLastCell_CompletesTrial()
    {
        var prefilled = new Dictionary<(int plot, int subsample, int traitOrdinal), int>();
        foreach (var plot in RatingWorkbookFactory.Plots)
        {
            foreach (var subsample in RatingWorkbookFactory.Subsamples)
            {
                for (var trait = 1; trait <= 9; trait++)
                {
                    prefilled[(plot, subsample, trait)] = 1;
                }
            }
        }
        prefilled.Remove((101, 1, 1));
        prefilled.Remove((101, 1, 2));

        var trialId = await CreateTrialAsync(prefilled);
        var clientId1 = Guid.NewGuid().ToString();
        var clientId2 = Guid.NewGuid().ToString();

        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 1 value 5" },
            new TranscriptionResult { Text = "plot 101 subsample 1 value 6" });

        SyncVoiceRecordingResult first;
        using (var scope = _fixture.CreateScope())
        {
            first = await CreateService(scope, fake).SyncAsync(
                AudioBytes(), "clip.webm", Command(trialId, clientId1, plot: 101, subsample: 1, value: 5));
        }

        first.Status.Should().Be(VoiceRecordingStatus.Written);
        first.WrittenCells.Select(c => NormalizeAddress(c.CellAddress)).Should().Equal("C22");
        (await GetTrialStatusAsync(trialId)).Should().Be(TrialStatus.InProgress);

        SyncVoiceRecordingResult second;
        using (var scope = _fixture.CreateScope())
        {
            second = await CreateService(scope, fake).SyncAsync(
                AudioBytes(), "clip.webm", Command(trialId, clientId2, plot: 101, subsample: 1, value: 6));
        }

        second.Status.Should().Be(VoiceRecordingStatus.Written);
        second.Replayed.Should().BeFalse();
        second.WrittenCells.Select(c => NormalizeAddress(c.CellAddress)).Should().Equal("D22");
        second.WrittenCells[0].Value.Should().Be(6);
        (await GetTrialStatusAsync(trialId)).Should().Be(TrialStatus.Completed);

        var newest = OutputCopies(trialId).OrderByDescending(File.GetLastWriteTimeUtc).First();
        ReadCell(newest, plot: 101, subsample: 1, traitOrdinal: 1).Should().Be(5);
        ReadCell(newest, plot: 101, subsample: 1, traitOrdinal: 2).Should().Be(6);
    }

    [Fact]
    public async Task SyncAsync_WriteAfterCompletion_StaysCompleted()
    {
        var prefilled = new Dictionary<(int plot, int subsample, int traitOrdinal), int>();
        foreach (var plot in RatingWorkbookFactory.Plots)
        {
            foreach (var subsample in RatingWorkbookFactory.Subsamples)
            {
                for (var trait = 1; trait <= 9; trait++)
                {
                    prefilled[(plot, subsample, trait)] = 1;
                }
            }
        }
        prefilled.Remove((101, 1, 1));
        prefilled.Remove((102, 1, 1));

        var trialId = await CreateTrialAsync(prefilled);
        using (var setupScope = _fixture.CreateScope())
        {
            await setupScope.ServiceProvider.GetRequiredService<ITrialRepository>()
                .UpdateStatusAsync(trialId, TrialStatus.Completed);
        }

        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 1 value 5" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: 101, subsample: 1, value: 5));

        result.Status.Should().Be(VoiceRecordingStatus.Written);
        result.WrittenCells.Should().ContainSingle();
        (await GetTrialStatusAsync(trialId)).Should().Be(TrialStatus.Completed);
    }

    [Fact]
    public async Task SyncAsync_NoWriteStaysFlagged_LeavesTrialStatusUntouched()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 1 value 99" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: 101, subsample: 1, value: 99));

        result.Status.Should().Be(VoiceRecordingStatus.Flagged);
        result.NeedsReview.Should().BeTrue();
        result.WrittenCells.Should().BeEmpty();
        OutputCopies(trialId).Should().BeEmpty();
        (await GetTrialStatusAsync(trialId)).Should().Be(TrialStatus.Created);

        await AssertStoredAsync(clientId, row => row.Status.Should().Be(VoiceRecordingStatus.Flagged));
    }

    [Fact]
    public async Task SyncAsync_MultiValue_OneOutOfRangeValue_FlagsWithoutWriting()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 1 values 3, 12" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null));

        result.Status.Should().Be(VoiceRecordingStatus.Flagged);
        result.NeedsReview.Should().BeTrue();
        result.WasTranscriptOnly.Should().BeTrue();
        result.Value.Should().Be(3, "the first parsed value is still reported");
        result.ParsedValue.Should().Be(3);
        result.WrittenCells.Should().BeEmpty();
        result.SkippedPresentCells.Should().BeEmpty();
        OutputCopies(trialId).Should().BeEmpty();
        fake.CallCount.Should().Be(1, "gate B runs AFTER transcription, unlike gate A");

        await AssertStoredAsync(clientId, row =>
        {
            row.Status.Should().Be(VoiceRecordingStatus.Flagged);
            row.ParsedValue.Should().Be(3);
        });
    }

    [Fact]
    public async Task SyncAsync_TraitOrdinalZero_DefaultsToOne_WritesAtTraitOne()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 1 value 5" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, traitOrdinal: 0, value: null));

        result.Status.Should().Be(VoiceRecordingStatus.Written);
        result.NeedsReview.Should().BeFalse();
        result.TraitOrdinal.Should().Be(1, "a missing/zero traitOrdinal defaults to 1");
        result.ParsedValue.Should().Be(5);
        result.WrittenCells.Should().ContainSingle().Which.Subsample.Should().Be(1);
        result.WrittenCells[0].TraitOrdinal.Should().Be(1);
        NormalizeAddress(result.WrittenCells[0].CellAddress).Should().Be("C22");
        result.WrittenCells[0].Value.Should().Be(5);
        result.SkippedPresentCells.Should().BeEmpty();
        OutputCopies(trialId).Should().ContainSingle();
        fake.CallCount.Should().Be(1);

        await AssertStoredAsync(clientId, row => row.Status.Should().Be(VoiceRecordingStatus.Written));
    }

    [Fact]
    public async Task SyncAsync_TraitOrdinalZero_DefaultsToOne()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 1 values 3, 6, 1" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, traitOrdinal: 0, value: null));

        result.Status.Should().Be(VoiceRecordingStatus.Written);
        result.NeedsReview.Should().BeFalse("a complete, confident, uncontested parse needs no review");
        result.WasTranscriptOnly.Should().BeTrue();
        result.TraitOrdinal.Should().Be(1, "a missing/zero traitOrdinal defaults to 1");
        result.WrittenCells.Should().HaveCount(3);
        result.WrittenCells.Select(c => NormalizeAddress(c.CellAddress)).Should().Equal("C22", "D22", "E22");
        result.WrittenCells.Select(c => c.Value).Should().Equal(3, 6, 1);
        result.SkippedPresentCells.Should().BeEmpty();
        result.MissingSubsampleIndices.Should().BeEmpty();
        OutputCopies(trialId).Should().ContainSingle();
        fake.CallCount.Should().Be(1);

        await AssertStoredAsync(clientId, row => row.Status.Should().Be(VoiceRecordingStatus.Written));
    }

    [Fact]
    public async Task SyncAsync_TraitOrdinalNine_OverflowFlagsInsteadOfSilentlyDropping()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 1 values 3, 6, 1" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, traitOrdinal: 9, value: null));

        result.Status.Should().Be(VoiceRecordingStatus.Flagged);
        result.NeedsReview.Should().BeTrue();
        result.OutOfTraitRangeValueCount.Should().Be(2, "6 and 1 have no free cell left in the LEAF3 block (I:K) past trait 9");
        result.WrittenCells.Should().ContainSingle();
        result.WrittenCells[0].Value.Should().Be(3);
        NormalizeAddress(result.WrittenCells[0].CellAddress).Should().Be("K22");
        result.SkippedPresentCells.Should().BeEmpty();
        OutputCopies(trialId).Should().ContainSingle("the single legal write still exports a copy");
        fake.CallCount.Should().Be(1);

        await AssertStoredAsync(clientId, row => row.Status.Should().Be(VoiceRecordingStatus.Flagged));
    }

    [Fact]
    public async Task SyncAsync_ForwardedTraitOrdinalFour_WritesFromColumnFAndPersistsResolvedOrdinal()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "values 3 6 1" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null,
                traitOrdinal: 4, value: null, defaultPlot: 101, defaultSubsample: 1));

        result.Status.Should().Be(VoiceRecordingStatus.Written);
        result.NeedsReview.Should().BeFalse("a values-only utterance under a client default target is authoritative");
        result.UsedDefaultTarget.Should().BeTrue();
        result.TraitOrdinal.Should().Be(4, "the client's confirmed anchor is echoed verbatim, never substituted with 1");
        result.WrittenCells.Should().HaveCount(3);
        result.WrittenCells.Select(c => NormalizeAddress(c.CellAddress)).Should().Equal("F22", "G22", "H22");
        result.WrittenCells.Select(c => c.TraitOrdinal).Should().Equal(4, 5, 6);
        result.WrittenCells.Select(c => c.Value).Should().Equal(3, 6, 1);
        result.SkippedPresentCells.Should().BeEmpty();
        result.MissingSubsampleIndices.Should().BeEmpty();
        OutputCopies(trialId).Should().ContainSingle();
        fake.CallCount.Should().Be(1);

        var copy = OutputCopies(trialId).Single();
        ReadCell(copy, plot: 101, subsample: 1, traitOrdinal: 4).Should().Be(3);
        ReadCell(copy, plot: 101, subsample: 1, traitOrdinal: 6).Should().Be(1);
        ReadCell(copy, plot: 101, subsample: 1, traitOrdinal: 1).Should().BeNull(
            "the forwarded anchor owns the run — the default first cell receives nothing");

        await AssertStoredAsync(clientId, row =>
        {
            row.Status.Should().Be(VoiceRecordingStatus.Written);
            row.ResolvedTraitOrdinal.Should().Be(4,
                "the resolved ordinal is stored verbatim on both paths; > 9 passes normalization unchanged");
        });
    }

    [Fact]
    public async Task SyncAsync_TraitOrdinalAboveNine_FlagsWithoutWriting()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "values 3 6 1" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null,
                traitOrdinal: 10, value: null, defaultPlot: 101, defaultSubsample: 1));

        result.Status.Should().Be(VoiceRecordingStatus.Flagged);
        result.NeedsReview.Should().BeTrue();
        result.TraitOrdinal.Should().Be(10, "the out-of-range ordinal is echoed, not clamped and never 0");
        result.WrittenCells.Should().BeEmpty();
        result.SkippedPresentCells.Should().BeEmpty();
        result.OutOfTraitRangeValueCount.Should().Be(0, "the ordinal gate runs before the write, so nothing was dropped mid-write");
        OutputCopies(trialId).Should().BeEmpty("a flagged sync never exports a workbook copy");
        fake.CallCount.Should().Be(1);

        await AssertStoredAsync(clientId, row =>
        {
            row.Status.Should().Be(VoiceRecordingStatus.Flagged);
            row.ResolvedTraitOrdinal.Should().Be(10, "the flagged row stores the anchor the reviewer must fix");
        });
    }

    [Fact]
    public async Task SyncAsync_NegativeTraitOrdinal_BehavesLikeAbsent()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 1 value 5" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null,
                traitOrdinal: -1, value: null));

        result.Status.Should().Be(VoiceRecordingStatus.Written);
        result.TraitOrdinal.Should().Be(1, "a negative traitOrdinal normalizes to 1 exactly like an absent one");
        result.WrittenCells.Should().ContainSingle();
        NormalizeAddress(result.WrittenCells[0].CellAddress).Should().Be("C22");
        result.WrittenCells[0].Value.Should().Be(5);
        OutputCopies(trialId).Should().ContainSingle();

        await AssertStoredAsync(clientId, row =>
        {
            row.Status.Should().Be(VoiceRecordingStatus.Written);
            row.ResolvedTraitOrdinal.Should().Be(1, "the stored resolved ordinal never carries the negative value");
        });
    }

    [Fact]
    public async Task SyncAsync_AbsentTraitOrdinal_DefaultsToOneAndStoresResolvedOrdinal()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 1 value 5" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null,
                traitOrdinal: 0, value: null));

        result.Status.Should().Be(VoiceRecordingStatus.Written);
        result.TraitOrdinal.Should().Be(1, "an absent/zero traitOrdinal echoes the normalized 1, never 0");
        result.WrittenCells.Should().ContainSingle();
        NormalizeAddress(result.WrittenCells[0].CellAddress).Should().Be("C22");
        result.WrittenCells[0].Value.Should().Be(5);
        OutputCopies(trialId).Should().ContainSingle();

        await AssertStoredAsync(clientId, row =>
        {
            row.Status.Should().Be(VoiceRecordingStatus.Written);
            row.ResolvedTraitOrdinal.Should().Be(1,
                "the row stores the resolved anchor the write used (1), symmetric with the echo");
        });
    }

    [Fact]
    public async Task SyncAsync_PlotZero_TreatedUnconfirmed_ResolvesFromTranscript()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 3 value 7" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: 0, subsample: null, value: null));

        result.Status.Should().Be(VoiceRecordingStatus.Written);
        result.NeedsReview.Should().BeFalse();
        result.WasTranscriptOnly.Should().BeTrue("plot 0 normalizes to unconfirmed and nothing else was confirmed");
        result.Plot.Should().Be(101);
        result.Subsample.Should().Be(3);
        result.Value.Should().Be(7);
        result.WrittenCells.Should().ContainSingle().Which.Subsample.Should().Be(3);
        OutputCopies(trialId).Should().ContainSingle();

        await AssertStoredAsync(clientId, row =>
        {
            row.Status.Should().Be(VoiceRecordingStatus.Written);
            row.ConfirmedPlot.Should().BeNull("plot 0 is not stored as a confirmed 0");
            row.ParsedPlot.Should().Be(101);
        });
    }

    [Fact]
    public async Task SyncAsync_OldContractAllZeros_MatchingTranscript_WritesResolvedTarget()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 3 value 7" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: 0, subsample: 0, value: 0));

        result.Status.Should().Be(VoiceRecordingStatus.Written);
        result.NeedsReview.Should().BeTrue("confirmed 0 disagrees with the parsed 7 and must be reviewed");
        result.WasTranscriptOnly.Should().BeFalse("value 0 was confirmed and is never treated as absent");
        result.Plot.Should().Be(101);
        result.Subsample.Should().Be(3);
        result.Value.Should().Be(0, "0 is a legal rating; the transcript cannot override a confirmed value");
        result.WrittenCells.Should().ContainSingle().Which.Subsample.Should().Be(3);
        result.WrittenCells[0].Value.Should().Be(0);
        OutputCopies(trialId).Should().ContainSingle();

        await AssertStoredAsync(clientId, row =>
        {
            row.Status.Should().Be(VoiceRecordingStatus.Written);
            row.ConfirmedPlot.Should().BeNull("plot 0 is never persisted as a confirmed 0");
            row.ConfirmedSubsample.Should().BeNull();
            row.ConfirmedValue.Should().Be(0);
            row.ParsedPlot.Should().Be(101);
            row.ParsedSubsample.Should().Be(3);
            row.ParsedValue.Should().Be(7);
        });
    }

    [Fact]
    public async Task SyncAsync_OldContractAllZeros_EmptyTranscript_FlagsWithNothingResolvable()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(new TranscriptionResult { Text = "" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: 0, subsample: 0, value: 0));

        result.Status.Should().Be(VoiceRecordingStatus.Flagged);
        result.NeedsReview.Should().BeTrue();
        result.WasTranscriptOnly.Should().BeFalse("value 0 was confirmed");
        result.Plot.Should().BeNull();
        result.Subsample.Should().BeNull();
        result.Value.Should().Be(0);
        result.WrittenCells.Should().BeEmpty();
        OutputCopies(trialId).Should().BeEmpty();
        fake.CallCount.Should().Be(1);

        await AssertStoredAsync(clientId, row => row.Status.Should().Be(VoiceRecordingStatus.Flagged));
    }

    [Fact]
    public async Task SyncAsync_PartialConfirmedPlotOnly_CleanRest_WritesWithoutReview()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 3 value 7" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: 101, subsample: null, value: null));

        result.Status.Should().Be(VoiceRecordingStatus.Written);
        result.NeedsReview.Should().BeFalse("a complete uncontested parse needs no review");
        result.WasTranscriptOnly.Should().BeFalse("plot was confirmed");
        result.Plot.Should().Be(101);
        result.Subsample.Should().Be(3);
        result.Value.Should().Be(7);
        result.WrittenCells.Should().ContainSingle().Which.Subsample.Should().Be(3);
        OutputCopies(trialId).Should().ContainSingle();

        await AssertStoredAsync(clientId, row =>
        {
            row.Status.Should().Be(VoiceRecordingStatus.Written);
            row.ConfirmedPlot.Should().Be(101);
            row.ConfirmedSubsample.Should().BeNull();
            row.ConfirmedValue.Should().BeNull();
        });
    }

    // Pins the END STATE, not the `!parsed.IsComplete` term specifically: the confidence term
    // already forces review here (every incomplete parse scores 0.5 < 0.75). Do not "clean up" the
    // seemingly redundant IsComplete term — it is a forward guard for a different failure mode.
    [Fact]
    public async Task SyncAsync_PartialParseWithoutDefaultTarget_NeedsReview()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 3" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null,
                traitOrdinal: 1, value: 7));

        result.UsedDefaultTarget.Should().BeFalse("no client default target was sent");
        result.NeedsReview.Should().BeTrue(
            "an incomplete parse with no default target to excuse it must still require review");
    }

    [Fact]
    public async Task SyncAsync_PartialConfirmedMismatch_WritesConfirmedButFlagsReview()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 102 subsample 3 value 7" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: 101, subsample: null, value: null));

        result.Status.Should().Be(VoiceRecordingStatus.Written);
        result.NeedsReview.Should().BeTrue("the parsed plot 102 disagrees with confirmed 101");
        result.WasTranscriptOnly.Should().BeFalse();
        result.Plot.Should().Be(101, "a confirmed plot always beats the transcript");
        result.Subsample.Should().Be(3);
        result.Value.Should().Be(7);
        result.ParsedPlot.Should().Be(102);
        result.WrittenCells.Should().ContainSingle().Which.Subsample.Should().Be(3);
        result.WrittenCells[0].Value.Should().Be(7);
        OutputCopies(trialId).Should().ContainSingle();

        await AssertStoredAsync(clientId, row =>
        {
            row.Status.Should().Be(VoiceRecordingStatus.Written);
            row.ConfirmedPlot.Should().Be(101);
            row.ParsedPlot.Should().Be(102);
        });
    }

    [Fact]
    public async Task SyncAsync_TranscriptionFailure_NoConfirmedFields_FlagsWithoutWriting()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, new ThrowingTranscriptionService());

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null));

        result.Status.Should().Be(VoiceRecordingStatus.Flagged);
        result.NeedsReview.Should().BeTrue();
        result.WasTranscriptOnly.Should().BeTrue();
        result.Confidence.Should().Be(0);
        result.WrittenCells.Should().BeEmpty();
        OutputCopies(trialId).Should().BeEmpty();
        BlobFiles(trialId).Should().ContainSingle("the audio is still preserved for review");

        await AssertStoredAsync(clientId, row =>
        {
            row.Status.Should().Be(VoiceRecordingStatus.Flagged);
            row.Transcript.Should().BeEmpty();
            row.ConfirmedPlot.Should().BeNull("the omission survives as null on the row");
        });
    }

    [Fact]
    public async Task SyncAsync_TranscriptionFailure_Reports403ReasonAndFlagsReview()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, new ThrowingTranscriptionService("request failed with 403"));

        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null));

        result.TranscriptionError.Should().NotBeNullOrEmpty();
        result.TranscriptionError.Should().Contain("403");
        result.Transcript.Should().BeEmpty();
        result.NeedsReview.Should().BeTrue();
    }

    [Fact]
    public async Task SyncAsync_CellAlreadyPresent_AppendsIntoNextFreeCell()
    {
        var trialId = await CreateTrialAsync(prefilledCells: new Dictionary<(int, int, int), int>
        {
            [(101, 3, 1)] = 5,
        });
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 3 value 7" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(AudioBytes(), "clip.webm", Command(trialId, clientId));

        result.Status.Should().Be(VoiceRecordingStatus.Written);
        result.NeedsReview.Should().BeFalse("an appended value is as complete as a direct write");
        result.WrittenCells.Should().ContainSingle();
        result.WrittenCells[0].Subsample.Should().Be(3);
        result.WrittenCells[0].TraitOrdinal.Should().Be(2);
        result.WrittenCells[0].Value.Should().Be(7);
        result.SkippedPresentCells.Should().BeEmpty("append mode never reports occupies as skipped");
        result.MissingSubsampleIndices.Should().BeEmpty();

        var copy = OutputCopies(trialId).Should().ContainSingle().Subject;
        ReadCell(copy, 101, 3, 1).Should().Be(5);
        ReadCell(copy, 101, 3, 2).Should().Be(7);

        await AssertStoredAsync(clientId, row => row.Status.Should().Be(VoiceRecordingStatus.Written));
    }

    [Fact]
    public async Task SyncAsync_SameClientRecordingId_ReplaysWithoutTranscriptionOrWrite()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var firstFake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 3 value 7" });

        using (var scope = _fixture.CreateScope())
        {
            var service = CreateService(scope, firstFake);
            var first = await service.SyncAsync(AudioBytes(), "clip.webm", Command(trialId, clientId));
            first.Status.Should().Be(VoiceRecordingStatus.Written);
        }

        var replayFake = new FakeTranscriptionService();
        SyncVoiceRecordingResult replay;
        using (var scope = _fixture.CreateScope())
        {
            var service = CreateService(scope, replayFake);
            replay = await service.SyncAsync(AudioBytes(seed: 42), "clip.webm", Command(trialId, clientId));
        }

        replay.Replayed.Should().BeTrue();
        replay.Status.Should().Be(VoiceRecordingStatus.Replay);
        replay.NeedsReview.Should().BeFalse();
        replay.Transcript.Should().BeEmpty();
        replay.Confidence.Should().BeNull("a replay ran no parse");
        replay.ParsedPlot.Should().BeNull();
        replay.ParsedValue.Should().BeNull();
        replay.PlotExists.Should().BeFalse("a replay ran no workbook write");
        replay.SkippedPresentCells.Should().BeEmpty();
        replay.MissingSubsampleIndices.Should().BeEmpty();
        replayFake.CallCount.Should().Be(0);
        firstFake.CallCount.Should().Be(1);
        OutputCopies(trialId).Should().ContainSingle("a replay must not create a second workbook copy");
        BlobFiles(trialId).Should().ContainSingle("a replay must not store a second blob");

        await AssertStoredAsync(clientId, row => row.Status.Should().Be(
            VoiceRecordingStatus.Written,
            "a replay must not overwrite the first submission's terminal status"));
    }

    [Fact]
    public async Task SyncAsync_ClientRecordingIdConflict_LosingRaceAnsweredAsReplay()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var winner = new VoiceRecording
        {
            Id = Guid.NewGuid(),
            ClientRecordingId = clientId,
            TrialId = trialId,
            BlobPath = Path.Combine("Recordings", trialId.ToString(), $"{clientId}.webm"),
            MimeType = "audio/webm",
            Status = VoiceRecordingStatus.Written,
            ConfirmedPlot = 101,
            ConfirmedSubsample = 3,
            ConfirmedValue = 7,
            ResolvedTraitOrdinal = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var fake = new FakeTranscriptionService();

        SyncVoiceRecordingResult result;
        using (var scope = _fixture.CreateScope())
        {
            var realRepo = scope.ServiceProvider.GetRequiredService<IVoiceRecordingRepository>();
            var service = new RecordingSyncService(
                new RaceLosingVoiceRecordingRepository(realRepo, winner),
                scope.ServiceProvider.GetRequiredService<IVoiceBlobStore>(),
                fake,
                scope.ServiceProvider.GetRequiredService<ITrialRepository>(),
                scope.ServiceProvider.GetRequiredService<IRatingRepository>(),
                scope.ServiceProvider.GetRequiredService<ITrialWorkbookStore>(),
                scope.ServiceProvider.GetRequiredService<ITrialWriteGate>(),
                _fixture.CurrentUser,
                NullLogger<RecordingSyncService>.Instance);

            result = await service.SyncAsync(AudioBytes(), "clip.webm", Command(trialId, clientId));
        }

        result.Replayed.Should().BeTrue();
        result.Status.Should().Be(VoiceRecordingStatus.Replay);
        result.Plot.Should().Be(101);
        result.Subsample.Should().Be(3);
        result.Value.Should().Be(7);
        result.NeedsReview.Should().BeFalse();
        fake.CallCount.Should().Be(0, "the conflict path must not transcribe");
        OutputCopies(trialId).Should().BeEmpty("the conflict path must not write a workbook");
        BlobFiles(trialId).Should().ContainSingle(
            "the loser's blob stays because the winner committed the same client key");
    }

    [Fact]
    public async Task SyncAsync_SameClientRecordingId_ReplayEchoesCommittedTargetNotStaleDefaults()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var firstFake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 102 subsample 1 values 3 6 1" });

        SyncVoiceRecordingResult first;
        using (var scope = _fixture.CreateScope())
        {
            var service = CreateService(scope, firstFake);
            first = await service.SyncAsync(
                AudioBytes(), "clip.webm",
                Command(trialId, clientId, plot: null, subsample: null, value: null,
                    defaultPlot: 101, defaultSubsample: 2));
            first.Status.Should().Be(VoiceRecordingStatus.Written);
            first.Plot.Should().Be(102);
            first.Subsample.Should().Be(1);
            first.UsedDefaultTarget.Should().BeFalse("the target was spoken, not defaulted");
            first.WrittenCells.Select(c => NormalizeAddress(c.CellAddress)).Should().Equal("C26", "D26", "E26");
        }

        var replayFake = new FakeTranscriptionService();
        SyncVoiceRecordingResult replay;
        using (var scope = _fixture.CreateScope())
        {
            var service = CreateService(scope, replayFake);
            replay = await service.SyncAsync(
                AudioBytes(seed: 42), "clip.webm",
                Command(trialId, clientId, plot: null, subsample: null, value: null,
                    defaultPlot: 101, defaultSubsample: 2));
        }

        replay.Replayed.Should().BeTrue();
        replay.Status.Should().Be(VoiceRecordingStatus.Replay);
        replay.NeedsReview.Should().BeFalse();
        replay.Transcript.Should().BeEmpty();
        replay.Plot.Should().Be(102, "a replay echoes what was actually written, not the caller's stale defaults");
        replay.Subsample.Should().Be(1);
        replay.UsedDefaultTarget.Should().BeFalse("nothing was defaulted in the committed write");
        replay.Confidence.Should().BeNull("a replay ran no parse");
        replay.ParsedPlot.Should().BeNull();
        replay.ParsedSubsample.Should().BeNull();
        replay.ParsedValue.Should().BeNull();
        replay.PlotExists.Should().BeFalse("a replay ran no workbook write");
        replay.WrittenCells.Should().BeEmpty("a replay performs no write");
        replay.MissingSubsampleIndices.Should().BeEmpty();
        replayFake.CallCount.Should().Be(0);
        firstFake.CallCount.Should().Be(1);
        OutputCopies(trialId).Should().ContainSingle("a replay must not create a second workbook copy");
    }

    [Fact]
    public async Task SyncAsync_ReplayOfTraitFourRecording_EchoesCommittedTraitOrdinal()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var firstFake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "values 3 6 1" });

        using (var scope = _fixture.CreateScope())
        {
            var service = CreateService(scope, firstFake);
            var first = await service.SyncAsync(
                AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null,
                    traitOrdinal: 4, value: null, defaultPlot: 101, defaultSubsample: 1));
            first.Status.Should().Be(VoiceRecordingStatus.Written);
            first.TraitOrdinal.Should().Be(4);
            first.WrittenCells.Select(c => NormalizeAddress(c.CellAddress)).Should().Equal("F22", "G22", "H22");
        }

        var replayFake = new FakeTranscriptionService();
        SyncVoiceRecordingResult replay;
        using (var scope = _fixture.CreateScope())
        {
            var service = CreateService(scope, replayFake);
            replay = await service.SyncAsync(
                AudioBytes(seed: 42), "clip.webm", Command(trialId, clientId, plot: null, subsample: null,
                    traitOrdinal: 0, value: null, defaultPlot: 101, defaultSubsample: 1));
        }

        replay.Replayed.Should().BeTrue();
        replay.Status.Should().Be(VoiceRecordingStatus.Replay);
        replay.NeedsReview.Should().BeFalse();
        replay.TraitOrdinal.Should().Be(4, "a replay re-reports the anchor persisted on the row, not the caller's omitted one");
        replay.Plot.Should().Be(101);
        replay.Subsample.Should().Be(1);
        replay.WrittenCells.Should().BeEmpty("a replay performs no write");
        replay.PlotExists.Should().BeFalse();
        replayFake.CallCount.Should().Be(0);
        firstFake.CallCount.Should().Be(1);
        OutputCopies(trialId).Should().ContainSingle("a replay must not create a second workbook copy");

        await AssertStoredAsync(clientId, row =>
        {
            row.Status.Should().Be(VoiceRecordingStatus.Written);
            row.ResolvedTraitOrdinal.Should().Be(4, "a replay never clobbers the committed anchor");
        });
    }

    [Fact]
    public async Task SyncAsync_OutOfRangeValue_StoresBlobButFlagsWithoutTranscription()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService();
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(AudioBytes(), "clip.webm", Command(trialId, clientId, value: 11));

        result.Status.Should().Be(VoiceRecordingStatus.Flagged);
        result.NeedsReview.Should().BeTrue();
        result.Transcript.Should().BeEmpty();
        result.Confidence.Should().BeNull();
        result.ParsedPlot.Should().BeNull("the value gate runs no parse");
        result.PlotExists.Should().BeFalse("the value gate runs no workbook write");
        result.WrittenCells.Should().BeEmpty();
        result.SkippedPresentCells.Should().BeEmpty();
        fake.CallCount.Should().Be(0, "an out-of-range recording must never be transcribed");
        BlobFiles(trialId).Should().ContainSingle("the recording audio is kept as review evidence");
        OutputCopies(trialId).Should().BeEmpty();

        await AssertStoredAsync(clientId, row => row.Status.Should().Be(VoiceRecordingStatus.Flagged));
    }

    [Fact]
    public async Task SyncAsync_UnknownPlot_FlagsForReviewWithoutWriting()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 999 subsample 3 value 7" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(AudioBytes(), "clip.webm", Command(trialId, clientId, plot: 999));

        result.Status.Should().Be(VoiceRecordingStatus.Flagged);
        result.NeedsReview.Should().BeTrue();
        result.WrittenCells.Should().BeEmpty();
        result.SkippedPresentCells.Should().BeEmpty();
        result.MissingSubsampleIndices.Should().Equal(3);
        OutputCopies(trialId).Should().BeEmpty();

        await AssertStoredAsync(clientId, row => row.Status.Should().Be(VoiceRecordingStatus.Flagged));
    }

    [Fact]
    public async Task SyncAsync_MismatchedTranscript_WritesConfirmedButFlagsReview()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 102 subsample 2 value 8" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(AudioBytes(), "clip.webm", Command(trialId, clientId));

        result.Status.Should().Be(VoiceRecordingStatus.Written);
        result.NeedsReview.Should().BeTrue();
        result.WasTranscriptOnly.Should().BeFalse("the command supplied confirmed fields");
        result.ParsedPlot.Should().Be(102);
        result.ParsedValue.Should().Be(8);
        result.PlotExists.Should().BeTrue();
        result.WrittenCells.Should().ContainSingle().Which.Subsample.Should().Be(3);
        result.WrittenCells[0].Value.Should().Be(7);
        NormalizeAddress(result.WrittenCells[0].CellAddress).Should().Be("C24");
        OutputCopies(trialId).Should().ContainSingle();

        await AssertStoredAsync(clientId, row =>
        {
            row.Status.Should().Be(VoiceRecordingStatus.Written);
            row.ConfirmedPlot.Should().Be(101);
            row.ConfirmedSubsample.Should().Be(3);
            row.ConfirmedValue.Should().Be(7);
            row.ParsedPlot.Should().Be(102);
            row.ParsedSubsample.Should().Be(2);
            row.ParsedValue.Should().Be(8);
            row.Confidence.Should().Be(0.95);
        });
    }

    [Fact]
    public async Task SyncAsync_TranscriptionFailure_WritesConfirmedSingleCellAndFlagsReview()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, new ThrowingTranscriptionService());

        var result = await service.SyncAsync(AudioBytes(), "clip.webm", Command(trialId, clientId));

        result.Status.Should().Be(VoiceRecordingStatus.Written);
        result.NeedsReview.Should().BeTrue("the transcription failed, which always requires review");
        result.WasTranscriptOnly.Should().BeFalse("the command supplied confirmed fields");
        result.Transcript.Should().BeEmpty();
        result.Confidence.Should().Be(0);
        result.WrittenCells.Should().ContainSingle().Which.Subsample.Should().Be(3);
        result.WrittenCells[0].Value.Should().Be(7);
        result.SkippedPresentCells.Should().BeEmpty();
        OutputCopies(trialId).Should().ContainSingle();

        await AssertStoredAsync(clientId, row => row.Status.Should().Be(VoiceRecordingStatus.Written));
    }

    [Fact]
    public async Task SyncAsync_UnknownTrial_ThrowsKeyNotFound()
    {
        var ghostTrialId = Guid.NewGuid();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService();
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var act = () => service.SyncAsync(AudioBytes(), "clip.webm", Command(ghostTrialId, clientId));
        await act.Should().ThrowAsync<KeyNotFoundException>();

        fake.CallCount.Should().Be(0);
        BlobFiles(ghostTrialId).Should().BeEmpty("no blob must be stored for an unknown trial");
    }

    [Fact]
    public async Task SyncAsync_MissingWorkbook_FlagsWithoutCrashing()
    {
        var trialId = await CreateTrialAsync(
            workbookRelativePathFactory: id => Path.Combine("Working", id.ToString(), "DoesNotExist.xlsx"));
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 3 value 7" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(AudioBytes(), "clip.webm", Command(trialId, clientId));

        result.Status.Should().Be(VoiceRecordingStatus.Flagged);
        result.NeedsReview.Should().BeTrue();
        result.WrittenCells.Should().BeEmpty();
        OutputCopies(trialId).Should().BeEmpty();
        BlobFiles(trialId).Should().ContainSingle("the audio is still preserved for review");

        await AssertStoredAsync(clientId, row => row.Status.Should().Be(VoiceRecordingStatus.Flagged));
    }

    [Fact]
    public async Task SyncAsync_ValueZero_IsWrittenToEmptyCellNotSkipped()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 3 value 0", Duration = 1.2 });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var result = await service.SyncAsync(AudioBytes(), "clip.webm", Command(trialId, clientId, value: 0));

        result.Status.Should().Be(VoiceRecordingStatus.Written);
        result.NeedsReview.Should().BeFalse();
        result.WasTranscriptOnly.Should().BeFalse("value 0 is a confirmed field, not an absent one");
        result.ParsedValue.Should().Be(0);
        result.WrittenCells.Should().ContainSingle().Which.Subsample.Should().Be(3);
        result.WrittenCells[0].Value.Should().Be(0);
        result.SkippedPresentCells.Should().BeEmpty();
        OutputCopies(trialId).Should().ContainSingle();

        var copy = OutputCopies(trialId).Single();
        ReadCell(copy, 101, 3, 1).Should().NotBeNull("value 0 must be stored as data, not left empty");
        ReadCell(copy, 101, 3, 1).Should().Be(0);

        await AssertStoredAsync(clientId, row =>
        {
            row.Status.Should().Be(VoiceRecordingStatus.Written);
            row.ConfirmedValue.Should().Be(0);
            row.ParsedValue.Should().Be(0);
        });
    }

    [Fact]
    public async Task SyncAsync_SecondSyncSameTrial_BoundedCopyNameCarriesFirstOutputForward()
    {
        var trialId = await CreateTrialAsync();
        var clientId1 = Guid.NewGuid().ToString();
        var clientId2 = Guid.NewGuid().ToString();

        var fake1 = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 3 value 7", Duration = 1.2 });
        using (var scope = _fixture.CreateScope())
        {
            var first = await CreateService(scope, fake1).SyncAsync(
                AudioBytes(), "clip.webm", Command(trialId, clientId1, subsample: 3, value: 7));
            first.Status.Should().Be(VoiceRecordingStatus.Written);
        }

        var fake2 = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 1 value 5", Duration = 1.1 });
        using (var scope = _fixture.CreateScope())
        {
            var second = await CreateService(scope, fake2).SyncAsync(
                AudioBytes(seed: 42), "clip.webm", Command(trialId, clientId2, subsample: 1, value: 5));
            second.Status.Should().Be(VoiceRecordingStatus.Written);
            second.WrittenCells.Should().ContainSingle().Which.Subsample.Should().Be(1);
        }

        var copies = OutputCopies(trialId);
        copies.Should().HaveCount(2, "one copy per sync — names must not accumulate into a chain");
        copies.Should().OnlyContain(
            path => ReadCell(path, 101, 3, 1) == 7,
            "each successive input must be the previous sync's output copy, not the pristine workbook");
        copies.Should().Contain(
            path => ReadCell(path, 101, 1, 1) == 5,
            "a later copy must accumulate the second sync's write on top of the first");

        foreach (var copy in copies)
        {
            var stem = Path.GetFileNameWithoutExtension(copy);
            var markerIndex = stem.IndexOf("_out_", StringComparison.Ordinal);
            markerIndex.Should().Be(
                stem.LastIndexOf("_out_", StringComparison.Ordinal),
                "the name carries a single bounded stamp instead of one stamp per write");
            stem[(markerIndex + "_out_".Length)..].Should().MatchRegex(
                @"^\d{8}_\d{6}_\d{3}$", "current copies carry a millisecond stamp");
            Path.GetFileName(copy).Length.Should().BeLessThanOrEqualTo(
                129, "100-char base + 5 + 17 + 5 stays far below the 255-char filesystem limit");
            stem[..markerIndex].Length.Should().BeLessThanOrEqualTo(100, "the base is truncated");
        }
    }

    [Fact]
    public async Task CorrectAsync_FlaggedRow_AppliesCorrectionAndWrites()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101" });
        using (var scope = _fixture.CreateScope())
        {
            var flagged = await CreateService(scope, fake).SyncAsync(
                AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null));
            flagged.Status.Should().Be(VoiceRecordingStatus.Flagged);
        }
        fake.CallCount.Should().Be(1);
        BlobFiles(trialId).Should().ContainSingle();

        SyncVoiceRecordingResult result;
        using (var scope = _fixture.CreateScope())
        {
            result = await CreateService(scope, new FakeTranscriptionService())
                .CorrectAsync(clientId, plot: 101, subsample: 3, value: 7, traitOrdinal: 1);
        }

        result.Status.Should().Be(VoiceRecordingStatus.Written);
        result.NeedsReview.Should().BeFalse();
        result.Replayed.Should().BeFalse();
        result.WasTranscriptOnly.Should().BeFalse("the reviewer supplied the full target");
        result.Plot.Should().Be(101);
        result.Subsample.Should().Be(3);
        result.Value.Should().Be(7);
        result.TraitOrdinal.Should().Be(1);
        result.ParsedPlot.Should().Be(101, "the stored parse is replayed on the correction result");
        result.WrittenCells.Should().ContainSingle().Which.Subsample.Should().Be(3);
        result.WrittenCells[0].Value.Should().Be(7);
        NormalizeAddress(result.WrittenCells[0].CellAddress).Should().Be("C24",
            "the correction's append scan lands in the row's first free cell (trait 1, column C)");
        OutputCopies(trialId).Should().ContainSingle();
        BlobFiles(trialId).Should().ContainSingle("a correction never re-uploads the audio");

        await AssertStoredAsync(clientId, row =>
        {
            row.Status.Should().Be(VoiceRecordingStatus.Written);
            row.ConfirmedPlot.Should().Be(101);
            row.ConfirmedSubsample.Should().Be(3);
            row.ConfirmedValue.Should().Be(7);
            row.ParsedPlot.Should().Be(101);
        });
    }

    [Fact]
    public async Task CorrectAsync_AlreadyWrittenRow_ThrowsInvalidOperation()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 3 value 7" });
        using (var scope = _fixture.CreateScope())
        {
            var written = await CreateService(scope, fake).SyncAsync(AudioBytes(), "clip.webm", Command(trialId, clientId));
            written.Status.Should().Be(VoiceRecordingStatus.Written);
        }

        using var correctionScope = _fixture.CreateScope();
        var correctionService = CreateService(correctionScope, new FakeTranscriptionService());
        var act = () => correctionService.CorrectAsync(clientId, plot: 101, subsample: 3, value: 8, traitOrdinal: 1);
        await act.Should().ThrowAsync<InvalidOperationException>();
        OutputCopies(trialId).Should().ContainSingle("a rejected correction creates no new copy");
    }

    [Fact]
    public async Task CorrectAsync_UnknownClientRecordingId_ThrowsKeyNotFound()
    {
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, new FakeTranscriptionService());

        var act = () => service.CorrectAsync(Guid.NewGuid().ToString(), plot: 101, subsample: 3, value: 7, traitOrdinal: 1);
        (await act.Should().ThrowAsync<KeyNotFoundException>())
            .Which.Message.Should().Contain("was not found");
    }

    [Fact]
    public async Task CorrectAsync_OutOfRangeValue_FlagsWithoutWriting()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        using (var scope = _fixture.CreateScope())
        {
            var flagged = await CreateService(scope, new FakeTranscriptionService(
                new TranscriptionResult { Text = "plot 101" })).SyncAsync(
                AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null));
            flagged.Status.Should().Be(VoiceRecordingStatus.Flagged);
        }

        SyncVoiceRecordingResult result;
        using (var scope = _fixture.CreateScope())
        {
            result = await CreateService(scope, new FakeTranscriptionService())
                .CorrectAsync(clientId, plot: 101, subsample: 3, value: 11, traitOrdinal: 1);
        }

        result.Status.Should().Be(VoiceRecordingStatus.Flagged);
        result.NeedsReview.Should().BeTrue();
        result.Value.Should().Be(11);
        result.WrittenCells.Should().BeEmpty();
        OutputCopies(trialId).Should().BeEmpty("neither sync nor correction wrote anything");
        BlobFiles(trialId).Should().ContainSingle();

        await AssertStoredAsync(clientId, row =>
        {
            row.Status.Should().Be(VoiceRecordingStatus.Flagged);
            row.ConfirmedPlot.Should().Be(101);
            row.ConfirmedSubsample.Should().Be(3);
            row.ConfirmedValue.Should().Be(11);
        });
    }

    [Fact]
    public async Task CorrectAsync_OccupiedTargetCell_AppendsToNextFreeCell()
    {
        var trialId = await CreateTrialAsync(prefilledCells: new Dictionary<(int, int, int), int>
        {
            [(101, 3, 1)] = 5,
        });
        var clientId = Guid.NewGuid().ToString();
        using (var scope = _fixture.CreateScope())
        {
            var flagged = await CreateService(scope, new FakeTranscriptionService(
                new TranscriptionResult { Text = "plot 101" })).SyncAsync(
                AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null));
            flagged.Status.Should().Be(VoiceRecordingStatus.Flagged);
        }

        SyncVoiceRecordingResult result;
        using (var scope = _fixture.CreateScope())
        {
            result = await CreateService(scope, new FakeTranscriptionService())
                .CorrectAsync(clientId, plot: 101, subsample: 3, value: 7, traitOrdinal: 1);
        }

        result.Status.Should().Be(VoiceRecordingStatus.Written);
        result.NeedsReview.Should().BeFalse();
        result.WasTranscriptOnly.Should().BeFalse();
        result.WrittenCells.Should().ContainSingle();
        result.WrittenCells[0].Subsample.Should().Be(3);
        result.WrittenCells[0].TraitOrdinal.Should().Be(2, "the first free cell after C is trait 2");
        NormalizeAddress(result.WrittenCells[0].CellAddress).Should().Be("D24");
        result.WrittenCells[0].Value.Should().Be(7);
        result.SkippedPresentCells.Should().BeEmpty("occupied cells are skipped silently in append mode");
        OutputCopies(trialId).Should().ContainSingle("the written append still exports a copy");

        var copy = OutputCopies(trialId).Single();
        ReadCell(copy, 101, 3, 1).Should().Be(5, "the occupied C24 survives untouched");
        ReadCell(copy, 101, 3, 2).Should().Be(7);
        BlobFiles(trialId).Should().ContainSingle("a correction never re-uploads the audio");

        await AssertStoredAsync(clientId, row =>
        {
            row.Status.Should().Be(VoiceRecordingStatus.Written);
            row.ConfirmedValue.Should().Be(7);
        });
    }

    [Fact]
    public async Task CorrectAsync_TraitOrdinalZero_AppendsToNextFreeCell()
    {
        var trialId = await CreateTrialAsync(prefilledCells: new Dictionary<(int, int, int), int>
        {
            [(101, 1, 1)] = 1,
            [(101, 1, 2)] = 1,
            [(101, 1, 3)] = 1,
            [(101, 1, 4)] = 1,
            [(101, 1, 5)] = 1,
            [(101, 1, 6)] = 1,
        });
        var clientId = Guid.NewGuid().ToString();
        using (var scope = _fixture.CreateScope())
        {
            var flagged = await CreateService(scope, new FakeTranscriptionService(
                new TranscriptionResult { Text = "plot 101" })).SyncAsync(
                AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null));
            flagged.Status.Should().Be(VoiceRecordingStatus.Flagged);
        }

        SyncVoiceRecordingResult result;
        using (var scope = _fixture.CreateScope())
        {
            result = await CreateService(scope, new FakeTranscriptionService())
                .CorrectAsync(clientId, plot: 101, subsample: 1, value: 5, traitOrdinal: 0);
        }

        result.Status.Should().Be(VoiceRecordingStatus.Written);
        result.NeedsReview.Should().BeFalse();
        result.Value.Should().Be(5);
        result.TraitOrdinal.Should().Be(1, "a missing/zero traitOrdinal defaults to 1");
        result.WrittenCells.Should().ContainSingle();
        result.WrittenCells[0].Subsample.Should().Be(1);
        result.WrittenCells[0].Value.Should().Be(5);
        NormalizeAddress(result.WrittenCells[0].CellAddress).Should().Be("I22");
        result.SkippedPresentCells.Should().BeEmpty("occupied cells are skipped silently in append mode");
        result.MissingSubsampleIndices.Should().BeEmpty();
        OutputCopies(trialId).Should().ContainSingle();

        var copy = OutputCopies(trialId).Single();
        ReadCell(copy, 101, 1, 7).Should().Be(5, "I22 is the first free cell after the occupied C..H");
        ReadCell(copy, 101, 1, 1).Should().Be(1);
        ReadCell(copy, 101, 1, 6).Should().Be(1);
        BlobFiles(trialId).Should().ContainSingle("a correction never re-uploads the audio");

        await AssertStoredAsync(clientId, row =>
        {
            row.Status.Should().Be(VoiceRecordingStatus.Written);
            row.ConfirmedPlot.Should().Be(101);
            row.ConfirmedSubsample.Should().Be(1);
            row.ConfirmedValue.Should().Be(5);
        });
    }

    [Fact]
    public async Task CorrectAsync_TraitOrdinalAboveNine_FlagsWithoutWriting()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        using (var scope = _fixture.CreateScope())
        {
            var flagged = await CreateService(scope, new FakeTranscriptionService(
                new TranscriptionResult { Text = "plot 101" })).SyncAsync(
                AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null));
            flagged.Status.Should().Be(VoiceRecordingStatus.Flagged);
        }

        SyncVoiceRecordingResult result;
        using (var scope = _fixture.CreateScope())
        {
            result = await CreateService(scope, new FakeTranscriptionService())
                .CorrectAsync(clientId, plot: 101, subsample: 3, value: 7, traitOrdinal: 10);
        }

        result.Status.Should().Be(VoiceRecordingStatus.Flagged);
        result.NeedsReview.Should().BeTrue();
        result.TraitOrdinal.Should().Be(10, "the explicit out-of-range ordinal is echoed, not clamped");
        result.WrittenCells.Should().BeEmpty();
        result.SkippedPresentCells.Should().BeEmpty();
        OutputCopies(trialId).Should().BeEmpty("flagged corrections never export a workbook copy");
        BlobFiles(trialId).Should().ContainSingle();

        await AssertStoredAsync(clientId, row =>
        {
            row.Status.Should().Be(VoiceRecordingStatus.Flagged);
            row.ConfirmedPlot.Should().Be(101);
            row.ConfirmedSubsample.Should().Be(3);
            row.ConfirmedValue.Should().Be(7);
        });
    }

    [Fact]
    public async Task CorrectAsync_ForwardedTraitOrdinal_WritesIntoForwardedColumnAndEchoesTrait()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        using (var scope = _fixture.CreateScope())
        {
            var flagged = await CreateService(scope, new FakeTranscriptionService(
                new TranscriptionResult { Text = "plot 101" })).SyncAsync(
                AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null));
            flagged.Status.Should().Be(VoiceRecordingStatus.Flagged);
        }

        SyncVoiceRecordingResult result;
        using (var scope = _fixture.CreateScope())
        {
            result = await CreateService(scope, new FakeTranscriptionService())
                .CorrectAsync(clientId, plot: 101, subsample: 3, value: 7, traitOrdinal: 2);
        }

        result.Status.Should().Be(VoiceRecordingStatus.Written);
        result.TraitOrdinal.Should().Be(2, "the PWA's forwarded trait ordinal is echoed verbatim");
        var copy = OutputCopies(trialId).Should().ContainSingle().Which;
        ReadCell(copy, plot: 101, subsample: 3, traitOrdinal: 2).Should().Be(7,
            "the correction must write the cell in the FORWARDED trait column");
        ReadCell(copy, plot: 101, subsample: 3, traitOrdinal: 1).Should().BeNull(
            "trait 1 must stay empty — the default column receives nothing");

        await AssertStoredAsync(clientId, row =>
        {
            row.ResolvedTraitOrdinal.Should().Be(2, "the corrected row records the reviewer's confirmed anchor");
            row.ConfirmedPlot.Should().Be(101);
            row.ConfirmedSubsample.Should().Be(3);
            row.ConfirmedValue.Should().Be(7);
        });
    }

    [Fact]
    public async Task CorrectAsync_ZeroPlot_FlagsWithoutWriting()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        using (var scope = _fixture.CreateScope())
        {
            var flagged = await CreateService(scope, new FakeTranscriptionService(
                new TranscriptionResult { Text = "plot 101" })).SyncAsync(
                AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null));
            flagged.Status.Should().Be(VoiceRecordingStatus.Flagged);
        }

        SyncVoiceRecordingResult result;
        using (var scope = _fixture.CreateScope())
        {
            result = await CreateService(scope, new FakeTranscriptionService())
                .CorrectAsync(clientId, plot: 0, subsample: 3, value: 7, traitOrdinal: 1);
        }

result.Status.Should().Be(VoiceRecordingStatus.Flagged);
result.NeedsReview.Should().BeTrue();
result.WrittenCells.Should().BeEmpty();
result.SkippedPresentCells.Should().BeEmpty();
OutputCopies(trialId).Should().BeEmpty("no addressable target ⇒ nothing written");

await AssertStoredAsync(clientId, row =>
{
    row.Status.Should().Be(VoiceRecordingStatus.Flagged);
    row.ConfirmedPlot.Should().BeNull("plot 0 is discarded by the correction's normalization guard and never persisted");
    row.ConfirmedSubsample.Should().Be(3);
    row.ConfirmedValue.Should().Be(7);
});
    }

    [Fact]
    public async Task SyncAsync_UnassignedFieldWorker_ThrowsForbiddenBeforeAnyWrite()
    {
        var trialId = await CreateTrialAsync();
        _fixture.CurrentUser.UserId = await PersistFieldWorkerAsync();
        _fixture.CurrentUser.Role = UserRole.FieldWorker;
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 3 value 7" });

        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        var act = () => service.SyncAsync(AudioBytes(), "clip.webm", Command(trialId, clientId));
        await act.Should().ThrowAsync<ForbiddenAccessException>();
        fake.CallCount.Should().Be(0, "the gate fires before transcription");
        BlobFiles(trialId).Should().BeEmpty("an unauthorized sync stores no audio");
        OutputCopies(trialId).Should().BeEmpty("an unauthorized sync writes no workbook");
        await AssertNotStoredAsync(clientId);
    }

    [Fact]
    public async Task SyncAsync_AssignedFieldWorker_Succeeds()
    {
        var trialId = await CreateTrialAsync();
        var fieldWorkerId = await PersistFieldWorkerAsync();
        await AssignTrialAsync(trialId, fieldWorkerId);
        _fixture.CurrentUser.UserId = fieldWorkerId;
        _fixture.CurrentUser.Role = UserRole.FieldWorker;
        var clientId = Guid.NewGuid().ToString();

        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 3 value 7" }));

        var result = await service.SyncAsync(AudioBytes(), "clip.webm", Command(trialId, clientId));

        result.Status.Should().Be(VoiceRecordingStatus.Written);
        OutputCopies(trialId).Should().ContainSingle();
        BlobFiles(trialId).Should().ContainSingle();
    }

    [Theory]
    [InlineData(UserRole.SystemAdmin)]
    [InlineData(UserRole.Researcher)]
    [InlineData(UserRole.TrialManager)]
    public async Task SyncAsync_Managers_UnassignedTrial_Succeeds(UserRole role)
    {
        var trialId = await CreateTrialAsync();
        _fixture.CurrentUser.Role = role;
        var clientId = Guid.NewGuid().ToString();

        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 3 value 7" }));

        var result = await service.SyncAsync(AudioBytes(), "clip.webm", Command(trialId, clientId));

        result.Status.Should().Be(VoiceRecordingStatus.Written);
    }

    [Fact]
    public async Task SyncAsync_UnassignedFieldWorker_Replay_ThrowsForbidden()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        using (var scope = _fixture.CreateScope())
        {
            await CreateService(scope, new FakeTranscriptionService(
                new TranscriptionResult { Text = "plot 101 subsample 3 value 7" }))
                .SyncAsync(AudioBytes(), "clip.webm", Command(trialId, clientId));
        }

        _fixture.CurrentUser.UserId = await PersistFieldWorkerAsync();
        _fixture.CurrentUser.Role = UserRole.FieldWorker;

        using var replayScope = _fixture.CreateScope();
        var replayFake = new FakeTranscriptionService();
        var service = CreateService(replayScope, replayFake);

        var act = () => service.SyncAsync(AudioBytes(seed: 42), "clip.webm", Command(trialId, clientId));
        await act.Should().ThrowAsync<ForbiddenAccessException>();
        replayFake.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task CorrectAsync_UnassignedFieldWorker_ThrowsForbidden()
    {
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        using (var scope = _fixture.CreateScope())
        {
            var flagged = await CreateService(scope, new FakeTranscriptionService(
                new TranscriptionResult { Text = "plot 101" }))
                .SyncAsync(AudioBytes(), "clip.webm",
                    Command(trialId, clientId, plot: null, subsample: null, value: null));
            flagged.Status.Should().Be(VoiceRecordingStatus.Flagged);
        }

        _fixture.CurrentUser.UserId = await PersistFieldWorkerAsync();
        _fixture.CurrentUser.Role = UserRole.FieldWorker;

        using var correctScope = _fixture.CreateScope();
        var service = CreateService(correctScope, new FakeTranscriptionService());

        var act = () => service.CorrectAsync(clientId, plot: 101, subsample: 3, value: 7, traitOrdinal: 1);
        await act.Should().ThrowAsync<ForbiddenAccessException>();

        await AssertStoredAsync(clientId, row =>
        {
            row.Status.Should().Be(VoiceRecordingStatus.Flagged);
            row.ConfirmedPlot.Should().BeNull("an unauthorized correction writes nothing");
        });
    }

    [Fact]
    public async Task CorrectAsync_AssignedFieldWorker_Succeeds()
    {
        var trialId = await CreateTrialAsync();
        var fieldWorkerId = await PersistFieldWorkerAsync();
        await AssignTrialAsync(trialId, fieldWorkerId);
        _fixture.CurrentUser.UserId = fieldWorkerId;
        _fixture.CurrentUser.Role = UserRole.FieldWorker;

        var clientId = Guid.NewGuid().ToString();
        using (var scope = _fixture.CreateScope())
        {
            var flagged = await CreateService(scope, new FakeTranscriptionService(
                new TranscriptionResult { Text = "plot 101" }))
                .SyncAsync(AudioBytes(), "clip.webm",
                    Command(trialId, clientId, plot: null, subsample: null, value: null));
            flagged.Status.Should().Be(VoiceRecordingStatus.Flagged);
        }

        SyncVoiceRecordingResult result;
        using (var scope = _fixture.CreateScope())
        {
            result = await CreateService(scope, new FakeTranscriptionService())
                .CorrectAsync(clientId, plot: 101, subsample: 3, value: 7, traitOrdinal: 1);
        }

        result.Status.Should().Be(VoiceRecordingStatus.Written);
        result.WrittenCells.Should().ContainSingle();
        OutputCopies(trialId).Should().ContainSingle();
    }

    private async Task<Guid> CreateTrialAsync(
        IReadOnlyDictionary<(int plot, int subsample, int traitOrdinal), int>? prefilledCells = null,
        Func<Guid, string>? workbookRelativePathFactory = null)
    {
        var trialId = Guid.NewGuid();
        var relativePath = workbookRelativePathFactory?.Invoke(trialId)
            ?? await SaveRealWorkbookAsync(trialId, prefilledCells);

        using (var scope = _fixture.CreateScope())
        {
            var trial = new Trial
            {
                Id = trialId,
                ExternalId = $"voice-test-{trialId:N}",
                FileName = "voice test.xlsx",
                Status = TrialStatus.Created,
                SourceWorkbookPath = relativePath,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };

            var trialRepo = scope.ServiceProvider.GetRequiredService<ITrialRepository>();
            await trialRepo.CreateAsync(trial);
        }

        _createdTrialIds.Add(trialId);
        return trialId;
    }

    private async Task<Guid> PersistFieldWorkerAsync()
    {
        var userId = Guid.NewGuid();
        using var scope = _fixture.CreateScope();
        var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();

        await userRepo.AddAsync(new User
        {
            Id = userId,
            Name = $"Field Worker {userId:N}",
            Email = $"fieldworker.{userId:N}@plottr02.local",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("TestPass123!"),
            Role = UserRole.FieldWorker,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        return userId;
    }

    private async Task AssignTrialAsync(Guid trialId, Guid userId)
    {
        using var scope = _fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITrialRepository>();
        await repo.SetAssignmentsAsync(trialId, [userId], userId);
    }

    private async Task<string> SaveRealWorkbookAsync(
        Guid trialId,
        IReadOnlyDictionary<(int plot, int subsample, int traitOrdinal), int>? prefilledCells)
    {
        using var scope = _fixture.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ITrialWorkbookStore>();
        using var stream = CreateRatingWorkbookStream(prefilledCells);
        return await store.SaveAsync(trialId, "voice test.xlsx", stream);
    }

    private static MemoryStream CreateRatingWorkbookStream(
        IReadOnlyDictionary<(int plot, int subsample, int traitOrdinal), int>? prefilledCells = null)
    {
        var scratchDir = Path.Combine(Path.GetTempPath(), "PlottR02Tests", Guid.NewGuid().ToString());
        try
        {
            var path = RatingWorkbookFactory.Create(
                Path.Combine(scratchDir, "voice test.xlsx"), prefilledCells);
            return new MemoryStream(File.ReadAllBytes(path));
        }
        finally
        {
            try
            {
                if (Directory.Exists(scratchDir))
                    Directory.Delete(scratchDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    private RecordingSyncService CreateService(IServiceScope scope, ITranscriptionService transcription)
        => new(
            scope.ServiceProvider.GetRequiredService<IVoiceRecordingRepository>(),
            scope.ServiceProvider.GetRequiredService<IVoiceBlobStore>(),
            transcription,
            scope.ServiceProvider.GetRequiredService<ITrialRepository>(),
            scope.ServiceProvider.GetRequiredService<IRatingRepository>(),
            scope.ServiceProvider.GetRequiredService<ITrialWorkbookStore>(),
            scope.ServiceProvider.GetRequiredService<ITrialWriteGate>(),
            _fixture.CurrentUser,
            NullLogger<RecordingSyncService>.Instance);

    private static SyncVoiceRecordingCommand Command(
        Guid trialId,
        string clientId,
        int? plot = 101,
        int? subsample = 3,
        int traitOrdinal = 1,
        int? value = 7,
        int? defaultPlot = null,
        int? defaultSubsample = null)
        => new()
        {
            ClientRecordingId = Guid.Parse(clientId),
            TrialId = trialId,
            Plot = plot,
            Subsample = subsample,
            TraitOrdinal = traitOrdinal,
            Value = value,
            DefaultPlot = defaultPlot,
            DefaultSubsample = defaultSubsample,
        };

    private static MemoryStream AudioBytes(int seed = 0)
        => new(Enumerable.Range(0, 512).Select(i => (byte)((i * 31 + seed * 7) % 256)).ToArray());

    private static string[] OutputCopies(Guid trialId)
    {
        var dir = Path.Combine(TrialWorkbookStore.ResolveServiceDirectory(), "Working", trialId.ToString());
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*_out_*.xlsx") : [];
    }

    private static string[] BlobFiles(Guid trialId)
    {
        var dir = Path.Combine(TrialWorkbookStore.ResolveServiceDirectory(), "Recordings", trialId.ToString());
        return Directory.Exists(dir) ? Directory.GetFiles(dir) : [];
    }

    private async Task AssertStoredAsync(string clientId, Action<VoiceRecording> assert)
    {
        using var scope = _fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IVoiceRecordingRepository>();
        var row = await repo.GetByClientRecordingIdAsync(clientId);
        row.Should().NotBeNull();
        assert(row!);
    }

    private async Task<string?> GetTrialStatusAsync(Guid trialId)
    {
        using var scope = _fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITrialRepository>();
        return (await repo.GetByIdAsync(trialId))?.Status;
    }

    private async Task AssertNotStoredAsync(string clientId)
    {
        using var scope = _fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IVoiceRecordingRepository>();
        (await repo.GetByClientRecordingIdAsync(clientId)).Should().BeNull(
            "an unauthorized sync must not persist an audit row");
    }

    private static string NormalizeAddress(string address) => address.Replace("$", "");

    private static int? ReadCell(string workbookPath, int plot, int subsample, int traitOrdinal)
    {
        using var wb = new XLWorkbook(workbookPath);
        var cell = wb.Worksheet(RatingWorkbookFactory.SheetName)
            .Cell(RatingWorkbookFactory.DataRowFor(plot, subsample), RatingWorkbookFactory.TraitColumnFor(traitOrdinal));
        return cell.IsEmpty() ? null : (int?)cell.GetDouble();
    }

    private sealed class RaceLosingVoiceRecordingRepository : IVoiceRecordingRepository
    {
        private readonly IVoiceRecordingRepository _inner;
        private readonly VoiceRecording _winner;
        private int _lookups;

        public RaceLosingVoiceRecordingRepository(IVoiceRecordingRepository inner, VoiceRecording winner)
        {
            _inner = inner;
            _winner = winner;
        }

        public Task<VoiceRecording> CreateAsync(VoiceRecording recording, CancellationToken ct = default)
            => throw new ClientRecordingIdConflictException(
                $"A voice recording with client recording id '{recording.ClientRecordingId}' already exists.");

        public Task<VoiceRecording?> GetByClientRecordingIdAsync(
            string clientRecordingId, CancellationToken ct = default)
            => Task.FromResult(_lookups++ == 0 ? null : _winner);

        public Task<VoiceRecording?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => _inner.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<VoiceRecording>> GetAllAsync(
            int? skip = null, int? take = null, CancellationToken ct = default)
            => _inner.GetAllAsync(skip, take, ct);

        public Task<bool> ExistsAsync(Guid id, CancellationToken ct = default)
            => _inner.ExistsAsync(id, ct);

        public Task UpdateAsync(VoiceRecording recording, CancellationToken ct = default)
            => _inner.UpdateAsync(recording, ct);
    }

    private sealed class ThrowingTranscriptionService : ITranscriptionService
    {
        private readonly string _message;

        public ThrowingTranscriptionService(string message = "Speech-to-text sidecar is unavailable.")
            => _message = message;

        public Task<TranscriptionResult> TranscribeAsync(Stream audio, string fileName, CancellationToken ct = default)
            => throw new HttpRequestException(_message);
    }
}