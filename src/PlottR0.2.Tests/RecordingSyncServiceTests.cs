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

/// <summary>
/// Integration tests for the V2E "send now" sync orchestration
/// (<see cref="RecordingSyncService"/>) against a real PostgreSQL (Testcontainers) and a
/// REAL rating-layout workbook saved through <see cref="ITrialWorkbookStore"/>, so the
/// policy-write step exercises the exact repo-root Service/ paths production uses.
/// Mirrors <see cref="VoiceRecordingRepositoryContractTests"/> for the trial/Db seeding
/// pattern and cleans up the Working and Recordings directories it creates.
/// </summary>
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
        // Clean up the real Service/Working/<id>/ and Service/Recordings/<id>/ trees
        // created by these tests, then wipe the database (mirrors TrialServiceTests).
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
                // Best-effort cleanup.
            }
        }

        await _fixture.ResetDatabaseAsync();
    }

    // ── Happy path: transcript-confident write ───────────────────────────────

    [Fact]
    public async Task SyncAsync_CompleteTranscript_WritesAndMarksWritten()
    {
        // Arrange
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 3 value 7", Language = "en", Duration = 1.2 });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        // Act
        var result = await service.SyncAsync(AudioBytes(), "clip.webm", Command(trialId, clientId));

        // Assert — result
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

        // Assert — side effects: one exported workbook copy, one stored blob
        OutputCopies(trialId).Should().ContainSingle();
        BlobFiles(trialId).Should().ContainSingle();

        // Assert — the audit row carries the terminal state
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

    // ── Transcript-only sync: the PWA sent no confirmed fields ──────────────

    [Fact]
    public async Task SyncAsync_TranscriptOnly_CompleteParse_WritesAndAutoAccepts()
    {
        // Arrange — command confirms NOTHING; the transcript is the sole authority
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 3 value 7" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        // Act
        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null));

        // Assert — the parsed target is written and accepted without review
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

        // The row persists no confirmed target — the transcript carried everything
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
        // Arrange — the transcript states plot and value but no subsample
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 value 7" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        // Act
        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null));

        // Assert — no addressable subset, so nothing is written and the sync flags
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
        // Arrange — the sidecar heard no understandable speech
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(new TranscriptionResult { Text = "" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        // Act
        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null));

        // Assert — nothing resolved: flagged with a zero-confidence parse and no write
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
        // Arrange — the transcript claims value 12; parsing is format-only so it survives
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 3 value 12" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        // Act
        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null));

        // Assert — gate B rejects the out-of-range value AFTER transcription; never writes
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
        // Arrange — the transcript states THREE values after the plural keyword; no field confirmed
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 1 values 3, 6, 1" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        // Act
        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null));

        // Assert — all three values are written into ONE row's trait cells (C22/D22/E22 for
        // plot 101 subsample 1 at traitOrdinal 1), so WrittenCells holds three cells
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

        // The stored single-value column carries the FIRST parsed value
        await AssertStoredAsync(clientId, row =>
        {
            row.Status.Should().Be(VoiceRecordingStatus.Written);
            row.ParsedPlot.Should().Be(101);
            row.ParsedSubsample.Should().Be(1);
            row.ParsedValue.Should().Be(3);
        });
    }

    // ── Category-block append: the sync fills the next free cells of the Part Rated
    //    block (row-8 metadata), and successive syncs accumulate leaf by leaf ────

    [Fact]
    public async Task SyncAsync_TranscriptOnly_ScanForFreeCells_WritesIntoPartRatedBlock()
    {
        // Arrange — transcript-only multi-value sync against a workbook carrying the row-8
        // Part Rated metadata; the sync writes in scan-for-free-cells mode, so the values
        // fill the first category block of row 22 (LEAF1 = C22:D22:E22).
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 1 values 3, 6, 1" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        // Act
        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null));

        // Assert — the values land in LEAF1 (C22/D22/E22) and the sync commits as Written
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
        // Arrange — TWO transcript-only syncs with DIFFERENT client ids against ONE trial:
        // the second chained sync reads the FIRST output copy as its input, so its values
        // append into the NEXT free category block (LEAF2 = F22:G22:H22) — LEAF1 stays
        // untouched and no recording is dropped or marked skipped.
        var trialId = await CreateTrialAsync();
        var clientId1 = Guid.NewGuid().ToString();
        var clientId2 = Guid.NewGuid().ToString();
        // One scripted transcription per sync (the fake throws on any extra call).
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

        // The second sync MUST use a DIFFERENT client id — the idempotency gate would
        // otherwise answer a Replay without any workbook work.
        SyncVoiceRecordingResult second;
        using (var scope = _fixture.CreateScope())
        {
            second = await CreateService(scope, fake).SyncAsync(
                AudioBytes(), "clip.webm", Command(trialId, clientId2, plot: null, subsample: null, value: null));
        }

        // Assert — LEAF2 received the second recording's values; two copies were exported
        second.Status.Should().Be(VoiceRecordingStatus.Written);
        second.Replayed.Should().BeFalse();
        second.NeedsReview.Should().BeFalse();
        second.WrittenCells.Select(c => NormalizeAddress(c.CellAddress)).Should().Equal("F22", "G22", "H22");
        second.WrittenCells.Select(c => c.Value).Should().Equal(4, 2, 6);
        second.SkippedPresentCells.Should().BeEmpty();
        OutputCopies(trialId).Should().HaveCount(2);
        BlobFiles(trialId).Should().HaveCount(2);

        // The newest copy physically holds BOTH leaves: LEAF1 (C/E) and LEAF2 (F/H)
        var newest = OutputCopies(trialId).OrderByDescending(File.GetLastWriteTimeUtc).First();
        ReadCell(newest, plot: 101, subsample: 1, traitOrdinal: 1).Should().Be(3);
        ReadCell(newest, plot: 101, subsample: 1, traitOrdinal: 3).Should().Be(1);
        ReadCell(newest, plot: 101, subsample: 1, traitOrdinal: 4).Should().Be(4);
        ReadCell(newest, plot: 101, subsample: 1, traitOrdinal: 6).Should().Be(6);

        await AssertStoredAsync(clientId2, row => row.Status.Should().Be(VoiceRecordingStatus.Written));
    }

    [Fact]
    public async Task SyncAsync_MultiValue_OneOutOfRangeValue_FlagsWithoutWriting()
    {
        // Arrange — the transcript's value list contains an out-of-range entry (12)
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 1 values 3, 12" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        // Act
        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null));

        // Assert — gate B rejects the WHOLE list when ANY value is out of range: flagged, nothing written
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
        // Arrange — the voice client omitted traitOrdinal (binds to 0); the server treats
        // that as the default 1, so the append scan starts at column C instead of flagging.
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 1 value 5" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        // Act — traitOrdinal 0 is normalized to 1 and the write proceeds
        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, traitOrdinal: 0, value: null));

        // Assert — the value writes at trait 1 (column C) and the result echoes the DEFAULTED
        // ordinal; nothing is flagged
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
        // Arrange — empty region row 22; the voice client omits traitOrdinal (binds to 0),
        // so the server defaults it to 1 and the append scan starts at column C.
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 1 values 3, 6, 1" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        // Act — traitOrdinal 0 is treated as 1
        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, traitOrdinal: 0, value: null));

        // Assert — Written with the three values in C22/D22/E22 and the defaulted ordinal echoed
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
        // Arrange — traitOrdinal 9 (column K): of the three spoken values only 3 has a
        // column (K22); 6 and 1 would land beyond trait 9 (columns L/M) — the sync must
        // flag the recording so the human review sees the lost ratings.
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 1 values 3, 6, 1" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        // Act
        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, traitOrdinal: 9, value: null));

        // Assert — flagged (values were dropped), yet the one legal write is reported and exported
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

    // ── Normalization: non-positive confirmed fields mean “not confirmed” ────

    [Fact]
    public async Task SyncAsync_PlotZero_TreatedUnconfirmed_ResolvesFromTranscript()
    {
        // Arrange — the legacy contract sent plot 0 instead of omitting it
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 3 value 7" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        // Act — plot 0 is normalized to “not confirmed”
        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: 0, subsample: null, value: null));

        // Assert — the transcript fills the unconfirmed plot
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
        // Arrange — the legacy PWA sent 0 for every field; the sidecar hears the real target.
        // design.md §9 says “value 7 confirmed”, but §2.2/§3/§8 are precise: value 0 is a
        // legal rating and stays confirmed-0. This test pins the PRECISE rules.
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 3 value 7" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        // Act — plot 0, subsample 0, value 0 all arrive from the old PWA contract
        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: 0, subsample: 0, value: 0));

        // Assert — plot/subsample resolve from the transcript; value stays confirmed 0
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
        // Arrange — the old contract sends 0s and the sidecar hears nothing
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(new TranscriptionResult { Text = "" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        // Act
        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: 0, subsample: 0, value: 0));

        // Assert — value 0 is legal, but no plot/subsample resolves: nothing is addressable
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

    // ── Partial confirmation: missing fields fill from the transcript ───────

    [Fact]
    public async Task SyncAsync_PartialConfirmedPlotOnly_CleanRest_WritesWithoutReview()
    {
        // Arrange — the caller confirms only the plot; the transcript fills subsample+value
        // cleanly (no disagreement), so the write needs no review.
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 3 value 7" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        // Act — only plot is confirmed
        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: 101, subsample: null, value: null));

        // Assert
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

    [Fact]
    public async Task SyncAsync_PartialConfirmedMismatch_WritesConfirmedButFlagsReview()
    {
        // Arrange — the caller confirms only plot 101; the transcript claims a different plot
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 102 subsample 3 value 7" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        // Act — only plot is confirmed
        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: 101, subsample: null, value: null));

        // Assert — the CONFIRMED plot wins per field, and the disagreement flags review
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

    // ── Transcription outage with nothing confirmed → nothing resolvable ────

    [Fact]
    public async Task SyncAsync_TranscriptionFailure_NoConfirmedFields_FlagsWithoutWriting()
    {
        // Arrange — sidecar down AND no confirmed fields on the command
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, new ThrowingTranscriptionService());

        // Act — transcript-only command, empty transcript (gate B flags)
        var result = await service.SyncAsync(
            AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null));

        // Assert — nothing resolves, so no workbook work is attempted
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

    // ── Already-present cell: append mode moves the write to the next free cell ─

    [Fact]
    public async Task SyncAsync_CellAlreadyPresent_AppendsIntoNextFreeCell()
    {
        // Arrange — subsample 3's trait-1 cell already holds 5; the sync offers 7. In the
        // category-block append mode (scanForFreeCells) the 7 lands in the row's NEXT free
        // cell instead of being reported skipped — a recording is never lost to a full cell.
        var trialId = await CreateTrialAsync(prefilledCells: new Dictionary<(int, int, int), int>
        {
            [(101, 3, 1)] = 5,
        });
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 3 value 7" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        // Act
        var result = await service.SyncAsync(AudioBytes(), "clip.webm", Command(trialId, clientId));

        // Assert — the 7 appends to trait 2 of the same row; nothing is skipped or dropped
        result.Status.Should().Be(VoiceRecordingStatus.Written);
        result.NeedsReview.Should().BeFalse("an appended value is as complete as a direct write");
        result.WrittenCells.Should().ContainSingle();
        result.WrittenCells[0].Subsample.Should().Be(3);
        result.WrittenCells[0].TraitOrdinal.Should().Be(2);
        result.WrittenCells[0].Value.Should().Be(7);
        result.SkippedPresentCells.Should().BeEmpty("append mode never reports occupies as skipped");
        result.MissingSubsampleIndices.Should().BeEmpty();

        // The original cell keeps its 5; the appended 7 was exported as a fresh output copy
        var copy = OutputCopies(trialId).Should().ContainSingle().Subject;
        ReadCell(copy, 101, 3, 1).Should().Be(5);
        ReadCell(copy, 101, 3, 2).Should().Be(7);

        await AssertStoredAsync(clientId, row => row.Status.Should().Be(VoiceRecordingStatus.Written));
    }

    // ── Idempotency: same ClientRecordingId short-circuits as a Replay ──────

    [Fact]
    public async Task SyncAsync_SameClientRecordingId_ReplaysWithoutTranscriptionOrWrite()
    {
        // Arrange
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

        // Act — second sync, same idempotency key, DIFFERENT audio + a fake with an empty
        // queue: any accidental transcription call throws and fails the test.
        var replayFake = new FakeTranscriptionService();
        SyncVoiceRecordingResult replay;
        using (var scope = _fixture.CreateScope())
        {
            var service = CreateService(scope, replayFake);
            replay = await service.SyncAsync(AudioBytes(seed: 42), "clip.webm", Command(trialId, clientId));
        }

        // Assert — replay semantics: no transcription, no second blob, no second copy
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

        // The stored row must KEEP the first submission's terminal status — a replay answers the
        // caller but never clobbers the persisted row (reviewer finding 7).
        await AssertStoredAsync(clientId, row => row.Status.Should().Be(
            VoiceRecordingStatus.Written,
            "a replay must not overwrite the first submission's terminal status"));
    }

    // ── Value gate: out of range → stored but flagged, never transcribed ────

    [Fact]
    public async Task SyncAsync_OutOfRangeValue_StoresBlobButFlagsWithoutTranscription()
    {
        // Arrange
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(); // empty queue: any call fails the test
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        // Act — value 11 is outside the legal 0..10 range
        var result = await service.SyncAsync(AudioBytes(), "clip.webm", Command(trialId, clientId, value: 11));

        // Assert — the audio is kept for review, but nothing else happens
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

    // ── Unknown plot → Flagged, no write ────────────────────────────────────

    [Fact]
    public async Task SyncAsync_UnknownPlot_FlagsForReviewWithoutWriting()
    {
        // Arrange — default layout only has plots 101..104
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 999 subsample 3 value 7" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        // Act
        var result = await service.SyncAsync(AudioBytes(), "clip.webm", Command(trialId, clientId, plot: 999));

        // Assert
        result.Status.Should().Be(VoiceRecordingStatus.Flagged);
        result.NeedsReview.Should().BeTrue();
        result.WrittenCells.Should().BeEmpty();
        result.SkippedPresentCells.Should().BeEmpty();
        result.MissingSubsampleIndices.Should().Equal(3);
        OutputCopies(trialId).Should().BeEmpty();

        await AssertStoredAsync(clientId, row => row.Status.Should().Be(VoiceRecordingStatus.Flagged));
    }

    // ── Transcript / confirmed mismatch → write proceeds, review required ───

    [Fact]
    public async Task SyncAsync_MismatchedTranscript_WritesConfirmedButFlagsReview()
    {
        // Arrange — the sidecar hears a DIFFERENT target than the PWA confirmed
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 102 subsample 2 value 8" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        // Act — confirmed command says plot 101, subsample 3, value 7
        var result = await service.SyncAsync(AudioBytes(), "clip.webm", Command(trialId, clientId));

        // Assert — the CONFIRMED fields are written (contract truth), the disagreement flags review
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

    // ── Transcription outage → the write continues from the RESOLVED fields ──

    [Fact]
    public async Task SyncAsync_TranscriptionFailure_WritesConfirmedSingleCellAndFlagsReview()
    {
        // Arrange — the sidecar is down; no transcript is available
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, new ThrowingTranscriptionService());

        // Act — the confirmed target (101/3/7) resolves without the transcript
        var result = await service.SyncAsync(AudioBytes(), "clip.webm", Command(trialId, clientId));

        // Assert — the write still proceeds from the confirmed fields, but only the SINGLE
        // confirmed subsample (design.md §3): the sync never offers the whole 1..4 set.
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

    // ── Missing trial → hard failure ────────────────────────────────────────

    [Fact]
    public async Task SyncAsync_UnknownTrial_ThrowsKeyNotFound()
    {
        // Arrange — no Trial row exists for this id
        var ghostTrialId = Guid.NewGuid();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService();
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        // Act & Assert
        var act = () => service.SyncAsync(AudioBytes(), "clip.webm", Command(ghostTrialId, clientId));
        await act.Should().ThrowAsync<KeyNotFoundException>();

        fake.CallCount.Should().Be(0);
        BlobFiles(ghostTrialId).Should().BeEmpty("no blob must be stored for an unknown trial");
    }

    // ── Missing workbook file → flagged, never crashes ──────────────────────

    [Fact]
    public async Task SyncAsync_MissingWorkbook_FlagsWithoutCrashing()
    {
        // Arrange — a Trial row whose SourceWorkbookPath points at a file that does not exist
        var trialId = await CreateTrialAsync(
            workbookRelativePathFactory: id => Path.Combine("Working", id.ToString(), "DoesNotExist.xlsx"));
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 3 value 7" });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        // Act — must not throw: the sync degrades to Flagged + review
        var result = await service.SyncAsync(AudioBytes(), "clip.webm", Command(trialId, clientId));

        // Assert
        result.Status.Should().Be(VoiceRecordingStatus.Flagged);
        result.NeedsReview.Should().BeTrue();
        result.WrittenCells.Should().BeEmpty();
        OutputCopies(trialId).Should().BeEmpty();
        BlobFiles(trialId).Should().ContainSingle("the audio is still preserved for review");

        await AssertStoredAsync(clientId, row => row.Status.Should().Be(VoiceRecordingStatus.Flagged));
    }

    // ── Value 0 (legal, unrated) is real data: written to an EMPTY cell ─────

    [Fact]
    public async Task SyncAsync_ValueZero_IsWrittenToEmptyCellNotSkipped()
    {
        // Arrange — all cells empty; value 0 is a legal rating (design.md §9), not "absent"
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 3 value 0", Duration = 1.2 });
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, fake);

        // Act — confirmed value 0 passes the value gate (0 is legal) and C24 is EMPTY
        var result = await service.SyncAsync(AudioBytes(), "clip.webm", Command(trialId, clientId, value: 0));

        // Assert — the cell is written and the exported copy physically holds the literal zero
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

    // ── Workbook accumulation: a later sync chains the earlier output copy ──

    [Fact]
    public async Task SyncAsync_SecondSyncSameTrial_ChainsFirstOutputCopy()
    {
        // Arrange — two DIFFERENT recordings for the SAME trial (fresh workbook, empty cells)
        var trialId = await CreateTrialAsync();
        var clientId1 = Guid.NewGuid().ToString();
        var clientId2 = Guid.NewGuid().ToString();

        // Sync 1: writes subsample 3 → value 7 → first output copy
        var fake1 = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 3 value 7", Duration = 1.2 });
        using (var scope = _fixture.CreateScope())
        {
            var first = await CreateService(scope, fake1).SyncAsync(
                AudioBytes(), "clip.webm", Command(trialId, clientId1, subsample: 3, value: 7));
            first.Status.Should().Be(VoiceRecordingStatus.Written);
        }

        // Sync 2: writes subsample 1 → value 5. Its read input is the FIRST sync's output copy,
        // so the exported copy must carry BOTH writes (accumulation, not a pristine reset).
        var fake2 = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 1 value 5", Duration = 1.1 });
        using (var scope = _fixture.CreateScope())
        {
            var second = await CreateService(scope, fake2).SyncAsync(
                AudioBytes(seed: 42), "clip.webm", Command(trialId, clientId2, subsample: 1, value: 5));
            second.Status.Should().Be(VoiceRecordingStatus.Written);
            second.WrittenCells.Should().ContainSingle().Which.Subsample.Should().Be(1);
        }

        // Every copy is chained from a file that already carries the first write (subsample 3 = 7),
        // and at least one copy accumulates the second write (subsample 1 = 5) on top of it.
        var copies = OutputCopies(trialId);
        copies.Should().NotBeEmpty();
        copies.Should().OnlyContain(
            path => ReadCell(path, 101, 3, 1) == 7,
            "each successive input must be the previous sync's output copy, not the pristine workbook");
        copies.Should().Contain(
            path => ReadCell(path, 101, 1, 1) == 5,
            "a later copy must accumulate the second sync's write on top of the first");
    }

    // ── Correction: reviewer confirms a flagged recording ────────────────────

    [Fact]
    public async Task CorrectAsync_FlaggedRow_AppliesCorrectionAndWrites()
    {
        // Arrange — sync a transcript-only PARTIAL recording so gate B flags it (no value);
        // the stored row is therefore Flagged and the workbook holds nothing for it.
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

        // Act — the reviewer confirms the target on the SAME row. The empty-queue fake makes
        // any correction-time transcription call throw, proving no re-transcription occurs.
        SyncVoiceRecordingResult result;
        using (var scope = _fixture.CreateScope())
        {
            result = await CreateService(scope, new FakeTranscriptionService())
                .CorrectAsync(clientId, plot: 101, subsample: 3, value: 7, traitOrdinal: 1);
        }

        // Assert — the correction writes the single confirmed target
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
        // Arrange — the first sync fully succeeded (Written is terminal)
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        var fake = new FakeTranscriptionService(
            new TranscriptionResult { Text = "plot 101 subsample 3 value 7" });
        using (var scope = _fixture.CreateScope())
        {
            var written = await CreateService(scope, fake).SyncAsync(AudioBytes(), "clip.webm", Command(trialId, clientId));
            written.Status.Should().Be(VoiceRecordingStatus.Written);
        }

        // Act & Assert — correcting a terminal row is a conflict; no transcription may occur
        using var correctionScope = _fixture.CreateScope();
        var correctionService = CreateService(correctionScope, new FakeTranscriptionService());
        var act = () => correctionService.CorrectAsync(clientId, plot: 101, subsample: 3, value: 8, traitOrdinal: 1);
        await act.Should().ThrowAsync<InvalidOperationException>();
        OutputCopies(trialId).Should().ContainSingle("a rejected correction creates no new copy");
    }

    [Fact]
    public async Task CorrectAsync_UnknownClientRecordingId_ThrowsKeyNotFound()
    {
        // Arrange — no row exists for this PWA idempotency key
        using var scope = _fixture.CreateScope();
        var service = CreateService(scope, new FakeTranscriptionService());

        // Act & Assert
        var act = () => service.CorrectAsync(Guid.NewGuid().ToString(), plot: 101, subsample: 3, value: 7, traitOrdinal: 1);
        (await act.Should().ThrowAsync<KeyNotFoundException>())
            .Which.Message.Should().Contain("was not found");
    }

    [Fact]
    public async Task CorrectAsync_OutOfRangeValue_FlagsWithoutWriting()
    {
        // Arrange — flag a row first (transcript-only partial → gate B)
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        using (var scope = _fixture.CreateScope())
        {
            var flagged = await CreateService(scope, new FakeTranscriptionService(
                new TranscriptionResult { Text = "plot 101" })).SyncAsync(
                AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null));
            flagged.Status.Should().Be(VoiceRecordingStatus.Flagged);
        }

        // Act — a value above 10 never reaches the workbook, mirroring the sync's gate A
        SyncVoiceRecordingResult result;
        using (var scope = _fixture.CreateScope())
        {
            result = await CreateService(scope, new FakeTranscriptionService())
                .CorrectAsync(clientId, plot: 101, subsample: 3, value: 11, traitOrdinal: 1);
        }

        // Assert — the row keeps the reviewer's target but stays flagged with no write
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
        // Arrange — the trait-1 target cell already holds 5; the row is flagged (transcript-only partial)
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

        // Act — the correction re-runs the same category-block append scan the voice sync
        // uses: the single confirmed value lands in the row's FIRST FREE cell (D24, trait 2)
        // instead of clobbering the occupied C24
        SyncVoiceRecordingResult result;
        using (var scope = _fixture.CreateScope())
        {
            result = await CreateService(scope, new FakeTranscriptionService())
                .CorrectAsync(clientId, plot: 101, subsample: 3, value: 7, traitOrdinal: 1);
        }

        // Assert — the append WROTE D24 (trait 2); C24 keeps its 5 and nothing is skipped
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
        // Arrange — subsample 1's LEAF1 (C:E) AND LEAF2 (F:H) blocks are fully occupied;
        // the row is flagged (transcript-only partial). The reviewer then confirms a correction
        // that omits traitOrdinal (binds to 0) — the server defaults it to 1, and the append
        // scan walks the occupied blocks until it finds the first free cell: I22 (LEAF3).
        var trialId = await CreateTrialAsync(prefilledCells: new Dictionary<(int, int, int), int>
        {
            [(101, 1, 1)] = 1,
            [(101, 1, 2)] = 1,
            [(101, 1, 3)] = 1, // LEAF1 block C22:E22
            [(101, 1, 4)] = 1,
            [(101, 1, 5)] = 1,
            [(101, 1, 6)] = 1, // LEAF2 block F22:H22
        });
        var clientId = Guid.NewGuid().ToString();
        using (var scope = _fixture.CreateScope())
        {
            var flagged = await CreateService(scope, new FakeTranscriptionService(
                new TranscriptionResult { Text = "plot 101" })).SyncAsync(
                AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null));
            flagged.Status.Should().Be(VoiceRecordingStatus.Flagged);
        }

        // Act — correct with traitOrdinal 0 (defaulted to 1), value 5 for subsample 1
        SyncVoiceRecordingResult result;
        using (var scope = _fixture.CreateScope())
        {
            result = await CreateService(scope, new FakeTranscriptionService())
                .CorrectAsync(clientId, plot: 101, subsample: 1, value: 5, traitOrdinal: 0);
        }

        // Assert — the single confirmed value landed in the row's first free cell (I22, trait 7)
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
        // Arrange — flag a row first (transcript-only partial)
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        using (var scope = _fixture.CreateScope())
        {
            var flagged = await CreateService(scope, new FakeTranscriptionService(
                new TranscriptionResult { Text = "plot 101" })).SyncAsync(
                AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null));
            flagged.Status.Should().Be(VoiceRecordingStatus.Flagged);
        }

        // Act — an explicit traitOrdinal ABOVE 9 cannot address any cell: the correction is
        // flagged without writing even though 0 is now a legal "default to 1" signal
        SyncVoiceRecordingResult result;
        using (var scope = _fixture.CreateScope())
        {
            result = await CreateService(scope, new FakeTranscriptionService())
                .CorrectAsync(clientId, plot: 101, subsample: 3, value: 7, traitOrdinal: 10);
        }

        // Assert — flagged, nothing written, nothing exported
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
        // Arrange — flag a row via a transcript-only PARTIAL sync (gate B flags it, nothing written).
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        using (var scope = _fixture.CreateScope())
        {
            var flagged = await CreateService(scope, new FakeTranscriptionService(
                new TranscriptionResult { Text = "plot 101" })).SyncAsync(
                AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null));
            flagged.Status.Should().Be(VoiceRecordingStatus.Flagged);
        }

        // Act — the reviewer records against trait 2; the PWA's ordinal must be forwarded
        // VERBATIM to the workbook column and echoed on the result, never substituted with 1.
        SyncVoiceRecordingResult result;
        using (var scope = _fixture.CreateScope())
        {
            result = await CreateService(scope, new FakeTranscriptionService())
                .CorrectAsync(clientId, plot: 101, subsample: 3, value: 7, traitOrdinal: 2);
        }

        // Assert — the cell landed in the FORWARDED trait column (D/trait 2), not the default (C/trait 1)
        result.Status.Should().Be(VoiceRecordingStatus.Written);
        result.TraitOrdinal.Should().Be(2, "the PWA's forwarded trait ordinal is echoed verbatim");
        var copy = OutputCopies(trialId).Should().ContainSingle().Which;
        ReadCell(copy, plot: 101, subsample: 3, traitOrdinal: 2).Should().Be(7,
            "the correction must write the cell in the FORWARDED trait column");
        ReadCell(copy, plot: 101, subsample: 3, traitOrdinal: 1).Should().BeNull(
            "trait 1 must stay empty — the default column receives nothing");
    }

    [Fact]
    public async Task CorrectAsync_ZeroPlot_FlagsWithoutWriting()
    {
        // Arrange — flag a row first (transcript-only partial → gate B)
        var trialId = await CreateTrialAsync();
        var clientId = Guid.NewGuid().ToString();
        using (var scope = _fixture.CreateScope())
        {
            var flagged = await CreateService(scope, new FakeTranscriptionService(
                new TranscriptionResult { Text = "plot 101" })).SyncAsync(
                AudioBytes(), "clip.webm", Command(trialId, clientId, plot: null, subsample: null, value: null));
            flagged.Status.Should().Be(VoiceRecordingStatus.Flagged);
        }

        // Act — plot 0 is discarded by the correction's normalization guard (mirrors the sync
        // path): nothing is addressable, so the write attempt must be skipped.
        SyncVoiceRecordingResult result;
        using (var scope = _fixture.CreateScope())
        {
            result = await CreateService(scope, new FakeTranscriptionService())
                .CorrectAsync(clientId, plot: 0, subsample: 3, value: 7, traitOrdinal: 1);
        }

// Assert — flagged with no write and no workbook copy; the guard's effect is visible on the
// ROW (plot 0 is discarded, never persisted), while the result's resolved echo (confirmed ??
// parsed) still surfaces the stored parse of 101.
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

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a Trial row. By default a real rating-layout workbook is saved through
    /// <see cref="ITrialWorkbookStore"/> so <c>SourceWorkbookPath</c> resolves on disk;
    /// pass <paramref name="workbookRelativePathFactory"/> to fake a missing/misconfigured file.
    /// </summary>
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

    /// <summary>Saves a real rating-shaped workbook through the store and returns its relative path.</summary>
    private async Task<string> SaveRealWorkbookAsync(
        Guid trialId,
        IReadOnlyDictionary<(int plot, int subsample, int traitOrdinal), int>? prefilledCells)
    {
        using var scope = _fixture.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ITrialWorkbookStore>();
        using var stream = CreateRatingWorkbookStream(prefilledCells);
        return await store.SaveAsync(trialId, "voice test.xlsx", stream);
    }

    /// <summary>
    /// Builds the rating-layout workbook in memory via <see cref="RatingWorkbookFactory"/>
    /// (workshop via a scratch temp file, then returns the bytes so no temp file leaks).
    /// </summary>
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
                // Best-effort cleanup.
            }
        }
    }

    /// <summary>Constructs the sync service manually with a scripted transcription double.</summary>
    private static RecordingSyncService CreateService(IServiceScope scope, ITranscriptionService transcription)
        => new(
            scope.ServiceProvider.GetRequiredService<IVoiceRecordingRepository>(),
            scope.ServiceProvider.GetRequiredService<IVoiceBlobStore>(),
            transcription,
            scope.ServiceProvider.GetRequiredService<ITrialRepository>(),
            scope.ServiceProvider.GetRequiredService<IRatingRepository>(),
            NullLogger<RecordingSyncService>.Instance);

    /// <summary>
    /// Builds a sync command. The confirmed fields are nullable: pass <see langword="null"/>
    /// to simulate a transcript-only sync (the PWA without an unlocked layout) or pass 0/
    /// non-positive values, which the service normalizes to “not confirmed”.
    /// </summary>
    private static SyncVoiceRecordingCommand Command(
        Guid trialId,
        string clientId,
        int? plot = 101,
        int? subsample = 3,
        int traitOrdinal = 1,
        int? value = 7)
        => new()
        {
            ClientRecordingId = Guid.Parse(clientId),
            TrialId = trialId,
            Plot = plot,
            Subsample = subsample,
            TraitOrdinal = traitOrdinal,
            Value = value,
        };

    private static MemoryStream AudioBytes(int seed = 0)
        => new(Enumerable.Range(0, 512).Select(i => (byte)((i * 31 + seed * 7) % 256)).ToArray());

    /// <summary>Output copies (input_&lt;name&gt;_out_*.xlsx) under Service/Working/&lt;trialId&gt;/.</summary>
    private static string[] OutputCopies(Guid trialId)
    {
        var dir = Path.Combine(TrialWorkbookStore.ResolveServiceDirectory(), "Working", trialId.ToString());
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*_out_*.xlsx") : [];
    }

    /// <summary>Stored blobs under Service/Recordings/&lt;trialId&gt;/.</summary>
    private static string[] BlobFiles(Guid trialId)
    {
        var dir = Path.Combine(TrialWorkbookStore.ResolveServiceDirectory(), "Recordings", trialId.ToString());
        return Directory.Exists(dir) ? Directory.GetFiles(dir) : [];
    }

    /// <summary>Re-reads the persisted VoiceRecording row through a fresh scope and asserts on it.</summary>
    private async Task AssertStoredAsync(string clientId, Action<VoiceRecording> assert)
    {
        using var scope = _fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IVoiceRecordingRepository>();
        var row = await repo.GetByClientRecordingIdAsync(clientId);
        row.Should().NotBeNull();
        assert(row!);
    }

    /// <summary>Strips <c>$</c> signs from a cell address for flexible assertion matching.</summary>
    private static string NormalizeAddress(string address) => address.Replace("$", "");

    /// <summary>
    /// Reads one trait cell from a workbook copy, or <see langword="null"/> when the cell is
    /// empty/blank. Distinguishes a written <c>0</c> (returns 0) from an empty cell (null).
    /// </summary>
    private static int? ReadCell(string workbookPath, int plot, int subsample, int traitOrdinal)
    {
        using var wb = new XLWorkbook(workbookPath);
        var cell = wb.Worksheet(RatingWorkbookFactory.SheetName)
            .Cell(RatingWorkbookFactory.DataRowFor(plot, subsample), RatingWorkbookFactory.TraitColumnFor(traitOrdinal));
        return cell.IsEmpty() ? null : (int?)cell.GetDouble();
    }

    /// <summary>
    /// A transcription double that unconditionally fails like an unavailable sidecar
    /// (<see cref="FakeTranscriptionService"/> is sealed and only knows how to succeed).
    /// </summary>
    private sealed class ThrowingTranscriptionService : ITranscriptionService
    {
        public Task<TranscriptionResult> TranscribeAsync(Stream audio, string fileName, CancellationToken ct = default)
            => throw new HttpRequestException("Speech-to-text sidecar is unavailable.");
    }
}