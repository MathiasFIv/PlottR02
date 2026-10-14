using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PlottR0._2.Contracts.Dtos.Trial;
using PlottR0._2.Contracts.Dtos.User;
using PlottR0._2.Contracts.Repositories;
using PlottR0._2.Domain;
using PlottR0._2.Repository.Repositories;
using PlottR0._2.Services.Abstractions;
using PlottR0._2.Tests.Support;
using PlottR0._2.Tests.Support.WorkbookBuilder;

namespace PlottR0._2.Tests;

/// <summary>
/// Integration tests for the trial ↔ user assignment API
/// (<see cref="ITrialService.AssignTrialUsersAsync"/> /
/// <see cref="ITrialService.ListTrialUsersAsync"/>) against a real PostgreSQL via
/// Testcontainers. Verifies the actor role gate, all-or-nothing target validation,
/// replace semantics (including clearing), assigned-user reads and the audit trail.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TrialAssignmentServiceTests : IAsyncLifetime
{
    private static readonly Guid DefaultActorUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly PostgresFixture _fixture;
    private readonly List<Guid> _createdTrialIds = [];

    public TrialAssignmentServiceTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _fixture.CurrentUser.UserId = DefaultActorUserId;
        _fixture.CurrentUser.Email = "test@plottr02.local";
        _fixture.CurrentUser.Name = "Test Actor";
        // Pin a privileged role so trials can be created for the assignment tests.
        _fixture.CurrentUser.Role = UserRole.SystemAdmin;

        // FK on Trial.CreatedBy requires the actor to exist as a User row.
        await EnsureTestUserExistsAsync();
    }

    public async Task DisposeAsync()
    {
        // Clean up Working directories written by CreateTrialAsync.
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

        _fixture.CurrentUser.ResetToDefaults();
        await _fixture.ResetDatabaseAsync();
    }

    // ── Actor permissions ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(UserRole.SystemAdmin)]
    [InlineData(UserRole.Researcher)]
    [InlineData(UserRole.TrialManager)]
    public async Task AssignTrialUsersAsync_PermittedRoles_Succeeds(UserRole role)
    {
        // Arrange — create the trial while privileged, then switch to the role under test.
        var trialId = await CreateTrialAsync();
        var target = await PersistUserAsync(UserRole.FieldWorker);
        _fixture.CurrentUser.Role = role;

        // Act
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            var act = () => service.AssignTrialUsersAsync(
                trialId, new AssignTrialUsersRequest { UserIds = [target] });
            await act.Should().NotThrowAsync();
        }

        // Assert — the target is now the sole assignee.
        using (var scope = _fixture.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<ITrialRepository>();
            var assignments = await repo.GetAssignmentsAsync(trialId);
            assignments.Select(a => a.UserId).Should().BeEquivalentTo([target]);
        }
    }

    [Theory]
    [InlineData(UserRole.FieldWorker)]
    [InlineData(null)]
    public async Task AssignTrialUsersAsync_ForbiddenRoles_ThrowForbidden(UserRole? role)
    {
        // Arrange
        var trialId = await CreateTrialAsync();
        var target = await PersistUserAsync(UserRole.FieldWorker);
        _fixture.CurrentUser.Role = role;

        // Act & Assert
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            var act = () => service.AssignTrialUsersAsync(
                trialId, new AssignTrialUsersRequest { UserIds = [target] });
            await act.Should().ThrowAsync<ForbiddenAccessException>()
                .WithMessage("*assign users to trials*");
        }
    }

    // ── Target validation ─────────────────────────────────────────────────────

    [Fact]
    public async Task AssignTrialUsersAsync_DuplicateIds_ThrowsArgumentException()
    {
        // Arrange
        var trialId = await CreateTrialAsync();
        var target = await PersistUserAsync(UserRole.FieldWorker);

        // Act & Assert — duplicates are rejected, never silently de-duped.
        using var scope = _fixture.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
        var act = () => service.AssignTrialUsersAsync(
            trialId, new AssignTrialUsersRequest { UserIds = [target, target] });
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("UserIds must be distinct.");
    }

    [Fact]
    public async Task AssignTrialUsersAsync_UnknownUser_ThrowsArgumentException()
    {
        // Arrange
        var trialId = await CreateTrialAsync();
        var unknown = Guid.NewGuid();

        // Act & Assert
        using var scope = _fixture.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
        var act = () => service.AssignTrialUsersAsync(
            trialId, new AssignTrialUsersRequest { UserIds = [unknown] });
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage($"User '{unknown}' was not found.");
    }

    [Fact]
    public async Task AssignTrialUsersAsync_NullRoleTarget_ThrowsArgumentException()
    {
        // Arrange — a persisted, role-less (legacy) user.
        var trialId = await CreateTrialAsync();
        var roleLess = await PersistUserAsync(null);

        // Act & Assert
        using var scope = _fixture.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
        var act = () => service.AssignTrialUsersAsync(
            trialId, new AssignTrialUsersRequest { UserIds = [roleLess] });
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage($"User '{roleLess}' has no role*");
    }

    [Fact]
    public async Task AssignTrialUsersAsync_SystemAdminTarget_ThrowsArgumentException()
    {
        // Arrange — a system administrator is above the matrix.
        var trialId = await CreateTrialAsync();
        var admin = await PersistUserAsync(UserRole.SystemAdmin);

        // Act & Assert
        using var scope = _fixture.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
        var act = () => service.AssignTrialUsersAsync(
            trialId, new AssignTrialUsersRequest { UserIds = [admin] });
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("System administrators are above the matrix and need no trial assignment.");
    }

    [Fact]
    public async Task AssignTrialUsersAsync_UnknownTrial_ThrowsKeyNotFound()
    {
        // Arrange
        var target = await PersistUserAsync(UserRole.FieldWorker);

        // Act & Assert
        using var scope = _fixture.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
        var act = () => service.AssignTrialUsersAsync(
            Guid.NewGuid(), new AssignTrialUsersRequest { UserIds = [target] });
        await act.Should().ThrowAsync<KeyNotFoundException>()
            .WithMessage("Trial '*' was not found.");
    }

    [Fact]
    public async Task AssignTrialUsersAsync_InvalidTarget_LeavesExistingAssignmentsUntouched()
    {
        // Arrange — a trial with one valid assignment already in place.
        var trialId = await CreateTrialAsync();
        var userA = await PersistUserAsync(UserRole.FieldWorker);
        var unknown = Guid.NewGuid();

        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            await service.AssignTrialUsersAsync(trialId, new AssignTrialUsersRequest { UserIds = [userA] });
        }

        // Act — one valid + one invalid id must fail as a whole.
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            var act = () => service.AssignTrialUsersAsync(
                trialId, new AssignTrialUsersRequest { UserIds = [userA, unknown] });
            await act.Should().ThrowAsync<ArgumentException>();
        }

        // Assert — the previous set is intact (all-or-nothing).
        using (var scope = _fixture.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<ITrialRepository>();
            var assignments = await repo.GetAssignmentsAsync(trialId);
            assignments.Select(a => a.UserId).Should().BeEquivalentTo([userA]);
        }
    }

    // ── Replace semantics ─────────────────────────────────────────────────────

    [Fact]
    public async Task AssignTrialUsersAsync_ReplacesWholeSet_AndEmptyListClears()
    {
        // Arrange
        var trialId = await CreateTrialAsync();
        var userA = await PersistUserAsync(UserRole.FieldWorker);
        var userB = await PersistUserAsync(UserRole.Researcher);
        var userC = await PersistUserAsync(UserRole.TrialManager);

        // Act — first set [A, B], then [B, C].
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            await service.AssignTrialUsersAsync(trialId, new AssignTrialUsersRequest { UserIds = [userA, userB] });
            await service.AssignTrialUsersAsync(trialId, new AssignTrialUsersRequest { UserIds = [userB, userC] });
        }

        // Assert — only B and C remain.
        using (var scope = _fixture.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<ITrialRepository>();
            (await repo.GetAssignmentsAsync(trialId))
                .Select(a => a.UserId).Should().BeEquivalentTo([userB, userC]);
            (await repo.IsAssignedAsync(trialId, userA)).Should().BeFalse();
            (await repo.IsAssignedAsync(trialId, userB)).Should().BeTrue();
        }

        // Act — empty list clears the crew.
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            await service.AssignTrialUsersAsync(trialId, new AssignTrialUsersRequest { UserIds = [] });
        }

        // Assert
        using (var scope = _fixture.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<ITrialRepository>();
            (await repo.GetAssignmentsAsync(trialId)).Should().BeEmpty();
        }
    }

    // ── List assigned users ───────────────────────────────────────────────────

    [Fact]
    public async Task ListTrialUsersAsync_ReturnsAssignedUsers()
    {
        // Arrange
        var trialId = await CreateTrialAsync();
        var userA = await PersistUserAsync(UserRole.FieldWorker);
        var userB = await PersistUserAsync(UserRole.Researcher);

        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            await service.AssignTrialUsersAsync(trialId, new AssignTrialUsersRequest { UserIds = [userA, userB] });
        }

        // Act
        IReadOnlyList<UserResponse> users;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            users = await service.ListTrialUsersAsync(trialId);
        }

        // Assert — both users, mapped to response DTOs.
        users.Select(u => u.Id).Should().BeEquivalentTo([userA, userB]);
        users.Should().OnlyContain(u => !string.IsNullOrWhiteSpace(u.Email));
    }

    [Fact]
    public async Task ListTrialUsersAsync_ForbiddenForFieldWorker()
    {
        // Arrange
        var trialId = await CreateTrialAsync();
        _fixture.CurrentUser.Role = UserRole.FieldWorker;

        // Act & Assert
        using var scope = _fixture.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
        var act = () => service.ListTrialUsersAsync(trialId);
        await act.Should().ThrowAsync<ForbiddenAccessException>();
    }

    [Fact]
    public async Task ListTrialUsersAsync_UnknownTrial_ThrowsKeyNotFound()
    {
        // Act & Assert
        using var scope = _fixture.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
        var act = () => service.ListTrialUsersAsync(Guid.NewGuid());
        await act.Should().ThrowAsync<KeyNotFoundException>()
            .WithMessage("Trial '*' was not found.");
    }

    // ── Admin-authored trial round trip (C1 regression) ───────────────────────

    [Fact]
    public async Task AdminCreatedTrial_CrewRoundTrip_CanBeAssignedAndRead()
    {
        // Arrange — a SystemAdmin authors a trial (no creator assignment) and a field
        // worker exists as a candidate. This mirrors the crew modal: it opens the trial,
        // reads the (empty) crew and PUTs the selection back.
        _fixture.CurrentUser.Role = UserRole.SystemAdmin;
        var trialId = await CreateTrialAsync();
        var fieldWorkerId = await PersistUserAsync(UserRole.FieldWorker);

        // (1) The freshly created admin trial exposes an EMPTY crew — no admin row to
        //     preselect and round-trip back into the SystemAdmin target rejection.
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            var initial = await service.ListTrialUsersAsync(trialId);
            initial.Should().BeEmpty("an admin-authored trial carries no assignments");
        }

        // (2) Assigning a field worker succeeds (previously impossible: the modal sent the
        //     admin row back, and AssignTrialUsersAsync rejects SystemAdmin targets → 400).
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            var act = () => service.AssignTrialUsersAsync(
                trialId, new AssignTrialUsersRequest { UserIds = [fieldWorkerId] });
            await act.Should().NotThrowAsync();
        }

        // (3) Listing again returns exactly the field worker.
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            var users = await service.ListTrialUsersAsync(trialId);
            users.Select(u => u.Id).Should().BeEquivalentTo([fieldWorkerId]);
        }
    }

    // ── Audit trail ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AssignTrialUsersAsync_LogsAuditEntry()
    {
        // Arrange
        var trialId = await CreateTrialAsync();
        var target = await PersistUserAsync(UserRole.FieldWorker);

        // Act
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            await service.AssignTrialUsersAsync(trialId, new AssignTrialUsersRequest { UserIds = [target] });
        }

        // Assert
        using (var scope = _fixture.CreateScope())
        {
            var auditService = scope.ServiceProvider.GetRequiredService<IAuditService>();
            var entries = await auditService.GetRecentAsync();
            entries.Should().Contain(e => e.Action == "Trial.UsersAssigned");

            var entry = entries.First(e => e.Action == "Trial.UsersAssigned");
            entry.EntityType.Should().Be("Trial");
            entry.EntityId.Should().Be(trialId.ToString());
            entry.ActorUserId.Should().Be(DefaultActorUserId);

            var details = JsonDocument.Parse(entry.Details!);
            details.RootElement.GetProperty("trialId").GetString().Should().Be(trialId.ToString());
            details.RootElement.GetProperty("count").GetInt32().Should().Be(1);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Creates a trial as the current (privileged) actor and tracks its id for cleanup.</summary>
    private async Task<Guid> CreateTrialAsync()
    {
        var fileName = TrialFaker.GenerateFileName();
        using var stream = TrialWorkbookFactory.CreateStream();
        using var scope = _fixture.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
        var summary = await service.CreateTrialAsync(fileName, stream);
        var trialId = Guid.Parse(summary.TrialId);
        _createdTrialIds.Add(trialId);
        return trialId;
    }

    /// <summary>Persists a User in the database and returns its id.</summary>
    private async Task<Guid> PersistUserAsync(UserRole? role)
    {
        var userId = Guid.NewGuid();
        using var scope = _fixture.CreateScope();
        var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();

        await userRepo.AddAsync(new User
        {
            Id = userId,
            Name = $"Assignment User {userId:N}",
            Email = $"assignment.{userId:N}@plottr02.local",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("TestPass123!"),
            Role = role,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        return userId;
    }

    /// <summary>Ensures the fixed test-actor User row exists so Trials.CreatedBy FK holds.</summary>
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
            Role = UserRole.SystemAdmin,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
    }
}
