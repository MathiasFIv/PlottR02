using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PlottR0._2.Contracts.Dtos.Trial;
using PlottR0._2.Contracts.Repositories;
using PlottR0._2.Domain;
using PlottR0._2.Repository.Repositories;
using PlottR0._2.Services.Abstractions;
using PlottR0._2.Tests.Support;
using PlottR0._2.Tests.Support.WorkbookBuilder;

namespace PlottR0._2.Tests;

/// <summary>
/// Integration tests for <see cref="ITrialService"/> against a real PostgreSQL
/// (via Testcontainers) and the real file-system <see cref="ITrialWorkbookStore"/>.
/// Verifies that trial creation persists the DB row, writes the workbook file,
/// and produces an audit entry; trial listing returns correct order.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TrialServiceTests : IAsyncLifetime
{
    private static readonly Guid DefaultActorUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly PostgresFixture _fixture;
    private readonly List<Guid> _createdTrialIds = [];

    public TrialServiceTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _fixture.CurrentUser.UserId = DefaultActorUserId;
        _fixture.CurrentUser.Email = "test@plottr02.local";
        _fixture.CurrentUser.Name = "Test Actor";
        // The create gate requires a privileged role; pin SystemAdmin for the default
        // actor so every test in this class starts from a permitted identity.
        _fixture.CurrentUser.Role = UserRole.SystemAdmin;

        // Create a User row matching the test actor so the FKCreatedBy constraint is satisfied.
        await EnsureTestUserExistsAsync();
    }

    public async Task DisposeAsync()
    {
        // Clean up Working directories created by the store for these trials.
        foreach (var trialId in _createdTrialIds)
        {
            try
            {
                var serviceDir = TrialWorkbookStore.ResolveServiceDirectory();
                var workingDir = Path.Combine(serviceDir, "Working", trialId.ToString());
                if (Directory.Exists(workingDir))
                    Directory.Delete(workingDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }

        await _fixture.ResetDatabaseAsync();
    }

    [Fact]
    public async Task CreateTrialAsync_ValidXlsx_PersistsTrialFileAndAudit()
    {
        // Arrange
        var fileName = TrialFaker.GenerateFileName();
        using var stream = TrialWorkbookFactory.CreateStream();

        // Act
        TrialSummary summary;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            summary = await service.CreateTrialAsync(fileName, stream);
        }

        var trialId = Guid.Parse(summary.TrialId);
        _createdTrialIds.Add(trialId);

        // Assert — summary shape
        summary.Should().NotBeNull();
        summary.TrialId.Should().NotBeNullOrWhiteSpace();
        summary.FileName.Should().Be(fileName);
        summary.Status.Should().Be(TrialStatus.Created);
        summary.ExternalId.Should().Be(Path.GetFileNameWithoutExtension(fileName));
        summary.CreatedBy.Should().Be(DefaultActorUserId);

        // Assert — PERSISTED in DB: re-read via fresh scope/repository
        string? savedSourceWorkbookPath = null;
        using (var verifyScope = _fixture.CreateScope())
        {
            var repo = verifyScope.ServiceProvider.GetRequiredService<ITrialRepository>();
            var persisted = await repo.GetByIdAsync(trialId);

            persisted.Should().NotBeNull();
            persisted!.Id.Should().Be(trialId);
            persisted.FileName.Should().Be(fileName);
            persisted.ExternalId.Should().Be(Path.GetFileNameWithoutExtension(fileName));
            persisted.Status.Should().Be(TrialStatus.Created);
            persisted.SourceWorkbookPath.Should().NotBeNullOrWhiteSpace();
            persisted.SourceWorkbookPath.Should().Contain(trialId.ToString());
            persisted.CreatedBy.Should().Be(DefaultActorUserId);
            savedSourceWorkbookPath = persisted.SourceWorkbookPath;
        }

        // Assert — FILE exists on disk
        var serviceDir = TrialWorkbookStore.ResolveServiceDirectory();
        var expectedPath = Path.Combine(serviceDir, savedSourceWorkbookPath!);
        File.Exists(expectedPath).Should().BeTrue("the uploaded workbook file should exist on disk");

        // Assert — AUDIT entry persisted
        using (var auditScope = _fixture.CreateScope())
        {
            var auditService = auditScope.ServiceProvider.GetRequiredService<IAuditService>();
            var entries = await auditService.GetRecentAsync();
            entries.Should().Contain(e => e.Action == "Trial.Created");
            var entry = entries.First(e => e.Action == "Trial.Created");
            entry.EntityType.Should().Be("Trial");
            entry.EntityId.Should().Be(trialId.ToString());
            entry.ActorUserId.Should().Be(DefaultActorUserId);

            // Details JSON contains expected fields
            entry.Details.Should().NotBeNullOrWhiteSpace();
            var details = JsonDocument.Parse(entry.Details!);
            details.RootElement.GetProperty("trialId").GetString().Should().Be(trialId.ToString());
            details.RootElement.GetProperty("fileName").GetString().Should().Be(fileName);
        }
    }

    [Fact]
    public async Task CreateTrialAsync_NotWorkbook_ThrowsInvalidDataException()
    {
        // Arrange — garbage content disguised as xlsx
        var fileName = "fake workbook.xlsx";
        var garbageBytes = Encoding.UTF8.GetBytes("This is not an xlsx file at all");
        using var stream = new MemoryStream(garbageBytes);

        // Act & Assert
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            var act = () => service.CreateTrialAsync(fileName, stream);
            await act.Should().ThrowAsync<InvalidDataException>();
        }

        // Assert — NO trial row persisted
        using (var verifyScope = _fixture.CreateScope())
        {
            var repo = verifyScope.ServiceProvider.GetRequiredService<ITrialRepository>();
            var count = await repo.CountAsync();
            count.Should().Be(0, "no trial should be persisted when the workbook is invalid");
        }
    }

    [Fact]
    public async Task CreateTrialAsync_WrongExtension_ThrowsArgumentException()
    {
        // Arrange — .txt extension
        var fileName = "document.txt";
        using var stream = TrialWorkbookFactory.CreateStream();

        // Act & Assert
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            var act = () => service.CreateTrialAsync(fileName, stream);
            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("*xlsx*");
        }

        // Assert — NO trial row persisted
        using (var verifyScope = _fixture.CreateScope())
        {
            var repo = verifyScope.ServiceProvider.GetRequiredService<ITrialRepository>();
            var count = await repo.CountAsync();
            count.Should().Be(0, "no trial should be persisted when the extension is wrong");
        }
    }

    [Fact]
    public async Task CreateTrialAsync_DbInsertFails_DeletesOrphanedFile()
    {
        // Arrange — use a faulting ITrialRepository that always throws on CreateAsync.
        var fileName = "orphan test.xlsx";
        using var stream = TrialWorkbookFactory.CreateStream();

        var store = new TrialWorkbookStore();
        var faultingRepo = new FaultingTrialRepository();

        using var userScope = _fixture.CreateScope();
        var userRepository = userScope.ServiceProvider.GetRequiredService<IUserRepository>();

        var service = new PlottR0._2.Services.Implementations.TrialService(
            faultingRepo,
            store,
            new FaultingAuditService(),
            _fixture.CurrentUser,
            NullLogger<PlottR0._2.Services.Implementations.TrialService>.Instance,
            new ClosedXmlRatingRepository(
                NullLogger<ClosedXmlRatingRepository>.Instance),
            userRepository);

        // We need a trialId to know which Working/<id>/ dir to check. Generate one ourselves.
        var trialId = Guid.NewGuid();

        // The file WILL be saved by the store before the DB insert fails.
        // Manually save it so we can verify it gets cleaned up.
        var relativePath = await store.SaveAsync(trialId, fileName, stream);

        // Assert — file exists before the failing call
        var serviceDir = TrialWorkbookStore.ResolveServiceDirectory();
        var filePath = Path.Combine(serviceDir, relativePath);
        File.Exists(filePath).Should().BeTrue("the workbook file should exist after SaveAsync");

        // Snapshot the Working/ directory state BEFORE the service call so we can
        // do a delta assertion (order-independent, no absolute count).
        var workingBase = Path.Combine(serviceDir, "Working");
        var dirsBefore = Directory.Exists(workingBase)
            ? new HashSet<string>(Directory.GetDirectories(workingBase))
            : new HashSet<string>();

        // Act & Assert — CreateTrialAsync will fail because the repo throws,
        // and the service should clean up the file.
        using var freshStream = TrialWorkbookFactory.CreateStream();
        var act = () => service.CreateTrialAsync(fileName, freshStream);
        await act.Should().ThrowAsync<InvalidOperationException>();

        // Assert — delta: no NEW Working/ dirs remain after the service call.
        // The service creates a dir then cleans it up on failure, so the set after
        // must be a subset of what existed before.
        var dirsAfter = Directory.Exists(workingBase)
            ? new HashSet<string>(Directory.GetDirectories(workingBase))
            : new HashSet<string>();

        dirsAfter.Should().BeSubsetOf(dirsBefore,
            "the service should have cleaned up its own Working/<id>/ dir, leaving no new dirs behind");

        // Our manually-created dir should still be present (we clean it up ourselves below).
        var manualDir = Path.Combine(workingBase, trialId.ToString());
        Directory.Exists(manualDir).Should().BeTrue("our manually-created dir still exists");

        // Cleanup our manual file
        await store.DeleteAsync(relativePath);
    }

    [Fact]
    public async Task ListTrialsAsync_ReturnsAll_OrderedByCreatedAt()
    {
        // Arrange — create 3 trials with small delays to ensure distinct CreatedAt
        var fileNames = TrialFaker.GenerateFileNames(3);

        foreach (var fileName in fileNames)
        {
            using var stream = TrialWorkbookFactory.CreateStream();
            using var scope = _fixture.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            var summary = await service.CreateTrialAsync(fileName, stream);
            _createdTrialIds.Add(Guid.Parse(summary.TrialId));

            // Small delay to ensure distinct timestamps for ordering assertion
            await Task.Delay(50);
        }

        // Act
        IReadOnlyList<TrialSummary> results;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            results = await service.ListTrialsAsync();
        }

        // Assert — all returned
        results.Should().HaveCount(3);

        // Assert — ordered by CreatedAt descending (newest first)
        results.Select(r => r.CreatedAt)
            .Should().BeInDescendingOrder("trials should be ordered by creation time, newest first");

        // Assert — each has correct shape
        results.Should().AllSatisfy(r =>
        {
            r.TrialId.Should().NotBeNullOrWhiteSpace();
            r.FileName.Should().EndWith(".xlsx");
            r.Status.Should().Be(TrialStatus.Created);
        });
    }

    [Fact]
    public async Task GetWorkbookExportAsync_UnknownTrial_ThrowsKeyNotFound()
    {
        // Arrange
        using var scope = _fixture.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITrialService>();

        // Act & Assert
        var act = () => service.GetWorkbookExportAsync(Guid.NewGuid());
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task GetWorkbookExportAsync_ExistingTrialNoOutputs_ReturnsPristine()
    {
        // Arrange — create a trial (DB row + source workbook written to Service/Working/).
        var fileName = TrialFaker.GenerateFileName();
        using var stream = TrialWorkbookFactory.CreateStream();
        TrialSummary summary;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            summary = await service.CreateTrialAsync(fileName, stream);
        }
        var trialId = Guid.Parse(summary.TrialId);
        _createdTrialIds.Add(trialId);

        // Act
        WorkbookExport export;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            export = await service.GetWorkbookExportAsync(trialId);
        }

        // Assert — no sync outputs exist, so the uploaded original is served by name.
        export.FileName.Should().Be(fileName);
        export.FilePath.Should().NotBeNullOrWhiteSpace();
        export.FilePath.Should().EndWith(fileName);
        File.Exists(export.FilePath).Should().BeTrue("the exported workbook should exist on disk");
    }

    [Fact]
    public async Task GetWorkbookExportAsync_ExistingTrialWithNewestOutput_ReturnsOutputCopy()
    {
        // Arrange — create a trial, then write two accumulated sync copies directly into
        // Service/Working/<trialId>/ (mirroring what RecordingSyncService produces).
        var fileName = TrialFaker.GenerateFileName();
        using var stream = TrialWorkbookFactory.CreateStream();
        TrialSummary summary;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            summary = await service.CreateTrialAsync(fileName, stream);
        }
        var trialId = Guid.Parse(summary.TrialId);
        _createdTrialIds.Add(trialId);

        var serviceDir = TrialWorkbookStore.ResolveServiceDirectory();
        var workingDir = Path.Combine(serviceDir, "Working", trialId.ToString());
        var sourceBase = Path.GetFileNameWithoutExtension(fileName);
        var older = TrialWorkbookFactory.Create(
            Path.Combine(workingDir, $"{sourceBase}_out_20260101_000001.xlsx"));
        var newer = TrialWorkbookFactory.Create(
            Path.Combine(workingDir, $"{sourceBase}_out_20260102_000002.xlsx"));

        // Act
        WorkbookExport export;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            export = await service.GetWorkbookExportAsync(trialId);
        }

        // Assert — the newest output copy replaces the pristine upload.
        export.FilePath.Should().Be(newer);
        export.FileName.Should().Be(Path.GetFileName(newer));
        File.Exists(export.FilePath).Should().BeTrue("the served output copy should exist on disk");
    }

    [Fact]
    public async Task GetWorkbookExportAsync_ExistingTrialAndSourceDeletedFromDisk_ThrowsFileNotFound()
    {
        // Arrange — create a trial, then remove its workbook file from disk so neither
        // an output copy nor the pristine source exists.
        var fileName = TrialFaker.GenerateFileName();
        using var stream = TrialWorkbookFactory.CreateStream();
        TrialSummary summary;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            summary = await service.CreateTrialAsync(fileName, stream);
        }
        var trialId = Guid.Parse(summary.TrialId);
        _createdTrialIds.Add(trialId);

        var serviceDir = TrialWorkbookStore.ResolveServiceDirectory();
        var workingDir = Path.Combine(serviceDir, "Working", trialId.ToString());
        foreach (var file in Directory.GetFiles(workingDir, "*.xlsx"))
        {
            File.Delete(file);
        }

        // Act & Assert — the DB row survives, but nothing is left to serve on disk.
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            var act = () => service.GetWorkbookExportAsync(trialId);
            await act.Should().ThrowAsync<FileNotFoundException>();
        }
    }

    [Fact]
    public async Task GetSheetAsync_UnknownTrial_ThrowsKeyNotFound()
    {
        // Arrange
        using var scope = _fixture.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITrialService>();

        // Act & Assert — maps to 404 with the frozen message
        var act = () => service.GetSheetAsync(Guid.NewGuid(), since: null);
        await act.Should().ThrowAsync<KeyNotFoundException>()
            .WithMessage("Trial '*' was not found.");
    }

    [Fact]
    public async Task GetSheetAsync_MatchingVersion_ReturnsUnchangedFastPath()
    {
        // Arrange — create a trial, then resolve its current version once.
        var fileName = TrialFaker.GenerateFileName();
        using var stream = RatingWorkbookFactory.CreateStream();
        TrialSummary summary;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            summary = await service.CreateTrialAsync(fileName, stream);
        }
        var trialId = Guid.Parse(summary.TrialId);
        _createdTrialIds.Add(trialId);

        string version;
        using (var scope = _fixture.CreateScope())
        {
            var export = scope.ServiceProvider.GetRequiredService<ITrialService>();
            version = (await export.GetSheetAsync(trialId, since: null)).Version;
        }

        // Act — echo the version back as `since`
        TrialSheet unchanged;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            unchanged = await service.GetSheetAsync(trialId, since: version);
        }

        // Assert — slim unchanged payload; no workbook open needed to answer it
        unchanged.Changed.Should().BeFalse("the echoed version matches the file, so nothing changed");
        unchanged.TrialId.Should().Be(trialId.ToString());
        unchanged.Version.Should().Be(version);
        unchanged.FileName.Should().BeNull("the fast path carries no payload");
        unchanged.GeneratedAt.Should().BeNull("the fast path carries no payload");
        unchanged.Columns.Should().BeNull();
        unchanged.Rows.Should().BeNull();
        unchanged.Metadata.Should().BeNull();
    }

    [Fact]
    public async Task GetSheetAsync_MismatchedVersion_ReturnsFullPayload()
    {
        // Arrange — create a trial; the default factory emits 16 keyed rows and 9 columns.
        var fileName = TrialFaker.GenerateFileName();
        using var stream = RatingWorkbookFactory.CreateStream();
        TrialSummary summary;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            summary = await service.CreateTrialAsync(fileName, stream);
        }
        var trialId = Guid.Parse(summary.TrialId);
        _createdTrialIds.Add(trialId);

        // Act — `since` never seen before ⇒ full payload
        TrialSheet sheet;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            sheet = await service.GetSheetAsync(trialId, since: "deadbeef-0-0");
        }

        // Assert — full payload with index-aligned columns and rows
        sheet.Changed.Should().BeTrue("a stale `since` must trigger a full read");
        sheet.TrialId.Should().Be(trialId.ToString());
        sheet.FileName.Should().Be(fileName);
        sheet.GeneratedAt.Should().NotBeNull();
        sheet.Version.Should().NotBeNullOrWhiteSpace();
        sheet.Columns.Should().HaveCount(9, "traits 1..9 map to columns C..K");
        sheet.Rows.Should().HaveCount(16, "4 plots × 4 subsamples are key-present");
        sheet.Rows!.Select(r => r.Row).Should().BeInAscendingOrder();
        foreach (var row in sheet.Rows!)
        {
            row.Values.Should().HaveCount(sheet.Columns!.Count, "values[i] ↔ columns[i].traitOrdinal");
        }
    }

    [Fact]
    public async Task GetSheetAsync_MalformedSince_IsIgnoredAndReturnsFullPayload()
    {
        // Arrange — create a trial.
        var fileName = TrialFaker.GenerateFileName();
        using var stream = RatingWorkbookFactory.CreateStream();
        TrialSummary summary;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            summary = await service.CreateTrialAsync(fileName, stream);
        }
        var trialId = Guid.Parse(summary.TrialId);
        _createdTrialIds.Add(trialId);

        // Act — a poller that hand-edits its query string must never hard-fail: the value is
        // compared as a plain string, never parsed.
        TrialSheet sheet;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            sheet = await service.GetSheetAsync(trialId, since: "not a version !!");
        }

        // Assert — full payload, no exception
        sheet.Changed.Should().BeTrue("garbage never equals the version, so the full payload wins");
        sheet.Columns.Should().NotBeNull();
        sheet.Rows.Should().NotBeNull();
    }

    [Fact]
    public async Task GetSheetAsync_SourceDeletedFromDisk_ThrowsFileNotFound()
    {
        // Arrange — create a trial, then remove every workbook file from disk.
        var fileName = TrialFaker.GenerateFileName();
        using var stream = TrialWorkbookFactory.CreateStream();
        TrialSummary summary;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            summary = await service.CreateTrialAsync(fileName, stream);
        }
        var trialId = Guid.Parse(summary.TrialId);
        _createdTrialIds.Add(trialId);

        var serviceDir = TrialWorkbookStore.ResolveServiceDirectory();
        var workingDir = Path.Combine(serviceDir, "Working", trialId.ToString());
        foreach (var file in Directory.GetFiles(workingDir, "*.xlsx"))
        {
            File.Delete(file);
        }

        // Act & Assert — maps to 404 with the frozen message
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            var act = () => service.GetSheetAsync(trialId, since: null);
            await act.Should().ThrowAsync<FileNotFoundException>()
                .WithMessage($"Workbook for trial '{trialId}' was not found on disk.");
        }
    }

    // ── Create role gate ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(UserRole.SystemAdmin)]
    [InlineData(UserRole.Researcher)]
    [InlineData(UserRole.TrialManager)]
    public async Task CreateTrialAsync_PermittedRoles_Succeeds(UserRole role)
    {
        // Arrange — only these three roles may create trials.
        _fixture.CurrentUser.Role = role;
        var fileName = TrialFaker.GenerateFileName();
        using var stream = TrialWorkbookFactory.CreateStream();

        // Act
        TrialSummary summary;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            summary = await service.CreateTrialAsync(fileName, stream);
        }
        _createdTrialIds.Add(Guid.Parse(summary.TrialId));

        // Assert
        summary.TrialId.Should().NotBeNullOrWhiteSpace();
        summary.FileName.Should().Be(fileName);
    }

    [Theory]
    [InlineData(UserRole.FieldWorker)]
    [InlineData(null)]
    public async Task CreateTrialAsync_ForbiddenRoles_ThrowForbiddenBeforePersisting(UserRole? role)
    {
        // Arrange — FieldWorker and role-less/anonymous actors are denied.
        _fixture.CurrentUser.Role = role;
        var fileName = TrialFaker.GenerateFileName();
        using var stream = TrialWorkbookFactory.CreateStream();

        // Act & Assert
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            var act = () => service.CreateTrialAsync(fileName, stream);
            await act.Should().ThrowAsync<ForbiddenAccessException>();
        }

        // Assert — the gate fires before any DB write.
        using (var verifyScope = _fixture.CreateScope())
        {
            var repo = verifyScope.ServiceProvider.GetRequiredService<ITrialRepository>();
            (await repo.CountAsync()).Should().Be(0,
                "the role gate must reject before anything is persisted");
        }
    }

    // ── Creator auto-assignment ───────────────────────────────────────────────

    [Theory]
    [InlineData(UserRole.Researcher, 1)]
    [InlineData(UserRole.TrialManager, 1)]
    [InlineData(UserRole.SystemAdmin, 0)]
    public async Task CreateTrialAsync_CreatorAssignment_FollowsRole(UserRole role, int expectedRows)
    {
        // Arrange
        _fixture.CurrentUser.Role = role;
        var fileName = TrialFaker.GenerateFileName();
        using var stream = TrialWorkbookFactory.CreateStream();

        // Act
        TrialSummary summary;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            summary = await service.CreateTrialAsync(fileName, stream);
        }
        var trialId = Guid.Parse(summary.TrialId);
        _createdTrialIds.Add(trialId);

        // Assert — a researcher/trial-manager creator is auto-assigned exactly once,
        // persisted atomically with the trial; a system administrator is above the matrix
        // and carries NO assignment row.
        using (var scope = _fixture.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<ITrialRepository>();
            var assignments = await repo.GetAssignmentsAsync(trialId);

            assignments.Should().HaveCount(expectedRows);

            if (expectedRows == 1)
            {
                var only = assignments[0];
                only.Id.Should().NotBeEmpty();
                only.TrialId.Should().Be(trialId);
                only.UserId.Should().Be(DefaultActorUserId);
                only.AssignedBy.Should().Be(DefaultActorUserId);

                (await repo.IsAssignedAsync(trialId, DefaultActorUserId)).Should().BeTrue();
                (await repo.GetAssignedTrialIdsAsync(DefaultActorUserId)).Should().Contain(trialId);
            }
            else
            {
                (await repo.IsAssignedAsync(trialId, DefaultActorUserId)).Should().BeFalse(
                    "an admin-authored trial carries no creator assignment");
            }
        }
    }

    [Fact]
    public async Task CreateTrialAsync_NoUserId_ThrowsForbiddenBeforePersisting()
    {
        // Arrange — a privileged role but no user id (e.g. a stale/malformed token).
        // Persisting Guid.Empty would poison CreatedBy and any creator assignment.
        _fixture.CurrentUser.Role = UserRole.Researcher;
        _fixture.CurrentUser.UserId = null;
        var fileName = TrialFaker.GenerateFileName();
        using var stream = TrialWorkbookFactory.CreateStream();

        // Act & Assert
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            var act = () => service.CreateTrialAsync(fileName, stream);
            await act.Should().ThrowAsync<ForbiddenAccessException>()
                .WithMessage("*no user identifier*");
        }

        // Assert — the identity gate fires before any DB write.
        using (var verifyScope = _fixture.CreateScope())
        {
            var repo = verifyScope.ServiceProvider.GetRequiredService<ITrialRepository>();
            (await repo.CountAsync()).Should().Be(0,
                "a privileged actor without a user id must be rejected before anything is persisted");
        }
    }

    // ── Trial access scoping (Increment 3) ────────────────────────────────────

    [Fact]
    public async Task ListTrialsAsync_FieldWorker_ReturnsOnlyAssignedTrials()
    {
        // Arrange — three trials created by the (manager) default actor, with a field
        // worker assigned to the first and third only.
        var trialA = await CreateTrialViaServiceAsync();
        var trialB = await CreateTrialViaServiceAsync();
        var trialC = await CreateTrialViaServiceAsync();
        var fieldWorkerId = await PersistUserAsync(UserRole.FieldWorker);
        await AssignTrialAsync(trialA, fieldWorkerId);
        await AssignTrialAsync(trialC, fieldWorkerId);

        _fixture.CurrentUser.UserId = fieldWorkerId;
        _fixture.CurrentUser.Role = UserRole.FieldWorker;

        // Act
        IReadOnlyList<TrialSummary> results;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            results = await service.ListTrialsAsync();
        }

        // Assert — only the assigned trials, never the unassigned one.
        results.Select(r => Guid.Parse(r.TrialId)).Should().BeEquivalentTo(new[] { trialA, trialC });
        results.Select(r => Guid.Parse(r.TrialId)).Should().NotContain(trialB);
    }

    [Theory]
    [InlineData(UserRole.SystemAdmin)]
    [InlineData(UserRole.Researcher)]
    [InlineData(UserRole.TrialManager)]
    public async Task ListTrialsAsync_Managers_ReturnAllTrialsRegardlessOfAssignment(UserRole role)
    {
        // Arrange — two trials; the actor is deliberately NOT assigned to either.
        await CreateTrialViaServiceAsync();
        await CreateTrialViaServiceAsync();

        _fixture.CurrentUser.UserId = Guid.NewGuid();
        _fixture.CurrentUser.Role = role;

        // Act
        IReadOnlyList<TrialSummary> results;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            results = await service.ListTrialsAsync();
        }

        // Assert — managers bypass the assignment filter entirely.
        results.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(UserRole.FieldWorker)]
    public async Task ListTrialsAsync_NonManagerWithoutAssignments_ReturnsEmpty(UserRole? role)
    {
        // Arrange — a trial exists, but the actor has no assignment to it.
        await CreateTrialViaServiceAsync();
        var actorId = await PersistUserAsync(UserRole.FieldWorker);

        _fixture.CurrentUser.UserId = actorId;
        _fixture.CurrentUser.Role = role;

        // Act
        IReadOnlyList<TrialSummary> results;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            results = await service.ListTrialsAsync();
        }

        // Assert — an unassigned (or role-less) actor sees an empty list, never everything.
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task GetWorkbookExportAsync_AssignedFieldWorker_Succeeds()
    {
        // Arrange — a trial with the field worker assigned.
        var trialId = await CreateTrialViaServiceAsync();
        var fieldWorkerId = await PersistUserAsync(UserRole.FieldWorker);
        await AssignTrialAsync(trialId, fieldWorkerId);
        _fixture.CurrentUser.UserId = fieldWorkerId;
        _fixture.CurrentUser.Role = UserRole.FieldWorker;

        // Act
        WorkbookExport export;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            export = await service.GetWorkbookExportAsync(trialId);
        }

        // Assert
        export.FilePath.Should().NotBeNullOrWhiteSpace();
        File.Exists(export.FilePath).Should().BeTrue();
    }

    [Fact]
    public async Task GetWorkbookExportAsync_UnassignedFieldWorker_ThrowsForbidden()
    {
        // Arrange — a trial exists, but the actor is not assigned to it.
        var trialId = await CreateTrialViaServiceAsync();
        var fieldWorkerId = await PersistUserAsync(UserRole.FieldWorker);
        _fixture.CurrentUser.UserId = fieldWorkerId;
        _fixture.CurrentUser.Role = UserRole.FieldWorker;

        // Act & Assert
        using var scope = _fixture.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
        var act = () => service.GetWorkbookExportAsync(trialId);
        await act.Should().ThrowAsync<ForbiddenAccessException>();
    }

    [Fact]
    public async Task GetWorkbookExportAsync_UnassignedFieldWorkerUnknownTrial_ThrowsKeyNotFound()
    {
        // Arrange — an unassigned field worker asks for a trial that does not exist. The
        // existence check (404) must win over the assignment check (403) so the response
        // never discloses whether a trial with that id exists.
        var fieldWorkerId = await PersistUserAsync(UserRole.FieldWorker);
        _fixture.CurrentUser.UserId = fieldWorkerId;
        _fixture.CurrentUser.Role = UserRole.FieldWorker;

        // Act & Assert
        using var scope = _fixture.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
        var act = () => service.GetWorkbookExportAsync(Guid.NewGuid());
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task GetSheetAsync_AssignedFieldWorker_Succeeds()
    {
        // Arrange
        var trialId = await CreateTrialViaServiceAsync(ratingWorkbook: true);
        var fieldWorkerId = await PersistUserAsync(UserRole.FieldWorker);
        await AssignTrialAsync(trialId, fieldWorkerId);
        _fixture.CurrentUser.UserId = fieldWorkerId;
        _fixture.CurrentUser.Role = UserRole.FieldWorker;

        // Act
        TrialSheet sheet;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            sheet = await service.GetSheetAsync(trialId, since: null);
        }

        // Assert
        sheet.Should().NotBeNull();
    }

    [Fact]
    public async Task GetSheetAsync_UnassignedFieldWorker_ThrowsForbidden()
    {
        // Arrange — a trial exists, but the actor is not assigned to it.
        var trialId = await CreateTrialViaServiceAsync();
        var fieldWorkerId = await PersistUserAsync(UserRole.FieldWorker);
        _fixture.CurrentUser.UserId = fieldWorkerId;
        _fixture.CurrentUser.Role = UserRole.FieldWorker;

        // Act & Assert
        using var scope = _fixture.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
        var act = () => service.GetSheetAsync(trialId, since: null);
        await act.Should().ThrowAsync<ForbiddenAccessException>();
    }

    // ── SetAssignmentsAsync replace semantics + lookups ───────────────────────

    [Fact]
    public async Task SetAssignmentsAsync_ReplacesWholeSet_AndLookupsRoundTrip()
    {
        // Arrange — a trial created by the default SystemAdmin actor (admins carry no
        // creator assignment), plus three extra persisted users to assign.
        _fixture.CurrentUser.Role = UserRole.SystemAdmin;
        var fileName = TrialFaker.GenerateFileName();
        Guid trialId;
        using (var stream = TrialWorkbookFactory.CreateStream())
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            var summary = await service.CreateTrialAsync(fileName, stream);
            trialId = Guid.Parse(summary.TrialId);
        }
        _createdTrialIds.Add(trialId);

        var userA = await PersistUserAsync();
        var userB = await PersistUserAsync();
        var userC = await PersistUserAsync();

        using (var scope = _fixture.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<ITrialRepository>();

            // First set replaces the creator's auto-assignment with A + B.
            await repo.SetAssignmentsAsync(trialId, [userA, userB], DefaultActorUserId);
            var first = await repo.GetAssignmentsAsync(trialId);
            first.Should().HaveCount(2, "SetAssignments replaces the entire set");
            first.Select(a => a.UserId).Should().BeEquivalentTo([userA, userB]);
            first.Should().OnlyContain(a => a.AssignedBy == DefaultActorUserId);
            first.Select(a => a.AssignedAt).Should().BeInAscendingOrder();

            (await repo.IsAssignedAsync(trialId, userA)).Should().BeTrue();
            (await repo.IsAssignedAsync(trialId, DefaultActorUserId)).Should().BeFalse(
                "the creator's auto-assignment was part of the replaced set");
            (await repo.GetAssignedTrialIdsAsync(userA)).Should().Contain(trialId);

            // Second set: B is retained, A is dropped, C is added.
            await repo.SetAssignmentsAsync(trialId, [userB, userC], DefaultActorUserId);
            var second = await repo.GetAssignmentsAsync(trialId);
            second.Should().HaveCount(2);
            second.Select(a => a.UserId).Should().BeEquivalentTo([userB, userC]);

            (await repo.IsAssignedAsync(trialId, userA)).Should().BeFalse();
            (await repo.IsAssignedAsync(trialId, userB)).Should().BeTrue();
            (await repo.GetAssignedTrialIdsAsync(userB)).Should().Contain(trialId);
            (await repo.GetAssignedTrialIdsAsync(userC)).Should().Contain(trialId);

            // Empty set clears every assignment for the trial.
            await repo.SetAssignmentsAsync(trialId, [], DefaultActorUserId);
            (await repo.GetAssignmentsAsync(trialId)).Should().BeEmpty();
        }
    }

    /// <summary>
    /// Creates a trial through the service (as the current manager actor) and registers it
    /// for Working-directory cleanup. Pass <paramref name="ratingWorkbook"/> to upload the
    /// rating-layout workbook (needed by the sheet tests) rather than the plain one.
    /// </summary>
    private async Task<Guid> CreateTrialViaServiceAsync(bool ratingWorkbook = false)
    {
        var fileName = TrialFaker.GenerateFileName();
        using var stream = ratingWorkbook
            ? RatingWorkbookFactory.CreateStream()
            : TrialWorkbookFactory.CreateStream();
        using var scope = _fixture.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
        var summary = await service.CreateTrialAsync(fileName, stream);
        var trialId = Guid.Parse(summary.TrialId);
        _createdTrialIds.Add(trialId);
        return trialId;
    }

    /// <summary>Replaces a trial's assignment set with the single given user (creator added by attribution).</summary>
    private async Task AssignTrialAsync(Guid trialId, Guid userId)
    {
        using var scope = _fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITrialRepository>();
        await repo.SetAssignmentsAsync(trialId, [userId], DefaultActorUserId);
    }

    /// <summary>
    /// Persists an additional <see cref="User"/> (FieldWorker by default) so assignment
    /// rows satisfy the FK to Users, and returns its id.
    /// </summary>
    private async Task<Guid> PersistUserAsync(UserRole? role = UserRole.FieldWorker)
    {
        var userId = Guid.NewGuid();
        using var scope = _fixture.CreateScope();
        var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();

        await userRepo.AddAsync(new User
        {
            Id = userId,
            Name = $"Assigned User {userId:N}",
            Email = $"assigned.{userId:N}@plottr02.local",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("TestPass123!"),
            Role = role,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        return userId;
    }

    /// <summary>
    /// Ensures a User row with the <see cref="DefaultActorUserId"/> exists in the database
    /// so the FKCreatedBy constraint on Trials is satisfied.
    /// </summary>
    private async Task EnsureTestUserExistsAsync()
    {
        using var scope = _fixture.CreateScope();
        var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var existing = await userRepo.GetByIdAsync(DefaultActorUserId);
        if (existing is not null)
            return;

        var user = new User
        {
            Id = DefaultActorUserId,
            Name = "Test Actor",
            Email = "test@plottr02.local",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("TestPass123!"),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await userRepo.AddAsync(user);
    }

    /// <summary>
    /// A minimal <see cref="ITrialRepository"/> that throws on <see cref="CreateAsync"/>
    /// to simulate a DB failure during trial creation.
    /// </summary>
    private sealed class FaultingTrialRepository : ITrialRepository
    {
        public Task<Trial> CreateAsync(Trial trial, CancellationToken ct = default)
            => throw new InvalidOperationException("Simulated DB insert failure.");

        public Task<Trial?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult<Trial?>(null);

        public Task<IReadOnlyList<Trial>> GetAllAsync(int? skip = null, int? take = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Trial>>([]);

        public Task<bool> ExistsAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<int> CountAsync(CancellationToken ct = default)
            => Task.FromResult(0);

        public Task SetAssignmentsAsync(Guid trialId, IReadOnlyList<Guid> userIds, Guid assignedById, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<TrialAssignment>> GetAssignmentsAsync(Guid trialId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TrialAssignment>>([]);

        public Task<IReadOnlyList<Guid>> GetAssignedTrialIdsAsync(Guid userId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>([]);

        public Task<bool> IsAssignedAsync(Guid trialId, Guid userId, CancellationToken ct = default)
            => Task.FromResult(false);
    }

    /// <summary>
    /// A minimal <see cref="IAuditService"/> that does nothing (audit not needed for the failure test).
    /// </summary>
    private sealed class FaultingAuditService : IAuditService
    {
        public Task LogAsync(string action, string entityType, string? entityId, Guid? actorUserId, string? actorName, string? details, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<PlottR0._2.Domain.AuditLog>> GetRecentAsync(int count = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PlottR0._2.Domain.AuditLog>>([]);
    }
}
