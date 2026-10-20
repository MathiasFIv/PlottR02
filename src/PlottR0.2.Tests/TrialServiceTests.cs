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

        _fixture.CurrentUser.Role = UserRole.SystemAdmin;

        await EnsureTestUserExistsAsync();
    }

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
            }
            catch
            {
            }
        }

        await _fixture.ResetDatabaseAsync();
    }

    [Fact]
    public async Task CreateTrialAsync_ValidXlsx_PersistsTrialFileAndAudit()
    {
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

        summary.Should().NotBeNull();
        summary.TrialId.Should().NotBeNullOrWhiteSpace();
        summary.FileName.Should().Be(fileName);
        summary.Status.Should().Be(TrialStatus.Created);
        summary.ExternalId.Should().Be(Path.GetFileNameWithoutExtension(fileName));
        summary.CreatedBy.Should().Be(DefaultActorUserId);
        summary.CreatedByName.Should().Be("Test Actor",
            "the create response resolves the actor's display name immediately");
        summary.IsAssignedToCurrentUser.Should().BeFalse(
            "a system-administrator creator is above the matrix and carries no assignment");

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

        var serviceDir = TrialWorkbookStore.ResolveServiceDirectory();
        var expectedPath = Path.Combine(serviceDir, savedSourceWorkbookPath!);
        File.Exists(expectedPath).Should().BeTrue("the uploaded workbook file should exist on disk");

        using (var auditScope = _fixture.CreateScope())
        {
            var auditService = auditScope.ServiceProvider.GetRequiredService<IAuditService>();
            var entries = await auditService.GetRecentAsync();
            entries.Should().Contain(e => e.Action == "Trial.Created");
            var entry = entries.First(e => e.Action == "Trial.Created");
            entry.EntityType.Should().Be("Trial");
            entry.EntityId.Should().Be(trialId.ToString());
            entry.ActorUserId.Should().Be(DefaultActorUserId);

            entry.Details.Should().NotBeNullOrWhiteSpace();
            var details = JsonDocument.Parse(entry.Details!);
            details.RootElement.GetProperty("trialId").GetString().Should().Be(trialId.ToString());
            details.RootElement.GetProperty("fileName").GetString().Should().Be(fileName);
        }
    }

    [Fact]
    public async Task CreateTrialAsync_NotWorkbook_ThrowsInvalidDataException()
    {
        var fileName = "fake workbook.xlsx";
        var garbageBytes = Encoding.UTF8.GetBytes("This is not an xlsx file at all");
        using var stream = new MemoryStream(garbageBytes);

        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            var act = () => service.CreateTrialAsync(fileName, stream);
            await act.Should().ThrowAsync<InvalidDataException>();
        }

        using (var verifyScope = _fixture.CreateScope())
        {
            var repo = verifyScope.ServiceProvider.GetRequiredService<ITrialRepository>();
            var count = (await repo.GetAllAsync()).Count;
            count.Should().Be(0, "no trial should be persisted when the workbook is invalid");
        }
    }

    [Fact]
    public async Task CreateTrialAsync_WrongExtension_ThrowsArgumentException()
    {
        var fileName = "document.txt";
        using var stream = TrialWorkbookFactory.CreateStream();

        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            var act = () => service.CreateTrialAsync(fileName, stream);
            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("*xlsx*");
        }

        using (var verifyScope = _fixture.CreateScope())
        {
            var repo = verifyScope.ServiceProvider.GetRequiredService<ITrialRepository>();
            var count = (await repo.GetAllAsync()).Count;
            count.Should().Be(0, "no trial should be persisted when the extension is wrong");
        }
    }

    [Fact]
    public async Task CreateTrialAsync_DbInsertFails_DeletesOrphanedFile()
    {
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
            userRepository,
            new PlottR0._2.Services.Implementations.TrialWriteGate());

        var trialId = Guid.NewGuid();

        var relativePath = await store.SaveAsync(trialId, fileName, stream);

        var serviceDir = TrialWorkbookStore.ResolveServiceDirectory();
        var filePath = Path.Combine(serviceDir, relativePath);
        File.Exists(filePath).Should().BeTrue("the workbook file should exist after SaveAsync");

        var workingBase = Path.Combine(serviceDir, "Working");
        var dirsBefore = Directory.Exists(workingBase)
            ? new HashSet<string>(Directory.GetDirectories(workingBase))
            : new HashSet<string>();

        using var freshStream = TrialWorkbookFactory.CreateStream();
        var act = () => service.CreateTrialAsync(fileName, freshStream);
        await act.Should().ThrowAsync<InvalidOperationException>();

        var dirsAfter = Directory.Exists(workingBase)
            ? new HashSet<string>(Directory.GetDirectories(workingBase))
            : new HashSet<string>();

        dirsAfter.Should().BeSubsetOf(dirsBefore,
            "the service should have cleaned up its own Working/<id>/ dir, leaving no new dirs behind");

        var manualDir = Path.Combine(workingBase, trialId.ToString());
        Directory.Exists(manualDir).Should().BeTrue("our manually-created dir still exists");

        await store.DeleteAsync(relativePath);
    }

    [Fact]
    public async Task ListTrialsAsync_ReturnsAll_OrderedByCreatedAt()
    {
        var fileNames = TrialFaker.GenerateFileNames(3);

        foreach (var fileName in fileNames)
        {
            using var stream = TrialWorkbookFactory.CreateStream();
            using var scope = _fixture.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            var summary = await service.CreateTrialAsync(fileName, stream);
            _createdTrialIds.Add(Guid.Parse(summary.TrialId));

            await Task.Delay(50);
        }

        IReadOnlyList<TrialSummary> results;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            results = await service.ListTrialsAsync();
        }

        results.Should().HaveCount(3);

        results.Select(r => r.CreatedAt)
            .Should().BeInDescendingOrder("trials should be ordered by creation time, newest first");

        results.Should().AllSatisfy(r =>
        {
            r.TrialId.Should().NotBeNullOrWhiteSpace();
            r.FileName.Should().EndWith(".xlsx");
            r.Status.Should().Be(TrialStatus.Created);
            r.CreatedByName.Should().Be("Test Actor",
                "the creator's display name is resolved from the joined user row");
            r.IsAssignedToCurrentUser.Should().BeFalse(
                "the default SystemAdmin actor is assigned to no trial");
        });
    }

    [Fact]
    public async Task GetWorkbookExportAsync_UnknownTrial_ThrowsKeyNotFound()
    {
        using var scope = _fixture.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITrialService>();

        var act = () => service.GetWorkbookExportAsync(Guid.NewGuid());
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task GetWorkbookExportAsync_ExistingTrialNoOutputs_ReturnsPristine()
    {
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

        WorkbookExport export;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            export = await service.GetWorkbookExportAsync(trialId);
        }

        export.FileName.Should().Be(fileName);
        export.FilePath.Should().NotBeNullOrWhiteSpace();
        export.FilePath.Should().EndWith(fileName);
        File.Exists(export.FilePath).Should().BeTrue("the exported workbook should exist on disk");
    }

    [Fact]
    public async Task GetWorkbookExportAsync_ExistingTrialWithNewestOutput_ReturnsOutputCopy()
    {
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

        WorkbookExport export;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            export = await service.GetWorkbookExportAsync(trialId);
        }

        export.FilePath.Should().Be(newer);
        export.FileName.Should().Be(Path.GetFileName(newer));
        File.Exists(export.FilePath).Should().BeTrue("the served output copy should exist on disk");
    }

    [Fact]
    public async Task GetWorkbookExportAsync_ExistingTrialAndSourceDeletedFromDisk_ThrowsFileNotFound()
    {
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

        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            var act = () => service.GetWorkbookExportAsync(trialId);
            await act.Should().ThrowAsync<FileNotFoundException>();
        }
    }

    [Fact]
    public async Task GetWorkbookExportAsync_ColdLocalCache_RehydratesFromDurableStore()
    {
        var cacheRoot = Path.Combine(
            Path.GetTempPath(), "PlottrTrialServiceRehydrate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheRoot);
        var objectStore = new InMemoryObjectStore();
        var store = new TrialWorkbookStore(cacheRoot, objectStore);

        var trialId = Guid.NewGuid();
        try
        {
            var fileName = TrialFaker.GenerateFileName();
            using (var stream = TrialWorkbookFactory.CreateStream())
            {
                await store.SaveAsync(trialId, fileName, stream);
            }

            using (var scope = _fixture.CreateScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<ITrialRepository>();
                await repo.CreateAsync(new Trial
                {
                    Id = trialId,
                    ExternalId = Path.GetFileNameWithoutExtension(fileName),
                    FileName = fileName,
                    Status = TrialStatus.Created,
                    SourceWorkbookPath = Path.Combine("Working", trialId.ToString(), fileName),
                    CreatedBy = DefaultActorUserId,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });
            }

            Directory.Delete(Path.Combine(cacheRoot, "Working", trialId.ToString()), recursive: true);
            objectStore.ResetCounters();

            WorkbookExport export;
            using (var scope = _fixture.CreateScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<ITrialRepository>();
                var userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();
                var service = new PlottR0._2.Services.Implementations.TrialService(
                    repo,
                    store,
                    new FaultingAuditService(),
                    _fixture.CurrentUser,
                    NullLogger<PlottR0._2.Services.Implementations.TrialService>.Instance,
                    new ClosedXmlRatingRepository(NullLogger<ClosedXmlRatingRepository>.Instance),
                    userRepository,
                    new PlottR0._2.Services.Implementations.TrialWriteGate());

                export = await service.GetWorkbookExportAsync(trialId);
            }

            export.FileName.Should().Be(fileName);
            File.Exists(export.FilePath).Should().BeTrue(
                "a cold local cache must be rehydrated from the durable object store");
            objectStore.GetToFileKeys.Should().Contain($"Workbooks/{trialId}/{fileName}",
                "the rehydrating ResolveExportAsync path must download the durable source, not resolve against an empty cache");
        }
        finally
        {
            try
            {
                if (Directory.Exists(cacheRoot))
                    Directory.Delete(cacheRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task GetSheetAsync_UnknownTrial_ThrowsKeyNotFound()
    {
        using var scope = _fixture.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITrialService>();

        var act = () => service.GetSheetAsync(Guid.NewGuid(), since: null);
        await act.Should().ThrowAsync<KeyNotFoundException>()
            .WithMessage("Trial '*' was not found.");
    }

    [Fact]
    public async Task GetSheetAsync_MatchingVersion_ReturnsUnchangedFastPath()
    {
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

        TrialSheet unchanged;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            unchanged = await service.GetSheetAsync(trialId, since: version);
        }

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

        TrialSheet sheet;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            sheet = await service.GetSheetAsync(trialId, since: "deadbeef-0-0");
        }

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

        TrialSheet sheet;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            sheet = await service.GetSheetAsync(trialId, since: "not a version !!");
        }

        sheet.Changed.Should().BeTrue("garbage never equals the version, so the full payload wins");
        sheet.Columns.Should().NotBeNull();
        sheet.Rows.Should().NotBeNull();
    }

    [Fact]
    public async Task GetSheetAsync_SourceDeletedFromDisk_ThrowsFileNotFound()
    {
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

        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            var act = () => service.GetSheetAsync(trialId, since: null);
            await act.Should().ThrowAsync<FileNotFoundException>()
                .WithMessage($"Workbook for trial '{trialId}' was not found on disk.");
        }
    }

    [Theory]
    [InlineData(UserRole.SystemAdmin)]
    [InlineData(UserRole.Researcher)]
    [InlineData(UserRole.TrialManager)]
    public async Task CreateTrialAsync_PermittedRoles_Succeeds(UserRole role)
    {
        _fixture.CurrentUser.Role = role;
        var fileName = TrialFaker.GenerateFileName();
        using var stream = TrialWorkbookFactory.CreateStream();

        TrialSummary summary;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            summary = await service.CreateTrialAsync(fileName, stream);
        }
        _createdTrialIds.Add(Guid.Parse(summary.TrialId));

        summary.TrialId.Should().NotBeNullOrWhiteSpace();
        summary.FileName.Should().Be(fileName);
        summary.CreatedByName.Should().Be("Test Actor",
            "every permitted creator's display name is surfaced in the create response");
        summary.IsAssignedToCurrentUser.Should().Be(role != UserRole.SystemAdmin,
            "the create response badges a researcher/trial-manager creator, never an admin");
    }

    [Theory]
    [InlineData(UserRole.FieldWorker)]
    [InlineData(null)]
    public async Task CreateTrialAsync_ForbiddenRoles_ThrowForbiddenBeforePersisting(UserRole? role)
    {
        _fixture.CurrentUser.Role = role;
        var fileName = TrialFaker.GenerateFileName();
        using var stream = TrialWorkbookFactory.CreateStream();

        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            var act = () => service.CreateTrialAsync(fileName, stream);
            await act.Should().ThrowAsync<ForbiddenAccessException>();
        }

        using (var verifyScope = _fixture.CreateScope())
        {
            var repo = verifyScope.ServiceProvider.GetRequiredService<ITrialRepository>();
            (await repo.GetAllAsync()).Count.Should().Be(0,
                "the role gate must reject before anything is persisted");
        }
    }

    [Theory]
    [InlineData(UserRole.Researcher, 1)]
    [InlineData(UserRole.TrialManager, 1)]
    [InlineData(UserRole.SystemAdmin, 0)]
    public async Task CreateTrialAsync_CreatorAssignment_FollowsRole(UserRole role, int expectedRows)
    {
        _fixture.CurrentUser.Role = role;
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

        summary.IsAssignedToCurrentUser.Should().Be(expectedRows == 1,
            "the returned badge must match the creator auto-assignment row");
        summary.CreatedByName.Should().Be("Test Actor",
            "the create response always carries the actor's display name");

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
        _fixture.CurrentUser.Role = UserRole.Researcher;
        _fixture.CurrentUser.UserId = null;
        var fileName = TrialFaker.GenerateFileName();
        using var stream = TrialWorkbookFactory.CreateStream();

        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            var act = () => service.CreateTrialAsync(fileName, stream);
            await act.Should().ThrowAsync<ForbiddenAccessException>()
                .WithMessage("*no user identifier*");
        }

        using (var verifyScope = _fixture.CreateScope())
        {
            var repo = verifyScope.ServiceProvider.GetRequiredService<ITrialRepository>();
            (await repo.GetAllAsync()).Count.Should().Be(0,
                "a privileged actor without a user id must be rejected before anything is persisted");
        }
    }

    [Fact]
    public async Task ListTrialsAsync_FieldWorker_ReturnsOnlyAssignedTrials()
    {
        var trialA = await CreateTrialViaServiceAsync();
        var trialB = await CreateTrialViaServiceAsync();
        var trialC = await CreateTrialViaServiceAsync();
        var fieldWorkerId = await PersistUserAsync(UserRole.FieldWorker);
        await AssignTrialAsync(trialA, fieldWorkerId);
        await AssignTrialAsync(trialC, fieldWorkerId);

        _fixture.CurrentUser.UserId = fieldWorkerId;
        _fixture.CurrentUser.Role = UserRole.FieldWorker;

        IReadOnlyList<TrialSummary> results;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            results = await service.ListTrialsAsync();
        }

        results.Select(r => Guid.Parse(r.TrialId)).Should().BeEquivalentTo(new[] { trialA, trialC });
        results.Select(r => Guid.Parse(r.TrialId)).Should().NotContain(trialB);

        results.Should().OnlyContain(r => r.IsAssignedToCurrentUser,
            "field workers only receive their own assigned trials");

        results.Should().OnlyContain(r => r.CreatedByName == "Test Actor",
            "the list resolves each trial's creator display name from the joined user row");
    }

    [Theory]
    [InlineData(UserRole.SystemAdmin)]
    [InlineData(UserRole.Researcher)]
    [InlineData(UserRole.TrialManager)]
    public async Task ListTrialsAsync_Managers_ReturnAllTrialsRegardlessOfAssignment(UserRole role)
    {
        await CreateTrialViaServiceAsync();
        await CreateTrialViaServiceAsync();

        _fixture.CurrentUser.UserId = Guid.NewGuid();
        _fixture.CurrentUser.Role = role;

        IReadOnlyList<TrialSummary> results;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            results = await service.ListTrialsAsync();
        }

        results.Should().HaveCount(2);
        results.Should().OnlyContain(r => !r.IsAssignedToCurrentUser,
            "the manager is deliberately assigned to none of the returned trials");

        results.Should().OnlyContain(r => r.CreatedByName == "Test Actor",
            "the manager list resolves each trial's creator display name server-side");
    }

    [Fact]
    public async Task ListTrialsAsync_Manager_FlagsOnlyPersonallyAssignedTrials()
    {
        var trialA = await CreateTrialViaServiceAsync();
        var trialB = await CreateTrialViaServiceAsync();
        var trialC = await CreateTrialViaServiceAsync();
        var managerId = await PersistUserAsync(UserRole.Researcher);
        await AssignTrialAsync(trialA, managerId);
        await AssignTrialAsync(trialC, managerId);

        _fixture.CurrentUser.UserId = managerId;
        _fixture.CurrentUser.Role = UserRole.Researcher;

        IReadOnlyList<TrialSummary> results;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            results = await service.ListTrialsAsync();
        }

        results.Should().HaveCount(3);
        results.Single(r => Guid.Parse(r.TrialId) == trialA).IsAssignedToCurrentUser
            .Should().BeTrue("the manager is assigned to trial A");
        results.Single(r => Guid.Parse(r.TrialId) == trialB).IsAssignedToCurrentUser
            .Should().BeFalse("the manager is not assigned to trial B");
        results.Single(r => Guid.Parse(r.TrialId) == trialC).IsAssignedToCurrentUser
            .Should().BeTrue("the manager is assigned to trial C");
    }

    [Fact]
    public async Task ListTrialsAsync_ManagerWithoutUserId_ReturnsAllUnflagged()
    {
        await CreateTrialViaServiceAsync();
        await CreateTrialViaServiceAsync();

        _fixture.CurrentUser.UserId = null;
        _fixture.CurrentUser.Role = UserRole.Researcher;

        IReadOnlyList<TrialSummary> results;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            results = await service.ListTrialsAsync();
        }

        results.Should().HaveCount(2);
        results.Should().OnlyContain(r => !r.IsAssignedToCurrentUser,
            "a manager with no user id is assigned to nothing, so no row may be badged");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(UserRole.FieldWorker)]
    public async Task ListTrialsAsync_NonManagerWithoutAssignments_ReturnsEmpty(UserRole? role)
    {
        await CreateTrialViaServiceAsync();
        var actorId = await PersistUserAsync(UserRole.FieldWorker);

        _fixture.CurrentUser.UserId = actorId;
        _fixture.CurrentUser.Role = role;

        IReadOnlyList<TrialSummary> results;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            results = await service.ListTrialsAsync();
        }

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task ListTrialsAsync_DeletedCreator_OmitsName()
    {
        var trialId = await CreateTrialViaServiceAsync();

        using (var scope = _fixture.CreateScope())
        {
            var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            await userRepo.DeleteAsync(DefaultActorUserId);
        }

        _fixture.CurrentUser.UserId = Guid.NewGuid();
        _fixture.CurrentUser.Role = UserRole.SystemAdmin;

        IReadOnlyList<TrialSummary> results;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            results = await service.ListTrialsAsync();
        }

        var row = results.Single(r => Guid.Parse(r.TrialId) == trialId);
        row.CreatedBy.Should().BeNull("deleting the creator sets the CreatedBy FK to null");
        row.CreatedByName.Should().BeNull("the creator row is gone, so no display name resolves");
    }

    [Fact]
    public async Task GetWorkbookExportAsync_AssignedFieldWorker_Succeeds()
    {
        var trialId = await CreateTrialViaServiceAsync();
        var fieldWorkerId = await PersistUserAsync(UserRole.FieldWorker);
        await AssignTrialAsync(trialId, fieldWorkerId);
        _fixture.CurrentUser.UserId = fieldWorkerId;
        _fixture.CurrentUser.Role = UserRole.FieldWorker;

        WorkbookExport export;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            export = await service.GetWorkbookExportAsync(trialId);
        }

        export.FilePath.Should().NotBeNullOrWhiteSpace();
        File.Exists(export.FilePath).Should().BeTrue();
    }

    [Fact]
    public async Task GetWorkbookExportAsync_UnassignedFieldWorker_ThrowsForbidden()
    {
        var trialId = await CreateTrialViaServiceAsync();
        var fieldWorkerId = await PersistUserAsync(UserRole.FieldWorker);
        _fixture.CurrentUser.UserId = fieldWorkerId;
        _fixture.CurrentUser.Role = UserRole.FieldWorker;

        using var scope = _fixture.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
        var act = () => service.GetWorkbookExportAsync(trialId);
        await act.Should().ThrowAsync<ForbiddenAccessException>();
    }

    [Fact]
    public async Task GetWorkbookExportAsync_UnassignedFieldWorkerUnknownTrial_ThrowsKeyNotFound()
    {
        var fieldWorkerId = await PersistUserAsync(UserRole.FieldWorker);
        _fixture.CurrentUser.UserId = fieldWorkerId;
        _fixture.CurrentUser.Role = UserRole.FieldWorker;

        using var scope = _fixture.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
        var act = () => service.GetWorkbookExportAsync(Guid.NewGuid());
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task GetSheetAsync_AssignedFieldWorker_Succeeds()
    {
        var trialId = await CreateTrialViaServiceAsync(ratingWorkbook: true);
        var fieldWorkerId = await PersistUserAsync(UserRole.FieldWorker);
        await AssignTrialAsync(trialId, fieldWorkerId);
        _fixture.CurrentUser.UserId = fieldWorkerId;
        _fixture.CurrentUser.Role = UserRole.FieldWorker;

        TrialSheet sheet;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            sheet = await service.GetSheetAsync(trialId, since: null);
        }

        sheet.Should().NotBeNull();
    }

    [Fact]
    public async Task GetSheetAsync_UnassignedFieldWorker_ThrowsForbidden()
    {
        var trialId = await CreateTrialViaServiceAsync();
        var fieldWorkerId = await PersistUserAsync(UserRole.FieldWorker);
        _fixture.CurrentUser.UserId = fieldWorkerId;
        _fixture.CurrentUser.Role = UserRole.FieldWorker;

        using var scope = _fixture.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
        var act = () => service.GetSheetAsync(trialId, since: null);
        await act.Should().ThrowAsync<ForbiddenAccessException>();
    }

    [Fact]
    public async Task SetAssignmentsAsync_ReplacesWholeSet_AndLookupsRoundTrip()
    {
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

            await repo.SetAssignmentsAsync(trialId, [userB, userC], DefaultActorUserId);
            var second = await repo.GetAssignmentsAsync(trialId);
            second.Should().HaveCount(2);
            second.Select(a => a.UserId).Should().BeEquivalentTo([userB, userC]);

            (await repo.IsAssignedAsync(trialId, userA)).Should().BeFalse();
            (await repo.IsAssignedAsync(trialId, userB)).Should().BeTrue();
            (await repo.GetAssignedTrialIdsAsync(userB)).Should().Contain(trialId);
            (await repo.GetAssignedTrialIdsAsync(userC)).Should().Contain(trialId);

            await repo.SetAssignmentsAsync(trialId, [], DefaultActorUserId);
            (await repo.GetAssignmentsAsync(trialId)).Should().BeEmpty();
        }
    }

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

    private async Task AssignTrialAsync(Guid trialId, Guid userId)
    {
        using var scope = _fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITrialRepository>();
        await repo.SetAssignmentsAsync(trialId, [userId], DefaultActorUserId);
    }

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

        public Task SetAssignmentsAsync(Guid trialId, IReadOnlyList<Guid> userIds, Guid assignedById, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<TrialAssignment>> GetAssignmentsAsync(Guid trialId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TrialAssignment>>([]);

        public Task<IReadOnlyList<Guid>> GetAssignedTrialIdsAsync(Guid userId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>([]);

        public Task<bool> IsAssignedAsync(Guid trialId, Guid userId, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task UpdateStatusAsync(Guid trialId, string status, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class FaultingAuditService : IAuditService
    {
        public Task LogAsync(string action, string entityType, string? entityId, Guid? actorUserId, string? actorName, string? details, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<PlottR0._2.Domain.AuditLog>> GetRecentAsync(int count = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PlottR0._2.Domain.AuditLog>>([]);
    }
}
