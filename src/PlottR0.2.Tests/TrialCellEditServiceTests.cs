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
            }
        }

        await _fixture.ResetDatabaseAsync();
    }

    [Fact]
    public async Task EditCellAsync_EmptyCell_WritesPersistsAuditsAndBumpsVersion()
    {
        var trialId = await CreateTrialViaServiceAsync();

        string versionBefore;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            versionBefore = (await service.GetSheetAsync(trialId, since: null)).Version;
        }

        CellEditResult result;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            result = await service.EditCellAsync(
                trialId, new EditCellRequest { Plot = 101, Subsample = 1, TraitOrdinal = 1, Value = 5 });
        }

        result.TrialId.Should().Be(trialId.ToString());
        result.Written.Should().BeTrue();
        result.Skipped.Should().BeFalse();
        result.SkippedReason.Should().BeNull();
        result.PriorValue.Should().BeNull("the cell was empty");
        result.CellAddress.Replace("$", "").Should().Be("C22");
        result.Value.Should().Be(5);
        result.GeneratedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));

        result.Version.Should().NotBeNullOrWhiteSpace();
        result.Version.Should().NotBe(versionBefore, "a new _out_ copy flips the version");
        result.FileName.Should().NotBeNull();

        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            var sheet = await service.GetSheetAsync(trialId, since: result.Version);
            sheet.Changed.Should().BeFalse("the returned version must match what is on disk now");
        }

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
        var trialId = await CreateTrialViaServiceAsync();
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            (await service.EditCellAsync(
                trialId, new EditCellRequest { Plot = 101, Subsample = 1, TraitOrdinal = 1, Value = 5 }))
                .Written.Should().BeTrue();
        }

        CellEditResult result;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            result = await service.EditCellAsync(
                trialId, new EditCellRequest { Plot = 101, Subsample = 1, TraitOrdinal = 1, Value = 8, Overwrite = false });
        }

        result.Written.Should().BeFalse();
        result.Skipped.Should().BeTrue();
        result.SkippedReason.Should().Be(SingleCellWriteResult.CellAlreadyFilled);
        result.PriorValue.Should().Be(5);

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
        var trialId = await CreateTrialViaServiceAsync();
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            await service.EditCellAsync(
                trialId, new EditCellRequest { Plot = 101, Subsample = 1, TraitOrdinal = 1, Value = 5 });
        }

        CellEditResult result;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            result = await service.EditCellAsync(
                trialId, new EditCellRequest { Plot = 101, Subsample = 1, TraitOrdinal = 1, Value = 9, Overwrite = true });
        }

        result.Written.Should().BeTrue();
        result.Skipped.Should().BeFalse();
        result.PriorValue.Should().Be(5);

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
        var trialId = await CreateTrialViaServiceAsync();

        CellEditResult result;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            result = await service.EditCellAsync(
                trialId, new EditCellRequest { Plot = 101, Subsample = 9, TraitOrdinal = 1, Value = 5 });
        }

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
        using var scope = _fixture.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITrialService>();

        var act = () => service.EditCellAsync(
            Guid.NewGuid(), new EditCellRequest { Plot = 101, Subsample = 1, TraitOrdinal = 1, Value = 5 });
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task EditCellAsync_NullRequest_ThrowsArgumentNull()
    {
        using var scope = _fixture.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITrialService>();

        var act = () => service.EditCellAsync(Guid.NewGuid(), null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Theory]
    [InlineData(0, 1, 1, 5)]
    [InlineData(-1, 1, 1, 5)]
    [InlineData(101, 0, 1, 5)]
    [InlineData(101, 1, 0, 5)]
    [InlineData(101, 1, 10, 5)]
    [InlineData(101, 1, 1, -1)]
    [InlineData(101, 1, 1, 11)]
    public async Task EditCellAsync_OutOfRangeFields_ThrowArgument(int plot, int subsample, int trait, int value)
    {
        using var scope = _fixture.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITrialService>();

        var act = () => service.EditCellAsync(
            Guid.NewGuid(), new EditCellRequest { Plot = plot, Subsample = subsample, TraitOrdinal = trait, Value = value });
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task EditCellAsync_MissingValue_ThrowsArgument()
    {
        using var scope = _fixture.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITrialService>();

        var act = () => service.EditCellAsync(
            Guid.NewGuid(), new EditCellRequest { Plot = 101, Subsample = 1, TraitOrdinal = 1, Value = null });
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task EditCellAsync_UnassignedFieldWorker_ThrowsForbidden()
    {
        var trialId = await CreateTrialViaServiceAsync();
        var fieldWorkerId = await PersistUserAsync(UserRole.FieldWorker);
        _fixture.CurrentUser.UserId = fieldWorkerId;
        _fixture.CurrentUser.Role = UserRole.FieldWorker;

        using var scope = _fixture.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
        var act = () => service.EditCellAsync(
            trialId, new EditCellRequest { Plot = 101, Subsample = 1, TraitOrdinal = 1, Value = 5 });
        await act.Should().ThrowAsync<ForbiddenAccessException>();
    }

    [Fact]
    public async Task EditCellAsync_AssignedFieldWorker_Succeeds()
    {
        var trialId = await CreateTrialViaServiceAsync();
        var fieldWorkerId = await PersistUserAsync(UserRole.FieldWorker);
        await AssignTrialAsync(trialId, fieldWorkerId);
        _fixture.CurrentUser.UserId = fieldWorkerId;
        _fixture.CurrentUser.Role = UserRole.FieldWorker;

        CellEditResult result;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            result = await service.EditCellAsync(
                trialId, new EditCellRequest { Plot = 101, Subsample = 1, TraitOrdinal = 1, Value = 7 });
        }

        result.Written.Should().BeTrue();
        result.PriorValue.Should().BeNull();
    }

    [Fact]
    public async Task EditCellAsync_SourceDeletedFromDisk_ThrowsFileNotFound()
    {
        var trialId = await CreateTrialViaServiceAsync();
        var serviceDir = TrialWorkbookStore.ResolveServiceDirectory();
        var workingDir = Path.Combine(serviceDir, "Working", trialId.ToString());
        foreach (var file in Directory.GetFiles(workingDir, "*.xlsx"))
        {
            File.Delete(file);
        }

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
        var trialId = await CreateTrialViaServiceAsync();

        string versionBefore;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            versionBefore = (await service.GetSheetAsync(trialId, since: null)).Version;
        }

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

        results.Should().OnlyContain(r => r.Written, "both target cells were empty");
        results.Select(r => r.Version).Should().OnlyContain(v => v != versionBefore);

        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            var sheet = await service.GetSheetAsync(trialId, since: null);
            var row = sheet.Rows!.Single(r => r.Plot == 101 && r.Subsample == 1);
            row.Values[0].Should().Be(3, "the first edit must survive the second edit's export");
            row.Values[1].Should().Be(4, "the second edit must be visible in the copy the second edit exported");
        }

        var workingDir = Path.Combine(
            TrialWorkbookStore.ResolveServiceDirectory(), "Working", trialId.ToString());
        var copies = Directory.GetFiles(workingDir, "*_out_*.xlsx");
        copies.Should().HaveCount(2, "each serialized edit exports its own copy");
        foreach (var copy in copies)
        {
            var stem = Path.GetFileNameWithoutExtension(copy);
            stem.IndexOf("_out_", StringComparison.Ordinal).Should().Be(
                stem.LastIndexOf("_out_", StringComparison.Ordinal),
                "a copy name carries a single stamp instead of one per edit");
            stem.Length.Should().BeLessThanOrEqualTo(125, "the name stays bounded, never chained");
        }
    }

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
