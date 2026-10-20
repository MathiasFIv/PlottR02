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

        _fixture.CurrentUser.ResetToDefaults();
        await _fixture.ResetDatabaseAsync();
    }

    [Theory]
    [InlineData(UserRole.SystemAdmin)]
    [InlineData(UserRole.Researcher)]
    [InlineData(UserRole.TrialManager)]
    public async Task AssignTrialUsersAsync_PermittedRoles_Succeeds(UserRole role)
    {
        var trialId = await CreateTrialAsync();
        var target = await PersistUserAsync(UserRole.FieldWorker);
        _fixture.CurrentUser.Role = role;

        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            var act = () => service.AssignTrialUsersAsync(
                trialId, new AssignTrialUsersRequest { UserIds = [target] });
            await act.Should().NotThrowAsync();
        }

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
        var trialId = await CreateTrialAsync();
        var target = await PersistUserAsync(UserRole.FieldWorker);
        _fixture.CurrentUser.Role = role;

        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            var act = () => service.AssignTrialUsersAsync(
                trialId, new AssignTrialUsersRequest { UserIds = [target] });
            await act.Should().ThrowAsync<ForbiddenAccessException>()
                .WithMessage("*assign users to trials*");
        }
    }

    [Fact]
    public async Task AssignTrialUsersAsync_DuplicateIds_ThrowsArgumentException()
    {
        var trialId = await CreateTrialAsync();
        var target = await PersistUserAsync(UserRole.FieldWorker);

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
        var trialId = await CreateTrialAsync();
        var unknown = Guid.NewGuid();

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
        var trialId = await CreateTrialAsync();
        var roleLess = await PersistUserAsync(null);

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
        var trialId = await CreateTrialAsync();
        var admin = await PersistUserAsync(UserRole.SystemAdmin);

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
        var target = await PersistUserAsync(UserRole.FieldWorker);

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
        var trialId = await CreateTrialAsync();
        var userA = await PersistUserAsync(UserRole.FieldWorker);
        var unknown = Guid.NewGuid();

        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            await service.AssignTrialUsersAsync(trialId, new AssignTrialUsersRequest { UserIds = [userA] });
        }

        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            var act = () => service.AssignTrialUsersAsync(
                trialId, new AssignTrialUsersRequest { UserIds = [userA, unknown] });
            await act.Should().ThrowAsync<ArgumentException>();
        }

        using (var scope = _fixture.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<ITrialRepository>();
            var assignments = await repo.GetAssignmentsAsync(trialId);
            assignments.Select(a => a.UserId).Should().BeEquivalentTo([userA]);
        }
    }

    [Fact]
    public async Task AssignTrialUsersAsync_ReplacesWholeSet_AndEmptyListClears()
    {
        var trialId = await CreateTrialAsync();
        var userA = await PersistUserAsync(UserRole.FieldWorker);
        var userB = await PersistUserAsync(UserRole.Researcher);
        var userC = await PersistUserAsync(UserRole.TrialManager);

        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            await service.AssignTrialUsersAsync(trialId, new AssignTrialUsersRequest { UserIds = [userA, userB] });
            await service.AssignTrialUsersAsync(trialId, new AssignTrialUsersRequest { UserIds = [userB, userC] });
        }

        using (var scope = _fixture.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<ITrialRepository>();
            (await repo.GetAssignmentsAsync(trialId))
                .Select(a => a.UserId).Should().BeEquivalentTo([userB, userC]);
            (await repo.IsAssignedAsync(trialId, userA)).Should().BeFalse();
            (await repo.IsAssignedAsync(trialId, userB)).Should().BeTrue();
        }

        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            await service.AssignTrialUsersAsync(trialId, new AssignTrialUsersRequest { UserIds = [] });
        }

        using (var scope = _fixture.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<ITrialRepository>();
            (await repo.GetAssignmentsAsync(trialId)).Should().BeEmpty();
        }
    }

    [Fact]
    public async Task ListTrialUsersAsync_ReturnsAssignedUsers()
    {
        var trialId = await CreateTrialAsync();
        var userA = await PersistUserAsync(UserRole.FieldWorker);
        var userB = await PersistUserAsync(UserRole.Researcher);

        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            await service.AssignTrialUsersAsync(trialId, new AssignTrialUsersRequest { UserIds = [userA, userB] });
        }

        IReadOnlyList<UserResponse> users;
        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            users = await service.ListTrialUsersAsync(trialId);
        }

        users.Select(u => u.Id).Should().BeEquivalentTo([userA, userB]);
        users.Should().OnlyContain(u => !string.IsNullOrWhiteSpace(u.Email));
    }

    [Fact]
    public async Task ListTrialUsersAsync_ForbiddenForFieldWorker()
    {
        var trialId = await CreateTrialAsync();
        _fixture.CurrentUser.Role = UserRole.FieldWorker;

        using var scope = _fixture.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
        var act = () => service.ListTrialUsersAsync(trialId);
        await act.Should().ThrowAsync<ForbiddenAccessException>();
    }

    [Fact]
    public async Task ListTrialUsersAsync_UnknownTrial_ThrowsKeyNotFound()
    {
        using var scope = _fixture.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
        var act = () => service.ListTrialUsersAsync(Guid.NewGuid());
        await act.Should().ThrowAsync<KeyNotFoundException>()
            .WithMessage("Trial '*' was not found.");
    }

    [Fact]
    public async Task AdminCreatedTrial_CrewRoundTrip_CanBeAssignedAndRead()
    {
        _fixture.CurrentUser.Role = UserRole.SystemAdmin;
        var trialId = await CreateTrialAsync();
        var fieldWorkerId = await PersistUserAsync(UserRole.FieldWorker);

        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            var initial = await service.ListTrialUsersAsync(trialId);
            initial.Should().BeEmpty("an admin-authored trial carries no assignments");
        }

        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            var act = () => service.AssignTrialUsersAsync(
                trialId, new AssignTrialUsersRequest { UserIds = [fieldWorkerId] });
            await act.Should().NotThrowAsync();
        }

        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            var users = await service.ListTrialUsersAsync(trialId);
            users.Select(u => u.Id).Should().BeEquivalentTo([fieldWorkerId]);
        }
    }

    [Fact]
    public async Task AssignTrialUsersAsync_LogsAuditEntry()
    {
        var trialId = await CreateTrialAsync();
        var target = await PersistUserAsync(UserRole.FieldWorker);

        using (var scope = _fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITrialService>();
            await service.AssignTrialUsersAsync(trialId, new AssignTrialUsersRequest { UserIds = [target] });
        }

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
