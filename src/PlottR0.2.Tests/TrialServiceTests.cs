using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
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

        var service = new PlottR0._2.Services.Implementations.TrialService(
            faultingRepo,
            store,
            new FaultingAuditService(),
            _fixture.CurrentUser,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PlottR0._2.Services.Implementations.TrialService>.Instance);

        // We need a trialId to know which Working/<id>/ dir to check. Generate one ourselves.
        var trialId = Guid.NewGuid();

        // The file WILL be saved by the store before the DB insert fails.
        // Manually save it so we can verify it gets cleaned up.
        var relativePath = await store.SaveAsync(trialId, fileName, stream);

        // Assert — file exists before the failing call
        var serviceDir = TrialWorkbookStore.ResolveServiceDirectory();
        var filePath = Path.Combine(serviceDir, relativePath);
        File.Exists(filePath).Should().BeTrue("the workbook file should exist after SaveAsync");

        // Act & Assert — CreateTrialAsync will fail because the repo throws,
        // and the service should clean up the file.
        // Note: we can't call service.CreateTrialAsync because it generates its own trialId.
        // Instead, exercise the cleanup path directly by verifying the store's DeleteAsync works.
        // But to test the full service flow, we construct a service that uses our faulting repo
        // AND the real store. The service will save the file, fail on DB insert, and clean up.

        // Actually, we CAN test via the service — the store.SaveAsync will succeed (it doesn't
        // need a DB), and then the repo.CreateAsync will throw. The service catches and deletes.
        using var freshStream = TrialWorkbookFactory.CreateStream();
        var act = () => service.CreateTrialAsync(fileName, freshStream);
        await act.Should().ThrowAsync<InvalidOperationException>();

        // Assert — the file from the SERVICE call should be cleaned up.
        // The service generates its own trialId, so we need to find the file.
        // Since we can't predict the service's trialId, check that NO new Working dirs remain
        // beyond what we created manually.
        var workingBase = Path.Combine(serviceDir, "Working");
        var dirsAfter = Directory.Exists(workingBase)
            ? Directory.GetDirectories(workingBase).Length
            : 0;
        dirsAfter.Should().Be(1, "only our manually-created Working dir should remain; the service should have cleaned up its own");

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
