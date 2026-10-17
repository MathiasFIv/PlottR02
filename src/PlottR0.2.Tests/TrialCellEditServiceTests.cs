using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PlottR0._2.Contracts.Dtos.Trial;
using PlottR0._2.Contracts.Ratings;
using PlottR0._2.Contracts.Repositories;
using PlottR0._2.Domain;
using PlottR0._2.Repository.Repositories;
using PlottR0._2.Services.Abstractions;
using PlottR0._2.Tests.Support;
using PlottR0._2.Tests.Support.WorkbookBuilder;

namespace PlottR0._2.Tests;

/// <summary>
/// Integration tests for <see cref="ITrialService.EditCellAsync"/> against a real PostgreSQL
/// (via Testcontainers) and the real file-system <see cref="ITrialWorkbookStore"/>. Exercises
/// the whole orchestration: validate → load trial → access check → resolve workbook → write the
/// single cell through the ClosedXML repository → persist + audit on a write → re-resolve the
/// version. Mirrors <see cref="TrialServiceTests"/>'s setup (SystemAdmin actor, Working-dir
/// cleanup).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TrialCellEditServiceTests : IAsyncLifetime
{
    private static readonly Guid DefaultActorUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly PostgresFixture _fixture;
    private readonly List<Guid> _createdTrialIds = [];

    public TrialCellEditServiceTests(PostgresFixture fixture)
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
                // Best-effort cleanup.
            }
        }

        await _fixture.ResetDatabaseAsync();
    }

    [Fact]
    public async Task EditCellAsync_EmptyCell_WritesPersistsAuditsAndBumpsVersion()
    {
        // Arrange — a rating-layout trial (plot 101/subsample 1 exists, its cells empty).
        var trialId = await CreateTrialViaServiceAsync();

        string versionBefore;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            versionBefore = (await service.GetSheetAsync(trialId, since: null)).Version;
        }

        // Act — write 5 into plot 101 subsample 1 trait 1
        CellEditResult result;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            result = await service.EditCellAsync(
                trialId, new EditCellRequest { Plot = 101, Subsample = 1, TraitOrdinal = 1, Value = 5 });
        }

        // Assert — written, no skip, prior value absent, address reported
        result.TrialId.Should().Be(trialId.ToString());
        result.Written.Should().BeTrue();
        result.Skipped.Should().BeFalse();
        result.SkippedReason.Should().BeNull();
        result.PriorValue.Should().BeNull("the cell was empty");
        result.CellAddress.Replace("$", "").Should().Be("C22");
        result.Value.Should().Be(5);
        result.GeneratedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));

        // Assert — the fresh copy changes the opaque version
        result.Version.Should().NotBeNullOrWhiteSpace();
        result.Version.Should().NotBe(versionBefore, "a new _out_ copy flips the version");
        result.FileName.Should().NotBeNull();

        // Assert — the just-written value is readable through the sheet read at that version
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            var sheet = await service.GetSheetAsync(trialId, since: result.Version);
            sheet.Changed.Should().BeFalse("the returned version must match what is on disk now");
        }

        // Assert — a "Trial.CellEdited" audit entry with the expected detail fields
        using (var scope = _fixture.CreateScope())
        {
            var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();
            var entry = (await audit.GetRecentAsync()).Should().ContainSingle(e => e.Action == "Trial.CellEdited").Subject;
            entry.EntityType.Should().Be("Trial");
            entry.EntityId.Should().Be(trialId.ToString());
            entry.ActorUserId.Should().Be(DefaultActorUserId);
            entry.ActorName.Should().Be("Test Actor");

            var details = JsonDocument.Parse(entry.Details!);
            details.RootElement.GetProperty("trialId").GetString().Should().Be(trialId.ToString());
            details.RootElement.GetProperty("plot").GetInt32().Should().Be(101);
            details.RootElement.GetProperty("subsample").GetInt32().Should().Be(1);
            details.RootElement.GetProperty("traitOrdinal").GetInt32().Should().Be(1);
            details.RootElement.GetProperty("cellAddress").GetString()!.Replace("$", "").Should().Be("C22");
            details.RootElement.GetProperty("priorValue").ValueKind.Should().Be(JsonValueKind.Null);
            details.RootElement.GetProperty("newValue").GetInt32().Should().Be(5);
            details.RootElement.GetProperty("overwrite").GetBoolean().Should().BeFalse();
        }
    }

    [Fact]
    public async Task EditCellAsync_FilledCell_OverwriteOff_SkipsWithoutAuditOrCopy()
    {
        // Arrange — fill C22 via a first edit, then attempt a second WITHOUT overwrite.
        var trialId = await CreateTrialViaServiceAsync();
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            (await service.EditCellAsync(
                trialId, new EditCellRequest { Plot = 101, Subsample = 1, TraitOrdinal = 1, Value = 5 }))
                .Written.Should().BeTrue();
        }

        // Act — overwrite not requested
        CellEditResult result;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            result = await service.EditCellAsync(
                trialId, new EditCellRequest { Plot = 101, Subsample = 1, TraitOrdinal = 1, Value = 8, Overwrite = false });
        }

        // Assert — refused with the filled-cell reason and the prior value carried
        result.Written.Should().BeFalse();
        result.Skipped.Should().BeTrue();
        result.SkippedReason.Should().Be(SingleCellWriteResult.CellAlreadyFilled);
        result.PriorValue.Should().Be(5);

        // Assert — a refused write records NO second audit entry, and the value stays 5
        using (var scope = _fixture.CreateScope())
        {
            var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();
            (await audit.GetRecentAsync()).Count(e => e.Action == "Trial.CellEdited")
                .Should().Be(1, "only the first (writing) edit is audited");

            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            var sheet = await service.GetSheetAsync(trialId, since: result.Version);
            sheet.Changed.Should().BeFalse("a refused write exports nothing, so the version is unchanged");
        }
    }

    [Fact]
    public async Task EditCellAsync_FilledCell_Overwrite_OverwritesKeepsPriorAndAudits()
    {
        // Arrange — fill C22 with 5, then overwrite with 9.
        var trialId = await CreateTrialViaServiceAsync();
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            await service.EditCellAsync(
                trialId, new EditCellRequest { Plot = 101, Subsample = 1, TraitOrdinal = 1, Value = 5 });
        }

        // Act — overwrite requested
        CellEditResult result;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            result = await service.EditCellAsync(
                trialId, new EditCellRequest { Plot = 101, Subsample = 1, TraitOrdinal = 1, Value = 9, Overwrite = true });
        }

        // Assert — written; prior value retained for the audit/undo
        result.Written.Should().BeTrue();
        result.Skipped.Should().BeFalse();
        result.PriorValue.Should().Be(5);

        // Assert — the audit entry records the prior value and the overwrite flag
        using (var scope = _fixture.CreateScope())
        {
            var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();
            var entries = await audit.GetRecentAsync();
            entries.Count(e => e.Action == "Trial.CellEdited").Should().Be(2);

            var details = JsonDocument.Parse(entries.First(e => e.Action == "Trial.CellEdited").Details!);
            details.RootElement.GetProperty("priorValue").GetInt32().Should().Be(5);
            details.RootElement.GetProperty("newValue").GetInt32().Should().Be(9);
            details.RootElement.GetProperty("overwrite").GetBoolean().Should().BeTrue();
        }
    }

    [Fact]
    public async Task EditCellAsync_UnknownRow_SkipsRowNotFoundWithoutAudit()
    {
        // Arrange — subsample 9 of plot 101 does not exist in the rating layout.
        var trialId = await CreateTrialViaServiceAsync();

        // Act
        CellEditResult result;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            result = await service.EditCellAsync(
                trialId, new EditCellRequest { Plot = 101, Subsample = 9, TraitOrdinal = 1, Value = 5 });
        }

        // Assert
        result.Written.Should().BeFalse();
        result.SkippedReason.Should().Be(SingleCellWriteResult.RowNotFound);
        result.CellAddress.Should().BeEmpty();

        using (var scope = _fixture.CreateScope())
        {
            var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();
            (await audit.GetRecentAsync()).Should().NotContain(e => e.Action == "Trial.CellEdited");
        }
    }

    [Fact]
    public async Task EditCellAsync_UnknownTrial_ThrowsKeyNotFound()
    {
        // Arrange
        using var scope = _fixture.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITrialService>();

        // Act & Assert
        var act = () => service.EditCellAsync(
            Guid.NewGuid(), new EditCellRequest { Plot = 101, Subsample = 1, TraitOrdinal = 1, Value = 5 });
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task EditCellAsync_NullRequest_ThrowsArgumentNull()
    {
        // Arrange
        using var scope = _fixture.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITrialService>();

        // Act & Assert
        var act = () => service.EditCellAsync(Guid.NewGuid(), null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Theory]
    [InlineData(0, 1, 1, 5)]   // plot ≤ 0
    [InlineData(-1, 1, 1, 5)]  // plot < 0
    [InlineData(101, 0, 1, 5)] // subsample ≤ 0
    [InlineData(101, 1, 0, 5)] // traitOrdinal < 1
    [InlineData(101, 1, 10, 5)]// traitOrdinal > 9
    [InlineData(101, 1, 1, -1)]// value < 0
    [InlineData(101, 1, 1, 11)]// value > 10
    public async Task EditCellAsync_OutOfRangeFields_ThrowArgument(int plot, int subsample, int trait, int value)
    {
        // Arrange — validation fires before any trial lookup or write.
        using var scope = _fixture.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITrialService>();

        // Act & Assert
        var act = () => service.EditCellAsync(
            Guid.NewGuid(), new EditCellRequest { Plot = plot, Subsample = subsample, TraitOrdinal = trait, Value = value });
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task EditCellAsync_MissingValue_ThrowsArgument()
    {
        // Arrange — Value is nullable: a missing value is a client error, never a write of 0.
        using var scope = _fixture.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITrialService>();

        // Act & Assert
        var act = () => service.EditCellAsync(
            Guid.NewGuid(), new EditCellRequest { Plot = 101, Subsample = 1, TraitOrdinal = 1, Value = null });
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task EditCellAsync_UnassignedFieldWorker_ThrowsForbidden()
    {
        // Arrange — a trial exists, but the actor is not assigned to it.
        var trialId = await CreateTrialViaServiceAsync();
        var fieldWorkerId = await PersistUserAsync(UserRole.FieldWorker);
        _fixture.CurrentUser.UserId = fieldWorkerId;
        _fixture.CurrentUser.Role = UserRole.FieldWorker;

        // Act & Assert
        using var scope = _fixture.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
        var act = () => service.EditCellAsync(
            trialId, new EditCellRequest { Plot = 101, Subsample = 1, TraitOrdinal = 1, Value = 5 });
        await act.Should().ThrowAsync<ForbiddenAccessException>();
    }

    [Fact]
    public async Task EditCellAsync_AssignedFieldWorker_Succeeds()
    {
        // Arrange — a field worker assigned to the trial may edit a cell.
        var trialId = await CreateTrialViaServiceAsync();
        var fieldWorkerId = await PersistUserAsync(UserRole.FieldWorker);
        await AssignTrialAsync(trialId, fieldWorkerId);
        _fixture.CurrentUser.UserId = fieldWorkerId;
        _fixture.CurrentUser.Role = UserRole.FieldWorker;

        // Act
        CellEditResult result;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            result = await service.EditCellAsync(
                trialId, new EditCellRequest { Plot = 101, Subsample = 1, TraitOrdinal = 1, Value = 7 });
        }

        // Assert
        result.Written.Should().BeTrue();
        result.PriorValue.Should().BeNull();
    }

    [Fact]
    public async Task EditCellAsync_SourceDeletedFromDisk_ThrowsFileNotFound()
    {
        // Arrange — create a trial, then remove every workbook file from disk.
        var trialId = await CreateTrialViaServiceAsync();
        var serviceDir = TrialWorkbookStore.ResolveServiceDirectory();
        var workingDir = Path.Combine(serviceDir, "Working", trialId.ToString());
        foreach (var file in Directory.GetFiles(workingDir, "*.xlsx"))
        {
            File.Delete(file);
        }

        // Act & Assert — maps to 404
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            var act = () => service.EditCellAsync(
                trialId, new EditCellRequest { Plot = 101, Subsample = 1, TraitOrdinal = 1, Value = 5 });
            await act.Should().ThrowAsync<FileNotFoundException>();
        }
    }

    [Fact]
    public async Task EditCellAsync_ConcurrentEditsDifferentCells_BothLandAndAdvanceVersion()
    {
        // Arrange — one trial; capture the version before anything was written to it.
        var trialId = await CreateTrialViaServiceAsync();

        string versionBefore;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            versionBefore = (await service.GetSheetAsync(trialId, since: null)).Version;
        }

        // Act — two edits to DIFFERENT cells of the SAME trial, each on its OWN scope so they
        // contend exactly like two concurrent web requests. Without per-trial serialization
        // both resolve the same input copy and the second export overwrites the first.
        async Task<CellEditResult> EditAsync(int traitOrdinal, int value)
        {
            using var scope = _fixture.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            return await service.EditCellAsync(
                trialId,
                new EditCellRequest { Plot = 101, Subsample = 1, TraitOrdinal = traitOrdinal, Value = value });
        }

        var results = await Task.WhenAll(
            Task.Run(() => EditAsync(1, 3)),
            Task.Run(() => EditAsync(2, 4)));

        // Assert — both writes landed and each moved the version past the pre-edit one, so
        // neither caller is left polling a version that hides its own write.
        results.Should().OnlyContain(r => r.Written, "both target cells were empty");
        results.Select(r => r.Version).Should().OnlyContain(v => v != versionBefore);

        // Assert — the workbook the sheet endpoint now serves carries BOTH values: the second
        // write chained on the first rather than overwriting it.
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            var sheet = await service.GetSheetAsync(trialId, since: null);
            var row = sheet.Rows!.Single(r => r.Plot == 101 && r.Subsample == 1);
            row.Values[0].Should().Be(3, "the first edit must survive the second edit's export");
            row.Values[1].Should().Be(4, "the second edit must be visible in the chained copy");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Creates a rating-layout trial through the service and registers it for cleanup.</summary>
    private async Task<Guid> CreateTrialViaServiceAsync()
    {
        var fileName = TrialFaker.GenerateFileName();
        using var stream = RatingWorkbookFactory.CreateStream();
        using var scope = _fixture.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
        var summary = await service.CreateTrialAsync(fileName, stream);
        var trialId = Guid.Parse(summary.TrialId);
        _createdTrialIds.Add(trialId);
        return trialId;
    }

    /// <summary>Replaces a trial's assignment set with the single given user.</summary>
    private async Task AssignTrialAsync(Guid trialId, Guid userId)
    {
        using var scope = _fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITrialRepository>();
        await repo.SetAssignmentsAsync(trialId, [userId], DefaultActorUserId);
    }

    /// <summary>Persists an additional user (FieldWorker by default) and returns its id.</summary>
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

    /// <summary>Ensures the default actor's User row exists so the FKCreatedBy constraint is satisfied.</summary>
    private async Task EnsureTestUserExistsAsync()
    {
        using var scope = _fixture.CreateScope();
        var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        if (await userRepo.GetByIdAsync(DefaultActorUserId) is not null)
            return;

        await userRepo.AddAsync(new User
        {
            Id = DefaultActorUserId,
            Name = "Test Actor",
            Email = "test@plottr02.local",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("TestPass123!"),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
    }
}
